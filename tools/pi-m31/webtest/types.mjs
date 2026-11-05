import { Leaf } from '../../../EVent/js/evn.js';
const leaf = new Leaf('types-' + Math.random().toString(36).slice(2, 6));
await leaf.connect(`ws://${process.argv[2]}:8091/`);
const all = await leaf.list();
const want = new RegExp(process.argv[3] ?? '.');
for (const e of all) if (want.test(e.id)) console.log(e.kind.padEnd(8), e.id, JSON.stringify(e.type?.signature ?? e.type?.signatures ?? e.type).slice(0, 900));
process.exit(0);
