using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

// Hand-rolled JSON both ways; no serializer dependency in a mod this small.
// Writer: the ClioCouchRW writer, plus nested objects/arrays. Reader: a strict little recursive
// parser producing Dictionary<string, object> / List<object> / string / double / bool / null.

namespace ClioLabRW
{
    internal sealed class Json
    {
        private readonly StringBuilder _sb = new StringBuilder(256);
        private bool _first = true;

        public Json() { _sb.Append('{'); }

        private Json Key(string k)
        {
            if (!_first) _sb.Append(',');
            _first = false;
            AppendQuoted(_sb, k);
            _sb.Append(':');
            return this;
        }

        public Json Str(string k, string v)
        {
            Key(k);
            if (v == null) _sb.Append("null"); else AppendQuoted(_sb, v);
            return this;
        }

        public Json Num(string k, long v) { Key(k); _sb.Append(v.ToString(CultureInfo.InvariantCulture)); return this; }

        public Json Num(string k, float v, int decimals = 2)
        {
            Key(k);
            if (float.IsNaN(v) || float.IsInfinity(v)) _sb.Append("null");
            else _sb.Append(Math.Round(v, decimals).ToString(CultureInfo.InvariantCulture));
            return this;
        }

        public Json Bool(string k, bool v) { Key(k); _sb.Append(v ? "true" : "false"); return this; }

        /// <summary>Raw JSON value (an array or object built elsewhere). Null writes null.</summary>
        public Json Raw(string k, string rawJson) { Key(k); _sb.Append(rawJson ?? "null"); return this; }

        public Json Strs(string k, IEnumerable<string> values)
        {
            List<string> items = new List<string>();
            if (values != null) foreach (string v in values) items.Add(Quote(v));
            return Raw(k, Array(items));
        }

        public override string ToString() => _sb.ToString() + "}";

        /// <summary>Always emits brackets, so an empty list is [] and never {} or null.</summary>
        public static string Array(List<string> rawItems)
        {
            if (rawItems == null || rawItems.Count == 0) return "[]";
            return "[" + string.Join(",", rawItems) + "]";
        }

        public static string Quote(string s)
        {
            if (s == null) return "null";
            StringBuilder sb = new StringBuilder(s.Length + 2);
            AppendQuoted(sb, s);
            return sb.ToString();
        }

        private static void AppendQuoted(StringBuilder sb, string s)
        {
            sb.Append('"');
            foreach (char c in s)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }

        // ---------------------------------------------------------------- reader

        public static object Parse(string text)
        {
            int i = 0;
            object v = ParseValue(text, ref i);
            SkipWs(text, ref i);
            if (i != text.Length) throw new FormatException("trailing characters at " + i);
            return v;
        }

        private static void SkipWs(string s, ref int i)
        {
            while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
            // Tolerate a UTF-8 BOM that survived decoding as U+FEFF.
            while (i < s.Length && s[i] == '﻿') i++;
        }

        private static object ParseValue(string s, ref int i)
        {
            SkipWs(s, ref i);
            if (i >= s.Length) throw new FormatException("unexpected end");
            char c = s[i];
            if (c == '{') return ParseObject(s, ref i);
            if (c == '[') return ParseArray(s, ref i);
            if (c == '"') return ParseString(s, ref i);
            if (c == 't' && Match(s, ref i, "true")) return true;
            if (c == 'f' && Match(s, ref i, "false")) return false;
            if (c == 'n' && Match(s, ref i, "null")) return null;
            return ParseNumber(s, ref i);
        }

        private static bool Match(string s, ref int i, string word)
        {
            if (string.CompareOrdinal(s, i, word, 0, word.Length) != 0) throw new FormatException("bad literal at " + i);
            i += word.Length;
            return true;
        }

        private static Dictionary<string, object> ParseObject(string s, ref int i)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            i++; // {
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == '}') { i++; return d; }
            while (true)
            {
                SkipWs(s, ref i);
                string k = ParseString(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length || s[i] != ':') throw new FormatException("expected ':' at " + i);
                i++;
                d[k] = ParseValue(s, ref i);
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated object");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == '}') { i++; return d; }
                throw new FormatException("expected ',' or '}' at " + i);
            }
        }

        private static List<object> ParseArray(string s, ref int i)
        {
            List<object> l = new List<object>();
            i++; // [
            SkipWs(s, ref i);
            if (i < s.Length && s[i] == ']') { i++; return l; }
            while (true)
            {
                l.Add(ParseValue(s, ref i));
                SkipWs(s, ref i);
                if (i >= s.Length) throw new FormatException("unterminated array");
                if (s[i] == ',') { i++; continue; }
                if (s[i] == ']') { i++; return l; }
                throw new FormatException("expected ',' or ']' at " + i);
            }
        }

        private static string ParseString(string s, ref int i)
        {
            if (s[i] != '"') throw new FormatException("expected string at " + i);
            i++;
            StringBuilder sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'u':
                        sb.Append((char)int.Parse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: sb.Append(e); break;
                }
            }
            throw new FormatException("unterminated string");
        }

        private static double ParseNumber(string s, ref int i)
        {
            int start = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            if (start == i) throw new FormatException("unexpected character '" + s[i] + "' at " + i);
            return double.Parse(s.Substring(start, i - start), NumberStyles.Float, CultureInfo.InvariantCulture);
        }
    }

    /// <summary>Typed accessors over a parsed command object; missing keys fall back to defaults.</summary>
    internal sealed class Args
    {
        private readonly Dictionary<string, object> _d;
        public Args(Dictionary<string, object> d) { _d = d ?? new Dictionary<string, object>(); }

        public bool Has(string k) => _d.ContainsKey(k) && _d[k] != null;
        public string Str(string k, string def = null) => _d.TryGetValue(k, out object v) && v != null ? Convert.ToString(v, CultureInfo.InvariantCulture) : def;
        public float Num(string k, float def = 0f) => _d.TryGetValue(k, out object v) && v is double d ? (float)d : def;
        public int Int(string k, int def = 0) => _d.TryGetValue(k, out object v) && v is double d ? (int)Math.Round(d) : def;
        public bool Bool(string k, bool def = false) => _d.TryGetValue(k, out object v) && v is bool b ? b : def;

        public List<object> List(string k)
        {
            if (!_d.TryGetValue(k, out object v) || v == null) return new List<object>();
            if (v is List<object> l) return l;
            return new List<object> { v };   // a lone value is a one-item list
        }

        public Dictionary<string, object> Dict(string k) =>
            _d.TryGetValue(k, out object v) && v is Dictionary<string, object> d ? d : new Dictionary<string, object>();

        public static Args Of(object o) => new Args(o as Dictionary<string, object>);
    }
}
