using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using ServerDocumentationTool.Common;

namespace ServerDocumentationTool
{
    /// <summary>
    /// Settings in %ProgramData%\ServerDocumentationTool\appsettings.json. Created with the defaults on first start; an
    /// update replaces only the exe, so the settings stay. Missing or invalid values fall back to the defaults.
    /// </summary>
    internal sealed class Settings
    {
        internal static readonly Dictionary<string, object> Defaults = N.Node(
            "DefaultOutputRoot", @"C:\ServerDocumentation",
            "DefaultClientName", "Client",
            "ParallelServers", 4L,
            "ServerTimeLimitMinutes", 30L,
            "ConnectTimeoutSec", 30L,
            "WmiQueryTimeoutSec", 180L,
            "SectionTimeoutSec", 300L,
            "GroupMemberDisplayCap", 15L,
            "ApplicationDisplayCap", 200L,
            "IncludeWorkstations", false,
            "ServerOperatingSystemFilter", "Windows Server",
            "DiscoveryStaleDays", 90L,
            "UpdateRepository", "bobsdomain/ServerDocumentationTool",
            "CheckForUpdatesOnStart", true);

        private readonly Dictionary<string, object> values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        public string Path { get; private set; }
        public string LoadError { get; private set; }

        internal static string DefaultPath { get { return System.IO.Path.Combine(AppInfo.DataDirectory, "appsettings.json"); } }

        internal static Settings Load(string path = null)
        {
            var s = new Settings { Path = path ?? DefaultPath };
            foreach (var kv in Defaults) s.values[kv.Key] = kv.Value;
            try
            {
                if (File.Exists(s.Path))
                {
                    var d = Json.Parse(File.ReadAllText(s.Path)) as Dictionary<string, object>;
                    if (d != null) foreach (var kv in d) s.values[kv.Key] = kv.Value;
                }
                else
                {
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(s.Path));
                    File.WriteAllText(s.Path, Json.Serialize(Defaults, true) + Environment.NewLine, new UTF8Encoding(false));
                }
            }
            catch (Exception ex)
            {
                s.LoadError = ex.Message;
            }
            return s;
        }

        public string Str(string key)
        {
            object v; string d = N.S(Defaults[key]);
            if (values.TryGetValue(key, out v) && v != null)
            {
                string x = N.S(v);
                if (!string.IsNullOrWhiteSpace(x)) return x;
            }
            return d;
        }

        public int Int(string key, int min, int max)
        {
            object v; int n;
            int d = (int)(long)Defaults[key];
            if (values.TryGetValue(key, out v) && v != null && int.TryParse(N.S(v), NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                return n < min ? min : n > max ? max : n;
            return d;
        }

        public bool Bool(string key)
        {
            object v; bool b;
            if (values.TryGetValue(key, out v) && v != null)
            {
                if (v is bool) return (bool)v;
                if (bool.TryParse(N.S(v), out b)) return b;
            }
            return (bool)Defaults[key];
        }
    }
}
