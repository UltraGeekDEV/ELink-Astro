// EVent for C++: dynamic types and values. Any NOTES type, known only at run time from its signature (as the network
// agreed it), read and written without a compiled struct; values convert to and from JSON (the NON value shape).
//
//     auto schema = evn::dyn::Schema::parse(descriptor.expected, &error);
//     evn::dyn::Value v;
//     evn::dyn::decode(bytes, schema->root(), v);
//     double c = v["Celsius"].as_double();
//     v["Where"]["Floor"] = 2;
//     std::string json = evn::dyn::to_json(v);
//
// Part of the portable core: C++17, no exceptions thrown, no I/O, no threads (register custom leaves at start-up).
#pragma once

#include <cerrno>
#include <cmath>
#include <cstdlib>
#include <deque>
#include <functional>
#include <limits>
#include <map>
#include <memory>
#include <string>
#include <vector>

#include "non.hpp"

namespace evn {
namespace dyn {

enum class Kind : uint8_t { Void, Bool, Int8, UInt8, Int16, UInt16, Int32, UInt32, Int64, UInt64, Float, Double, Decimal, Char16, String, Custom, Record, List };

struct Type;

struct Field {
    std::string name;
    const Type* type = nullptr;
    bool optional = false;
};

/// One type of a schema: a leaf, a record (fields in canonical order) or a list (with an optional cap).
struct Type {
    Kind kind = Kind::Void;
    std::string name;               // leaf or record name
    const Type* element = nullptr;  // List
    long cap = -1;                  // List: the cap, or -1
    std::vector<Field> fields;      // Record

    bool is_record() const { return kind == Kind::Record; }
    bool is_list() const { return kind == Kind::List; }
    std::string ref() const { return kind != Kind::List ? name : element->ref() + (cap < 0 ? "[]" : "[<=" + std::to_string(cap) + "]"); }
    const Field* field(const std::string& n) const {
        for (const auto& f : fields) {
            if (f.name == n) return &f;
        }
        return nullptr;
    }
};

/// Custom leaves (an application's own leaf types, like C#'s "stamp16"): how to step over one value. Their values are
/// kept as raw bytes (Value::Tag::Bytes). Register them before parsing schemas that use them.
inline std::map<std::string, std::function<bool(Reader&)>>& custom_leaves() {
    static std::map<std::string, std::function<bool(Reader&)>> leaves;
    return leaves;
}

inline void register_leaf(const std::string& name, std::function<bool(Reader&)> step_over) { custom_leaves()[name] = std::move(step_over); }

/// A parsed signature: every type it names, and its root.
class Schema {
public:
    /// Parses a canonical signature ("=Root", "Name{field:TypeRef,...}", ...). Null, with the reason in `error`, if it
    /// isn't one: no root, an unknown leaf, a record defined twice or not at all, a record that contains itself
    /// through required fields only.
    static std::shared_ptr<const Schema> parse(const std::vector<std::string>& signature, std::string* error = nullptr) {
        std::shared_ptr<Schema> s(new Schema());
        s->signature_ = signature;
        std::string why;
        if (!s->build(why)) {
            if (error) *error = why;
            return nullptr;
        }
        return s;
    }

    const Type& root() const { return *root_; }
    /// The signature in canonical form (fields and entries sorted), as the network compares it.
    std::vector<std::string> canonical() const {
        std::vector<std::string> out{"=" + root_->ref()};
        for (const auto& r : records_) {
            std::string text = r.first + "{";
            for (size_t i = 0; i < r.second->fields.size(); i++) {
                const Field& f = r.second->fields[i];
                text += (i > 0 ? "," : "") + f.name + ":" + f.type->ref() + (f.optional ? "?" : "");
            }
            out.push_back(text + "}");
        }
        std::sort(out.begin(), out.end());
        return out;
    }
    const std::vector<std::string>& signature() const { return signature_; }
    /// The record named `name`, or null.
    const Type* record(const std::string& name) const {
        auto r = records_.find(name);
        return r == records_.end() ? nullptr : r->second;
    }

private:
    Schema() = default;
    std::deque<Type> types_;
    std::map<std::string, Type*> records_;
    std::map<std::string, const Type*> leaves_;
    std::vector<std::string> signature_;
    const Type* root_ = nullptr;

    static bool leaf_kind(const std::string& n, Kind& k) {
        static const std::map<std::string, Kind> kinds = {
            {"Void", Kind::Void},       {"bool", Kind::Bool},     {"int8", Kind::Int8},       {"uint8", Kind::UInt8},
            {"int16", Kind::Int16},     {"uint16", Kind::UInt16}, {"int32", Kind::Int32},     {"uint32", Kind::UInt32},
            {"int64", Kind::Int64},     {"UInt64", Kind::UInt64}, {"float", Kind::Float},     {"double", Kind::Double},
            {"decimal", Kind::Decimal}, {"char16", Kind::Char16}, {"string", Kind::String}};
        auto f = kinds.find(n);
        if (f == kinds.end()) return false;
        k = f->second;
        return true;
    }

    const Type* resolve(const std::string& ref, std::string& why) {
        if (!ref.empty() && ref.back() == ']') {
            size_t open = ref.rfind('[');
            if (open == std::string::npos || open == 0) {
                why = "bad type reference '" + ref + "'";
                return nullptr;
            }
            std::string inner = ref.substr(open + 1, ref.size() - open - 2);
            long cap = -1;
            if (!inner.empty()) {
                if (inner.size() < 3 || inner.compare(0, 2, "<=") != 0 || inner.find_first_not_of("0123456789", 2) != std::string::npos) {
                    why = "bad cap in '" + ref + "'";
                    return nullptr;
                }
                cap = std::strtol(inner.c_str() + 2, nullptr, 10);
            }
            const Type* element = resolve(ref.substr(0, open), why);
            if (element == nullptr) return nullptr;
            types_.emplace_back();
            Type& t = types_.back();
            t.kind = Kind::List;
            t.name = element->name;
            t.element = element;
            t.cap = cap;
            return &t;
        }
        auto record = records_.find(ref);
        if (record != records_.end()) return record->second;
        auto known = leaves_.find(ref);
        if (known != leaves_.end()) return known->second;
        Kind k;
        bool builtin = leaf_kind(ref, k);
        if (!builtin && custom_leaves().count(ref) == 0) {
            why = "'" + ref + "' is neither a record of this signature nor a known leaf type";
            return nullptr;
        }
        types_.emplace_back();
        Type& t = types_.back();
        t.kind = builtin ? k : Kind::Custom;
        t.name = ref;
        leaves_[ref] = &t;
        return &t;
    }

    bool build(std::string& why) {
        std::string root;
        std::vector<std::pair<Type*, std::string>> bodies;
        for (const auto& e : signature_) {
            if (!e.empty() && e[0] == '=') {
                if (!root.empty()) {
                    why = "two root entries";
                    return false;
                }
                root = e.substr(1);
                continue;
            }
            size_t open = e.find('{');
            if (open == std::string::npos || open == 0 || e.back() != '}') {
                why = "'" + e + "' is not a record definition";
                return false;
            }
            std::string name = e.substr(0, open);
            if (records_.count(name) || !valid_name(name)) {
                why = "record '" + name + "' is defined twice or badly named";
                return false;
            }
            types_.emplace_back();
            Type& t = types_.back();
            t.kind = Kind::Record;
            t.name = name;
            records_[name] = &t;
            bodies.push_back({&t, e.substr(open + 1, e.size() - open - 2)});
        }
        if (root.empty()) {
            why = "no root entry";
            return false;
        }
        for (auto& b : bodies) {
            size_t start = 0;
            const std::string& body = b.second;
            while (start < body.size()) {
                size_t end = body.find(',', start);
                if (end == std::string::npos) end = body.size();
                std::string item = body.substr(start, end - start);
                start = end + 1;
                size_t colon = item.find(':');
                if (colon == std::string::npos || colon == 0) {
                    why = "bad field '" + item + "' in " + b.first->name;
                    return false;
                }
                Field f;
                f.name = item.substr(0, colon);
                std::string ref = item.substr(colon + 1);
                if (!ref.empty() && ref.back() == '?') {
                    f.optional = true;
                    ref.pop_back();
                }
                f.type = resolve(ref, why);
                if (f.type == nullptr) return false;
                if (b.first->field(f.name)) {
                    why = b.first->name + " has the field '" + f.name + "' twice";
                    return false;
                }
                b.first->fields.push_back(f);
            }
            std::sort(b.first->fields.begin(), b.first->fields.end(), [](const Field& x, const Field& y) { return x.name < y.name; });
        }
        root_ = resolve(root, why);
        if (root_ == nullptr) return false;
        // A cycle of required record fields means every value would be infinite.
        std::map<const Type*, int> state;
        std::function<bool(const Type*)> visit = [&](const Type* t) {
            state[t] = 1;
            for (const auto& f : t->fields) {
                if (f.optional || !f.type->is_record()) continue;
                if (state[f.type] == 1) return false;
                if (state[f.type] == 0 && !visit(f.type)) return false;
            }
            state[t] = 2;
            return true;
        };
        for (auto& r : records_) {
            if (state[r.second] == 0 && !visit(r.second)) {
                why = r.first + " contains itself through required fields only";
                return false;
            }
        }
        return true;
    }
};

// ---------------------------------------------------------------------------------------------------- values

/// A dynamic value: null (an absent optional field), Void, a bool, an integer (signed or unsigned), a floating point
/// number, a decimal, a char16, a string, raw bytes (a custom leaf), a record (named fields) or a list.
class Value {
public:
    enum class Tag : uint8_t { Null, Void, Bool, Int, UInt, Double, Decimal, Char, String, Bytes, Record, List };

    Value() = default;
    Value(bool v) : tag_(Tag::Bool), b_(v) {}
    Value(int v) : tag_(Tag::Int), i_(v) {}
    Value(long v) : tag_(Tag::Int), i_(v) {}
    Value(long long v) : tag_(Tag::Int), i_(v) {}
    Value(unsigned v) : tag_(Tag::UInt), u_(v) {}
    Value(unsigned long v) : tag_(Tag::UInt), u_(v) {}
    Value(unsigned long long v) : tag_(Tag::UInt), u_(v) {}
    Value(double v) : tag_(Tag::Double), d_(v) {}
    Value(float v) : tag_(Tag::Double), d_(v) {}
    Value(const Decimal& v) : tag_(Tag::Decimal), m_(v) {}
    Value(char16_t v) : tag_(Tag::Char), c_(v) {}
    Value(const char* v) : tag_(Tag::String), text_(v) {}
    Value(std::string v) : tag_(Tag::String), text_(std::move(v)) {}

    static Value void_value() {
        Value v;
        v.tag_ = Tag::Void;
        return v;
    }
    static Value record() {
        Value v;
        v.tag_ = Tag::Record;
        return v;
    }
    static Value list() {
        Value v;
        v.tag_ = Tag::List;
        return v;
    }
    static Value bytes(std::vector<uint8_t> raw) {
        Value v;
        v.tag_ = Tag::Bytes;
        v.text_.assign(raw.begin(), raw.end());
        return v;
    }
    /// A number with its literal kept (from JSON): a decimal field gets it exactly.
    static Value number(const std::string& literal) {
        Value v;
        bool integral = literal.find_first_of(".eE") == std::string::npos;
        errno = 0;
        if (integral && !literal.empty() && literal[0] == '-') {
            long long x = std::strtoll(literal.c_str(), nullptr, 10);
            if (errno == 0) v = Value(x);
        } else if (integral) {
            unsigned long long x = std::strtoull(literal.c_str(), nullptr, 10);
            if (errno == 0) v = Value(x);
        }
        if (v.is_null()) v = Value(std::strtod(literal.c_str(), nullptr));
        v.text_ = literal;
        return v;
    }

    Tag tag() const { return tag_; }
    bool is_null() const { return tag_ == Tag::Null; }
    bool is_number() const { return tag_ == Tag::Int || tag_ == Tag::UInt || tag_ == Tag::Double || tag_ == Tag::Decimal; }

    bool as_bool() const { return tag_ == Tag::Bool && b_; }
    /// The value as int64 (0 if it isn't an integer or doesn't fit).
    int64_t as_int() const {
        if (tag_ == Tag::Int) return i_;
        if (tag_ == Tag::UInt && u_ <= static_cast<uint64_t>(std::numeric_limits<int64_t>::max())) return static_cast<int64_t>(u_);
        return 0;
    }
    uint64_t as_uint() const {
        if (tag_ == Tag::UInt) return u_;
        if (tag_ == Tag::Int && i_ >= 0) return static_cast<uint64_t>(i_);
        return 0;
    }
    /// Any number as a double (0 otherwise).
    double as_double() const {
        switch (tag_) {
            case Tag::Double: return d_;
            case Tag::Int: return static_cast<double>(i_);
            case Tag::UInt: return static_cast<double>(u_);
            case Tag::Decimal: return m_.to_double();
            default: return 0;
        }
    }
    Decimal as_decimal() const { return tag_ == Tag::Decimal ? m_ : Decimal{}; }
    char16_t as_char() const { return tag_ == Tag::Char ? c_ : 0; }
    /// A string's text, a custom leaf's bytes, or a JSON number's literal.
    const std::string& text() const { return text_; }
    const std::string& as_string() const { return text_; }

    // Records: fields by name (kept in name order, the NOTES order).
    bool has(const std::string& field) const { return find(field) != nullptr; }
    const Value* find(const std::string& field) const {
        auto it = std::lower_bound(names_.begin(), names_.end(), field);
        return it != names_.end() && *it == field ? &items_[static_cast<size_t>(it - names_.begin())] : nullptr;
    }
    /// The field, created (null) if missing; a null value becomes a record first.
    Value& operator[](const std::string& field) {
        if (tag_ == Tag::Null) tag_ = Tag::Record;
        auto it = std::lower_bound(names_.begin(), names_.end(), field);
        size_t at = static_cast<size_t>(it - names_.begin());
        if (it == names_.end() || *it != field) {
            names_.insert(it, field);
            items_.insert(items_.begin() + static_cast<std::ptrdiff_t>(at), Value());
        }
        return items_[at];
    }
    const Value& operator[](const std::string& field) const {
        static const Value none;
        auto v = find(field);
        return v ? *v : none;
    }
    void erase(const std::string& field) {
        auto it = std::lower_bound(names_.begin(), names_.end(), field);
        if (it != names_.end() && *it == field) {
            items_.erase(items_.begin() + (it - names_.begin()));
            names_.erase(it);
        }
    }
    const std::vector<std::string>& field_names() const { return names_; }

    // Lists.
    void push_back(Value v) {
        if (tag_ == Tag::Null) tag_ = Tag::List;
        items_.push_back(std::move(v));
    }
    size_t size() const { return items_.size(); }
    Value& operator[](size_t i) { return items_[i]; }
    const Value& operator[](size_t i) const { return items_[i]; }
    /// A list's elements, or a record's field values (in name order).
    const std::vector<Value>& items() const { return items_; }

private:
    Tag tag_ = Tag::Null;
    bool b_ = false;
    int64_t i_ = 0;
    uint64_t u_ = 0;
    double d_ = 0;
    Decimal m_;
    char16_t c_ = 0;
    std::string text_;
    std::vector<std::string> names_;  // record field names, sorted
    std::vector<Value> items_;         // list elements, or record field values (parallel to names_)
};

namespace detail {
// One UTF-16 code unit from a string holding exactly one BMP character (UTF-8).
inline bool single_unit(const std::string& s, char16_t& out) {
    const auto* p = reinterpret_cast<const unsigned char*>(s.data());
    uint32_t cp;
    size_t n;
    if (s.size() == 1 && p[0] < 0x80) {
        cp = p[0];
        n = 1;
    } else if (s.size() == 2 && (p[0] & 0xE0) == 0xC0) {
        cp = ((p[0] & 0x1Fu) << 6) | (p[1] & 0x3Fu);
        n = 2;
    } else if (s.size() == 3 && (p[0] & 0xF0) == 0xE0) {
        cp = ((p[0] & 0x0Fu) << 12) | ((p[1] & 0x3Fu) << 6) | (p[2] & 0x3Fu);
        n = 3;
    } else {
        return false;
    }
    (void)n;
    out = static_cast<char16_t>(cp);
    return true;
}

inline void append_utf8(std::string& out, uint32_t cp) {
    if (cp < 0x80) {
        out.push_back(static_cast<char>(cp));
    } else if (cp < 0x800) {
        out.push_back(static_cast<char>(0xC0 | (cp >> 6)));
        out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
    } else if (cp < 0x10000) {
        out.push_back(static_cast<char>(0xE0 | (cp >> 12)));
        out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
        out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
    } else {
        out.push_back(static_cast<char>(0xF0 | (cp >> 18)));
        out.push_back(static_cast<char>(0x80 | ((cp >> 12) & 0x3F)));
        out.push_back(static_cast<char>(0x80 | ((cp >> 6) & 0x3F)));
        out.push_back(static_cast<char>(0x80 | (cp & 0x3F)));
    }
}

template <class T>
bool integer_in(const Value& v, T& out) {
    if (v.tag() == Value::Tag::Int) {
        int64_t x = v.as_int();
        if (std::is_signed<T>::value ? (x < static_cast<int64_t>(std::numeric_limits<T>::min()) || x > static_cast<int64_t>(std::numeric_limits<T>::max()))
                                     : (x < 0 || static_cast<uint64_t>(x) > static_cast<uint64_t>(std::numeric_limits<T>::max())))
            return false;
        out = static_cast<T>(x);
        return true;
    }
    if (v.tag() == Value::Tag::UInt) {
        uint64_t x = v.as_uint();
        if (x > static_cast<uint64_t>(std::numeric_limits<T>::max())) return false;
        out = static_cast<T>(x);
        return true;
    }
    return false;  // never a fraction, a bool or a string into an integer
}

inline bool floating(const Value& v, double& out) {
    if (v.is_number()) {
        out = v.as_double();
        return true;
    }
    if (v.tag() == Value::Tag::String) {  // non-finite numbers travel as strings in JSON
        if (v.text() == "NaN") out = std::numeric_limits<double>::quiet_NaN();
        else if (v.text() == "Infinity") out = std::numeric_limits<double>::infinity();
        else if (v.text() == "-Infinity") out = -std::numeric_limits<double>::infinity();
        else return false;
        return true;
    }
    return false;
}

inline bool decimal_of(const Value& v, Decimal& out) {
    switch (v.tag()) {
        case Value::Tag::Decimal: out = v.as_decimal(); return true;
        case Value::Tag::Int: out = Decimal::from_int64(v.as_int()); return true;
        case Value::Tag::UInt: return Decimal::parse(std::to_string(v.as_uint()), out);
        case Value::Tag::Double: {
            if (!v.text().empty() && Decimal::parse(v.text(), out)) return true;  // a JSON literal, exactly
            if (!std::isfinite(v.as_double())) return false;
            return Decimal::parse(evn::detail::decimal_text(v.as_double()), out);
        }
        default: return false;
    }
}
}  // namespace detail

/// Writes `v` as a value of `t` in the NON layout. False (with the reason) if it doesn't fit: a missing required field,
/// a field the type doesn't have, a number out of range or with a fraction where an integer goes, a list over its cap.
inline bool encode(const Value& v, const Type& t, Writer& w, std::string* why = nullptr, int depth = 0) {
    auto fail = [&](const std::string& reason) {
        if (why && why->empty()) *why = reason;
        return false;
    };
    using Tag = Value::Tag;
    switch (t.kind) {
        case Kind::Void:
            if (v.tag() != Tag::Void && v.tag() != Tag::Null) return fail("not Void");
            w.u8(0);
            return true;
        case Kind::Bool:
            if (v.tag() != Tag::Bool) return fail("not a bool");
            w.u8(v.as_bool() ? 1 : 0);
            return true;
#define EVN_DYN_INT(KIND, CT, NAME)                                              \
    case Kind::KIND: {                                                           \
        CT x;                                                                    \
        if (!detail::integer_in(v, x)) return fail("not an " NAME " (in range)"); \
        return Codec<CT>::write(w, x, 0);                                        \
    }
            EVN_DYN_INT(Int8, int8_t, "int8")
            EVN_DYN_INT(UInt8, uint8_t, "uint8")
            EVN_DYN_INT(Int16, int16_t, "int16")
            EVN_DYN_INT(UInt16, uint16_t, "uint16")
            EVN_DYN_INT(Int32, int32_t, "int32")
            EVN_DYN_INT(UInt32, uint32_t, "uint32")
            EVN_DYN_INT(Int64, int64_t, "int64")
            EVN_DYN_INT(UInt64, uint64_t, "UInt64")
#undef EVN_DYN_INT
        case Kind::Float: {
            double d;
            if (!detail::floating(v, d)) return fail("not a number");
            return Codec<float>::write(w, static_cast<float>(d), 0);
        }
        case Kind::Double: {
            double d;
            if (!detail::floating(v, d)) return fail("not a number");
            return Codec<double>::write(w, d, 0);
        }
        case Kind::Decimal: {
            Decimal m;
            if (!detail::decimal_of(v, m)) return fail("not a decimal (in range)");
            return Codec<Decimal>::write(w, m, 0);
        }
        case Kind::Char16: {
            char16_t c = v.as_char();
            if (v.tag() != Tag::Char && !(v.tag() == Tag::String && detail::single_unit(v.text(), c))) return fail("not a char16");
            return Codec<char16_t>::write(w, c, 0);
        }
        case Kind::String:
            if (v.tag() != Tag::String) return fail("not a string");
            return Codec<std::string>::write(w, v.text(), 0);
        case Kind::Custom:
            if (v.tag() != Tag::Bytes) return fail("not the bytes of a " + t.name);
            w.bytes(v.text().data(), v.text().size());
            return true;
        case Kind::List: {
            if (v.tag() != Tag::List && v.tag() != Tag::Null) return fail("not a list");
            if ((t.cap >= 0 && v.size() > static_cast<size_t>(t.cap)) || depth >= max_nesting_depth) return fail("over the cap of " + t.ref());
            w.i32(static_cast<int32_t>(v.size()));
            for (const auto& item : v.items()) {
                if (!encode(item, *t.element, w, why, depth + 1)) return false;
            }
            return true;
        }
        case Kind::Record: {
            if (v.tag() != Tag::Record) return fail("not a record (" + t.name + ")");
            for (const auto& n : v.field_names()) {
                if (!t.field(n)) return fail(t.name + " has no field '" + n + "'");
            }
            for (const auto& f : t.fields) {
                const Value* member = v.find(f.name);
                bool absent = member == nullptr || member->is_null();
                if (f.optional) {
                    w.u8(absent ? 0 : 1);
                    if (absent) continue;
                    if (depth >= max_nesting_depth) return fail("nested too deep");
                    if (!encode(*member, *f.type, w, why, depth + 1)) return fail(t.name + "." + f.name);
                    continue;
                }
                if (absent) return fail(t.name + "." + f.name + " is required");
                if (!encode(*member, *f.type, w, why, depth)) return fail(t.name + "." + f.name);
            }
            return true;
        }
    }
    return false;
}

inline bool encode(const Value& v, const Type& t, std::vector<uint8_t>& out, std::string* why = nullptr) {
    Writer w;
    if (!encode(v, t, w, why)) return false;
    out = std::move(w.out);
    return true;
}

/// Reads one value of `t`.
inline bool decode(Reader& r, const Type& t, Value& out, int depth = 0) {
    switch (t.kind) {
        case Kind::Void: {
            uint8_t b;
            out = Value::void_value();
            return r.u8(b);
        }
        case Kind::Bool: {
            bool b;
            if (!Codec<bool>::read(r, b, 0)) return false;
            out = Value(b);
            return true;
        }
#define EVN_DYN_INT(KIND, CT)                          \
    case Kind::KIND: {                                 \
        CT x;                                          \
        if (!Codec<CT>::read(r, x, 0)) return false;   \
        out = std::is_signed<CT>::value ? Value(static_cast<long long>(x)) : Value(static_cast<unsigned long long>(x)); \
        return true;                                   \
    }
        EVN_DYN_INT(Int8, int8_t)
        EVN_DYN_INT(UInt8, uint8_t)
        EVN_DYN_INT(Int16, int16_t)
        EVN_DYN_INT(UInt16, uint16_t)
        EVN_DYN_INT(Int32, int32_t)
        EVN_DYN_INT(UInt32, uint32_t)
        EVN_DYN_INT(Int64, int64_t)
        EVN_DYN_INT(UInt64, uint64_t)
#undef EVN_DYN_INT
        case Kind::Float: {
            float f;
            if (!Codec<float>::read(r, f, 0)) return false;
            out = Value(f);
            return true;
        }
        case Kind::Double: {
            double d;
            if (!Codec<double>::read(r, d, 0)) return false;
            out = Value(d);
            return true;
        }
        case Kind::Decimal: {
            Decimal m;
            if (!Codec<Decimal>::read(r, m, 0)) return false;
            out = Value(m);
            return true;
        }
        case Kind::Char16: {
            char16_t c;
            if (!Codec<char16_t>::read(r, c, 0)) return false;
            out = Value(c);
            return true;
        }
        case Kind::String: {
            std::string s;
            if (!r.str(s)) return false;
            out = Value(std::move(s));
            return true;
        }
        case Kind::Custom: {
            auto step = custom_leaves().find(t.name);
            const uint8_t* start = r.p;
            if (step == custom_leaves().end() || !step->second(r)) return false;
            out = Value::bytes(std::vector<uint8_t>(start, r.p));
            return true;
        }
        case Kind::List: {
            int32_t count;
            if (depth >= max_nesting_depth || !r.i32(count) || count < 0 || static_cast<size_t>(count) > r.n || (t.cap >= 0 && count > t.cap)) return false;
            out = Value::list();
            for (int32_t i = 0; i < count; i++) {
                size_t before = r.n;
                Value item;
                if (!decode(r, *t.element, item, depth + 1) || r.n == before) return false;
                out.push_back(std::move(item));
            }
            return true;
        }
        case Kind::Record: {
            out = Value::record();
            for (const auto& f : t.fields) {
                if (f.optional) {
                    uint8_t present;
                    if (!r.u8(present) || present > 1) return false;
                    if (present == 0) continue;  // absent: not in the record
                    if (depth >= max_nesting_depth || !decode(r, *f.type, out[f.name], depth + 1)) return false;
                    continue;
                }
                if (!decode(r, *f.type, out[f.name], depth)) return false;
            }
            return true;
        }
    }
    return false;
}

/// Reads a whole value; false unless the bytes are exactly one value of `t`.
inline bool decode(const std::vector<uint8_t>& bytes, const Type& t, Value& out) {
    Reader r(bytes);
    return decode(r, t, out) && r.n == 0;
}

// ---------------------------------------------------------------------------------------------------- JSON

namespace detail {
inline std::string number_text(double d) {
    char buffer[40];
    for (int precision = 1; precision <= 17; precision++) {
        std::snprintf(buffer, sizeof buffer, "%.*g", precision, d);
        if (std::strtod(buffer, nullptr) == d) break;
    }
    return buffer;
}

inline void write_json(const Value& v, std::string& out) {
    using Tag = Value::Tag;
    switch (v.tag()) {
        case Tag::Null:
        case Tag::Void: out += "null"; return;
        case Tag::Bool: out += v.as_bool() ? "true" : "false"; return;
        case Tag::Int: out += std::to_string(v.as_int()); return;
        case Tag::UInt: out += std::to_string(v.as_uint()); return;
        case Tag::Double: {
            double d = v.as_double();
            if (std::isnan(d)) out += "\"NaN\"";
            else if (std::isinf(d)) out += d > 0 ? "\"Infinity\"" : "\"-Infinity\"";
            else out += number_text(d);
            return;
        }
        case Tag::Decimal: out += v.as_decimal().to_string(); return;
        case Tag::Char: {
            std::string s;
            append_utf8(s, v.as_char());
            out += evn::detail::json_string(s);
            return;
        }
        case Tag::String: out += evn::detail::json_string(v.text()); return;
        case Tag::Bytes: {
            static const char* digits = "0123456789ABCDEF";
            out.push_back('"');
            for (unsigned char c : v.text()) {
                out.push_back(digits[c >> 4]);
                out.push_back(digits[c & 15]);
            }
            out.push_back('"');
            return;
        }
        case Tag::Record: {
            out.push_back('{');
            for (size_t i = 0; i < v.field_names().size(); i++) {
                if (i > 0) out.push_back(',');
                out += evn::detail::json_string(v.field_names()[i]);
                out.push_back(':');
                write_json(v.items()[i], out);
            }
            out.push_back('}');
            return;
        }
        case Tag::List: {
            out.push_back('[');
            for (size_t i = 0; i < v.size(); i++) {
                if (i > 0) out.push_back(',');
                write_json(v[i], out);
            }
            out.push_back(']');
            return;
        }
    }
}

struct JsonParser {
    const std::string& s;
    size_t i = 0;
    std::string error;
    int depth = 0;

    void ws() {
        while (i < s.size() && (s[i] == ' ' || s[i] == '\t' || s[i] == '\n' || s[i] == '\r')) i++;
    }
    bool fail(const std::string& why) {
        if (error.empty()) error = why + " at " + std::to_string(i);
        return false;
    }
    bool literal(const char* word) {
        size_t n = std::strlen(word);
        if (s.compare(i, n, word) != 0) return false;
        i += n;
        return true;
    }
    bool hex4(uint32_t& v) {
        if (i + 4 > s.size()) return false;
        v = 0;
        for (int k = 0; k < 4; k++) {
            char c = s[i++];
            v <<= 4;
            if (c >= '0' && c <= '9') v |= static_cast<uint32_t>(c - '0');
            else if (c >= 'a' && c <= 'f') v |= static_cast<uint32_t>(c - 'a' + 10);
            else if (c >= 'A' && c <= 'F') v |= static_cast<uint32_t>(c - 'A' + 10);
            else return false;
        }
        return true;
    }
    bool string(std::string& out) {
        if (i >= s.size() || s[i] != '"') return fail("expected a string");
        i++;
        while (i < s.size() && s[i] != '"') {
            char c = s[i++];
            if (static_cast<unsigned char>(c) < 0x20) return fail("control character in a string");
            if (c != '\\') {
                out.push_back(c);
                continue;
            }
            if (i >= s.size()) return fail("bad escape");
            char e = s[i++];
            switch (e) {
                case '"': out.push_back('"'); break;
                case '\\': out.push_back('\\'); break;
                case '/': out.push_back('/'); break;
                case 'b': out.push_back('\b'); break;
                case 'f': out.push_back('\f'); break;
                case 'n': out.push_back('\n'); break;
                case 'r': out.push_back('\r'); break;
                case 't': out.push_back('\t'); break;
                case 'u': {
                    uint32_t cp;
                    if (!hex4(cp)) return fail("bad \\u escape");
                    if (cp >= 0xD800 && cp < 0xDC00 && s.compare(i, 2, "\\u") == 0) {
                        i += 2;
                        uint32_t low;
                        if (!hex4(low) || low < 0xDC00 || low > 0xDFFF) return fail("bad surrogate pair");
                        cp = 0x10000 + ((cp - 0xD800) << 10) + (low - 0xDC00);
                    }
                    append_utf8(out, cp);
                    break;
                }
                default: return fail("bad escape");
            }
        }
        if (i >= s.size()) return fail("unterminated string");
        i++;
        return true;
    }
    bool value(Value& out) {
        ws();
        if (i >= s.size()) return fail("unexpected end");
        if (++depth > 256) return fail("nested too deep");
        char c = s[i];
        bool ok = true;
        if (c == '{') {
            i++;
            out = Value::record();
            ws();
            if (i < s.size() && s[i] == '}') {
                i++;
            } else {
                while (ok) {
                    ws();
                    std::string key;
                    if (!string(key)) return false;
                    ws();
                    if (i >= s.size() || s[i] != ':') return fail("expected ':'");
                    i++;
                    if (out.has(key)) return fail("duplicate field '" + key + "'");
                    if (!value(out[key])) return false;
                    ws();
                    if (i < s.size() && s[i] == ',') {
                        i++;
                        continue;
                    }
                    if (i < s.size() && s[i] == '}') {
                        i++;
                        break;
                    }
                    return fail("expected ',' or '}'");
                }
            }
        } else if (c == '[') {
            i++;
            out = Value::list();
            ws();
            if (i < s.size() && s[i] == ']') {
                i++;
            } else {
                while (true) {
                    Value item;
                    if (!value(item)) return false;
                    out.push_back(std::move(item));
                    ws();
                    if (i < s.size() && s[i] == ',') {
                        i++;
                        continue;
                    }
                    if (i < s.size() && s[i] == ']') {
                        i++;
                        break;
                    }
                    return fail("expected ',' or ']'");
                }
            }
        } else if (c == '"') {
            std::string text;
            if (!string(text)) return false;
            out = Value(std::move(text));
        } else if (literal("true")) {
            out = Value(true);
        } else if (literal("false")) {
            out = Value(false);
        } else if (literal("null")) {
            out = Value();
        } else if (c == '-' || (c >= '0' && c <= '9')) {
            size_t start = i;
            if (s[i] == '-') i++;
            while (i < s.size() && ((s[i] >= '0' && s[i] <= '9') || s[i] == '.' || s[i] == 'e' || s[i] == 'E' || s[i] == '+' || s[i] == '-')) i++;
            std::string lit = s.substr(start, i - start);
            char* end = nullptr;
            std::strtod(lit.c_str(), &end);
            if (end == nullptr || *end != '\0') return fail("bad number");
            out = Value::number(lit);
        } else {
            return fail("unexpected character");
        }
        depth--;
        return true;
    }
};
}  // namespace detail

/// The value as compact JSON: records as objects, lists as arrays, absent/Void as null, non-finite floats as strings,
/// char16 as a one-character string, a custom leaf's bytes as hex.
inline std::string to_json(const Value& v) {
    std::string out;
    detail::write_json(v, out);
    return out;
}

/// Parses JSON into a value (numbers keep their literal, so a decimal field gets it exactly). Whether it fits a type is
/// checked when it is encoded.
inline bool parse_json(const std::string& json, Value& out, std::string* error = nullptr) {
    detail::JsonParser p{json, 0, {}, 0};
    if (!p.value(out)) {
        if (error) *error = p.error;
        return false;
    }
    p.ws();
    if (p.i != json.size()) {
        if (error) *error = "text after the value";
        return false;
    }
    return true;
}

}  // namespace dyn
}  // namespace evn
