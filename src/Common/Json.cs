using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Text;

namespace HPControl
{
    /// <summary>Small dependency-free JSON reader/writer + reflection mapper (public fields and properties).</summary>
    public static class Json
    {
        // ---------------- writer ----------------
        public static string Serialize(object o, bool pretty = false)
        {
            var sb = new StringBuilder(256);
            Write(sb, o, pretty, 0);
            return sb.ToString();
        }

        static void Indent(StringBuilder sb, bool pretty, int level)
        {
            if (!pretty) return;
            sb.Append('\n');
            sb.Append(' ', level * 2);
        }

        static void Write(StringBuilder sb, object o, bool pretty, int lvl)
        {
            if (o == null) { sb.Append("null"); return; }
            switch (o)
            {
                case string s: WriteString(sb, s); return;
                case bool b: sb.Append(b ? "true" : "false"); return;
                case double d: WriteNum(sb, d); return;
                case float f: WriteNum(sb, f); return;
                case int i: sb.Append(i.ToString(CultureInfo.InvariantCulture)); return;
                case long l: sb.Append(l.ToString(CultureInfo.InvariantCulture)); return;
                case uint ui: sb.Append(ui.ToString(CultureInfo.InvariantCulture)); return;
                case byte by: sb.Append(by.ToString(CultureInfo.InvariantCulture)); return;
                case short sh: sb.Append(sh.ToString(CultureInfo.InvariantCulture)); return;
                case decimal de: sb.Append(de.ToString(CultureInfo.InvariantCulture)); return;
                case ushort us: sb.Append(us.ToString(CultureInfo.InvariantCulture)); return;
                case ulong ul: sb.Append(ul.ToString(CultureInfo.InvariantCulture)); return;
                case sbyte sb2: sb.Append(sb2.ToString(CultureInfo.InvariantCulture)); return;
                case char ch: WriteString(sb, ch.ToString()); return;
                case DateTime dt: WriteString(sb, dt.ToString("o", CultureInfo.InvariantCulture)); return;
                case Enum e: WriteString(sb, e.ToString()); return;
                case Guid g: WriteString(sb, g.ToString()); return;
            }
            if (o is IDictionary dict)
            {
                sb.Append('{'); bool first = true;
                foreach (DictionaryEntry kv in dict)
                {
                    if (!first) sb.Append(','); first = false;
                    Indent(sb, pretty, lvl + 1);
                    WriteString(sb, Convert.ToString(kv.Key, CultureInfo.InvariantCulture)); sb.Append(pretty ? ": " : ":");
                    Write(sb, kv.Value, pretty, lvl + 1);
                }
                if (!first) Indent(sb, pretty, lvl);
                sb.Append('}'); return;
            }
            if (o is byte[] bytes)
            {
                sb.Append('['); for (int i = 0; i < bytes.Length; i++) { if (i > 0) sb.Append(','); sb.Append(bytes[i]); }
                sb.Append(']'); return;
            }
            if (o is IEnumerable en)
            {
                sb.Append('['); bool first = true;
                foreach (var item in en)
                {
                    if (!first) sb.Append(','); first = false;
                    if (pretty && !(item is double || item is int || item is float)) Indent(sb, pretty, lvl + 1);
                    Write(sb, item, pretty, lvl + 1);
                }
                sb.Append(']'); return;
            }
            // object: public fields + readable properties
            sb.Append('{'); bool f1 = true;
            var t = o.GetType();
            foreach (var fi in t.GetFields(BindingFlags.Public | BindingFlags.Instance))
            {
                if (fi.IsDefined(typeof(NonSerializedAttribute), false)) continue;
                if (!f1) sb.Append(','); f1 = false;
                Indent(sb, pretty, lvl + 1);
                WriteString(sb, fi.Name); sb.Append(pretty ? ": " : ":");
                Write(sb, fi.GetValue(o), pretty, lvl + 1);
            }
            foreach (var pi in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!pi.CanRead || pi.GetIndexParameters().Length > 0) continue;
                if (!f1) sb.Append(','); f1 = false;
                Indent(sb, pretty, lvl + 1);
                WriteString(sb, pi.Name); sb.Append(pretty ? ": " : ":");
                Write(sb, pi.GetValue(o, null), pretty, lvl + 1);
            }
            if (!f1) Indent(sb, pretty, lvl);
            sb.Append('}');
        }

        static void WriteNum(StringBuilder sb, double d)
        {
            if (double.IsNaN(d) || double.IsInfinity(d)) { sb.Append("null"); return; }
            sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
        }

        static void WriteString(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.AppendFormat("\\u{0:x4}", (int)c); else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------------- reader ----------------
        public static object Parse(string s)
        {
            int i = 0;
            var v = ParseValue(s, ref i);
            return v;
        }

        static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        static object ParseValue(string s, ref int i)
        {
            Ws(s, ref i);
            if (i >= s.Length) throw new FormatException("Unexpected end of JSON");
            char c = s[i];
            if (c == '{')
            {
                var d = new Dictionary<string, object>(StringComparer.Ordinal); i++;
                Ws(s, ref i);
                if (s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(s, ref i); string k = ParseString(s, ref i); Ws(s, ref i);
                    if (s[i] != ':') throw new FormatException("Expected ':' at " + i); i++;
                    d[k] = ParseValue(s, ref i); Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == '}') { i++; return d; }
                    throw new FormatException("Expected ',' or '}' at " + i);
                }
            }
            if (c == '[')
            {
                var l = new List<object>(); i++; Ws(s, ref i);
                if (s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(ParseValue(s, ref i)); Ws(s, ref i);
                    if (s[i] == ',') { i++; continue; }
                    if (s[i] == ']') { i++; return l; }
                    throw new FormatException("Expected ',' or ']' at " + i);
                }
            }
            if (c == '"') return ParseString(s, ref i);
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "true", 0, 4) == 0) { i += 4; return true; }
            if (s.Length - i >= 5 && string.CompareOrdinal(s, i, "false", 0, 5) == 0) { i += 5; return false; }
            if (s.Length - i >= 4 && string.CompareOrdinal(s, i, "null", 0, 4) == 0) { i += 4; return null; }
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (st == i) throw new FormatException("Bad JSON token at " + i);
            return double.Parse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static string ParseString(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("Expected string at " + i);
            i++; var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c == '\\')
                {
                    char e = s[i++];
                    switch (e)
                    {
                        case 'n': sb.Append('\n'); break;
                        case 'r': sb.Append('\r'); break;
                        case 't': sb.Append('\t'); break;
                        case 'b': sb.Append('\b'); break;
                        case 'f': sb.Append('\f'); break;
                        case 'u': sb.Append((char)Convert.ToInt32(s.Substring(i, 4), 16)); i += 4; break;
                        default: sb.Append(e); break;
                    }
                }
                else sb.Append(c);
            }
            throw new FormatException("Unterminated string");
        }

        // ---------------- mapper ----------------
        public static T Deserialize<T>(string s) where T : new() => (T)ToType(Parse(s), typeof(T), null);

        public static T ConvertTo<T>(object parsed) => (T)ToType(parsed, typeof(T), null);

        /// <summary>Copies values from parsed JSON onto an existing object (keeps defaults for missing members).</summary>
        public static void Populate(object target, object parsed)
        {
            if (!(parsed is Dictionary<string, object> d) || target == null) return;
            var t = target.GetType();
            foreach (var kv in d)
            {
                var fi = t.GetField(kv.Key, BindingFlags.Public | BindingFlags.Instance);
                if (fi != null)
                {
                    try { fi.SetValue(target, ToType(kv.Value, fi.FieldType, fi.GetValue(target))); } catch { }
                    continue;
                }
                var pi = t.GetProperty(kv.Key, BindingFlags.Public | BindingFlags.Instance);
                if (pi != null && pi.CanWrite)
                {
                    try { pi.SetValue(target, ToType(kv.Value, pi.PropertyType, pi.CanRead ? pi.GetValue(target, null) : null), null); } catch { }
                }
            }
        }

        public static object ToType(object v, Type t, object existing)
        {
            if (v == null)
            {
                if (t == typeof(double)) return double.NaN;
                if (t == typeof(float)) return float.NaN;
                return t.IsValueType ? Activator.CreateInstance(t) : null;
            }
            if (t == typeof(object)) return v;
            if (t == typeof(string)) return v is string ? v : Convert.ToString(v, CultureInfo.InvariantCulture);
            if (t == typeof(double)) return Convert.ToDouble(v, CultureInfo.InvariantCulture);
            if (t == typeof(float)) return Convert.ToSingle(v, CultureInfo.InvariantCulture);
            if (t == typeof(int)) return Convert.ToInt32(v, CultureInfo.InvariantCulture);
            if (t == typeof(long)) return Convert.ToInt64(v, CultureInfo.InvariantCulture);
            if (t == typeof(uint)) return Convert.ToUInt32(v, CultureInfo.InvariantCulture);
            if (t == typeof(byte)) return Convert.ToByte(v, CultureInfo.InvariantCulture);
            if (t == typeof(bool)) return v is bool b ? b : Convert.ToBoolean(v, CultureInfo.InvariantCulture);
            if (t == typeof(DateTime)) return DateTime.Parse(Convert.ToString(v, CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            if (t.IsEnum) return Enum.Parse(t, Convert.ToString(v, CultureInfo.InvariantCulture), true);
            if (t.IsArray)
            {
                var et = t.GetElementType();
                var src = v as List<object> ?? new List<object>();
                var arr = Array.CreateInstance(et, src.Count);
                for (int i = 0; i < src.Count; i++) arr.SetValue(ToType(src[i], et, null), i);
                return arr;
            }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(List<>))
            {
                var et = t.GetGenericArguments()[0];
                var list = (IList)Activator.CreateInstance(t);
                if (v is List<object> src) foreach (var it in src) list.Add(ToType(it, et, null));
                return list;
            }
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Dictionary<,>))
            {
                var vt = t.GetGenericArguments()[1];
                var dict = (IDictionary)Activator.CreateInstance(t);
                if (v is Dictionary<string, object> src) foreach (var kv in src) dict[kv.Key] = ToType(kv.Value, vt, null);
                return dict;
            }
            var obj = existing ?? Activator.CreateInstance(t);
            Populate(obj, v);
            return obj;
        }

        // ---------------- helpers for dynamic access ----------------
        public static string Str(this Dictionary<string, object> d, string k, string def = null)
            => d != null && d.TryGetValue(k, out var v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : def;
        public static double Num(this Dictionary<string, object> d, string k, double def = 0)
            => d != null && d.TryGetValue(k, out var v) && v is IConvertible ? Convert.ToDouble(v, CultureInfo.InvariantCulture) : def;
        public static bool Bool(this Dictionary<string, object> d, string k, bool def = false)
            => d != null && d.TryGetValue(k, out var v) && v is bool b ? b : def;
        public static Dictionary<string, object> Obj(this Dictionary<string, object> d, string k)
            => d != null && d.TryGetValue(k, out var v) ? v as Dictionary<string, object> : null;
        public static List<object> Arr(this Dictionary<string, object> d, string k)
            => d != null && d.TryGetValue(k, out var v) ? v as List<object> : null;
    }
}
