// EVent for C++: the wire. Frames, the link protocol's messages and the NOTES descriptor, in EVent's plain layout.
//
// Part of the portable core: C++17, no exceptions thrown, no I/O, no threads.
#pragma once

#include <cstdint>
#include <string>
#include <vector>

#include "non.hpp"

namespace evn {
namespace wire {

enum class PackageType : uint8_t { Invalid = 0, Data = 1, BroadcastHandshake = 2, ServerAdminEvent = 3, FunctionCall = 4, FunctionReturn = 5 };

/// The largest frame a node accepts (length field included).
inline constexpr uint32_t max_frame_size = 202u * 1024u * 1024u;

// The link protocol's own IDs.
namespace ids {
inline constexpr const char* InterconnectRunning = "InterconnectRunning";
inline constexpr const char* ListEvents = "ListEvents";
inline constexpr const char* QueryEvents = "QueryEvents";
inline constexpr const char* EventAdded = "EventAdded";
inline constexpr const char* EventRemoved = "EventRemoved";
inline constexpr const char* LinkFault = "LinkFault";
inline constexpr const char* TryInitiate = "TryInitiateNOTESDescriptor";
inline constexpr const char* Finalize = "FinalizeNOTESDescriptor";
inline constexpr const char* Abort = "AbortNOTESDescriptor";
inline constexpr const char* QueryDescriptors = "QueryDescriptors";
inline constexpr const char* QueryDescriptorSet = "NOTESQueryDescriptorSet";
inline constexpr const char* ShareDescriptors = "NOTESShareDescriptors";
inline constexpr const char* StopEvents = "StopEvents";
inline constexpr const char* AllowEvents = "AllowEvents";
inline constexpr const char* AwaitReady = "AwaitReady";
inline constexpr const char* AuthChallenge = "AuthChallenge";
inline constexpr const char* AuthResponse = "AuthResponse";
inline constexpr const char* AuthResult = "AuthResult";
inline constexpr const char* Ping = "Ping";
inline constexpr const char* Pong = "Pong";
}  // namespace ids

/// The link protocol's version: sent in InterconnectRunning with the oldest version accepted from a peer (the same
/// numbers as the .NET node's EVentNode.ProtocolVersion / MinimumProtocolVersion).
inline constexpr int32_t protocol_version = 2;
inline constexpr int32_t minimum_protocol_version = 1;

/// What one end of a link says about itself in InterconnectRunning: int32 version, int32 minimum, string
/// implementation. An empty payload is a peer from before versions (version 0).
struct Handshake {
    int32_t version = 0;
    int32_t minimum = 0;
    std::string implementation;
    std::string node_name;  // optional, added later: the peer's name (empty from a peer that doesn't say)

    bool accepts(const Handshake& peer) const { return peer.version >= minimum && version >= peer.minimum; }
    std::string describe() const {
        return (implementation.empty() ? std::string("unknown") : implementation) + " (protocol " + std::to_string(version) + ", accepts " +
               std::to_string(minimum) + "+)";
    }
};

inline Handshake own_handshake() { return {protocol_version, minimum_protocol_version, "evn-cpp"}; }

inline std::vector<uint8_t> handshake_payload(const Handshake& h) {
    Writer w;
    w.i32(h.version);
    w.i32(h.minimum);
    w.str(h.implementation);
    w.str(h.node_name);
    return w.out;
}

inline bool read_handshake(const std::vector<uint8_t>& data, Handshake& h) {
    if (data.empty()) {
        h = Handshake{};
        return true;
    }
    Reader r(data);
    if (!(r.i32(h.version) && r.i32(h.minimum) && r.str(h.implementation))) return false;
    std::string name;
    if (r.str(name)) h.node_name = std::move(name);  // absent from older peers: ignored
    return true;
}

/// One frame: int32 length (of what follows) | int32 idLen | id (UTF-8) | type | data.
struct Package {
    std::string id;
    PackageType type = PackageType::Invalid;
    std::vector<uint8_t> data;
};

inline bool write_package(Writer& w, const Package& p) {
    uint64_t length = 4 + p.id.size() + 1 + p.data.size();
    if (length + 4 > max_frame_size) return false;
    w.i32(static_cast<int32_t>(length));
    w.str(p.id);
    w.u8(static_cast<uint8_t>(p.type));
    w.bytes(p.data.data(), p.data.size());
    return true;
}

inline bool read_package(Reader& r, Package& p) {
    int32_t length, idLength;
    const uint8_t* body;
    if (!r.i32(length) || length < 5 || static_cast<size_t>(length) > r.n || !r.take(static_cast<size_t>(length), body)) return false;
    Reader b(body, static_cast<size_t>(length));
    const uint8_t* id;
    uint8_t type;
    if (!b.i32(idLength) || idLength < 0 || idLength > length - 5 || !b.take(static_cast<size_t>(idLength), id) || !b.u8(type) || type > 5)
        return false;
    p.id.assign(reinterpret_cast<const char*>(id), static_cast<size_t>(idLength));
    p.type = static_cast<PackageType>(type);
    p.data.assign(b.p, b.p + b.n);
    return true;
}

/// Splits a byte stream into packages.
class FrameParser {
public:
    enum class Result { NeedMore, Frame, Malformed };

    void feed(const uint8_t* data, size_t size) { buffer_.insert(buffer_.end(), data, data + size); }

    /// The next whole package, if one has arrived. Malformed: the stream can't be trusted any more (`why` says why).
    Result next(Package& out, std::string& why) {
        size_t available = buffer_.size() - start_;
        if (available < 4) return Result::NeedMore;
        Reader peek(buffer_.data() + start_, available);
        int32_t length;
        peek.i32(length);
        if (length < 5 || static_cast<uint32_t>(length) > max_frame_size - 4) {
            why = "frame length " + std::to_string(length) + " is out of range";
            return Result::Malformed;
        }
        if (available < 4 + static_cast<size_t>(length)) return Result::NeedMore;
        Reader frame(buffer_.data() + start_, 4 + static_cast<size_t>(length));
        if (!read_package(frame, out)) {
            why = "frame contents are not a valid package";
            return Result::Malformed;
        }
        start_ += 4 + static_cast<size_t>(length);
        if (start_ == buffer_.size()) {
            buffer_.clear();
            start_ = 0;
        } else if (start_ > 65536 && start_ * 2 > buffer_.size()) {
            buffer_.erase(buffer_.begin(), buffer_.begin() + static_cast<std::ptrdiff_t>(start_));
            start_ = 0;
        }
        return Result::Frame;
    }

    void reset() {
        buffer_.clear();
        start_ = 0;
    }

private:
    std::vector<uint8_t> buffer_;
    size_t start_ = 0;
};

// ------------------------------------------------------------------ plain layout helpers

/// A plain collection: int32 count, then per item an int32 byte length and the item (length 0: a null item).
template <class T, class WriteItem>
void write_collection(Writer& w, const std::vector<T>& items, WriteItem write_item) {
    w.i32(static_cast<int32_t>(items.size()));
    for (const auto& item : items) {
        Writer one;
        write_item(one, item);
        w.i32(static_cast<int32_t>(one.out.size()));
        w.bytes(one.out.data(), one.out.size());
    }
}

/// Reads a plain collection; null items are skipped when `keep_nulls` is false, else read as T{}.
template <class T, class ReadItem>
bool read_collection(Reader& r, std::vector<T>& items, ReadItem read_item, bool keep_nulls = false) {
    int32_t count;
    if (!r.i32(count) || count < 0 || static_cast<size_t>(count) > r.n / 4) return false;
    items.clear();
    for (int32_t i = 0; i < count; i++) {
        int32_t length;
        const uint8_t* at;
        if (!r.i32(length) || length < 0 || !r.take(static_cast<size_t>(length), at)) return false;
        if (length == 0) {
            if (keep_nulls) items.emplace_back();
            continue;
        }
        Reader item(at, static_cast<size_t>(length));
        T value{};
        if (!read_item(item, value)) return false;
        items.push_back(std::move(value));
    }
    return true;
}

inline void write_strings(Writer& w, const std::vector<std::string>& items) {
    write_collection(w, items, [](Writer& o, const std::string& s) { o.str(s); });
}

inline bool read_strings(Reader& r, std::vector<std::string>& items) {
    // A null entry reads as "" (as the .NET side compares signatures).
    return read_collection(r, items, [](Reader& i, std::string& s) { return i.str(s); }, true);
}

inline std::vector<uint8_t> string_payload(const std::string& s) {
    Writer w;
    w.str(s);
    return w.out;
}

inline std::vector<uint8_t> strings_payload(const std::vector<std::string>& items) {
    Writer w;
    write_strings(w, items);
    return w.out;
}

// ------------------------------------------------------------------ link authentication (see auth.hpp)

/// Reads an AuthChallenge: the server's nonce and its realm (its node ID).
inline bool read_challenge(const std::vector<uint8_t>& data, std::vector<uint8_t>& nonce, std::string& realm) {
    Reader r(data);
    int32_t length;
    const uint8_t* at;
    if (!r.i32(length) || length < 0 || !r.take(static_cast<size_t>(length), at)) return false;
    nonce.assign(at, at + length);
    return r.str(realm) && r.n == 0;
}

/// An AuthResponse: the user ID, the leaf's nonce and its proof.
inline std::vector<uint8_t> response_payload(const std::string& user_id, const std::vector<uint8_t>& client_nonce, const std::vector<uint8_t>& proof) {
    Writer w;
    w.str(user_id);
    w.i32(static_cast<int32_t>(client_nonce.size()));
    w.bytes(client_nonce.data(), client_nonce.size());
    w.i32(static_cast<int32_t>(proof.size()));
    w.bytes(proof.data(), proof.size());
    return w.out;
}

/// Reads an AuthResult: the server's proof.
inline bool read_result(const std::vector<uint8_t>& data, std::vector<uint8_t>& proof) {
    Reader r(data);
    int32_t length;
    const uint8_t* at;
    if (!r.i32(length) || length < 0 || !r.take(static_cast<size_t>(length), at)) return false;
    proof.assign(at, at + length);
    return r.n == 0;
}

// ------------------------------------------------------------------ function calls

/// Identifies a call on one hop.
struct Handle {
    /// Bit 63 of the call ID marks a call nobody answers (publish): the receiver delivers it and sends no FunctionReturn.
    /// Call IDs count up from zero and wrap far below it. A node that doesn't know the bit answers anyway; the caller drops
    /// the answer, since no call waits for it.
    static constexpr uint64_t unacknowledged_flag = 1ull << 63;

    uint64_t call_id = 0;
    int32_t client_id = 0;

    bool unacknowledged() const { return (call_id & unacknowledged_flag) != 0; }
};

/// A call or its answers: Parameters (a whole frame: same ID, FunctionCall, the input), ReturnValues (one package per
/// answer), Handle. Plain layout order: Parameters, ReturnValues, Handle.
struct Function {
    Package parameters;
    std::vector<Package> returns;
    Handle handle;
};

inline std::vector<uint8_t> function_payload(const Function& f) {
    Writer w;
    write_package(w, f.parameters);
    write_collection(w, f.returns, [](Writer& o, const Package& p) { write_package(o, p); });
    w.le(f.handle.call_id);
    w.i32(f.handle.client_id);
    return w.out;
}

/// The whole frame of a call (a FunctionCall package holding a function that holds the parameters package), written once
/// into an array of the exact size: the input is copied a single time, where building the pieces one inside the other
/// copies it three. Empty if the frame would be too large.
inline std::vector<uint8_t> call_frame(const std::string& id, const std::vector<uint8_t>& input, const Handle& handle) {
    const uint64_t parameters = 4 + 4 + id.size() + 1 + input.size();    // the parameters package, length field included
    const uint64_t function = parameters + 4 + 12;                       // + no answers + the handle
    const uint64_t length = 4 + id.size() + 1 + function;                // the call's package, after its length field
    if (length + 4 > max_frame_size) return {};
    Writer w;
    w.out.reserve(static_cast<size_t>(length + 4));
    w.i32(static_cast<int32_t>(length));
    w.str(id);
    w.u8(static_cast<uint8_t>(PackageType::FunctionCall));
    w.i32(static_cast<int32_t>(parameters - 4));
    w.str(id);
    w.u8(static_cast<uint8_t>(PackageType::FunctionCall));
    w.bytes(input.data(), input.size());
    w.i32(0);  // no answers yet
    w.le(handle.call_id);
    w.i32(handle.client_id);
    return w.out;
}

inline bool read_function(const std::vector<uint8_t>& data, Function& f) {
    Reader r(data);
    return read_package(r, f.parameters) &&
           read_collection(r, f.returns, [](Reader& i, Package& p) { return read_package(i, p); }) &&
           r.le(f.handle.call_id) && r.i32(f.handle.client_id);
}

// ------------------------------------------------------------------ NOTES descriptor

using evn::Annotation;

/// The agreed type of an ID: input signature (Expected), answer signature (Returns; ["=Void"] for an event), and the
/// first proposer's documentation. Two descriptors are the same type when ID, Expected and Returns are equal.
struct Descriptor {
    std::vector<std::string> expected;
    std::string id;
    std::vector<std::string> returns;
    int32_t weight = 0;  // RandomWeight: breaks ties between equal proposals (lower wins)
    std::string description;
    std::vector<Annotation> input_annotations, return_annotations;

    bool same_type(const Descriptor& o) const { return id == o.id && expected == o.expected && returns == o.returns; }
    bool same_proposal(const Descriptor& o) const { return same_type(o) && weight == o.weight; }
    bool is_event() const { return returns.size() == 1 && returns[0] == "=Void"; }
};

inline void write_annotation(Writer& w, const Annotation& a) {
    w.str(a.record);
    w.str(a.field);
    w.str(a.description);
    w.str(a.min);
    w.str(a.max);
    w.str(a.default_json);
}

inline bool read_annotation(Reader& r, Annotation& a) {
    return r.str(a.record) && r.str(a.field) && r.str(a.description) && r.str(a.min) && r.str(a.max) && r.str(a.default_json);
}

// Plain layout order: Expected, EventID, Returns, RandomWeight, Description, InputAnnotations, ReturnAnnotations.
inline void write_descriptor(Writer& w, const Descriptor& d) {
    write_strings(w, d.expected);
    w.str(d.id);
    write_strings(w, d.returns);
    w.i32(d.weight);
    w.str(d.description);
    write_collection(w, d.input_annotations, write_annotation);
    write_collection(w, d.return_annotations, write_annotation);
}

inline bool read_descriptor(Reader& r, Descriptor& d) {
    return read_strings(r, d.expected) && r.str(d.id) && read_strings(r, d.returns) && r.i32(d.weight) && r.str(d.description) &&
           read_collection(r, d.input_annotations, read_annotation) && read_collection(r, d.return_annotations, read_annotation);
}

inline std::vector<uint8_t> descriptor_payload(const Descriptor& d) {
    Writer w;
    write_descriptor(w, d);
    return w.out;
}

inline std::vector<uint8_t> descriptors_payload(const std::vector<Descriptor>& items) {
    Writer w;
    write_collection(w, items, write_descriptor);
    return w.out;
}

inline bool read_descriptors(Reader& r, std::vector<Descriptor>& items) { return read_collection(r, items, read_descriptor); }

}  // namespace wire
}  // namespace evn
