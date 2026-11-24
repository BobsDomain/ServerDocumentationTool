using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace ServerDocumentationTool.Common
{
    /// <summary>
    /// The collected data is a tree of Dictionary&lt;string, object&gt; nodes, lists and plain values. It has the same shape and
    /// field names as the RawData JSON written since v0.9.9, so old and new runs stay comparable and old JSON can be
    /// rendered again. These helpers read that tree the way the PowerShell engine did ([string] casts, truthiness).
    /// </summary>
    internal static class N
    {
        internal static Dictionary<string, object> New()
        {
            return new Dictionary<string, object>(StringComparer.Ordinal);
        }

        /// <summary>Value of a key (exact match first, then case-insensitive). Null when the node or key is missing.</summary>
        internal static object Get(object node, string key)
        {
            var d = node as IDictionary<string, object>;
            if (d != null)
            {
                object v;
                if (d.TryGetValue(key, out v)) return v;
                foreach (var kv in d)
                {
                    if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv.Value;
                }
                return null;
            }
            var hd = node as IDictionary;
            if (hd != null && hd.Contains(key)) return hd[key];
            return null;
        }

        internal static object Path(object node, params string[] keys)
        {
            object cur = node;
            foreach (string k in keys)
            {
                if (cur == null) return null;
                cur = Get(cur, k);
            }
            return cur;
        }

        /// <summary>PowerShell-style [string] conversion: null to "", invariant culture numbers, True/False.</summary>
        internal static string S(object value)
        {
            if (value == null) return string.Empty;
            var s = value as string;
            if (s != null) return s;
            if (value is bool) return (bool)value ? "True" : "False";
            if (value is DateTime) return ((DateTime)value).ToString("MM/dd/yyyy HH:mm:ss", CultureInfo.InvariantCulture);
            if (value is double) return ((double)value).ToString("R", CultureInfo.InvariantCulture);
            if (value is float) return ((float)value).ToString("R", CultureInfo.InvariantCulture);
            if (value is decimal) return ((decimal)value).ToString(CultureInfo.InvariantCulture);
            var f = value as IFormattable;
            if (f != null) return f.ToString(null, CultureInfo.InvariantCulture);
            return Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        }

        internal static string S(object node, string key) { return S(Get(node, key)); }

        /// <summary>Cell text: lists are joined with ", " (like the PowerShell table builder).</summary>
        internal static string Cell(object value)
        {
            if (value == null) return string.Empty;
            if (value is string) return (string)value;
            if (value is IDictionary) return value.GetType().FullName;
            var e = value as IEnumerable;
            if (e != null) return string.Join(", ", e.Cast<object>().Select(x => x == null ? string.Empty : S(x)));
            return S(value);
        }

        /// <summary>Items of a list value; a single non-list value becomes a one-item list; null becomes empty.</summary>
        internal static List<object> L(object value)
        {
            var list = new List<object>();
            if (value == null) return list;
            if (value is string || value is IDictionary) { list.Add(value); return list; }
            var e = value as IEnumerable;
            if (e == null) { list.Add(value); return list; }
            foreach (object o in e) list.Add(o);
            return list;
        }

        /// <summary>Non-null items of a list value.</summary>
        internal static List<object> Items(object value)
        {
            return L(value).Where(x => x != null).ToList();
        }

        internal static List<object> Items(object node, string key) { return Items(Get(node, key)); }

        /// <summary>PowerShell truthiness ([bool]$x).</summary>
        internal static bool Truthy(object value)
        {
            if (value == null) return false;
            if (value is bool) return (bool)value;
            var s = value as string;
            if (s != null) return s.Length > 0;
            if (value is int) return (int)value != 0;
            if (value is long) return (long)value != 0;
            if (value is double) return (double)value != 0;
            if (value is decimal) return (decimal)value != 0;
            if (value is uint) return (uint)value != 0;
            if (value is ulong) return (ulong)value != 0;
            var e = value as ICollection;
            if (e != null && !(value is IDictionary)) return e.Count > 0;
            return true;
        }

        internal static bool Truthy(object node, string key) { return Truthy(Get(node, key)); }

        /// <summary>Strict boolean ($x -eq $true): true only for a real true or the text "True".</summary>
        internal static bool IsTrue(object value)
        {
            if (value is bool) return (bool)value;
            var s = value as string;
            return s != null && string.Equals(s.Trim(), "True", StringComparison.OrdinalIgnoreCase);
        }

        internal static double? Dbl(object value)
        {
            if (value == null) return null;
            if (value is double) return (double)value;
            if (value is float) return (float)value;
            if (value is decimal) return (double)(decimal)value;
            if (value is int) return (int)value;
            if (value is long) return (long)value;
            if (value is uint) return (uint)value;
            if (value is ulong) return (ulong)value;
            string s = S(value).Trim();
            if (s.Length == 0) return null;
            double d;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            if (double.TryParse(s, NumberStyles.Float, CultureInfo.CurrentCulture, out d)) return d;
            return null;
        }

        /// <summary>[int]::TryParse of the [string] form; returns false when not an integer.</summary>
        internal static bool TryInt(object value, out int result)
        {
            return int.TryParse(S(value), NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        }

        /// <summary>[int] cast semantics used by sort/compare in the PowerShell engine (rounds doubles; null/"" = 0).</summary>
        internal static int ToInt(object value)
        {
            if (value == null) return 0;
            int i;
            if (TryInt(value, out i)) return i;
            double? d = Dbl(value);
            if (d.HasValue) return (int)Math.Round(d.Value, MidpointRounding.ToEven);
            return 0;
        }

        /// <summary>PowerShell -like (wildcards * and ?), case-insensitive.</summary>
        internal static bool Like(string value, string pattern)
        {
            string rx = "^" + Regex.Escape(pattern ?? string.Empty).Replace("\\*", ".*").Replace("\\?", ".") + "$";
            return Regex.IsMatch(value ?? string.Empty, rx, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        }

        /// <summary>Date text as the PowerShell engine printed it ([string] of a DateTime: MM/dd/yyyy HH:mm:ss, local time).
        /// RawData JSON written by Windows PowerShell stores dates as "/Date(ms)/".</summary>
        internal static string DateText(object value)
        {
            if (value == null) return string.Empty;
            if (value is DateTime) return S(value);
            string s = S(value);
            Match m = Regex.Match(s, @"^/Date\((-?\d+)([+-]\d{4})?\)/$");
            if (m.Success)
            {
                long ms = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
                DateTime dt = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms).ToLocalTime();
                return S(dt);
            }
            return s;
        }

        internal static Dictionary<string, object> Node(params object[] keyValues)
        {
            var d = New();
            for (int i = 0; i + 1 < keyValues.Length; i += 2) d[(string)keyValues[i]] = keyValues[i + 1];
            return d;
        }
    }
}
