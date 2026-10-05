// EVent for C++: NON, the NOTES type system (types, canonical signatures, the NON byte layout).
//
// Part of the portable core: C++17, no exceptions thrown, no I/O, no threads. A C++ type becomes a NOTES type by
// mapping its fields once, next to its definition:
//
//     struct Reading { float Celsius; std::string Room; std::optional<int32_t> Samples; std::vector<double> History; };
//     EVN_RECORD(Reading, Celsius, Room, Samples, History)                 // NOTES name "Reading"
//     EVN_RECORD_NAMED(app::Reading, "Reading", Celsius, Room, ...)       // for a qualified or differently named type
//     EVN_DOCS(Reading, evn::doc("Celsius", "degrees Celsius").range(-40, 125),
//                       evn::doc("Samples", "readings averaged").default_json("1"))  // field documentation (optional)
//
// Built-in leaves: bool, int8_t/uint8_t, int16_t/uint16_t, int32_t/uint32_t, int64_t/uint64_t (and any other integer
// type of those sizes), float, double, char16_t (char16), std::string (string), evn::Decimal (decimal), evn::Void.
// Lists: std::vector<T> (T[]) and evn::Capped<T, N> (T[<=N]). Optional fields: std::optional<T>, or evn::Box<T> for a
// record that contains itself. An application's own leaf type: specialize evn::Codec (see "custom leaves" below).
#pragma once

#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <cstring>
#include <map>
#include <memory>
#include <optional>
#include <set>
#include <string>
#include <tuple>
#include <type_traits>
#include <utility>
#include <vector>

namespace evn {

// ---------------------------------------------------------------------------------------------------- bytes

/// Appends little-endian values to a byte vector.
struct Writer {
    std::vector<uint8_t> out;

    void bytes(const void* data, size_t n) {
        auto p = static_cast<const uint8_t*>(data);
        out.insert(out.end(), p, p + n);
    }
    void u8(uint8_t v) { out.push_back(v); }
    template <class U>
    void le(U v) {  // U: an unsigned integer type
        for (size_t i = 0; i < sizeof(U); i++) out.push_back(static_cast<uint8_t>(v >> (8 * i)));
    }
    void i32(int32_t v) { le(static_cast<uint32_t>(v)); }
    void str(const std::string& s) {
        i32(static_cast<int32_t>(s.size()));
        bytes(s.data(), s.size());
    }
};

/// Reads little-endian values from a byte range; every read fails (returns false) rather than running past the end.
struct Reader {
    const uint8_t* p = nullptr;
    size_t n = 0;

    Reader() = default;
    Reader(const uint8_t* data, size_t size) : p(data), n(size) {}
    explicit Reader(const std::vector<uint8_t>& v) : p(v.data()), n(v.size()) {}

    bool take(size_t k, const uint8_t*& at) {
        if (k > n) return false;
        at = p;
        p += k;
        n -= k;
        return true;
    }
    bool u8(uint8_t& v) {
        const uint8_t* at;
        if (!take(1, at)) return false;
        v = *at;
        return true;
    }
    template <class U>
    bool le(U& v) {
        const uint8_t* at;
        if (!take(sizeof(U), at)) return false;
        U x = 0;
        for (size_t i = 0; i < sizeof(U); i++) x |= static_cast<U>(static_cast<U>(at[i]) << (8 * i));
        v = x;
        return true;
    }
    bool i32(int32_t& v) {
        uint32_t u;
        if (!le(u)) return false;
        v = static_cast<int32_t>(u);
        return true;
    }
    bool str(std::string& s) {
        int32_t len;
        const uint8_t* at;
        if (!i32(len) || len < 0 || !take(static_cast<size_t>(len), at)) return false;
        s.assign(reinterpret_cast<const char*>(at), static_cast<size_t>(len));
        return true;
    }
};

// ---------------------------------------------------------------------------------------------------- value types

/// The only value of the Void type (a function without input, the answer of an event). One byte on the wire.
struct Void {
    bool operator==(const Void&) const { return true; }
};

/// A .NET decimal, exactly as decimal.GetBits gives it: a 96-bit magnitude (lo, mid, hi), a scale 0..28 (flags bits
/// 16-23) and a sign (flags bit 31). 1.50 is magnitude 150, scale 2.
struct Decimal {
    uint32_t lo = 0, mid = 0, hi = 0, flags = 0;

    bool negative() const { return (flags & 0x80000000u) != 0; }
    int scale() const { return static_cast<int>((flags >> 16) & 0xFF); }
    /// What .NET accepts: no bits outside sign and scale, scale at most 28.
    bool valid() const { return (flags & ~0x80FF0000u) == 0 && scale() <= 28; }

    static Decimal from_int64(int64_t v) {
        Decimal d;
        uint64_t m = v < 0 ? 0 - static_cast<uint64_t>(v) : static_cast<uint64_t>(v);
        d.lo = static_cast<uint32_t>(m);
        d.mid = static_cast<uint32_t>(m >> 32);
        d.flags = v < 0 ? 0x80000000u : 0;
        return d;
    }

    /// Decimal text: optional '-', digits, optional '.' and up to 28 digits. False if it isn't one or doesn't fit.
    static bool parse(const std::string& text, Decimal& out) {
        size_t i = 0;
        bool neg = false;
        if (i < text.size() && (text[i] == '-' || text[i] == '+')) neg = text[i++] == '-';
        uint32_t w[3] = {0, 0, 0};
        int scale = 0, digits = 0;
        bool point = false;
        for (; i < text.size(); i++) {
            char c = text[i];
            if (c == '.' && !point) {
                point = true;
                continue;
            }
            if (c < '0' || c > '9') return false;
            uint64_t carry = static_cast<uint64_t>(c - '0');
            for (auto& x : w) {
                uint64_t v = static_cast<uint64_t>(x) * 10 + carry;
                x = static_cast<uint32_t>(v);
                carry = v >> 32;
            }
            if (carry != 0) return false;
            digits++;
            if (point && ++scale > 28) return false;
        }
        if (digits == 0) return false;
        out.lo = w[0];
        out.mid = w[1];
        out.hi = w[2];
        out.flags = (static_cast<uint32_t>(scale) << 16) | (neg ? 0x80000000u : 0);
        return true;
    }

    /// Exact decimal text, with as many fraction digits as the scale (1.50 stays "1.50"), like .NET's ToString().
    std::string to_string() const {
        uint32_t w[3] = {lo, mid, hi};
        std::string digits;
        while (w[0] != 0 || w[1] != 0 || w[2] != 0) {
            uint64_t rem = 0;
            for (int k = 2; k >= 0; k--) {
                uint64_t cur = (rem << 32) | w[k];
                w[k] = static_cast<uint32_t>(cur / 10);
                rem = cur % 10;
            }
            digits.push_back(static_cast<char>('0' + rem));
        }
        int s = scale();
        while (static_cast<int>(digits.size()) <= s) digits.push_back('0');
        std::string text = negative() && (lo | mid | hi) != 0 ? "-" : "";
        for (size_t k = digits.size(); k-- > 0;) {
            text.push_back(digits[k]);
            if (static_cast<int>(k) == s && s > 0) text.push_back('.');
        }
        return text;
    }

    double to_double() const {
        double m = static_cast<double>(hi) * 18446744073709551616.0 + static_cast<double>(mid) * 4294967296.0 + lo;
        for (int s = scale(); s > 0; s--) m /= 10;
        return negative() ? -m : m;
    }

    bool operator==(const Decimal& o) const { return lo == o.lo && mid == o.mid && hi == o.hi && flags == o.flags; }
    bool operator!=(const Decimal& o) const { return !(*this == o); }
};

/// A list with a cap of N elements: T[<=N]. A value over the cap can't be sent, and isn't read.
template <class T, size_t N>
struct Capped : std::vector<T> {
    using std::vector<T>::vector;
    static constexpr size_t cap = N;
};

/// An optional field that holds its record on the heap, for a record that contains itself
/// (struct Node { int32_t Value; evn::Box<Node> Next; }). Empty = absent. Copies deeply.
template <class T>
class Box {
public:
    Box() = default;
    Box(const T& v) : p_(new T(v)) {}
    Box(T&& v) : p_(new T(std::move(v))) {}
    Box(const Box& o) : p_(o.p_ ? new T(*o.p_) : nullptr) {}
    Box(Box&&) noexcept = default;
    Box& operator=(const Box& o) {
        if (this != &o) p_.reset(o.p_ ? new T(*o.p_) : nullptr);
        return *this;
    }
    Box& operator=(Box&&) noexcept = default;

    bool has_value() const { return p_ != nullptr; }
    explicit operator bool() const { return has_value(); }
    T& operator*() { return *p_; }
    const T& operator*() const { return *p_; }
    T* operator->() { return p_.get(); }
    const T* operator->() const { return p_.get(); }
    T& emplace() {
        p_.reset(new T());
        return *p_;
    }
    void reset() { p_.reset(); }

private:
    std::unique_ptr<T> p_;
};

// ---------------------------------------------------------------------------------------------------- schema

/// The NOTES names that may not appear in a record, field or leaf name.
inline constexpr const char* reserved_characters = "{}[]:,=<>|?@";

inline bool valid_name(const std::string& name) {
    if (name.empty()) return false;
    for (unsigned char c : name) {
        if (c < 0x20 || c == 0x7F || std::strchr(reserved_characters, c) != nullptr) return false;
    }
    return true;
}

/// Documentation of one record field (not part of the type): record, field, description, min, max, default (JSON).
struct Annotation {
    std::string record, field, description, min, max, default_json;
};

namespace detail {
// A number as an invariant decimal (what .NET's ranges are): no exponent, no trailing zeros.
inline std::string decimal_text(double v) {
    if (v == static_cast<double>(static_cast<long long>(v)) && v > -9e18 && v < 9e18) return std::to_string(static_cast<long long>(v));
    char buffer[400];
    std::snprintf(buffer, sizeof buffer, "%.15f", v);
    std::string s = buffer;
    while (!s.empty() && s.back() == '0') s.pop_back();
    if (!s.empty() && s.back() == '.') s.pop_back();
    return s;
}
inline std::string json_string(const std::string& s) {
    std::string out = "\"";
    for (unsigned char c : s) {
        if (c == '"' || c == '\\') {
            out.push_back('\\');
            out.push_back(static_cast<char>(c));
        } else if (c < 0x20) {
            char esc[8];
            std::snprintf(esc, sizeof esc, "\\u%04X", c);
            out += esc;
        } else {
            out.push_back(static_cast<char>(c));
        }
    }
    return out + "\"";
}
}  // namespace detail

/// Documentation of one field of a mapped struct (see EVN_DOCS): what it means, the range it is meant to stay in (a
/// string's or list's length), and a default (JSON). Travels with the types this program proposes; never enforced.
struct FieldDoc {
    std::string field, description, min, max, default_text;

    FieldDoc& range(double lo, double hi) {
        min = detail::decimal_text(lo);
        max = detail::decimal_text(hi);
        return *this;
    }
    FieldDoc& at_least(double lo) {
        min = detail::decimal_text(lo);
        return *this;
    }
    FieldDoc& at_most(double hi) {
        max = detail::decimal_text(hi);
        return *this;
    }
    /// The default as JSON text: "1", "\"unnamed\"", "{\"Floor\":0}".
    FieldDoc& default_json(std::string json) {
        default_text = std::move(json);
        return *this;
    }
    FieldDoc& default_value(double v) { return default_json(detail::decimal_text(v)); }
    FieldDoc& default_value(bool v) { return default_json(v ? "true" : "false"); }
    FieldDoc& default_value(const std::string& v) { return default_json(detail::json_string(v)); }
    FieldDoc& default_value(const char* v) { return default_json(detail::json_string(v)); }
};

inline FieldDoc doc(std::string field, std::string description = "") {
    FieldDoc d;
    d.field = std::move(field);
    d.description = std::move(description);
    return d;
}

/// Collects the record definitions of one type while its signature is built.
struct Schema {
    struct Definition {
        const void* tag;
        std::string text;
    };
    std::map<std::string, Definition> records;       // by NOTES name
    std::map<const void*, int> in_progress;          // records being described: optional fields and lists above them
    std::set<std::string> leaves;
    std::vector<Annotation> annotations;             // documented fields, one set per record name
    std::string error;

    void fail(const std::string& why) {
        if (error.empty()) error = why;
    }
};

/// A type's canonical NOTES signature: "=Root" and one "Name{field:TypeRef,...}" per record, sorted. Empty with a
/// reason in `error` if the type can't be a NOTES type (a name clash, a reserved character, an infinite record).
struct Signature {
    std::vector<std::string> entries;
    std::vector<Annotation> annotations;  // field documentation (EVN_DOCS), sorted by record, then field
    std::string error;
    bool ok() const { return error.empty() && !entries.empty(); }
};

/// Maximum nesting of lists, optional fields and records a value may have when read (hostile input protection).
inline constexpr int max_nesting_depth = 64;

template <class T, class Enable = void>
struct Codec {};  // no specialization: not a NOTES type

namespace detail {
template <class T>
struct Tag {
    static constexpr char id = 0;
};
template <class T>
constexpr char Tag<T>::id;

template <class T>
struct is_optional_field : std::false_type {};
template <class T>
struct is_optional_field<std::optional<T>> : std::true_type {
    using inner = T;
    static bool present(const std::optional<T>& v) { return v.has_value(); }
    static const T& get(const std::optional<T>& v) { return *v; }
    static T& emplace(std::optional<T>& v) { return v.emplace(); }
    static void clear(std::optional<T>& v) { v.reset(); }
};
template <class T>
struct is_optional_field<Box<T>> : std::true_type {
    using inner = T;
    static bool present(const Box<T>& v) { return v.has_value(); }
    static const T& get(const Box<T>& v) { return *v; }
    static T& emplace(Box<T>& v) { return v.emplace(); }
    static void clear(Box<T>& v) { v.reset(); }
};

template <class T, size_t Size, bool Signed>
constexpr bool is_int_of = std::is_integral<T>::value && !std::is_same<T, bool>::value && !std::is_same<T, char>::value &&
                           !std::is_same<T, char16_t>::value && !std::is_same<T, char32_t>::value &&
                           !std::is_same<T, wchar_t>::value && sizeof(T) == Size && std::is_signed<T>::value == Signed;

template <class T, size_t Size, bool Signed>
struct IntLeaf {
    static bool write(Writer& w, const T& v, int) {
        using U = std::make_unsigned_t<T>;
        w.le(static_cast<U>(v));
        return true;
    }
    static bool read(Reader& r, T& v, int) {
        std::make_unsigned_t<T> u;
        if (!r.le(u)) return false;
        v = static_cast<T>(u);
        return true;
    }
};
}  // namespace detail

// ------------------------------------------------------------------ leaves

#define EVN_DETAIL_LEAF_REF(NAME)                            \
    static std::string ref(Schema& s, int) {                 \
        s.leaves.insert(NAME);                               \
        return NAME;                                         \
    }

template <class T>
struct Codec<T, std::enable_if_t<detail::is_int_of<T, 1, true>>> : detail::IntLeaf<T, 1, true> { EVN_DETAIL_LEAF_REF("int8") };
template <class T>
struct Codec<T, std::enable_if_t<detail::is_int_of<T, 1, false>>> : detail::IntLeaf<T, 1, false> { EVN_DETAIL_LEAF_REF("uint8") };
template <class T>
struct Codec<T, std::enable_if_t<detail::is_int_of<T, 2, true>>> : detail::IntLeaf<T, 2, true> { EVN_DETAIL_LEAF_REF("int16") };
template <class T>
struct Codec<T, std::enable_if_t<detail::is_int_of<T, 2, false>>> : detail::IntLeaf<T, 2, false> { EVN_DETAIL_LEAF_REF("uint16") };
template <class T>
struct Codec<T, std::enable_if_t<detail::is_int_of<T, 4, true>>> : detail::IntLeaf<T, 4, true> { EVN_DETAIL_LEAF_REF("int32") };
template <class T>
struct Codec<T, std::enable_if_t<detail::is_int_of<T, 4, false>>> : detail::IntLeaf<T, 4, false> { EVN_DETAIL_LEAF_REF("uint32") };
template <class T>
struct Codec<T, std::enable_if_t<detail::is_int_of<T, 8, true>>> : detail::IntLeaf<T, 8, true> { EVN_DETAIL_LEAF_REF("int64") };
template <class T>
struct Codec<T, std::enable_if_t<detail::is_int_of<T, 8, false>>> : detail::IntLeaf<T, 8, false> { EVN_DETAIL_LEAF_REF("UInt64") };

template <>
struct Codec<bool> {
    EVN_DETAIL_LEAF_REF("bool")
    static bool write(Writer& w, const bool& v, int) {
        w.u8(v ? 1 : 0);
        return true;
    }
    static bool read(Reader& r, bool& v, int) {
        uint8_t b;
        if (!r.u8(b) || b > 1) return false;  // strict: 0 or 1
        v = b == 1;
        return true;
    }
};

template <>
struct Codec<char16_t> {
    EVN_DETAIL_LEAF_REF("char16")
    static bool write(Writer& w, const char16_t& v, int) {
        w.le(static_cast<uint16_t>(v));
        return true;
    }
    static bool read(Reader& r, char16_t& v, int) {
        uint16_t u;
        if (!r.le(u)) return false;
        v = static_cast<char16_t>(u);
        return true;
    }
};

template <>
struct Codec<float> {
    EVN_DETAIL_LEAF_REF("float")
    static bool write(Writer& w, const float& v, int) {
        uint32_t u;
        std::memcpy(&u, &v, 4);
        w.le(u);
        return true;
    }
    static bool read(Reader& r, float& v, int) {
        uint32_t u;
        if (!r.le(u)) return false;
        std::memcpy(&v, &u, 4);
        return true;
    }
};

template <>
struct Codec<double> {
    EVN_DETAIL_LEAF_REF("double")
    static bool write(Writer& w, const double& v, int) {
        uint64_t u;
        std::memcpy(&u, &v, 8);
        w.le(u);
        return true;
    }
    static bool read(Reader& r, double& v, int) {
        uint64_t u;
        if (!r.le(u)) return false;
        std::memcpy(&v, &u, 8);
        return true;
    }
};

template <>
struct Codec<Decimal> {
    EVN_DETAIL_LEAF_REF("decimal")
    static bool write(Writer& w, const Decimal& v, int) {
        if (!v.valid()) return false;
        w.le(v.lo);
        w.le(v.mid);
        w.le(v.hi);
        w.le(v.flags);
        return true;
    }
    static bool read(Reader& r, Decimal& v, int) {
        return r.le(v.lo) && r.le(v.mid) && r.le(v.hi) && r.le(v.flags) && v.valid();
    }
};

template <>
struct Codec<std::string> {
    EVN_DETAIL_LEAF_REF("string")
    static bool write(Writer& w, const std::string& v, int) {
        if (v.size() > 0x7FFFFFFF) return false;
        w.str(v);
        return true;
    }
    static bool read(Reader& r, std::string& v, int) { return r.str(v); }
};

template <>
struct Codec<Void> {
    EVN_DETAIL_LEAF_REF("Void")
    static bool write(Writer& w, const Void&, int) {
        w.u8(0);
        return true;
    }
    static bool read(Reader& r, Void&, int) {
        uint8_t b;
        return r.u8(b);
    }
};

#undef EVN_DETAIL_LEAF_REF

// ------------------------------------------------------------------ custom leaves
//
// An application's own leaf type (like a C# class with no fields and its own FromBytes/ToBytes) is a Codec
// specialization with its NOTES name and its own bytes, which must delimit themselves:
//
//     struct Stamp { uint16_t Value = 0; };
//     template <> struct evn::Codec<Stamp> {
//         static std::string ref(evn::Schema& s, int) { return evn::leaf_ref(s, "stamp16"); }
//         static bool write(evn::Writer& w, const Stamp& v, int) { w.le(v.Value); return true; }
//         static bool read(evn::Reader& r, Stamp& v, int) { return r.le(v.Value); }
//     };

/// For a custom leaf's ref(): records its name and returns it.
inline std::string leaf_ref(Schema& s, const char* name) {
    if (!valid_name(name)) s.fail(std::string("'") + name + "' is not a valid NOTES name");
    s.leaves.insert(name);
    return name;
}

// ------------------------------------------------------------------ lists

namespace detail {
template <class T, class List>
struct ListCodec {
    static std::string ref(Schema& s, int depth, long cap) {
        std::string element = Codec<T>::ref(s, depth + 1);
        return cap < 0 ? element + "[]" : element + "[<=" + std::to_string(cap) + "]";
    }
    static bool write(Writer& w, const List& v, int depth, long cap) {
        if ((cap >= 0 && v.size() > static_cast<size_t>(cap)) || v.size() > 0x7FFFFFFF || depth >= max_nesting_depth) return false;
        w.i32(static_cast<int32_t>(v.size()));
        for (const auto& item : v) {
            size_t before = w.out.size();
            if (!Codec<T>::write(w, item, depth + 1)) return false;
            if (w.out.size() == before) return false;  // every element takes at least one byte
        }
        return true;
    }
    static bool read(Reader& r, List& v, int depth, long cap) {
        int32_t count;
        if (depth >= max_nesting_depth || !r.i32(count) || count < 0 || static_cast<size_t>(count) > r.n ||
            (cap >= 0 && count > cap))
            return false;  // every element takes at least one byte, so a count above what's left is a lie
        v.clear();
        v.reserve(static_cast<size_t>(count));
        for (int32_t i = 0; i < count; i++) {
            size_t before = r.n;
            T item{};
            if (!Codec<T>::read(r, item, depth + 1) || r.n == before) return false;
            v.push_back(std::move(item));
        }
        return true;
    }
};
}  // namespace detail

template <class T, class A>
struct Codec<std::vector<T, A>> {
    using L = detail::ListCodec<T, std::vector<T, A>>;
    static std::string ref(Schema& s, int depth) { return L::ref(s, depth, -1); }
    static bool write(Writer& w, const std::vector<T, A>& v, int depth) { return L::write(w, v, depth, -1); }
    static bool read(Reader& r, std::vector<T, A>& v, int depth) { return L::read(r, v, depth, -1); }
};

template <class T, size_t N>
struct Codec<Capped<T, N>> {
    using L = detail::ListCodec<T, Capped<T, N>>;
    static std::string ref(Schema& s, int depth) { return L::ref(s, depth, static_cast<long>(N)); }
    static bool write(Writer& w, const Capped<T, N>& v, int depth) { return L::write(w, v, depth, static_cast<long>(N)); }
    static bool read(Reader& r, Capped<T, N>& v, int depth) { return L::read(r, v, depth, static_cast<long>(N)); }
};

// ------------------------------------------------------------------ records

namespace detail {
/// One field of a record, type-erased: its name and what reads, writes and describes it.
template <class R>
struct FieldOps {
    std::string name;
    std::string (*ref)(Schema&, int);
    bool (*write)(Writer&, const R&, int);
    bool (*read)(Reader&, R&, int);
};

template <auto M>
struct MemberOf;
template <class C, class V, V C::*M>
struct MemberOf<M> {
    using Class = C;
    using Value = V;
};

template <auto M>
struct FieldCodec {
    using R = typename MemberOf<M>::Class;
    using V = typename MemberOf<M>::Value;

    static std::string ref(Schema& s, int depth) {
        if constexpr (is_optional_field<V>::value) {
            // An optional field ends recursion like a list does: its value may stop there.
            return Codec<typename is_optional_field<V>::inner>::ref(s, depth + 1) + "?";
        } else {
            return Codec<V>::ref(s, depth);
        }
    }
    static bool write(Writer& w, const R& record, int depth) {
        const V& v = record.*M;
        if constexpr (is_optional_field<V>::value) {
            using O = is_optional_field<V>;
            if (!O::present(v)) {
                w.u8(0);
                return true;
            }
            w.u8(1);
            return depth < max_nesting_depth && Codec<typename O::inner>::write(w, O::get(v), depth + 1);
        } else {
            return Codec<V>::write(w, v, depth);
        }
    }
    static bool read(Reader& r, R& record, int depth) {
        V& v = record.*M;
        if constexpr (is_optional_field<V>::value) {
            using O = is_optional_field<V>;
            uint8_t present;
            if (!r.u8(present) || present > 1) return false;
            if (present == 0) {
                O::clear(v);
                return true;
            }
            return depth < max_nesting_depth && Codec<typename O::inner>::read(r, O::emplace(v), depth + 1);
        } else {
            return Codec<V>::read(r, v, depth);
        }
    }
};

template <auto M>
FieldOps<typename MemberOf<M>::Class> field(const char* name) {
    return {name, &FieldCodec<M>::ref, &FieldCodec<M>::write, &FieldCodec<M>::read};
}

/// A record's NOTES name and its fields in canonical order (ordinal by name).
template <class R>
struct RecordDef {
    std::string name;
    std::vector<FieldOps<R>> fields;
    std::string error;

    RecordDef(const char* n, std::initializer_list<FieldOps<R>> list) : name(n), fields(list) {
        std::sort(fields.begin(), fields.end(), [](const FieldOps<R>& a, const FieldOps<R>& b) { return a.name < b.name; });
        if (!valid_name(name)) error = "'" + name + "' is not a valid NOTES name";
        for (size_t i = 0; i < fields.size() && error.empty(); i++) {
            if (!valid_name(fields[i].name)) error = name + ": '" + fields[i].name + "' is not a valid NOTES name";
            if (i > 0 && fields[i].name == fields[i - 1].name) error = name + " maps the field '" + fields[i].name + "' twice";
        }
    }
};

void evn_record();  // for the lookup below; the real ones are found by argument-dependent lookup

template <class T, class = void>
struct is_record : std::false_type {};
template <class T>
struct is_record<T, std::void_t<decltype(evn_record(static_cast<const T*>(nullptr)))>> : std::true_type {};

template <class T>
const RecordDef<T>& record_def() {
    return evn_record(static_cast<const T*>(nullptr));
}

void evn_docs();

template <class T, class = void>
struct has_docs : std::false_type {};
template <class T>
struct has_docs<T, std::void_t<decltype(evn_docs(static_cast<const T*>(nullptr)))>> : std::true_type {};

template <class T>
std::vector<FieldDoc> docs_of() {
    if constexpr (has_docs<T>::value) {
        return evn_docs(static_cast<const T*>(nullptr));
    } else {
        return {};
    }
}
}  // namespace detail

template <class T>
struct Codec<T, std::enable_if_t<detail::is_record<T>::value>> {
    static std::string ref(Schema& s, int depth) {
        const auto& def = detail::record_def<T>();
        if (!def.error.empty()) {
            s.fail(def.error);
            return def.name;
        }
        const void* tag = &detail::Tag<T>::id;
        auto progress = s.in_progress.find(tag);
        if (progress != s.in_progress.end()) {
            if (progress->second == depth) {
                s.fail(def.name + " contains itself through required fields only, so every value would be infinite; make a field on the cycle optional (std::optional or evn::Box) or a list");
            }
            return def.name;  // being described further up: recursion through a list or an optional field
        }
        auto known = s.records.find(def.name);
        if (known != s.records.end() && known->second.tag == tag) return def.name;
        s.in_progress[tag] = depth;
        std::string text = def.name + "{";
        for (size_t i = 0; i < def.fields.size(); i++) {
            if (i > 0) text += ",";
            text += def.fields[i].name + ":" + def.fields[i].ref(s, depth);
        }
        text += "}";
        s.in_progress.erase(tag);
        known = s.records.find(def.name);
        if (known == s.records.end()) {
            s.records[def.name] = {tag, text};
            for (const auto& d : detail::docs_of<T>()) {
                bool exists = false;
                for (const auto& f : def.fields) exists = exists || f.name == d.field;
                if (!exists) s.fail(def.name + " documents '" + d.field + "', which isn't one of its fields");
                s.annotations.push_back({def.name, d.field, d.description, d.min, d.max, d.default_text});
            }
        } else if (known->second.text != text) {
            s.fail("two different types are named '" + def.name + "' (" + known->second.text + "; " + text + "): NOTES identifies record types by name");
        }
        return def.name;
    }
    // Depth counts lists and optional fields: records nest deeper only through those.
    static bool write(Writer& w, const T& v, int depth) {
        for (const auto& f : detail::record_def<T>().fields) {
            if (!f.write(w, v, depth)) return false;
        }
        return true;
    }
    static bool read(Reader& r, T& v, int depth) {
        for (const auto& f : detail::record_def<T>().fields) {
            if (!f.read(r, v, depth)) return false;
        }
        return true;
    }
};

// ---------------------------------------------------------------------------------------------------- API

/// True if T can be sent: a leaf, a list, or a mapped record.
template <class T, class = void>
struct is_notes_type : std::false_type {};
template <class T>
struct is_notes_type<T, std::void_t<decltype(Codec<T>::ref(std::declval<Schema&>(), 0))>> : std::true_type {};

/// T's canonical NOTES signature (computed once per type).
template <class T>
const Signature& signature_of() {
    static_assert(is_notes_type<T>::value, "not a NOTES type: map the struct with EVN_RECORD, or use a built-in leaf, std::vector or evn::Capped");
    static const Signature sig = [] {
        Schema s;
        std::string root = Codec<T>::ref(s, 0);
        Signature result;
        for (const auto& leaf : s.leaves) {
            if (s.records.count(leaf)) s.fail("'" + leaf + "' names both a leaf type and a record type");
        }
        if (!s.error.empty()) {
            result.error = s.error;
            return result;
        }
        result.entries.push_back("=" + root);
        for (const auto& r : s.records) result.entries.push_back(r.second.text);
        std::sort(result.entries.begin(), result.entries.end());  // byte order: code point order for UTF-8 names
        result.annotations = s.annotations;
        std::sort(result.annotations.begin(), result.annotations.end(), [](const Annotation& a, const Annotation& b) {
            return a.record != b.record ? a.record < b.record : a.field < b.field;
        });
        return result;
    }();
    return sig;
}

/// The NON bytes of a value; false if it can't be written (a list over its cap, an invalid decimal, nesting too deep).
template <class T>
bool encode(const T& value, std::vector<uint8_t>& out) {
    Writer w;
    if (!Codec<T>::write(w, value, 0)) return false;
    out = std::move(w.out);
    return true;
}

/// Reads a whole NON value; false for bytes that aren't exactly one value of T (short, leftover, invalid).
template <class T>
bool decode(const uint8_t* data, size_t size, T& out) {
    Reader r(data, size);
    return Codec<T>::read(r, out, 0) && r.n == 0;
}

template <class T>
bool decode(const std::vector<uint8_t>& data, T& out) {
    return decode(data.data(), data.size(), out);
}

}  // namespace evn

// ---------------------------------------------------------------------------------------------------- mapping macros

#define EVN_PP_EXPAND(x) x
#define EVN_PP_NARG(...) EVN_PP_EXPAND(EVN_PP_NARG_(__VA_ARGS__, EVN_PP_RSEQ()))
#define EVN_PP_NARG_(...) EVN_PP_EXPAND(EVN_PP_ARG_N(__VA_ARGS__))
#define EVN_PP_ARG_N(_1, _2, _3, _4, _5, _6, _7, _8, _9, _10, _11, _12, _13, _14, _15, _16, _17, _18, _19, _20, _21, _22, \
                     _23, _24, _25, _26, _27, _28, _29, _30, _31, _32, _33, _34, _35, _36, _37, _38, _39, _40, _41, _42,  \
                     _43, _44, _45, _46, _47, _48, N, ...)                                                               \
    N
#define EVN_PP_RSEQ()                                                                                                  \
    48, 47, 46, 45, 44, 43, 42, 41, 40, 39, 38, 37, 36, 35, 34, 33, 32, 31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20, \
        19, 18, 17, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1
#define EVN_PP_CAT(a, b) EVN_PP_CAT_(a, b)
#define EVN_PP_CAT_(a, b) a##b

#define EVN_PP_F(T, f) ::evn::detail::field<&T::f>(#f)
#define EVN_PP_M1(T, a) EVN_PP_F(T, a)
#define EVN_PP_M2(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M1(T, __VA_ARGS__))
#define EVN_PP_M3(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M2(T, __VA_ARGS__))
#define EVN_PP_M4(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M3(T, __VA_ARGS__))
#define EVN_PP_M5(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M4(T, __VA_ARGS__))
#define EVN_PP_M6(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M5(T, __VA_ARGS__))
#define EVN_PP_M7(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M6(T, __VA_ARGS__))
#define EVN_PP_M8(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M7(T, __VA_ARGS__))
#define EVN_PP_M9(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M8(T, __VA_ARGS__))
#define EVN_PP_M10(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M9(T, __VA_ARGS__))
#define EVN_PP_M11(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M10(T, __VA_ARGS__))
#define EVN_PP_M12(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M11(T, __VA_ARGS__))
#define EVN_PP_M13(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M12(T, __VA_ARGS__))
#define EVN_PP_M14(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M13(T, __VA_ARGS__))
#define EVN_PP_M15(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M14(T, __VA_ARGS__))
#define EVN_PP_M16(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M15(T, __VA_ARGS__))
#define EVN_PP_M17(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M16(T, __VA_ARGS__))
#define EVN_PP_M18(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M17(T, __VA_ARGS__))
#define EVN_PP_M19(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M18(T, __VA_ARGS__))
#define EVN_PP_M20(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M19(T, __VA_ARGS__))
#define EVN_PP_M21(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M20(T, __VA_ARGS__))
#define EVN_PP_M22(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M21(T, __VA_ARGS__))
#define EVN_PP_M23(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M22(T, __VA_ARGS__))
#define EVN_PP_M24(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M23(T, __VA_ARGS__))
#define EVN_PP_M25(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M24(T, __VA_ARGS__))
#define EVN_PP_M26(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M25(T, __VA_ARGS__))
#define EVN_PP_M27(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M26(T, __VA_ARGS__))
#define EVN_PP_M28(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M27(T, __VA_ARGS__))
#define EVN_PP_M29(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M28(T, __VA_ARGS__))
#define EVN_PP_M30(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M29(T, __VA_ARGS__))
#define EVN_PP_M31(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M30(T, __VA_ARGS__))
#define EVN_PP_M32(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M31(T, __VA_ARGS__))
#define EVN_PP_M33(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M32(T, __VA_ARGS__))
#define EVN_PP_M34(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M33(T, __VA_ARGS__))
#define EVN_PP_M35(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M34(T, __VA_ARGS__))
#define EVN_PP_M36(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M35(T, __VA_ARGS__))
#define EVN_PP_M37(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M36(T, __VA_ARGS__))
#define EVN_PP_M38(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M37(T, __VA_ARGS__))
#define EVN_PP_M39(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M38(T, __VA_ARGS__))
#define EVN_PP_M40(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M39(T, __VA_ARGS__))
#define EVN_PP_M41(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M40(T, __VA_ARGS__))
#define EVN_PP_M42(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M41(T, __VA_ARGS__))
#define EVN_PP_M43(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M42(T, __VA_ARGS__))
#define EVN_PP_M44(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M43(T, __VA_ARGS__))
#define EVN_PP_M45(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M44(T, __VA_ARGS__))
#define EVN_PP_M46(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M45(T, __VA_ARGS__))
#define EVN_PP_M47(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M46(T, __VA_ARGS__))
#define EVN_PP_M48(T, a, ...) EVN_PP_F(T, a), EVN_PP_EXPAND(EVN_PP_M47(T, __VA_ARGS__))
#define EVN_PP_FIELDS(T, ...) EVN_PP_EXPAND(EVN_PP_CAT(EVN_PP_M, EVN_PP_NARG(__VA_ARGS__))(T, __VA_ARGS__))

/// Maps a struct (in the current namespace) as the NOTES record type NAME with the listed fields (1 to 48).
#define EVN_RECORD_NAMED(TYPE, NAME, ...)                                                             \
    inline const ::evn::detail::RecordDef<TYPE>& evn_record(const TYPE*) {                            \
        static const ::evn::detail::RecordDef<TYPE> def(NAME, {EVN_PP_FIELDS(TYPE, __VA_ARGS__)});    \
        return def;                                                                                   \
    }

/// Maps a struct as a NOTES record type named like the struct.
#define EVN_RECORD(TYPE, ...) EVN_RECORD_NAMED(TYPE, #TYPE, __VA_ARGS__)

/// Documents fields of a mapped struct (next to its EVN_RECORD): EVN_DOCS(T, evn::doc("Field", "what it is").range(0, 9), ...).
#define EVN_DOCS(TYPE, ...)                                              \
    inline std::vector<::evn::FieldDoc> evn_docs(const TYPE*) {          \
        return {__VA_ARGS__};                                            \
    }
