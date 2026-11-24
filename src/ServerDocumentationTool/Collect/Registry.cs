using System;
using System.Collections.Generic;
using System.Management;
using Microsoft.Win32;

namespace ServerDocumentationTool.Collect
{
    /// <summary>Read-only access to HKEY_LOCAL_MACHINE (64-bit view). Paths are relative to HKLM, e.g. SOFTWARE\Microsoft.</summary>
    internal interface IRegistryReader
    {
        string Method { get; }
        bool KeyExists(string path);
        string[] SubKeys(string path);    // empty when the key does not exist
        string[] ValueNames(string path); // empty when the key does not exist
        object Value(string path, string name); // string, int, long, string[], byte[] or null
        Dictionary<string, object> Values(string path); // all values of a key (case-insensitive names); empty when missing
    }

    internal static class RegistryAccess
    {
        /// <summary>
        /// Local server: the local registry. Remote: the Remote Registry service (fast, uses the alternate credentials for
        /// network access); if that is not available, the WMI registry provider (StdRegProv) over DCOM.
        /// </summary>
        internal static IRegistryReader Open(Target t, Wmi wmi, List<string> notes)
        {
            if (t.IsLocal) return new KeyReader(RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64), null, "Local registry");
            try
            {
                RegistryKey root = Hard.Run(() => NetworkAccess.Run(t.RemoteCredential, () =>
                {
                    RegistryKey k = RegistryKey.OpenRemoteBaseKey(RegistryHive.LocalMachine, t.ComputerName, RegistryView.Registry64);
                    using (RegistryKey sw = k.OpenSubKey("SOFTWARE")) { if (sw == null) throw new InvalidOperationException("SOFTWARE key not readable"); }
                    return k;
                }), Math.Max(15, t.ConnectTimeoutSec), "Connecting to the Remote Registry service");
                return new KeyReader(root, t.RemoteCredential, "Remote Registry service");
            }
            catch (Exception ex)
            {
                if (notes != null) notes.Add("Remote Registry service not available (" + ex.Message.Trim() + "); the WMI registry provider was used.");
                return new WmiReader(wmi, t.OperationTimeoutSec);
            }
        }
    }

    internal sealed class KeyReader : IRegistryReader
    {
        private readonly RegistryKey root;
        private readonly Credential cred;
        private readonly string method;

        internal KeyReader(RegistryKey root, Credential cred, string method)
        {
            this.root = root; this.cred = cred; this.method = method;
        }

        public string Method { get { return method; } }

        private T With<T>(string path, Func<RegistryKey, T> f, T missing)
        {
            return NetworkAccess.Run(cred, () =>
            {
                using (RegistryKey k = root.OpenSubKey(path, false))
                {
                    return k == null ? missing : f(k);
                }
            });
        }

        public bool KeyExists(string path) { return With(path, k => true, false); }
        public string[] SubKeys(string path) { return With(path, k => k.GetSubKeyNames(), new string[0]); }
        public string[] ValueNames(string path) { return With(path, k => k.GetValueNames(), new string[0]); }
        public object Value(string path, string name) { return With(path, k => k.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames), null); }

        public Dictionary<string, object> Values(string path)
        {
            var empty = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            return With(path, k =>
            {
                var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                foreach (string n in k.GetValueNames()) d[n] = k.GetValue(n, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
                return d;
            }, empty);
        }
    }

    /// <summary>StdRegProv (root\default) with the 64-bit provider architecture, so 64-bit keys are read on 64-bit servers.</summary>
    internal sealed class WmiReader : IRegistryReader
    {
        private const uint HKLM = 0x80000002;
        private readonly Wmi wmi;
        private readonly int timeout;

        internal WmiReader(Wmi wmi, int timeoutSec)
        {
            this.wmi = wmi; this.timeout = Math.Max(30, timeoutSec);
        }

        public string Method { get { return "WMI registry provider"; } }

        private ManagementBaseObject Call(string method, string path, string name)
        {
            var a = new Dictionary<string, object> { { "hDefKey", HKLM }, { "sSubKeyName", path } };
            if (name != null) a["sValueName"] = name;
            return wmi.InvokeStatic("root\\default", "StdRegProv", method, a, true, timeout);
        }

        private static uint Ret(ManagementBaseObject o)
        {
            return o == null ? 1u : Convert.ToUInt32(o["ReturnValue"]);
        }

        public bool KeyExists(string path)
        {
            return Ret(Call("EnumKey", path, null)) == 0;
        }

        public string[] SubKeys(string path)
        {
            ManagementBaseObject o = Call("EnumKey", path, null);
            if (Ret(o) != 0) return new string[0];
            return (o["sNames"] as string[]) ?? new string[0];
        }

        public string[] ValueNames(string path)
        {
            ManagementBaseObject o = Call("EnumValues", path, null);
            if (Ret(o) != 0) return new string[0];
            return (o["sNames"] as string[]) ?? new string[0];
        }

        public object Value(string path, string name)
        {
            object v;
            return Values(path).TryGetValue(name ?? string.Empty, out v) ? v : null;
        }

        public Dictionary<string, object> Values(string path)
        {
            var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            ManagementBaseObject e = Call("EnumValues", path, null);
            if (Ret(e) != 0) return d;
            var names = (e["sNames"] as string[]) ?? new string[0];
            var types = new List<int>();
            var raw = e["Types"] as System.Collections.IEnumerable;
            if (raw != null) foreach (object x in raw) types.Add(Convert.ToInt32(x));
            for (int i = 0; i < names.Length && i < types.Count; i++) d[names[i] ?? string.Empty] = Get(path, names[i], types[i]);
            return d;
        }

        private object Get(string path, string name, int type)
        {
            switch (type)
            {
                case 1: { var o = Call("GetStringValue", path, name); return Ret(o) == 0 ? o["sValue"] : null; }
                case 2: { var o = Call("GetExpandedStringValue", path, name); return Ret(o) == 0 ? o["sValue"] : null; }
                case 3: { var o = Call("GetBinaryValue", path, name); return Ret(o) == 0 ? o["uValue"] : null; }
                case 4: { var o = Call("GetDWORDValue", path, name); return Ret(o) == 0 && o["uValue"] != null ? (object)unchecked((int)Convert.ToUInt32(o["uValue"])) : null; }
                case 7: { var o = Call("GetMultiStringValue", path, name); return Ret(o) == 0 ? o["sValue"] : null; }
                case 11: { var o = Call("GetQWORDValue", path, name); return Ret(o) == 0 && o["uValue"] != null ? (object)unchecked((long)Convert.ToUInt64(o["uValue"])) : null; }
                default: return null;
            }
        }
    }
}
