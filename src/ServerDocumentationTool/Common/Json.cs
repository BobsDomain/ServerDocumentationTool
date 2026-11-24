using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ServerDocumentationTool.Common
{
    /// <summary>
    /// Small JSON reader/writer for the RawData files and appsettings.json. Objects become Dictionary&lt;string, object&gt;,
    /// arrays List&lt;object&gt;, whole numbers long, other numbers double. Strings stay strings (no date conversion), so
    /// "/Date(ms)/" values written by Windows PowerShell are kept as they are and shown by N.DateText.
    /// </summary>
    internal static class Json
    {
        // ------------------------------------------------------------------ read

        internal static object Parse(string text)
        {
            if (text == null) throw new ArgumentNullException("text");
            int i = 0;
            if (text.Length > 0 && text[0] == (char)0xFEFF) i = 1;
            object v = ReadValue(text, ref i);
            SkipWs(text, ref i);
            if (i != text.Length) throw Error(text, i, "unexpected text after the JSON value");
            return v;
        }

        private static FormatException Error(string t, int i, string msg)
        {
            return new FormatException("Invalid JSON at position " + i + ": " + msg + ".");
        }

        private static void SkipWs(string t, ref int i)
        {
            while (i < t.Length && (t[i] == ' ' || t[i] == '\t' || t[i] == '\r' || t[i] == '\n')) i++;
        }

        private static object ReadValue(string t, ref int i)
        {
            SkipWs(t, ref i);
            if (i >= t.Length) throw Error(t, i, "unexpected end");
            char c = t[i];
            switch (c)
            {
                case '{': return ReadObject(t, ref i);
                case '[': return ReadArray(t, ref i);
                case '"': return ReadString(t, ref i);
                case 't': Expect(t, ref i, "true"); return true;
                case 'f': Expect(t, ref i, "false"); return false;
                case 'n': Expect(t, ref i, "null"); return null;
                default:
                    if (c == '-' || (c >= '0' && c <= '9')) return ReadNumber(t, ref i);
                    throw Error(t, i, "unexpected character '" + c + "'");
            }
        }

        private static void Expect(string t, ref int i, string word)
        {
            if (string.CompareOrdinal(t, i, word, 0, word.Length) != 0) throw Error(t, i, "expected " + word);
            i += word.Length;
        }

        private static Dictionary<string, object> ReadObject(string t, ref int i)
        {
            var d = N.New();
            i++;
            SkipWs(t, ref i);
            if (i < t.Length && t[i] == '}') { i++; return d; }
            while (true)
            {
                SkipWs(t, ref i);
                if (i >= t.Length || t[i] != '"') throw Error(t, i, "expected a property name");
                string key = ReadString(t, ref i);
                SkipWs(t, ref i);
                if (i >= t.Length || t[i] != ':') throw Error(t, i, "expected ':'");
                i++;
                d[key] = ReadValue(t, ref i);
                SkipWs(t, ref i);
                if (i >= t.Length) throw Error(t, i, "unexpected end in object");
                if (t[i] == ',') { i++; continue; }
                if (t[i] == '}') { i++; return d; }
                throw Error(t, i, "expected ',' or '}'");
            }
        }

        private static List<object> ReadArray(string t, ref int i)
        {
            var l = new List<object>();
            i++;
            SkipWs(t, ref i);
            if (i < t.Length && t[i] == ']') { i++; return l; }
            while (true)
            {
                l.Add(ReadValue(t, ref i));
                SkipWs(t, ref i);
                if (i >= t.Length) throw Error(t, i, "unexpected end in array");
                if (t[i] == ',') { i++; continue; }
                if (t[i] == ']') { i++; return l; }
                throw Error(t, i, "expected ',' or ']'");
            }
        }

        private static string ReadString(string t, ref int i)
        {
            var sb = new StringBuilder();
            i++;
            while (i < t.Length)
            {
                char c = t[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= t.Length) break;
                char e = t[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        if (i + 4 > t.Length) throw Error(t, i, "bad \\u escape");
                        sb.Append((char)int.Parse(t.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture));
                        i += 4;
                        break;
                    default: throw Error(t, i, "bad escape");
                }
            }
            throw Error(t, i, "unterminated string");
        }

        private static object ReadNumber(string t, ref int i)
        {
            int start = i;
            if (t[i] == '-') i++;
            while (i < t.Length && char.IsDigit(t[i])) i++;
            bool frac = false;
            if (i < t.Length && t[i] == '.') { frac = true; i++; while (i < t.Length && char.IsDigit(t[i])) i++; }
            if (i < t.Length && (t[i] == 'e' || t[i] == 'E'))
            {
                frac = true; i++;
                if (i < t.Length && (t[i] == '+' || t[i] == '-')) i++;
                while (i < t.Length && char.IsDigit(t[i])) i++;
            }
            string s = t.Substring(start, i - start);
            long l;
            if (!frac && long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out l)) return l;
            return double.Parse(s, NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        // ------------------------------------------------------------------ write

        internal static string Serialize(object value, bool indent)
        {
            var sb = new StringBuilder();
            Write(sb, value, indent, 0);
            return sb.ToString();
        }

        private static void NewLine(StringBuilder sb, bool indent, int depth)
        {
            if (!indent) return;
            sb.Append("\r\n").Append(' ', depth * 2);
        }

        private static void Write(StringBuilder sb, object v, bool indent, int depth)
        {
            if (depth > 64) { sb.Append("null"); return; }
            if (v == null) { sb.Append("null"); return; }
            var s = v as string;
            if (s != null) { WriteString(sb, s); return; }
            if (v is bool) { sb.Append((bool)v ? "true" : "false"); return; }
            if (v is DateTime)
            {
                // Same as Windows PowerShell ConvertTo-Json, so old and new RawData files read the same way.
                long ms = (long)(((DateTime)v).ToUniversalTime() - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalMilliseconds;
                WriteString(sb, "/Date(" + ms.ToString(CultureInfo.InvariantCulture) + ")/");
                return;
            }
            if (v is double || v is float)
            {
                double d = Convert.ToDouble(v, CultureInfo.InvariantCulture);
                if (double.IsNaN(d) || double.IsInfinity(d)) sb.Append("null");
                else sb.Append(d.ToString("R", CultureInfo.InvariantCulture));
                return;
            }
            if (v is int || v is long || v is short || v is byte || v is uint || v is ulong || v is ushort || v is sbyte || v is decimal)
            {
                sb.Append(Convert.ToString(v, CultureInfo.InvariantCulture));
                return;
            }
            if (v is Enum) { WriteString(sb, v.ToString()); return; }
            var gd = v as IDictionary<string, object>;
            if (gd != null)
            {
                sb.Append('{');
                bool first = true;
                foreach (var kv in gd)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(sb, indent, depth + 1);
                    WriteString(sb, kv.Key);
                    sb.Append(indent ? ": " : ":");
                    Write(sb, kv.Value, indent, depth + 1);
                }
                if (!first) NewLine(sb, indent, depth);
                sb.Append('}');
                return;
            }
            var e = v as IEnumerable;
            if (e != null)
            {
                sb.Append('[');
                bool first = true;
                foreach (object o in e)
                {
                    if (!first) sb.Append(',');
                    first = false;
                    NewLine(sb, indent, depth + 1);
                    Write(sb, o, indent, depth + 1);
                }
                if (!first) NewLine(sb, indent, depth);
                sb.Append(']');
                return;
            }
            WriteString(sb, N.S(v));
        }

        private static void WriteString(StringBuilder sb, string s)
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
                        if (c < 0x20 || c == (char)0x2028 || c == (char)0x2029) sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                        else sb.Append(c);
                        break;
                }
            }
            sb.Append('"');
        }
    }
}
