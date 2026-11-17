using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Web.Script.Serialization;

namespace IABB.App
{
    /// <summary>Reads Engine\Config\appsettings.json (shared with the PowerShell engine). Missing keys fall back to defaults.</summary>
    internal sealed class AppConfig
    {
        private readonly Dictionary<string, object> values;

        private AppConfig(Dictionary<string, object> values, string path)
        {
            this.values = values ?? new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            Path = path;
        }

        public string Path { get; private set; }

        public static AppConfig Load(string engineRoot)
        {
            string path = System.IO.Path.Combine(engineRoot, "Config", "appsettings.json");
            var dict = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            try
            {
                if (File.Exists(path))
                {
                    var parsed = new JavaScriptSerializer().DeserializeObject(File.ReadAllText(path)) as Dictionary<string, object>;
                    if (parsed != null)
                    {
                        foreach (var kv in parsed) dict[kv.Key] = kv.Value;
                    }
                }
            }
            catch
            {
                // An unreadable settings file must not stop the app; defaults are used.
            }
            return new AppConfig(dict, path);
        }

        public string GetString(string key, string fallback)
        {
            object v;
            if (values.TryGetValue(key, out v) && v != null)
            {
                string s = Convert.ToString(v, CultureInfo.InvariantCulture);
                if (!string.IsNullOrWhiteSpace(s)) return s;
            }
            return fallback;
        }

        public int GetInt(string key, int fallback, int min, int max)
        {
            object v;
            if (values.TryGetValue(key, out v) && v != null)
            {
                int n;
                if (int.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                {
                    if (n < min) return min;
                    if (n > max) return max;
                    return n;
                }
            }
            return fallback;
        }

        public bool GetBool(string key, bool fallback)
        {
            object v;
            if (values.TryGetValue(key, out v) && v != null)
            {
                if (v is bool) return (bool)v;
                bool b;
                if (bool.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out b)) return b;
            }
            return fallback;
        }
    }
}
