// EVent for JavaScript: a leaf node over WebSocket. Runs in browsers and in Node 22+ (the global WebSocket; pass
// `WebSocket` in the options to use another, such as the `ws` package). No dependencies.
//
// A leaf is a typed participant of an EVent network with one link, to a router (a C# node with a WebSocketServer).
// It takes part in NOTES type agreement for its own endpoints, knows the network's agreed types and checks every event and
// call against them. It never forwards anything: that is the router's job. Types are given as NOTES signatures, which
// `type()` builds from a short description; values are plain JavaScript (records are objects, lists arrays).
//
//     import { Leaf, type } from './evn.js';
//     const Reading = type('Reading', { Reading: { Celsius: 'float', Label: 'string?', History: 'double[]' } });
//     const leaf = new Leaf('sensor');
//     await leaf.connect('ws://localhost:8081/');
//     await leaf.hook('Home.Reading', r => console.log(r.Celsius), { type: Reading });
//     await leaf.provide('Home.Count', () => 42, { input: type('Void'), output: type('int32') });
//     await leaf.fire('Home.Reading', { Celsius: 21.5, History: [] });
//     const counts = await leaf.call('Home.Count', null);      // [42, ...]: one answer per provider
//
// The leaf protocol is the one the C++ leaf speaks (see the wiki: C++ leaves); the frames travel in binary WebSocket messages.

// ---------------------------------------------------------------------------------------------------- bytes

const utf8 = new TextEncoder();
const utf8d = new TextDecoder();

/** Thrown for a malformed message from the peer. */
class WireError extends Error {}
/** Thrown by operations that can't complete (not connected, stopped, a type conflict). */
export class EvnError extends Error {}

const cmp = (a, b) => (a < b ? -1 : a > b ? 1 : 0);

/** Appends little-endian values to a growing buffer. */
class Writer {
    constructor() {
        this.buf = new Uint8Array(256);
        this.len = 0;
        this.view = new DataView(this.buf.buffer);
    }
    ensure(n) {
        if (this.len + n <= this.buf.length) return;
        let capacity = this.buf.length * 2;
        while (capacity < this.len + n) capacity *= 2;
        const bigger = new Uint8Array(capacity);
        bigger.set(this.buf.subarray(0, this.len));
        this.buf = bigger;
        this.view = new DataView(bigger.buffer);
    }
    u8(v) { this.ensure(1); this.buf[this.len++] = v; }
    u16(v) { this.ensure(2); this.view.setUint16(this.len, v, true); this.len += 2; }
    i16(v) { this.ensure(2); this.view.setInt16(this.len, v, true); this.len += 2; }
    u32(v) { this.ensure(4); this.view.setUint32(this.len, v >>> 0, true); this.len += 4; }
    i32(v) { this.ensure(4); this.view.setInt32(this.len, v, true); this.len += 4; }
    i64(v) { this.ensure(8); this.view.setBigInt64(this.len, v, true); this.len += 8; }
    u64(v) { this.ensure(8); this.view.setBigUint64(this.len, v, true); this.len += 8; }
    f32(v) { this.ensure(4); this.view.setFloat32(this.len, v, true); this.len += 4; }
    f64(v) { this.ensure(8); this.view.setFloat64(this.len, v, true); this.len += 8; }
    bytes(data) { this.ensure(data.length); this.buf.set(data, this.len); this.len += data.length; }
    str(s) { const b = utf8.encode(s); this.i32(b.length); this.bytes(b); }
    finish() { return this.buf.slice(0, this.len); }
}

/** Reads little-endian values; every read throws a WireError rather than running past the end. */
class Reader {
    constructor(buf) {
        this.buf = buf;
        this.pos = 0;
        this.view = new DataView(buf.buffer, buf.byteOffset, buf.byteLength);
    }
    get remaining() { return this.buf.length - this.pos; }
    need(n) { if (n < 0 || n > this.remaining) throw new WireError('short input'); }
    take(n) { this.need(n); const out = this.buf.subarray(this.pos, this.pos + n); this.pos += n; return out; }
    u8() { this.need(1); return this.buf[this.pos++]; }
    u16() { this.need(2); const v = this.view.getUint16(this.pos, true); this.pos += 2; return v; }
    i16() { this.need(2); const v = this.view.getInt16(this.pos, true); this.pos += 2; return v; }
    u32() { this.need(4); const v = this.view.getUint32(this.pos, true); this.pos += 4; return v; }
    i32() { this.need(4); const v = this.view.getInt32(this.pos, true); this.pos += 4; return v; }
    i64() { this.need(8); const v = this.view.getBigInt64(this.pos, true); this.pos += 8; return v; }
    u64() { this.need(8); const v = this.view.getBigUint64(this.pos, true); this.pos += 8; return v; }
    f32() { this.need(4); const v = this.view.getFloat32(this.pos, true); this.pos += 4; return v; }
    f64() { this.need(8); const v = this.view.getFloat64(this.pos, true); this.pos += 8; return v; }
    str() { const n = this.i32(); return utf8d.decode(this.take(n)); }
}

// ---------------------------------------------------------------------------------------------------- decimal

const TWO_96 = 1n << 96n;

// A .NET decimal as it is on the wire: 96-bit magnitude (lo, mid, hi) and flags (scale in bits 16-23, sign in bit 31).
function parseDecimal(value) {
    let text;
    if (typeof value === 'string') text = value.trim();
    else if (typeof value === 'bigint') text = value.toString();
    else if (typeof value === 'number' && Number.isFinite(value)) text = Number.isInteger(value) ? BigInt(value).toString() : String(value);
    else return null;
    const m = /^([+-]?)(\d*)\.?(\d*)$/.exec(text);
    if (!m || (m[2] + m[3]).length === 0) return null;
    const scale = m[3].length;
    const magnitude = BigInt((m[2] + m[3]) || '0');
    if (scale > 28 || magnitude >= TWO_96) return null;
    const mask = 0xFFFFFFFFn;
    return { lo: Number(magnitude & mask), mid: Number((magnitude >> 32n) & mask), hi: Number(magnitude >> 64n), flags: ((scale << 16) | (m[1] === '-' ? 0x80000000 : 0)) >>> 0 };
}

function decimalValid(d) {
    return (d.flags & ~0x80FF0000) >>> 0 === 0 && ((d.flags >>> 16) & 0xFF) <= 28;
}

function decimalText(d) {
    const magnitude = (BigInt(d.hi) << 64n) | (BigInt(d.mid) << 32n) | BigInt(d.lo);
    const scale = (d.flags >>> 16) & 0xFF;
    let digits = magnitude.toString().padStart(scale + 1, '0');
    if (scale > 0) digits = digits.slice(0, digits.length - scale) + '.' + digits.slice(digits.length - scale);
    return (d.flags >>> 31) === 1 && magnitude !== 0n ? '-' + digits : digits;
}

// ---------------------------------------------------------------------------------------------------- types

const LEAVES = new Set(['Void', 'bool', 'int8', 'uint8', 'int16', 'uint16', 'int32', 'uint32', 'int64', 'UInt64', 'float', 'double', 'decimal', 'char16', 'string']);
const INTEGER_RANGES = {
    int8: [-128n, 127n], uint8: [0n, 255n], int16: [-32768n, 32767n], uint16: [0n, 65535n],
    int32: [-2147483648n, 2147483647n], uint32: [0n, 4294967295n],
    int64: [-(1n << 63n), (1n << 63n) - 1n], UInt64: [0n, (1n << 64n) - 1n],
};
const MAX_NESTING_DEPTH = 64;
const RESERVED = '{}[]:,=<>|?@';

function validName(name) {
    if (!name) return false;
    for (const c of name) {
        const code = c.codePointAt(0);
        if (code < 0x20 || code === 0x7F || RESERVED.includes(c)) return false;
    }
    return true;
}

const customLeaves = new Map();

/**
 * Makes an application's own leaf type known (a C# class with no fields and its own bytes, like "stamp16"). `stepOver(reader)`
 * must consume exactly one value from the reader (reader.u8(), u16(), i32(), take(n), ...) or throw. Its values are the raw
 * bytes (a Uint8Array). Register before parsing types that use it.
 */
export function registerLeaf(name, stepOver) {
    if (!validName(name)) throw new EvnError(`'${name}' is not a valid NOTES name`);
    customLeaves.set(name, stepOver);
}

function typeRef(t) {
    return t.kind === 'list' ? typeRef(t.element) + (t.cap < 0 ? '[]' : `[<=${t.cap}]`) : t.name;
}

/** A parsed signature: every type it names, and its root. */
export class Schema {
    /** Parses a canonical signature (["=Root", "Name{field:TypeRef,...}", ...]). Throws EvnError if it isn't one. */
    static parse(signature) {
        const schema = new Schema(signature);
        schema.build();
        return schema;
    }

    constructor(signature) {
        this.signature = [...signature];
        this.records = new Map();
        this.leaves = new Map();
        this.root = null;
    }

    resolve(ref) {
        if (ref.endsWith(']')) {
            const open = ref.lastIndexOf('[');
            if (open <= 0) throw new EvnError(`bad type reference '${ref}'`);
            const inner = ref.slice(open + 1, -1);
            let cap = -1;
            if (inner !== '') {
                if (!/^<=\d+$/.test(inner)) throw new EvnError(`bad cap in '${ref}'`);
                cap = parseInt(inner.slice(2), 10);
            }
            const element = this.resolve(ref.slice(0, open));
            return { kind: 'list', name: element.name, element, cap, fields: null };
        }
        if (this.records.has(ref)) return this.records.get(ref);
        if (this.leaves.has(ref)) return this.leaves.get(ref);
        const builtin = LEAVES.has(ref);
        if (!builtin && !customLeaves.has(ref)) throw new EvnError(`'${ref}' is neither a record of this signature nor a known leaf type`);
        const leaf = { kind: builtin ? ref : 'custom', name: ref, element: null, cap: -1, fields: null };
        this.leaves.set(ref, leaf);
        return leaf;
    }

    build() {
        let root = '';
        const bodies = [];
        for (const e of this.signature) {
            if (e.startsWith('=')) {
                if (root) throw new EvnError('two root entries');
                root = e.slice(1);
                continue;
            }
            const open = e.indexOf('{');
            if (open <= 0 || !e.endsWith('}')) throw new EvnError(`'${e}' is not a record definition`);
            const name = e.slice(0, open);
            if (this.records.has(name) || !validName(name)) throw new EvnError(`record '${name}' is defined twice or badly named`);
            const record = { kind: 'record', name, element: null, cap: -1, fields: [] };
            this.records.set(name, record);
            bodies.push([record, e.slice(open + 1, -1)]);
        }
        if (!root) throw new EvnError('no root entry');
        for (const [record, body] of bodies) {
            for (const item of body === '' ? [] : body.split(',')) {
                const colon = item.indexOf(':');
                if (colon <= 0) throw new EvnError(`bad field '${item}' in ${record.name}`);
                const name = item.slice(0, colon);
                let ref = item.slice(colon + 1);
                let optional = false;
                if (ref.endsWith('?')) {
                    optional = true;
                    ref = ref.slice(0, -1);
                }
                if (record.fields.some(f => f.name === name)) throw new EvnError(`${record.name} has the field '${name}' twice`);
                record.fields.push({ name, type: this.resolve(ref), optional });
            }
            record.fields.sort((a, b) => cmp(a.name, b.name));
        }
        this.root = this.resolve(root);
        // A cycle of required record fields means every value would be infinite.
        const state = new Map();
        const visit = t => {
            state.set(t, 1);
            for (const f of t.fields) {
                if (f.optional || f.type.kind !== 'record') continue;
                if (state.get(f.type) === 1) return false;
                if (!state.get(f.type) && !visit(f.type)) return false;
            }
            state.set(t, 2);
            return true;
        };
        for (const r of this.records.values()) {
            if (!state.get(r) && !visit(r)) throw new EvnError(`${r.name} contains itself through required fields only`);
        }
    }

    /** The signature in canonical form (fields and entries sorted), as the network compares it. */
    canonical() {
        const out = ['=' + typeRef(this.root)];
        for (const [name, r] of this.records) {
            out.push(name + '{' + r.fields.map(f => f.name + ':' + typeRef(f.type) + (f.optional ? '?' : '')).join(',') + '}');
        }
        return out.sort(cmp);
    }
}

/**
 * A type as a NOTES signature (an array of strings, what the network agrees on). `root` is the type of the value: a leaf
 * ('string', 'int32', 'Void', ...), a record name, or either followed by [] or [<=N] for a list. `records` defines the
 * records by name, each as { field: typeRef }, where a typeRef is a leaf, a record, a list, or any of those with a trailing
 * ? for an optional field. Field order doesn't matter: the wire order is by name.
 *
 *     type('int32')
 *     type('Reading', { Reading: { Celsius: 'float', Label: 'string?', Where: 'Spot?', History: 'double[]' },
 *                       Spot: { Floor: 'int32', Room: 'string' } })
 */
export function type(root, records = {}) {
    const entries = ['=' + root];
    for (const [name, fields] of Object.entries(records)) {
        entries.push(name + '{' + Object.keys(fields).sort(cmp).map(f => f + ':' + fields[f]).join(',') + '}');
    }
    return Object.freeze(Schema.parse(entries).canonical());
}

// ---------------------------------------------------------------------------------------------------- values (NON)

class EncodeError extends Error {}

function integerIn(value, kind) {
    let big;
    if (typeof value === 'bigint') big = value;
    else if (typeof value === 'number' && Number.isInteger(value)) big = BigInt(value);
    else return null;
    const [min, max] = INTEGER_RANGES[kind];
    return big >= min && big <= max ? big : null;
}

function floating(value) {
    if (typeof value === 'number') return value;
    if (typeof value === 'bigint') return Number(value);
    if (value === 'NaN') return NaN;
    if (value === 'Infinity') return Infinity;
    if (value === '-Infinity') return -Infinity;
    return null;
}

const isObject = v => v !== null && typeof v === 'object' && !Array.isArray(v) && !(v instanceof Uint8Array);

// Writes `v` as a value of type `t` in the NON layout; throws EncodeError (with where and why) if it doesn't fit.
function encodeValue(v, t, w, depth = 0) {
    switch (t.kind) {
        case 'Void':
            if (v !== null && v !== undefined) throw new EncodeError('not Void (use null)');
            w.u8(0);
            return;
        case 'bool':
            if (typeof v !== 'boolean') throw new EncodeError('not a bool');
            w.u8(v ? 1 : 0);
            return;
        case 'int8': case 'int16': case 'int32': case 'uint8': case 'uint16': case 'uint32': {
            const big = integerIn(v, t.kind);
            if (big === null) throw new EncodeError(`not an ${t.kind} (in range)`);
            const n = Number(big);
            if (t.kind === 'int8') w.u8(n & 0xFF);
            else if (t.kind === 'uint8') w.u8(n);
            else if (t.kind === 'int16') w.i16(n);
            else if (t.kind === 'uint16') w.u16(n);
            else if (t.kind === 'int32') w.i32(n);
            else w.u32(n);
            return;
        }
        case 'int64': case 'UInt64': {
            const big = integerIn(v, t.kind);
            if (big === null) throw new EncodeError(`not an ${t.kind} (in range)`);
            if (t.kind === 'int64') w.i64(big); else w.u64(big);
            return;
        }
        case 'float': {
            const d = floating(v);
            if (d === null) throw new EncodeError('not a number');
            w.f32(d);
            return;
        }
        case 'double': {
            const d = floating(v);
            if (d === null) throw new EncodeError('not a number');
            w.f64(d);
            return;
        }
        case 'decimal': {
            const d = parseDecimal(v);
            if (d === null || !decimalValid(d)) throw new EncodeError('not a decimal (in range)');
            w.u32(d.lo); w.u32(d.mid); w.u32(d.hi); w.u32(d.flags);
            return;
        }
        case 'char16':
            if (typeof v !== 'string' || v.length !== 1) throw new EncodeError('not a char16 (a string of one UTF-16 code unit)');
            w.u16(v.charCodeAt(0));
            return;
        case 'string':
            if (typeof v !== 'string') throw new EncodeError('not a string');
            w.str(v);
            return;
        case 'custom':
            if (!(v instanceof Uint8Array)) throw new EncodeError(`not the bytes (Uint8Array) of a ${t.name}`);
            w.bytes(v);
            return;
        case 'list': {
            if (v === null || v === undefined) v = [];
            if (!Array.isArray(v)) throw new EncodeError('not a list');
            if ((t.cap >= 0 && v.length > t.cap) || depth >= MAX_NESTING_DEPTH) throw new EncodeError(`over the cap of ${typeRef(t)}`);
            w.i32(v.length);
            for (const item of v) encodeValue(item, t.element, w, depth + 1);
            return;
        }
        case 'record': {
            if (!isObject(v)) throw new EncodeError(`not a record (${t.name})`);
            for (const n of Object.keys(v)) {
                if (v[n] !== undefined && !t.fields.some(f => f.name === n)) throw new EncodeError(`${t.name} has no field '${n}'`);
            }
            for (const f of t.fields) {
                const member = v[f.name];
                const absent = member === undefined || member === null;
                if (f.optional) {
                    w.u8(absent ? 0 : 1);
                    if (absent) continue;
                    if (depth >= MAX_NESTING_DEPTH) throw new EncodeError('nested too deep');
                    encodeInto(member, f.type, w, depth + 1, `${t.name}.${f.name}`);
                    continue;
                }
                if (absent) throw new EncodeError(`${t.name}.${f.name} is required`);
                encodeInto(member, f.type, w, depth, `${t.name}.${f.name}`);
            }
            return;
        }
        default:
            throw new EncodeError(`unknown type ${t.name}`);
    }
}

function encodeInto(v, t, w, depth, where) {
    try {
        encodeValue(v, t, w, depth);
    } catch (e) {
        if (e instanceof EncodeError && !e.message.startsWith('@')) throw new EncodeError(`@${where}: ${e.message}`);
        throw e;
    }
}

/** The NON bytes of a value of `type` (a Schema or a signature); throws EvnError if it doesn't fit. */
export function encode(value, schemaOrSignature) {
    const schema = schemaOrSignature instanceof Schema ? schemaOrSignature : Schema.parse(schemaOrSignature);
    const w = new Writer();
    try {
        encodeValue(value, schema.root, w);
    } catch (e) {
        if (e instanceof EncodeError) throw new EvnError(e.message.replace(/^@/, ''));
        throw e;
    }
    return w.finish();
}

function normalizeInt(big) {
    return big >= BigInt(Number.MIN_SAFE_INTEGER) && big <= BigInt(Number.MAX_SAFE_INTEGER) ? Number(big) : big;
}

function decodeValue(r, t, depth = 0) {
    switch (t.kind) {
        case 'Void': r.u8(); return null;
        case 'bool': {
            const b = r.u8();
            if (b > 1) throw new WireError('a bool is 0 or 1');
            return b === 1;
        }
        case 'int8': { const b = r.u8(); return b > 127 ? b - 256 : b; }
        case 'uint8': return r.u8();
        case 'int16': return r.i16();
        case 'uint16': return r.u16();
        case 'int32': return r.i32();
        case 'uint32': return r.u32();
        case 'int64': return normalizeInt(r.i64());
        case 'UInt64': return normalizeInt(r.u64());
        case 'float': return r.f32();
        case 'double': return r.f64();
        case 'decimal': {
            const d = { lo: r.u32(), mid: r.u32(), hi: r.u32(), flags: r.u32() };
            if (!decimalValid(d)) throw new WireError('invalid decimal');
            return decimalText(d);
        }
        case 'char16': return String.fromCharCode(r.u16());
        case 'string': return r.str();
        case 'custom': {
            const step = customLeaves.get(t.name);
            if (!step) throw new WireError(`unknown leaf ${t.name}`);
            const start = r.pos;
            step(r);
            return r.buf.slice(start, r.pos);
        }
        case 'list': {
            const count = r.i32();
            if (depth >= MAX_NESTING_DEPTH || count < 0 || count > r.remaining || (t.cap >= 0 && count > t.cap)) throw new WireError('bad list');
            const items = [];
            for (let i = 0; i < count; i++) {
                const before = r.remaining;
                items.push(decodeValue(r, t.element, depth + 1));
                if (r.remaining === before) throw new WireError('an element took no bytes');
            }
            return items;
        }
        case 'record': {
            const out = {};
            for (const f of t.fields) {
                if (f.optional) {
                    const present = r.u8();
                    if (present > 1) throw new WireError('bad presence byte');
                    if (present === 0) continue; // absent: not in the record
                    if (depth >= MAX_NESTING_DEPTH) throw new WireError('nested too deep');
                    out[f.name] = decodeValue(r, f.type, depth + 1);
                    continue;
                }
                out[f.name] = decodeValue(r, f.type, depth);
            }
            return out;
        }
        default:
            throw new WireError(`unknown type ${t.name}`);
    }
}

/** Reads a whole value of a type from NON bytes; throws EvnError unless the bytes are exactly one value. */
export function decode(bytes, schemaOrSignature) {
    const schema = schemaOrSignature instanceof Schema ? schemaOrSignature : Schema.parse(schemaOrSignature);
    const value = tryDecode(bytes, schema);
    if (value === undefined) throw new EvnError(`the bytes are not a ${typeRef(schema.root)}`);
    return value;
}

// The value, or undefined if the bytes aren't exactly one value of the schema's type.
function tryDecode(bytes, schema) {
    const r = new Reader(bytes);
    try {
        const value = decodeValue(r, schema.root);
        return r.remaining === 0 ? value : undefined;
    } catch (e) {
        if (e instanceof WireError || e instanceof RangeError) return undefined;
        throw e;
    }
}

// ---------------------------------------------------------------------------------------------------- the wire

const PackageType = { Invalid: 0, Data: 1, BroadcastHandshake: 2, ServerAdminEvent: 3, FunctionCall: 4, FunctionReturn: 5 };
const MAX_FRAME_SIZE = 202 * 1024 * 1024;

const ids = {
    InterconnectRunning: 'InterconnectRunning', ListEvents: 'ListEvents', QueryEvents: 'QueryEvents', EventAdded: 'EventAdded',
    EventRemoved: 'EventRemoved', LinkFault: 'LinkFault', AuthChallenge: 'AuthChallenge', AuthResponse: 'AuthResponse', AuthResult: 'AuthResult', Ping: 'Ping', Pong: 'Pong', TryInitiate: 'TryInitiateNOTESDescriptor', Finalize: 'FinalizeNOTESDescriptor',
    Abort: 'AbortNOTESDescriptor', QueryDescriptors: 'QueryDescriptors', QueryDescriptorSet: 'NOTESQueryDescriptorSet',
    ShareDescriptors: 'NOTESShareDescriptors', StopEvents: 'StopEvents', AllowEvents: 'AllowEvents', AwaitReady: 'AwaitReady',
};
const PROTOCOL_IDS = new Set([...Object.values(ids), 'CreateInterconnect', 'DisconnectInterconnect', 'NOTESMergePing', 'HandleEvent']);

const PROTOCOL_VERSION = 2;
const MINIMUM_PROTOCOL_VERSION = 1;
const IMPLEMENTATION = 'evn-js';

function describeHandshake(h) {
    return `${h.implementation || 'unknown'} (protocol ${h.version}, accepts ${h.minimum}+)`;
}

function handshakePayload(name = '') {
    const w = new Writer();
    w.i32(PROTOCOL_VERSION); w.i32(MINIMUM_PROTOCOL_VERSION); w.str(IMPLEMENTATION); w.str(name);   // the name lets a network map draw this leaf
    return w.finish();
}

function readHandshake(data) {
    if (data.length === 0) return { version: 0, minimum: 0, implementation: '', nodeName: '' };
    const r = new Reader(data);
    const h = { version: r.i32(), minimum: r.i32(), implementation: r.str(), nodeName: '' };
    if (r.remaining >= 4) { try { h.nodeName = r.str(); } catch { /* a peer from before names */ } }
    return h;
}

// A package is { id, type, data }. One frame: int32 length (of what follows) | int32 idLen | id (UTF-8) | type | data.
function writePackage(w, p) {
    const id = utf8.encode(p.id);
    const length = 4 + id.length + 1 + p.data.length;
    if (length + 4 > MAX_FRAME_SIZE) throw new EvnError('the frame is too big');
    w.i32(length); w.i32(id.length); w.bytes(id); w.u8(p.type); w.bytes(p.data);
}

function packageBytes(p) {
    const w = new Writer();
    writePackage(w, p);
    return w.finish();
}

function readPackage(r) {
    const length = r.i32();
    if (length < 5 || length > r.remaining) throw new WireError('bad frame length');
    const b = new Reader(r.take(length));
    const idLength = b.i32();
    if (idLength < 0 || idLength > length - 5) throw new WireError('bad id length');
    const id = utf8d.decode(b.take(idLength));
    const type = b.u8();
    if (type > 5) throw new WireError('bad package type');
    return { id, type, data: b.take(b.remaining).slice() };
}

/** Splits a byte stream into packages. */
class FrameParser {
    constructor() { this.buf = new Uint8Array(0); }
    feed(chunk) {
        if (this.buf.length === 0) { this.buf = chunk; return; }
        const joined = new Uint8Array(this.buf.length + chunk.length);
        joined.set(this.buf); joined.set(chunk, this.buf.length);
        this.buf = joined;
    }
    /** The next whole package, or null if more bytes are needed; throws WireError if the stream can't be trusted any more. */
    next() {
        if (this.buf.length < 4) return null;
        const length = new DataView(this.buf.buffer, this.buf.byteOffset, 4).getInt32(0, true);
        if (length < 5 || length > MAX_FRAME_SIZE - 4) throw new WireError(`frame length ${length} is out of range`);
        if (this.buf.length < 4 + length) return null;
        const frame = new Reader(this.buf.subarray(0, 4 + length));
        let p;
        try { p = readPackage(frame); } catch (e) {
            if (e instanceof WireError || e instanceof RangeError) throw new WireError('frame contents are not a valid package');
            throw e;
        }
        this.buf = this.buf.subarray(4 + length);
        return p;
    }
}

// A plain collection: int32 count, then per item an int32 byte length and the item (length 0: a null item).
function writeCollection(w, items, writeItem) {
    w.i32(items.length);
    for (const item of items) {
        const one = new Writer();
        writeItem(one, item);
        w.i32(one.len);
        w.bytes(one.finish());
    }
}

function readCollection(r, readItem, keepNulls = false, nullItem = () => null) {
    const count = r.i32();
    if (count < 0 || count > r.remaining / 4) throw new WireError('bad collection');
    const items = [];
    for (let i = 0; i < count; i++) {
        const length = r.i32();
        const at = r.take(length);
        if (length === 0) {
            if (keepNulls) items.push(nullItem());
            continue;
        }
        items.push(readItem(new Reader(at)));
    }
    return items;
}

const writeStrings = (w, items) => writeCollection(w, items, (o, s) => o.str(s));
// A null entry reads as "" (as the .NET side compares signatures).
const readStrings = r => readCollection(r, i => i.str(), true, () => '');
const stringPayload = s => { const w = new Writer(); w.str(s); return w.finish(); };
const stringsPayload = items => { const w = new Writer(); writeStrings(w, items); return w.finish(); };

// A call or its answers: Parameters (a whole frame), ReturnValues (one package per answer), Handle.
function functionPayload(f) {
    const w = new Writer();
    writePackage(w, f.parameters);
    writeCollection(w, f.returns, (o, p) => writePackage(o, p));
    w.u64(BigInt(f.callId)); w.i32(f.clientId);
    return w.finish();
}

function readFunction(data) {
    const r = new Reader(data);
    const parameters = readPackage(r);
    const returns = readCollection(r, i => readPackage(i));
    const callId = r.u64();
    // Bit 63 of the call ID marks a call nobody answers (publish). The ID itself is kept as a number (exact below 2^53, which
    // the counting IDs are); it is only echoed in answers, and an unanswered call gets none.
    return { parameters, returns, callId: Number(callId & UNANSWERED_MASK), unanswered: (callId & UNANSWERED_FLAG) !== 0n, clientId: r.i32() };
}

const UNANSWERED_FLAG = 1n << 63n;
const UNANSWERED_MASK = UNANSWERED_FLAG - 1n;

// The agreed type of an ID: input signature (expected), answer signature (returns; ["=Void"] for an event), and the first
// proposer's documentation. Two descriptors are the same type when ID, expected and returns are equal.
function writeAnnotation(w, a) {
    w.str(a.record); w.str(a.field); w.str(a.description); w.str(a.min); w.str(a.max); w.str(a.defaultJson);
}
function readAnnotation(r) {
    return { record: r.str(), field: r.str(), description: r.str(), min: r.str(), max: r.str(), defaultJson: r.str() };
}
function writeDescriptor(w, d) {
    writeStrings(w, d.expected); w.str(d.id); writeStrings(w, d.returns); w.i32(d.weight); w.str(d.description);
    writeCollection(w, d.inputAnnotations, writeAnnotation);
    writeCollection(w, d.returnAnnotations, writeAnnotation);
}
function readDescriptor(r) {
    return {
        expected: readStrings(r), id: r.str(), returns: readStrings(r), weight: r.i32(), description: r.str(),
        inputAnnotations: readCollection(r, readAnnotation), returnAnnotations: readCollection(r, readAnnotation),
    };
}
const descriptorPayload = d => { const w = new Writer(); writeDescriptor(w, d); return w.finish(); };
const descriptorsPayload = items => { const w = new Writer(); writeCollection(w, items, writeDescriptor); return w.finish(); };
// A descriptor as the API shows it.
const describeType = d => ({
    id: d.id, expected: d.expected, returns: d.returns, description: d.description, isEvent: d.returns.length === 1 && d.returns[0] === '=Void',
    inputAnnotations: d.inputAnnotations, returnAnnotations: d.returnAnnotations,
});

// An ID an application chose: not one of the protocol's, and no empty or $-prefixed part (those are system endpoints).
function isApplicationId(id) {
    return id !== '' && !PROTOCOL_IDS.has(id) && id.split('.').every(part => part !== '' && !part.startsWith('$'));
}

const sameList = (a, b) => a.length === b.length && a.every((x, i) => x === b[i]);
const sameType = (a, b) => a.id === b.id && sameList(a.expected, b.expected) && sameList(a.returns, b.returns);
const isEvent = d => d.returns.length === 1 && d.returns[0] === '=Void';

// ---------------------------------------------------------------------------------------------------- the type store

const Conflict = -1, Accepted = 0, Pending = 1, Established = 2;

/** What this leaf knows about the network's agreed types, and how it answers the agreement protocol. */
class TypeStore {
    constructor() {
        this.established = new Map();
        this.pending = new Map();
        this.proposalTtlMs = 5000;
    }
    tryInitiate(d, now) {
        const known = this.established.get(d.id);
        if (known) return sameType(known, d) ? Established : Conflict;
        const pending = this.livePending(d.id, now);
        if (pending) {
            if (!sameType(pending, d)) return Conflict;
            if (pending.weight < d.weight) { this.setPending(d, now); return Accepted; }
            return Pending;
        }
        this.setPending(d, now);
        return Accepted;
    }
    finalize(d) {
        const known = this.established.get(d.id);
        if (known) return sameType(known, d) ? 1 : -1;
        this.established.set(d.id, d);
        this.pending.delete(d.id);
        return 1;
    }
    abort(d) {
        const pending = this.pending.get(d.id);
        if (pending && sameType(pending.descriptor, d) && pending.descriptor.weight === d.weight) this.pending.delete(d.id);
    }
    adopt(d) {
        if (!this.established.has(d.id)) this.established.set(d.id, d);
        return this.established.get(d.id);
    }
    find(id) { return this.established.get(id) ?? null; }
    findSet(idList) { return idList.map(id => this.find(id)).filter(Boolean); }
    share(items) { return items.filter(d => this.finalize(d) < 0).map(d => d.id); }
    all() { return [...this.established.values()]; }
    livePending(id, now) {
        const pending = this.pending.get(id);
        if (!pending) return null;
        if (now < pending.expires) return pending.descriptor;
        this.pending.delete(id);
        return null;
    }
    setPending(d, now) { this.pending.set(d.id, { descriptor: d, expires: now + this.proposalTtlMs }); }
}

// ---------------------------------------------------------------------------------------------------- authentication

// SHA-256 and HMAC, in plain JavaScript: crypto.subtle is asynchronous and absent from pages that are not secure contexts, and a leaf
// has to authenticate wherever it runs.
const SHA256_K = new Uint32Array([
    0x428a2f98, 0x71374491, 0xb5c0fbcf, 0xe9b5dba5, 0x3956c25b, 0x59f111f1, 0x923f82a4, 0xab1c5ed5, 0xd807aa98, 0x12835b01, 0x243185be, 0x550c7dc3, 0x72be5d74, 0x80deb1fe, 0x9bdc06a7, 0xc19bf174,
    0xe49b69c1, 0xefbe4786, 0x0fc19dc6, 0x240ca1cc, 0x2de92c6f, 0x4a7484aa, 0x5cb0a9dc, 0x76f988da, 0x983e5152, 0xa831c66d, 0xb00327c8, 0xbf597fc7, 0xc6e00bf3, 0xd5a79147, 0x06ca6351, 0x14292967,
    0x27b70a85, 0x2e1b2138, 0x4d2c6dfc, 0x53380d13, 0x650a7354, 0x766a0abb, 0x81c2c92e, 0x92722c85, 0xa2bfe8a1, 0xa81a664b, 0xc24b8b70, 0xc76c51a3, 0xd192e819, 0xd6990624, 0xf40e3585, 0x106aa070,
    0x19a4c116, 0x1e376c08, 0x2748774c, 0x34b0bcb5, 0x391c0cb3, 0x4ed8aa4a, 0x5b9cca4f, 0x682e6ff3, 0x748f82ee, 0x78a5636f, 0x84c87814, 0x8cc70208, 0x90befffa, 0xa4506ceb, 0xbef9a3f7, 0xc67178f2,
]);

/** SHA-256 of a Uint8Array, as a Uint8Array of 32 bytes. */
export function sha256(data) {
    const length = data.length;
    const padded = new Uint8Array(((length + 9 + 63) >> 6) << 6);
    padded.set(data);
    padded[length] = 0x80;
    const view = new DataView(padded.buffer);
    view.setUint32(padded.length - 8, Math.floor(length / 0x20000000), false);   // the length in bits, high word
    view.setUint32(padded.length - 4, (length << 3) >>> 0, false);
    const h = new Uint32Array([0x6a09e667, 0xbb67ae85, 0x3c6ef372, 0xa54ff53a, 0x510e527f, 0x9b05688c, 0x1f83d9ab, 0x5be0cd19]);
    const w = new Uint32Array(64);
    const rotr = (x, n) => (x >>> n) | (x << (32 - n));
    for (let offset = 0; offset < padded.length; offset += 64) {
        for (let i = 0; i < 16; i++) w[i] = view.getUint32(offset + i * 4, false);
        for (let i = 16; i < 64; i++) {
            const s0 = rotr(w[i - 15], 7) ^ rotr(w[i - 15], 18) ^ (w[i - 15] >>> 3);
            const s1 = rotr(w[i - 2], 17) ^ rotr(w[i - 2], 19) ^ (w[i - 2] >>> 10);
            w[i] = (w[i - 16] + s0 + w[i - 7] + s1) | 0;
        }
        let [a, b, c, d, e, f, g, hh] = h;
        for (let i = 0; i < 64; i++) {
            const t1 = (hh + (rotr(e, 6) ^ rotr(e, 11) ^ rotr(e, 25)) + ((e & f) ^ (~e & g)) + SHA256_K[i] + w[i]) | 0;
            const t2 = ((rotr(a, 2) ^ rotr(a, 13) ^ rotr(a, 22)) + ((a & b) ^ (a & c) ^ (b & c))) | 0;
            hh = g; g = f; f = e; e = (d + t1) | 0; d = c; c = b; b = a; a = (t1 + t2) | 0;
        }
        h[0] += a; h[1] += b; h[2] += c; h[3] += d; h[4] += e; h[5] += f; h[6] += g; h[7] += hh;
    }
    const out = new Uint8Array(32);
    const outView = new DataView(out.buffer);
    h.forEach((x, i) => outView.setUint32(i * 4, x, false));
    return out;
}

/** HMAC-SHA256 of `data` under `key` (both Uint8Array). */
export function hmacSha256(key, data) {
    if (key.length > 64) key = sha256(key);
    const inner = new Uint8Array(64 + data.length);
    const outer = new Uint8Array(64 + 32);
    for (let i = 0; i < 64; i++) {
        const k = i < key.length ? key[i] : 0;
        inner[i] = k ^ 0x36;
        outer[i] = k ^ 0x5c;
    }
    inner.set(data, 64);
    outer.set(sha256(inner), 64);
    return sha256(outer);
}

// The link authentication of the leaf protocol (see AuthProtocol in the C# library): a proof is HMAC-SHA256 under the secret over a label
// and four length-prefixed fields, so neither side's proof can be replayed as the other's.
const CLIENT_LABEL = 'EVENT-AUTH-1 client';
const SERVER_LABEL = 'EVENT-AUTH-1 server';
const NONCE_SIZE = 32;

/** The proof a client sends (label 'EVENT-AUTH-1 client') or a server answers with ('EVENT-AUTH-1 server'). Exported for tests and other implementations. */
export function authProof(label, secret, serverNonce, clientNonce, userId, realm) {
    const w = new Writer();
    w.bytes(utf8.encode(label));
    for (const field of [serverNonce, clientNonce, utf8.encode(userId), utf8.encode(realm)]) {
        w.u32(field.length);
        w.bytes(field);
    }
    return hmacSha256(utf8.encode(secret), w.finish());
}

const sameBytes = (a, b) => {
    let difference = a.length ^ b.length;
    for (let i = 0; i < Math.min(a.length, b.length); i++) difference |= a[i] ^ b[i];
    return difference === 0;
};

function randomBytes(n) {
    const crypto = globalThis.crypto;
    if (!crypto?.getRandomValues) throw new EvnError('no secure random numbers here: authentication needs crypto.getRandomValues');
    return crypto.getRandomValues(new Uint8Array(n));
}

const bytesField = bytes => { const w = new Writer(); w.i32(bytes.length); w.bytes(bytes); return w.finish(); };

// ---------------------------------------------------------------------------------------------------- the leaf

const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
const int32Bytes = v => { const w = new Writer(); w.i32(v); return w.finish(); };

/**
 * A leaf node: one link to a router over WebSocket. Options: `callTimeout` (ms a call waits for answers, default 30000),
 * `reconnect` (dial again after a lost link, default true), `reconnectDelay` (ms, default 1000), `handshakeTimeout` (ms,
 * default 10000), `WebSocket` (the constructor to use, default the global one) and `log(text)` for the leaf's own messages.
 */
export class Leaf {
    constructor(name, options = {}) {
        this.name = name;
        /** Credentials ({ userId, secret }) to answer a router that requires authentication; with them, a router that doesn't ask is refused unless `allowUnauthenticatedRouter`. */
        this.credentials = options.credentials ?? null;
        this.allowUnauthenticatedRouter = options.allowUnauthenticatedRouter ?? false;
        /** Keepalive ({ interval, timeout } in ms): ping the router after `interval` of quiet, drop the link after `timeout` (default 3 intervals) of silence. Off by default. */
        this.keepAlive = options.keepAlive ?? null;
        this.keepAliveTimer = null;
        this.lastHeard = 0;
        this.lastPing = 0;
        this.pendingPing = null;
        this.pingCount = 0;
        this.rttMs = null;
        this.authDone = !this.credentials;      // nothing is announced to the router until it has proved itself (when credentials are set)
        this.expectedServerProof = null;
        this.serverRealm = '';
        this.faultReason = '';
        this.callTimeout = options.callTimeout ?? 30000;
        this.reconnect = options.reconnect ?? true;
        this.reconnectDelay = options.reconnectDelay ?? 1000;
        this.handshakeTimeout = options.handshakeTimeout ?? 10000;
        this.WebSocketImpl = options.WebSocket ?? globalThis.WebSocket;
        this.logger = options.log ?? null;
        /** The last reason a fire, call or hook was refused (a type mismatch, a value that doesn't fit). */
        this.lastProblem = '';

        this.local = new Map();          // id -> entries: this leaf's handlers (its interest)
        this.tokenIds = new Map();
        this.remote = new Set();         // what the router routes onward (its interest)
        this.active = new Map();         // calls in flight
        this.lanes = new Map();          // event id -> the last queued unanswered call, so one ID's calls run in order
        this.store = new TypeStore();
        this.schemas = new Map();
        this.socket = null;
        this.up = false;
        this.routerHandshake = null;
        this.url = null;
        this.redial = false;
        this.stopping = false;
        this.nextCall = 1;
        this.nextToken = 1;
        this.linkHandlers = [];
        this.upWaiters = [];
        this.gateClosed = false;
        this.inside = 0;
        this.gateWaiters = [];
        this.insideWaiters = [];
        this.registerNotes();
    }

    /** True once the router's handshake has arrived and until the link drops. */
    get connected() { return this.up; }

    /** True when this leaf proved its user ID to the router and the router proved it knows the secret too (false when no credentials are set). */
    get authenticated() { return !!this.credentials && this.authDone && this.serverRealm !== ''; }

    /** The last measured ping round trip to the router in ms (needs keepalive), or null. */
    get roundTrip() { return this.rttMs; }

    /** The user ID this leaf authenticates as (its credentials), or null. */
    get userId() { return this.credentials?.userId ?? null; }

    /** Calls `handler(up, reason)` whenever the link comes up or goes down. */
    onLink(handler) { this.linkHandlers.push(handler); }

    log(text) { if (this.logger) this.logger(`${this.name}: ${text}`); }
    problem(text) { this.lastProblem = text; this.log(text); }

    // ------------------------------------------------------------------ the link

    /**
     * Links to the router at `url` (ws://host:port/) and resolves once its handshake has arrived. Rejects if the first dial
     * fails or the router speaks an incompatible protocol; after that a lost link is dialed again (`reconnect`).
     */
    connect(url) {
        if (this.stopping) return Promise.reject(new EvnError('the leaf has stopped'));
        this.url = url;
        this.redial = this.reconnect;
        if (this.up) return Promise.resolve();
        return new Promise((resolve, reject) => {
            const timer = setTimeout(() => { fail(new EvnError('the router did not answer in time')); }, this.handshakeTimeout);
            const done = () => { clearTimeout(timer); this.upWaiters = this.upWaiters.filter(w => w !== waiter); };
            const fail = e => { done(); this.redial = false; this.closeSocket(); reject(e); };
            const waiter = { resolve: () => { done(); resolve(); }, reject: fail };
            this.upWaiters.push(waiter);
            this.dial().catch(fail);
        });
    }

    async dial() {
        if (!this.WebSocketImpl) throw new EvnError('no WebSocket available: pass one in the options');
        const socket = new this.WebSocketImpl(this.url);
        socket.binaryType = 'arraybuffer';
        this.socket = socket;
        const parser = new FrameParser();
        await new Promise((resolve, reject) => {
            socket.addEventListener('open', () => {
                if (this.socket !== socket) return;
                this.remote.clear();
                this.startKeepAlive();
                this.authDone = !this.credentials;
                this.expectedServerProof = null;
                this.serverRealm = '';
                this.faultReason = '';
                this.send({ id: ids.InterconnectRunning, type: PackageType.ServerAdminEvent, data: handshakePayload(this.name) });
                resolve();
            });
            socket.addEventListener('error', () => reject(new EvnError(`couldn't reach ${this.url}`)));
            socket.addEventListener('close', e => {
                reject(new EvnError(`couldn't reach ${this.url}`));
                if (this.socket === socket) this.linkDown(e.reason || 'the router closed the link');
            });
            socket.addEventListener('message', ev => {
                if (this.socket !== socket) return;
                this.lastHeard = performance.now();
                const data = ev.data;
                if (typeof data === 'string') return this.fault('the router sent a text message');
                const chunk = data instanceof ArrayBuffer ? new Uint8Array(data) : new Uint8Array(data.buffer, data.byteOffset, data.byteLength);
                parser.feed(chunk);
                try {
                    for (let p = parser.next(); p; p = parser.next()) this.process(p);
                } catch (e) {
                    if (e instanceof WireError || e instanceof RangeError) return this.fault(e.message);
                    throw e;
                }
            });
        });
    }

    // Keepalive: after `interval` of quiet the router is pinged (anything it sends counts as hearing from it); after `timeout` of silence the link
    // is dropped, as if the connection had closed, and (reconnect on) dialed again. Only a router that announced protocol 2 is pinged.
    startKeepAlive() {
        this.stopKeepAlive();
        if (!this.keepAlive) return;
        const interval = this.keepAlive.interval;
        const timeout = this.keepAlive.timeout ?? interval * 3;
        this.lastHeard = performance.now();
        this.lastPing = 0;
        this.pendingPing = null;
        this.keepAliveTimer = setInterval(() => {
            const socket = this.socket;
            if (!socket || socket.readyState !== 1 || !this.routerHandshake || this.routerHandshake.version < 2) return;
            const now = performance.now();
            const silent = now - this.lastHeard;
            if (silent >= timeout) {
                this.faultReason = `keepalive timeout: nothing from the router for ${Math.round(silent)} ms (limit ${timeout} ms)`;
                this.log(this.faultReason);
                this.closeSocket();
            } else if (silent >= interval && now - this.lastPing >= interval) {
                this.lastPing = now;
                const token = BigInt(++this.pingCount);
                this.pendingPing = { token, sentAt: now };
                const w = new Writer();
                w.u64(token);
                this.send({ id: ids.Ping, type: PackageType.ServerAdminEvent, data: w.finish() });
            }
        }, Math.max(5, interval / 4));
    }

    stopKeepAlive() {
        if (this.keepAliveTimer) clearInterval(this.keepAliveTimer);
        this.keepAliveTimer = null;
    }

    // The router asks who this leaf is: answer with the proof, and remember what the router must prove in return.
    answerChallenge(p) {
        const r = new Reader(p.data);
        const serverNonce = r.take(r.i32()).slice();
        const realm = r.str();
        if (!this.credentials) {
            return this.refuse('the router requires authentication and this leaf has no credentials');
        }
        if (serverNonce.length !== NONCE_SIZE) throw new WireError('the challenge nonce has the wrong size');
        const { userId, secret } = this.credentials;
        const clientNonce = randomBytes(NONCE_SIZE);
        this.serverRealm = realm;
        this.expectedServerProof = authProof(SERVER_LABEL, secret, serverNonce, clientNonce, userId, realm);
        const proof = authProof(CLIENT_LABEL, secret, serverNonce, clientNonce, userId, realm);
        const w = new Writer();
        w.str(userId); w.bytes(bytesField(clientNonce)); w.bytes(bytesField(proof));
        this.send({ id: ids.AuthResponse, type: PackageType.ServerAdminEvent, data: w.finish() });
    }

    // The router proves it knows the secret too. One that cannot is not the router this leaf meant to talk to.
    checkServerProof(p) {
        const r = new Reader(p.data);
        const proof = r.take(r.i32());
        if (!this.expectedServerProof || !sameBytes(proof, this.expectedServerProof)) {
            return this.refuse('the router did not prove it knows the shared secret');
        }
        this.expectedServerProof = null;
        this.authDone = true;
        this.log(`authenticated as ${this.credentials.userId} to ${this.serverRealm}`);
    }

    // Authentication cannot go on: tell the router why, close the link, and do not dial again (nothing would change).
    refuse(reason) {
        this.redial = false;
        for (const w of [...this.upWaiters]) w.reject(new EvnError(reason));
        this.fault(reason);
    }

    // Tells the router why, and closes the link.
    fault(reason) {
        this.send({ id: ids.LinkFault, type: PackageType.ServerAdminEvent, data: stringPayload(reason) });
        this.log(`malformed input: ${reason}`);
        this.closeSocket();
    }

    closeSocket() {
        const socket = this.socket;
        if (!socket) return;
        try { socket.close(); } catch { /* already closed */ }
        if (socket.readyState !== 0 && socket.readyState !== 1) this.linkDown('closed');
    }

    linkDown(reason) {
        this.stopKeepAlive();
        reason = this.faultReason || reason;
        const wasUp = this.up;
        this.socket = null;
        this.up = false;
        this.remote.clear();
        for (const st of [...this.active.values()]) {
            if (st.remote) { st.remote = false; this.complete(st, false); } // calls complete with what arrived
        }
        this.log(`link down: ${reason}`);
        if (wasUp) for (const h of this.linkHandlers) this.safely(() => h(false, reason));
        if (this.redial && !this.stopping) {
            setTimeout(() => {
                if (!this.redial || this.stopping || this.socket) return;
                this.dial().catch(() => { /* the close event of the failed dial schedules the next attempt */ });
            }, this.reconnectDelay);
        }
        if (!this.redial) for (const w of [...this.upWaiters]) w.reject(new EvnError(`the link failed: ${reason}`));
    }

    safely(fn) { try { fn(); } catch (e) { this.log(`a handler threw: ${e}`); } }

    send(p) {
        const socket = this.socket;
        if (!socket || socket.readyState !== 1) return false;
        try { socket.send(packageBytes(p)); return true; } catch (e) { this.log(`send failed: ${e.message}`); return false; }
    }

    /** Leaves the network: closes the link, ends the calls in flight with what arrived. The leaf can't be used afterwards. */
    stop() {
        if (this.stopping) return;
        this.stopping = true;
        this.redial = false;
        this.stopKeepAlive();
        const socket = this.socket;
        this.socket = null;
        try { socket?.close(); } catch { /* already closed */ }
        const wasUp = this.up;
        this.up = false;
        for (const st of [...this.active.values()]) this.complete(st, true);
        for (const w of this.gateWaiters.splice(0)) w();
        for (const w of this.insideWaiters.splice(0)) w();
        for (const w of [...this.upWaiters]) w.reject(new EvnError('the leaf has stopped'));
        if (wasUp) for (const h of this.linkHandlers) this.safely(() => h(false, 'stopped'));
    }

    // One package from the router.
    process(p) {
        if (p.type === PackageType.ServerAdminEvent) {
            if (p.id === ids.InterconnectRunning) {
                const peer = readHandshake(p.data);
                const accepts = peer.version >= MINIMUM_PROTOCOL_VERSION && PROTOCOL_VERSION >= peer.minimum;
                if (!accepts) {
                    this.redial = false;
                    const own = { version: PROTOCOL_VERSION, minimum: MINIMUM_PROTOCOL_VERSION, implementation: IMPLEMENTATION };
                    const reason = `protocol version mismatch: the router is ${describeHandshake(peer)}, this leaf is ${describeHandshake(own)}`;
                    this.send({ id: ids.LinkFault, type: PackageType.ServerAdminEvent, data: stringPayload(reason) });
                    for (const w of [...this.upWaiters]) w.reject(new EvnError(reason));
                    this.closeSocket();
                    return;
                }
                this.routerHandshake = peer;
            }
            if (p.id === ids.Ping) {
                this.send({ id: ids.Pong, type: PackageType.ServerAdminEvent, data: p.data });   // answered at once, whatever else is going on
            } else if (p.id === ids.Pong) {
                const token = new Reader(p.data).u64();
                if (this.pendingPing && this.pendingPing.token === token) {
                    this.rttMs = performance.now() - this.pendingPing.sentAt;
                    this.pendingPing = null;
                }
            } else if (p.id === ids.AuthChallenge) {
                this.answerChallenge(p);
            } else if (p.id === ids.AuthResult) {
                this.checkServerProof(p);
            } else if (p.id === ids.InterconnectRunning || p.id === ids.QueryEvents) {
                // With credentials, what this leaf is interested in is said once the router has proved itself: the router asks after it has.
                if (this.authDone) this.send({ id: ids.ListEvents, type: PackageType.ServerAdminEvent, data: stringsPayload([...this.local.keys()]) });
            } else if (p.id === ids.ListEvents) {
                if (!this.authDone) {
                    if (!this.allowUnauthenticatedRouter) {
                        return this.refuse('the router did not authenticate: this leaf has credentials and requires its router to prove it knows the shared secret');
                    }
                    this.authDone = true;   // a router that does not ask for authentication, and this leaf is told to accept that
                    this.send({ id: ids.ListEvents, type: PackageType.ServerAdminEvent, data: stringsPayload([...this.local.keys()]) });
                }
                const list = readStrings(new Reader(p.data));
                for (const id of list) this.remote.add(id);
                const cameUp = !this.up;
                this.up = true;
                if (cameUp) {
                    this.log('link up');
                    for (const w of [...this.upWaiters]) w.resolve();
                    for (const h of this.linkHandlers) this.safely(() => h(true, ''));
                }
                if (list.includes(ids.ShareDescriptors)) this.shareDescriptors().catch(e => this.log(`sharing types failed: ${e.message}`));
            } else if (p.id === ids.EventAdded || p.id === ids.EventRemoved) {
                const id = new Reader(p.data).str();
                if (p.id === ids.EventAdded) this.remote.add(id); else this.remote.delete(id);
            } else if (p.id === ids.LinkFault) {
                let reason = '';
                try { reason = new Reader(p.data).str(); } catch { /* no reason */ }
                this.log(`the router dropped the link: ${reason}`);
                this.faultReason = reason ? `the router dropped the link: ${reason}` : '';
                if (!this.up) {
                    // Refused before the link was up (a wrong secret, an unknown user, a protocol it will not speak): say so now, and do not dial again.
                    this.redial = false;
                    for (const w of [...this.upWaiters]) w.reject(new EvnError(this.faultReason || 'the router refused the link'));
                }
                this.closeSocket();
            }
            return;
        }
        if (p.type === PackageType.FunctionCall) {
            const f = readFunction(p.data);
            if (f.parameters.id !== p.id) throw new WireError(`function call for '${p.id}' carries parameters for '${f.parameters.id}'`);
            this.answerCall(f, this.entries(p.id, true));
        } else if (p.type === PackageType.FunctionReturn) {
            const f = readFunction(p.data);
            const st = this.active.get(f.callId);
            if (!st || !st.remote) return; // timed out or cancelled already
            for (const r of f.returns) st.answers.push(r.data);
            st.remote = false;
            this.complete(st, false);
        } else if (p.type === PackageType.Data) {
            for (const e of this.entries(p.id, false)) this.safely(() => e.raw(p.data));
        }
    }

    // Runs this leaf's handlers for a call from the router and sends their answers back once all answered (always, even none).
    answerCall(f, mine) {
        if (f.unanswered) {
            // Nobody waits for an answer: run the handlers, one event ID's calls after the other in the order they arrived, send nothing.
            for (const e of mine) this.inLane(f.parameters.id, () => new Promise(done => this.invoke(e, f.parameters.data, () => done())));
            return;
        }
        const returns = [];
        let left = mine.length;
        // The answer carries no parameters (an empty package for the same ID): the caller has them, and the router reads only the
        // handle and the answers.
        const respond = () => this.send({
            id: f.parameters.id, type: PackageType.FunctionReturn,
            data: functionPayload({ parameters: { id: f.parameters.id, type: PackageType.FunctionCall, data: new Uint8Array(0) }, returns, callId: f.callId, clientId: f.clientId }),
        });
        if (left === 0) return respond();
        for (const e of mine) {
            this.invoke(e, f.parameters.data, answer => {
                if (answer && answer.length > 0) returns.push({ id: f.parameters.id, type: PackageType.FunctionReturn, data: answer });
                if (--left === 0) respond();
            });
        }
    }

    // After a link comes up: offer every known type to the network (it commits the ones it lacks).
    async shareDescriptors() {
        const known = this.store.all();
        if (known.length === 0) return;
        for (const c of await this.protocolCall(ids.ShareDescriptors, descriptorsPayload(known))) {
            for (const id of readStrings(new Reader(c))) this.log(`the network has a different type for ${id}`);
        }
    }

    // ------------------------------------------------------------------ handlers and interest

    // Runs a handler (bytes in, bytes or null out); `reply` takes its answer once. A handler that throws gives no answer.
    invoke(entry, input, reply) {
        let used = false;
        const once = a => { if (!used) { used = true; reply(a ?? null); } };
        Promise.resolve().then(() => entry.call(input)).then(once, e => { this.log(`a handler threw: ${e?.message ?? e}`); once(null); });
    }

    entries(id, calls) {
        return (this.local.get(id) ?? []).filter(e => (calls ? e.call : e.raw));
    }

    routedRemote(id) {
        if (this.remote.has(id)) return true;
        for (let i = id.indexOf('.'); i !== -1 && i + 1 < id.length; i = id.indexOf('.', i + 1)) {
            if (this.remote.has(id.slice(0, i + 1) + '*')) return true;
        }
        return false;
    }

    async addEntry(id, entry) {
        entry.token = this.nextToken++;
        const list = this.local.get(id) ?? [];
        this.local.set(id, list);
        list.push(entry);
        this.tokenIds.set(entry.token, id);
        if (list.length === 1) this.send({ id: ids.EventAdded, type: PackageType.ServerAdminEvent, data: stringPayload(id) });
        // The EventAdded notice travels ahead of this call on the link, and every node answers AwaitReady only after handling
        // what came before it: once it completes, the whole network routes `id` here.
        if (list.length === 1 && !entry.internal && this.up && !this.stopping) await this.startCall(ids.AwaitReady, new Uint8Array([0]), 5000);
        return entry.token;
    }

    addCall(id, call, internal = false) { return this.addEntry(id, { call, internal }); }

    /**
     * Withdraws a hook, function or raw hook by the token it returned. The router stops routing the ID here when nothing
     * else here handles it; the agreed type stays. False if the token isn't known.
     */
    remove(token) {
        const id = this.tokenIds.get(token);
        if (id === undefined) return false;
        this.tokenIds.delete(token);
        const list = (this.local.get(id) ?? []).filter(e => e.token !== token);
        if (list.length > 0) {
            this.local.set(id, list);
        } else {
            this.local.delete(id);
            this.send({ id: ids.EventRemoved, type: PackageType.ServerAdminEvent, data: stringPayload(id) });
        }
        return true;
    }

    // ------------------------------------------------------------------ the gate (network merges freeze fires, calls and agreements)

    async enter() {
        while (this.gateClosed && !this.stopping) await new Promise(resolve => this.gateWaiters.push(resolve));
        if (this.stopping) throw new EvnError('the leaf has stopped');
        this.inside++;
    }

    leave() {
        this.inside--;
        if (this.inside === 0) for (const w of this.insideWaiters.splice(0)) w();
    }

    async gated(fn) {
        await this.enter();
        try { return await fn(); } finally { this.leave(); }
    }

    // ------------------------------------------------------------------ calls

    /**
     * Calls `id` everywhere it is handled: the router (if it routes it onward) and this leaf's own handlers. Resolves with
     * every answer (bytes) once all answered, the timeout passed or the link dropped.
     */
    startCall(id, input, timeout = this.callTimeout) {
        return new Promise(resolve => {
            if (this.stopping) return resolve([]);
            const st = { id: this.nextCall++, remote: false, locals: 0, answers: [], finished: false, resolve, timer: null };
            this.active.set(st.id, st);
            const mine = this.entries(id, true);
            st.remote = this.up && this.routedRemote(id);
            st.locals = mine.length;
            st.timer = setTimeout(() => this.complete(st, true), timeout);
            if (st.remote) {
                const call = functionPayload({ parameters: { id, type: PackageType.FunctionCall, data: input }, returns: [], callId: st.id, clientId: 0 });
                if (!this.send({ id, type: PackageType.FunctionCall, data: call })) st.remote = false;
            }
            for (const e of mine) {
                this.invoke(e, input, a => {
                    if (a && a.length > 0 && !st.finished) st.answers.push(a);
                    st.locals--;
                    this.complete(st, false);
                });
            }
            this.complete(st, false);
        });
    }

    // Runs `job` (a function returning a promise) after the jobs queued before it for the same ID have finished; other IDs run side by side.
    inLane(id, job) {
        const next = (this.lanes.get(id) ?? Promise.resolve()).then(job);
        this.lanes.set(id, next);
        next.then(() => { if (this.lanes.get(id) === next) this.lanes.delete(id); });
    }

    // Hands `input` to the router (if it routes `id` onward) and to this leaf's own handlers; waits for nobody.
    publishBytes(id, input) {
        if (this.stopping) return;
        if (this.up && this.routedRemote(id)) {
            const handle = UNANSWERED_FLAG | BigInt(this.nextCall++);
            const call = functionPayload({ parameters: { id, type: PackageType.FunctionCall, data: input }, returns: [], callId: handle, clientId: 0 });
            if (!this.send({ id, type: PackageType.FunctionCall, data: call })) this.log(`could not publish ${id}: the link is down`);
        }
        for (const e of this.entries(id, true)) this.inLane(id, () => new Promise(done => this.invoke(e, input, () => done())));
    }

    // Finishes a call if everyone answered (or regardless, with `force`).
    complete(st, force) {
        if (st.finished) return;
        if (!force && (st.remote || st.locals > 0)) return;
        st.finished = true;
        this.active.delete(st.id);
        clearTimeout(st.timer);
        st.resolve(st.answers);
    }

    protocolCall(id, payload) { return this.startCall(id, payload, this.callTimeout); }

    // ------------------------------------------------------------------ NOTES

    // The agreed type of `id`: known here, or asked from the network and adopted. { check: 'ok' | 'nobody' | 'mismatch', descriptor }.
    // Nobody interested: 'nobody'; routed by name without an agreed type: 'mismatch'.
    async findAgreed(id) {
        const known = this.store.find(id);
        if (known) return { check: 'ok', descriptor: known };
        const here = this.local.has(id);
        const routed = here || (this.up && this.routedRemote(id));
        const byName = here || this.remote.has(id);
        if (!routed) return { check: 'nobody' };
        for (const answer of await this.protocolCall(ids.QueryDescriptors, stringPayload(id))) {
            try {
                const d = readDescriptor(new Reader(answer));
                if (d.id === id) return { check: 'ok', descriptor: this.store.adopt(d) };
            } catch (e) {
                if (!(e instanceof WireError)) throw e;
            }
        }
        return { check: byName ? 'mismatch' : 'nobody' };
    }

    // Agrees (input, output) as the type of `id` with the network: the known type must match; otherwise propose it
    // (TryInitiate everywhere, Finalize when all accepted), adopt an equal type agreed meanwhile, retry while an equal proposal
    // is pending, give up on a conflict. Returns whether the type is agreed.
    agree(id, input, output, description) {
        return this.gated(async () => {
            while (!this.stopping) {
                const known = this.store.find(id);
                if (known) {
                    if (sameList(known.expected, input) && sameList(known.returns, output)) return true;
                    this.problem(`${id}: the agreed type differs`);
                    return false;
                }
                const proposal = {
                    expected: input, id, returns: output, weight: Math.floor(Math.random() * 0x7FFFFFFF), description: description ?? '',
                    inputAnnotations: [], returnAnnotations: [],
                };
                const payload = descriptorPayload(proposal);
                const replies = async (callId, data) => (await this.protocolCall(callId, data)).map(a => { try { return new Reader(a).i32(); } catch { return null; } }).filter(v => v !== null);
                const initiated = await replies(ids.TryInitiate, payload);
                if (initiated.every(v => v === Accepted)) {
                    for (const v of await replies(ids.Finalize, payload)) {
                        if (v < 0) {
                            await this.protocolCall(ids.Abort, payload);
                            this.problem(`${id}: a different type was agreed while finalizing`);
                            return false;
                        }
                    }
                    const d = this.store.find(id);
                    return d !== null && sameList(d.expected, input) && sameList(d.returns, output);
                }
                await this.protocolCall(ids.Abort, payload); // withdraw it wherever it was accepted
                if (initiated.includes(Conflict)) {
                    this.problem(`${id}: the network has a different type`);
                    return false;
                }
                if (initiated.includes(Established)) {
                    let adopted = false;
                    for (const answer of await this.protocolCall(ids.QueryDescriptors, stringPayload(id))) {
                        try {
                            const d = readDescriptor(new Reader(answer));
                            if (sameType(d, proposal)) { this.store.adopt(d); adopted = true; break; }
                        } catch (e) {
                            if (!(e instanceof WireError)) throw e;
                        }
                    }
                    if (adopted) continue;
                }
                await sleep(100); // an equal proposal is pending: try again
            }
            return false;
        });
    }

    schemaFor(signature) {
        const key = signature.join(' ');
        let schema = this.schemas.get(key);
        if (schema === undefined) {
            try { schema = Schema.parse(signature); } catch (e) { this.problem(`can't read the type ${key}: ${e.message}`); schema = null; }
            this.schemas.set(key, schema);
        }
        return schema;
    }

    // The schemas (input, answer) for a hook or function: proposed from the given signatures if the network has no type
    // yet, else the agreed ones (which must be an event for a hook, a function for a provider).
    async schemasFor(id, event, proposeIn, proposeOut, description) {
        if (proposeIn) {
            let input, output;
            try {
                input = Schema.parse(proposeIn);
                output = event ? Schema.parse(['=Void']) : Schema.parse(proposeOut ?? ['=Void']);
            } catch (e) {
                throw new EvnError(`${id}: not a NOTES signature: ${e.message}`);
            }
            if (!(await this.agree(id, input.canonical(), output.canonical(), description))) throw new EvnError(`${id}: ${this.lastProblem || 'the type could not be agreed'}`);
        }
        const agreed = await this.gated(() => this.findAgreed(id));
        if (agreed.check !== 'ok' || isEvent(agreed.descriptor) !== event) throw new EvnError(`${id}: no agreed ${event ? 'event' : 'function'} type to use`);
        const input = this.schemaFor(agreed.descriptor.expected);
        const output = this.schemaFor(agreed.descriptor.returns);
        if (!input || !output) throw new EvnError(`${id}: the agreed type can't be used`);
        return { input, output };
    }

    // The NOTES protocol's functions, answered by this leaf like by any .NET node.
    registerNotes() {
        const withDescriptor = body => async input => {
            try { return body(readDescriptor(new Reader(input))); } catch (e) { if (e instanceof WireError) return null; throw e; }
        };
        const add = (id, handler) => { this.addCall(id, handler, true); };
        add(ids.TryInitiate, withDescriptor(d => int32Bytes(this.store.tryInitiate(d, Date.now()))));
        add(ids.Finalize, withDescriptor(d => int32Bytes(this.store.finalize(d))));
        add(ids.Abort, withDescriptor(d => { this.store.abort(d); return new Uint8Array([0]); }));
        add(ids.QueryDescriptors, async input => {
            try {
                const d = this.store.find(new Reader(input).str());
                return d ? descriptorPayload(d) : null; // unknown: no answer at all
            } catch (e) { if (e instanceof WireError) return null; throw e; }
        });
        add(ids.QueryDescriptorSet, async input => {
            try {
                const found = this.store.findSet(readStrings(new Reader(input)));
                return found.length ? descriptorsPayload(found) : null;
            } catch (e) { if (e instanceof WireError) return null; throw e; }
        });
        add(ids.ShareDescriptors, async input => {
            try {
                const conflicts = this.store.share(readCollection(new Reader(input), readDescriptor));
                return conflicts.length ? stringsPayload(conflicts) : null;
            } catch (e) { if (e instanceof WireError) return null; throw e; }
        });
        add(ids.StopEvents, async () => {
            this.gateClosed = true;
            while (this.inside > 0 && !this.stopping) await new Promise(resolve => this.insideWaiters.push(resolve)); // answer once the operations inside left
            return new Uint8Array([0]);
        });
        add(ids.AllowEvents, async () => {
            this.gateClosed = false;
            for (const w of this.gateWaiters.splice(0)) w();
            return new Uint8Array([0]);
        });
        add(ids.AwaitReady, async () => {
            while (this.gateClosed && !this.stopping) await new Promise(resolve => this.gateWaiters.push(resolve));
            return new Uint8Array([0]);
        });
    }

    // ------------------------------------------------------------------ the API

    /**
     * Subscribes to event `id`: `callback(value)` for every fire in the network (and this leaf's own). `options.type` is the
     * event's type (see `type()`), proposed if the network has none yet; without it, the network must already have one.
     * Resolves to a token for `remove`; rejects (EvnError) on a type conflict or when there is no type to use.
     */
    async hook(id, callback, options = {}) {
        const { input } = await this.schemasFor(id, true, options.type, null, options.description);
        return this.addCall(id, async bytes => {
            const value = tryDecode(bytes, input);
            if (value !== undefined) await callback(value);
            return null;
        });
    }

    /**
     * Provides function `id`: `handler(value)` returns the answer (or a promise of it), or undefined for no answer from
     * this provider. `options.input` and `options.output` are the types (see `type()`), proposed if the network has none yet.
     */
    async provide(id, handler, options = {}) {
        const { input, output } = await this.schemasFor(id, false, options.input, options.output, options.description);
        return this.addCall(id, async bytes => {
            const value = tryDecode(bytes, input);
            if (value === undefined) return null;
            const result = await handler(value);
            if (result === undefined) return null;
            try { return encode(result, output); } catch (e) { this.problem(`the answer of ${id} doesn't fit: ${e.message}`); return null; }
        });
    }

    /** Delivers `value` to every subscriber of event `id`, here and in the network, and waits for them. False only on a type mismatch. */
    fire(id, value, options = {}) {
        return this.gated(async () => {
            const agreed = await this.findAgreed(id);
            if (agreed.check === 'nobody') return true;
            if (agreed.check === 'mismatch' || !isEvent(agreed.descriptor)) { this.problem(`fire ${id}: not an event with an agreed type`); return false; }
            const schema = this.schemaFor(agreed.descriptor.expected);
            let bytes;
            try { bytes = encode(value, schema); } catch (e) { this.problem(`fire ${id}: the value doesn't fit the agreed type: ${e.message}`); return false; }
            await this.startCall(id, bytes, options.timeout ?? this.callTimeout);
            return true;
        });
    }

    /**
     * Fire and forget: hands `value` to every subscriber of event `id`, here and in the network, and resolves without waiting for any
     * of them. Nothing is kept for it and no acknowledgement travels, so streams of readings go several times faster than with `fire`.
     * Events from this leaf reach a subscriber in the order published (a subscriber's handlers get one ID's events one at a time).
     * Resolving says nothing about the event having been handled: a router or subscriber that is gone, a link that breaks or a handler
     * that throws loses it silently. True also when nobody subscribed; false on a type mismatch.
     */
    publish(id, value) {
        return this.gated(async () => {
            const agreed = await this.findAgreed(id);
            if (agreed.check === 'nobody') return true;
            if (agreed.check === 'mismatch' || !isEvent(agreed.descriptor)) { this.problem(`publish ${id}: not an event with an agreed type`); return false; }
            const schema = this.schemaFor(agreed.descriptor.expected);
            let bytes;
            try { bytes = encode(value, schema); } catch (e) { this.problem(`publish ${id}: the value doesn't fit the agreed type: ${e.message}`); return false; }
            this.publishBytes(id, bytes);
            return true;
        });
    }

    /**
     * Calls function `id` on every provider, here and in the network, and waits: resolves to an array with one answer per
     * provider (empty if there are none), or null on a type mismatch. Answers that can't be read are left out.
     */
    call(id, input, options = {}) {
        return this.gated(async () => {
            const agreed = await this.findAgreed(id);
            if (agreed.check === 'nobody') return [];
            if (agreed.check === 'mismatch' || isEvent(agreed.descriptor)) { this.problem(`call ${id}: not a function with an agreed type`); return null; }
            const inSchema = this.schemaFor(agreed.descriptor.expected);
            const outSchema = this.schemaFor(agreed.descriptor.returns);
            let bytes;
            try { bytes = encode(input, inSchema); } catch (e) { this.problem(`call ${id}: the value doesn't fit the agreed type: ${e.message}`); return null; }
            const answers = await this.startCall(id, bytes, options.timeout ?? this.callTimeout);
            return answers.map(a => tryDecode(a, outSchema)).filter(v => v !== undefined);
        });
    }

    /** Subscribes to raw (untyped) event `id`: `callback(bytes)`. No type checks; don't mix raw and typed use of one ID. */
    hookRaw(id, callback) {
        return this.addEntry(id, { raw: callback });
    }

    /** Sends raw event `id` (bytes, a Uint8Array) to its subscribers, here and in the network, without waiting. */
    fireRaw(id, bytes) {
        if (this.up && this.routedRemote(id)) this.send({ id, type: PackageType.Data, data: bytes });
        for (const e of this.entries(id, false)) queueMicrotask(() => this.safely(() => e.raw(bytes)));
    }

    /**
     * The agreed type of `id` (known here, or asked from the network): { id, expected, returns, description, isEvent,
     * inputAnnotations, returnAnnotations } (the annotations are the fields' documentation), or null if nobody agreed one.
     */
    async typeOf(id) {
        const agreed = await this.gated(() => this.findAgreed(id));
        return agreed.check === 'ok' ? describeType(agreed.descriptor) : null;
    }

    /** The agreed types this leaf knows. */
    knownTypes() {
        return this.store.all().map(describeType);
    }

    /**
     * Every endpoint of the network, sorted by ID, like C#'s NetworkDirectory: the IDs routed by name (typed or not), and each
     * namespace (a prefix route X.*) expanded through its $Describe, nested ones included. Each is { id, kind, type }, kind
     * 'event', 'function' or 'untyped' (raw events only, `type` null). The protocol's own IDs and IDs with a '$' part are left
     * out. The network may still be settling when a leaf has just joined: a list is what is routed at that moment.
     */
    async list(options = {}) {
        const routed = new Set([...this.remote, ...this.local.keys()]);
        const byName = [], prefixes = [];
        for (const id of routed) {
            if (id.length > 2 && id.endsWith('.*')) {
                const prefix = id.slice(0, -2);
                if (isApplicationId(prefix)) prefixes.push(prefix);
            } else if (isApplicationId(id)) {
                byName.push(id);
            }
        }
        const unknown = byName.filter(id => !this.store.find(id));
        if (unknown.length > 0) {
            for (const answer of await this.protocolCall(ids.QueryDescriptorSet, stringsPayload(unknown))) {
                try {
                    for (const d of readCollection(new Reader(answer), readDescriptor)) this.store.adopt(d);
                } catch (e) {
                    if (!(e instanceof WireError)) throw e;
                }
            }
        }
        const result = new Map();
        for (const id of byName) {
            const d = this.store.find(id);
            result.set(id, { id, kind: d ? (isEvent(d) ? 'event' : 'function') : 'untyped', type: d ? describeType(d) : null });
        }
        for (const prefix of prefixes) {
            if (await this.describeTree(prefix, 16, options, result)) continue;
            // A prefix route that isn't a namespace (an import of plain IDs): the agreed types known under it.
            for (const d of this.store.all()) {
                if (d.id.startsWith(prefix + '.') && isApplicationId(d.id) && !result.has(d.id)) {
                    result.set(d.id, { id: d.id, kind: isEvent(d) ? 'event' : 'function', type: describeType(d) });
                }
            }
        }
        return [...result.values()].sort((a, b) => cmp(a.id, b.id));
    }

    // Describes namespace `ns` (a prefix route) and those nested in it into `result`; false if it doesn't answer.
    async describeTree(ns, depth, options, result) {
        const answers = await this.call(`${ns}.$Describe`, '', options);
        if (!answers || answers.length === 0) return false;
        const d = answers[0];
        // A nested namespace names things as its own outer network does: respell them as this network reaches them.
        const local = d.Namespace;
        const respell = id => {
            if (local === ns || ns.length <= local.length || !ns.endsWith('.' + local)) return id;
            return id.startsWith(local + '.') ? ns + id.slice(local.length) : id;
        };
        const annotation = a => ({ record: a.Record, field: a.Field, description: a.Description, min: a.Min, max: a.Max, defaultJson: a.Default });
        for (const e of d.Endpoints ?? []) {
            const id = respell(e.Id);
            if (result.has(id)) continue;
            const event = e.Kind === 'event';
            result.set(id, {
                id, kind: e.Kind,
                type: {
                    id, expected: e.Input, returns: event ? ['=Void'] : e.Returns, description: e.Description, isEvent: event,
                    inputAnnotations: (e.InputAnnotations ?? []).map(annotation), returnAnnotations: (e.ReturnAnnotations ?? []).map(annotation),
                },
            });
        }
        if (depth > 1) for (const nested of d.Namespaces ?? []) await this.describeTree(respell(nested), depth - 1, options, result);
        return true;
    }
}

// ---------------------------------------------------------------------------------------------------- .non files

// NON, the wire's byte layout, at rest (the same format as C#'s NonFile):
//   'N' 'O' 'N' | version (1) | mode (0 known, 1 verbose)
//   known:   uint64 fingerprint of the type's signature
//   verbose: int32 length | the NOTES descriptor of the type (plain layout; id is the type's name, expected its signature)
//   then records, each: int32 length | the value in the NON layout

const NON_MAGIC = [0x4E, 0x4F, 0x4E];
const NON_VERSION = 1;

/** The fingerprint of a canonical signature (FNV-1a, 64 bit, over its entries joined by newlines), as a BigInt. */
export function fingerprint(signature) {
    let hash = 14695981039346656037n;
    for (const b of utf8.encode(signature.join('\n'))) hash = BigInt.asUintN(64, (hash ^ BigInt(b)) * 1099511628211n);
    return hash;
}

const asSchema = type => (type instanceof Schema ? type : Schema.parse(type));

/** One record: a value of `type` with its length in front. This is what is appended to an existing .non file. */
export function nonRecord(value, type) {
    const bytes = encode(value, asSchema(type));
    const w = new Writer();
    w.i32(bytes.length); w.bytes(bytes);
    return w.finish();
}

/**
 * A .non file of `records` (an array of values of `type`). `verbose: true` makes it describe itself (anyone can read it without
 * the type, `name` names the type); the default, known, is for a reader that has the type: a fingerprint of it, then the data.
 */
export function writeNonFile(records, type, { verbose = false, name = '' } = {}) {
    const schema = asSchema(type);
    const signature = schema.canonical();
    const w = new Writer();
    w.bytes(Uint8Array.from(NON_MAGIC)); w.u8(NON_VERSION); w.u8(verbose ? 1 : 0);
    if (verbose) {
        const d = new Writer();
        writeDescriptor(d, { expected: signature, id: name || typeRef(schema.root), returns: ['=Void'], weight: 0, description: '', inputAnnotations: [], returnAnnotations: [] });
        w.i32(d.len); w.bytes(d.finish());
    } else {
        w.u64(fingerprint(signature));
    }
    for (const record of records) w.bytes(nonRecord(record, schema));
    return w.finish();
}

/**
 * Reads a .non file: { mode: 'known' | 'verbose', name, signature, records: [values] }. A verbose file needs no type (its own
 * descriptor is used; give `type` to also require that it is that type); a known one needs it. Throws EvnError if the file is
 * malformed, cut off, or of another type (or another version of the type).
 */
export function readNonFile(bytes, type = null) {
    const r = new Reader(bytes);
    try {
        if (bytes.length < 5 || NON_MAGIC.some((m, i) => bytes[i] !== m)) throw new EvnError('not a .non file (no NON header)');
        r.take(3);
        const version = r.u8();
        if (version !== NON_VERSION) throw new EvnError(`a .non file of version ${version}; this reader knows version ${NON_VERSION}`);
        const mode = r.u8();
        const wanted = type ? asSchema(type) : null;
        let schema, name = '', signature;
        if (mode === 0) {
            if (!wanted) throw new EvnError('a known .non file needs the type to be read');
            const found = r.u64();
            signature = wanted.canonical();
            if (found !== fingerprint(signature)) throw new EvnError('the file holds another type (or another version of it): its fingerprint differs');
            schema = wanted;
        } else if (mode === 1) {
            const length = r.i32();
            const d = readDescriptor(new Reader(r.take(length)));
            name = d.id;
            signature = d.expected;
            if (wanted && !sameList(signature, wanted.canonical())) throw new EvnError(`the file holds another type than ${typeRef(wanted.root)} (${name})`);
            schema = wanted ?? Schema.parse(signature);
        } else {
            throw new EvnError(`unknown mode ${mode}`);
        }
        const records = [];
        while (r.remaining > 0) {
            if (r.remaining < 4) throw new EvnError(`the file ends inside the length of record ${records.length + 1}`);
            const length = r.i32();
            if (length < 1 || length > r.remaining) throw new EvnError(`record ${records.length + 1} is ${length < 1 ? 'empty' : 'cut off'} (a torn write?)`);
            const value = tryDecode(r.take(length), schema);
            if (value === undefined) throw new EvnError(`record ${records.length + 1} isn't a ${typeRef(schema.root)}`);
            records.push(value);
        }
        return { mode: mode === 0 ? 'known' : 'verbose', name, signature, records };
    } catch (e) {
        if (e instanceof WireError || e instanceof RangeError) throw new EvnError(`the .non file is malformed: ${e.message}`);
        throw e;
    }
}

export { Writer, Reader, FrameParser, TypeStore };
