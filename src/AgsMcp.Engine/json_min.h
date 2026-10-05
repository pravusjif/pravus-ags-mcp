//=============================================================================
// Minimal JSON reader/writer for the agsmcp engine plugin.
//
// The editor plugin (AGS.Plugin.Mcp.dll) is the only client; it speaks a tiny,
// fully-controlled line protocol, so a small self-contained JSON implementation
// is enough and avoids any external dependency (nlohmann/json is not vendored).
// Supports objects, arrays, strings, numbers (int/double), booleans and null.
//=============================================================================
#pragma once

#include <string>
#include <vector>
#include <utility>
#include <cstdint>
#include <cstdio>

namespace jm {

enum class Type { Null, Bool, Int, Double, Str, Arr, Obj };

struct Value {
    Type type = Type::Null;
    bool b = false;
    int64_t i = 0;
    double d = 0.0;
    std::string s;
    std::vector<Value> arr;
    std::vector<std::pair<std::string, Value> > obj;

    static Value MkInt(int64_t v) { Value x; x.type = Type::Int; x.i = v; return x; }
    static Value MkDouble(double v) { Value x; x.type = Type::Double; x.d = v; return x; }
    static Value MkBool(bool v) { Value x; x.type = Type::Bool; x.b = v; return x; }
    static Value MkStr(const std::string& v) { Value x; x.type = Type::Str; x.s = v; return x; }
    static Value MkArr() { Value x; x.type = Type::Arr; return x; }
    static Value MkObj() { Value x; x.type = Type::Obj; return x; }

    // Object helpers.
    void set(const std::string& k, const Value& v) { obj.push_back(std::make_pair(k, v)); }
    void set(const std::string& k, int64_t v) { set(k, MkInt(v)); }
    void set(const std::string& k, int v) { set(k, MkInt((int64_t)v)); }
    void set(const std::string& k, bool v) { set(k, MkBool(v)); }
    void set(const std::string& k, const std::string& v) { set(k, MkStr(v)); }
    void set(const std::string& k, const char* v) { set(k, MkStr(std::string(v ? v : ""))); }

    const Value* find(const std::string& k) const {
        for (size_t n = 0; n < obj.size(); ++n)
            if (obj[n].first == k) return &obj[n].second;
        return 0;
    }

    int64_t as_int(int64_t def = 0) const {
        if (type == Type::Int) return i;
        if (type == Type::Double) return (int64_t)d;
        if (type == Type::Bool) return b ? 1 : 0;
        return def;
    }
    bool as_bool(bool def = false) const {
        if (type == Type::Bool) return b;
        if (type == Type::Int) return i != 0;
        return def;
    }
    std::string as_str(const std::string& def = std::string()) const {
        return type == Type::Str ? s : def;
    }
};

// ----------------------------------------------------------------- writing

inline void dump_string(const std::string& s, std::string& out) {
    out.push_back('"');
    for (size_t n = 0; n < s.size(); ++n) {
        unsigned char c = (unsigned char)s[n];
        switch (c) {
            case '"':  out += "\\\""; break;
            case '\\': out += "\\\\"; break;
            case '\b': out += "\\b"; break;
            case '\f': out += "\\f"; break;
            case '\n': out += "\\n"; break;
            case '\r': out += "\\r"; break;
            case '\t': out += "\\t"; break;
            default:
                if (c < 0x20) {
                    char buf[8];
                    std::snprintf(buf, sizeof(buf), "\\u%04x", (int)c);
                    out += buf;
                } else {
                    out.push_back((char)c);
                }
        }
    }
    out.push_back('"');
}

inline void dump(const Value& v, std::string& out) {
    switch (v.type) {
        case Type::Null: out += "null"; break;
        case Type::Bool: out += v.b ? "true" : "false"; break;
        case Type::Int: {
            char buf[32];
            std::snprintf(buf, sizeof(buf), "%lld", (long long)v.i);
            out += buf;
            break;
        }
        case Type::Double: {
            char buf[64];
            std::snprintf(buf, sizeof(buf), "%.10g", v.d);
            out += buf;
            break;
        }
        case Type::Str: dump_string(v.s, out); break;
        case Type::Arr: {
            out.push_back('[');
            for (size_t n = 0; n < v.arr.size(); ++n) {
                if (n) out.push_back(',');
                dump(v.arr[n], out);
            }
            out.push_back(']');
            break;
        }
        case Type::Obj: {
            out.push_back('{');
            for (size_t n = 0; n < v.obj.size(); ++n) {
                if (n) out.push_back(',');
                dump_string(v.obj[n].first, out);
                out.push_back(':');
                dump(v.obj[n].second, out);
            }
            out.push_back('}');
            break;
        }
    }
}

inline std::string dump(const Value& v) {
    std::string out;
    dump(v, out);
    return out;
}

// ----------------------------------------------------------------- parsing

struct Parser {
    const char* p;
    const char* end;

    explicit Parser(const std::string& text) : p(text.c_str()), end(text.c_str() + text.size()) {}

    void skip_ws() {
        while (p < end && (*p == ' ' || *p == '\t' || *p == '\r' || *p == '\n')) ++p;
    }

    bool parse(Value& out) {
        skip_ws();
        if (p >= end) return false;
        char c = *p;
        if (c == '{') return parse_obj(out);
        if (c == '[') return parse_arr(out);
        if (c == '"') return parse_str(out);
        if (c == 't' || c == 'f') return parse_bool(out);
        if (c == 'n') return parse_null(out);
        return parse_num(out);
    }

    bool parse_obj(Value& out) {
        out = Value::MkObj();
        ++p; // '{'
        skip_ws();
        if (p < end && *p == '}') { ++p; return true; }
        for (;;) {
            skip_ws();
            if (p >= end || *p != '"') return false;
            Value key;
            if (!parse_str(key)) return false;
            skip_ws();
            if (p >= end || *p != ':') return false;
            ++p;
            Value val;
            if (!parse(val)) return false;
            out.obj.push_back(std::make_pair(key.s, val));
            skip_ws();
            if (p >= end) return false;
            if (*p == ',') { ++p; continue; }
            if (*p == '}') { ++p; return true; }
            return false;
        }
    }

    bool parse_arr(Value& out) {
        out = Value::MkArr();
        ++p; // '['
        skip_ws();
        if (p < end && *p == ']') { ++p; return true; }
        for (;;) {
            Value val;
            if (!parse(val)) return false;
            out.arr.push_back(val);
            skip_ws();
            if (p >= end) return false;
            if (*p == ',') { ++p; continue; }
            if (*p == ']') { ++p; return true; }
            return false;
        }
    }

    bool parse_str(Value& out) {
        out = Value::MkStr(std::string());
        ++p; // opening quote
        std::string s;
        while (p < end) {
            char c = *p++;
            if (c == '"') { out.s = s; return true; }
            if (c == '\\') {
                if (p >= end) return false;
                char e = *p++;
                switch (e) {
                    case '"': s.push_back('"'); break;
                    case '\\': s.push_back('\\'); break;
                    case '/': s.push_back('/'); break;
                    case 'b': s.push_back('\b'); break;
                    case 'f': s.push_back('\f'); break;
                    case 'n': s.push_back('\n'); break;
                    case 'r': s.push_back('\r'); break;
                    case 't': s.push_back('\t'); break;
                    case 'u': {
                        if (end - p < 4) return false;
                        int code = 0;
                        for (int k = 0; k < 4; ++k) {
                            char h = *p++;
                            code <<= 4;
                            if (h >= '0' && h <= '9') code += h - '0';
                            else if (h >= 'a' && h <= 'f') code += h - 'a' + 10;
                            else if (h >= 'A' && h <= 'F') code += h - 'A' + 10;
                            else return false;
                        }
                        // Minimal UTF-8 encoding (BMP only; enough for our protocol).
                        if (code < 0x80) {
                            s.push_back((char)code);
                        } else if (code < 0x800) {
                            s.push_back((char)(0xC0 | (code >> 6)));
                            s.push_back((char)(0x80 | (code & 0x3F)));
                        } else {
                            s.push_back((char)(0xE0 | (code >> 12)));
                            s.push_back((char)(0x80 | ((code >> 6) & 0x3F)));
                            s.push_back((char)(0x80 | (code & 0x3F)));
                        }
                        break;
                    }
                    default: return false;
                }
            } else {
                s.push_back(c);
            }
        }
        return false;
    }

    bool parse_bool(Value& out) {
        if (end - p >= 4 && std::string(p, p + 4) == "true") { p += 4; out = Value::MkBool(true); return true; }
        if (end - p >= 5 && std::string(p, p + 5) == "false") { p += 5; out = Value::MkBool(false); return true; }
        return false;
    }

    bool parse_null(Value& out) {
        if (end - p >= 4 && std::string(p, p + 4) == "null") { p += 4; out = Value(); return true; }
        return false;
    }

    bool parse_num(Value& out) {
        const char* start = p;
        bool is_double = false;
        if (p < end && (*p == '-' || *p == '+')) ++p;
        while (p < end) {
            char c = *p;
            if (c >= '0' && c <= '9') { ++p; continue; }
            if (c == '.' || c == 'e' || c == 'E' || c == '+' || c == '-') { is_double = true; ++p; continue; }
            break;
        }
        if (p == start) return false;
        std::string num(start, p);
        if (is_double) out = Value::MkDouble(std::strtod(num.c_str(), 0));
        else out = Value::MkInt((int64_t)std::strtoll(num.c_str(), 0, 10));
        return true;
    }
};

inline bool parse(const std::string& text, Value& out) {
    Parser parser(text);
    return parser.parse(out);
}

} // namespace jm
