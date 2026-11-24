using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Management;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace ServerDocumentationTool.Collect
{
    /// <summary>Read-only access to HKEY_LOCAL_MACHINE (64-bit view). Paths are relative to HKLM, e.g. SOFTWARE\Microsoft.</summary>
    internal interface IRegistryReader
    {
        string Method { get; }
        /// <summary>Subkey names; empty when the key does not exist. Throws on any other error.</summary>
        string[] SubKeys(string path);
        /// <summary>The named values that exist (string, int, long, string[], byte[]). When stopIfFirstMissing is set and the
        /// first name does not exist, nothing else is read. Empty when the key does not exist. Throws on any other error.</summary>
        Dictionary<string, object> Values(string path, IList<string> names, bool stopIfFirstMissing = false);
        /// <summary>All values of a key; empty when the key does not exist.</summary>
        Dictionary<string, object> AllValues(string path);
        /// <summary>How many keys to read at the same time.</summary>
        int Parallelism { get; }
    }

    internal static class RegistryAccess
    {
        /// <summary>
        /// Local server: the local registry. Remote: the Remote Registry service (v2.0.1: own reader, one network call per
        /// value, several keys at a time); if that is not available, the WMI registry provider (StdRegProv) over DCOM.
        /// </summary>
        internal static IRegistryReader Open(Target t, Wmi wmi, List<string> notes)
        {
            if (t.IsLocal) return NativeReader.Local();
            try
            {
                return Hard.Run(() => NativeReader.Remote(t.ComputerName, t.RemoteCredential), Math.Max(15, t.ConnectTimeoutSec), "Connecting to the Remote Registry service");
            }
            catch (Exception ex)
            {
                if (notes != null) notes.Add("Remote Registry service not available (" + ex.Message.Trim() + "); the WMI registry provider was used.");
                return new WmiReader(wmi, t.OperationTimeoutSec);
            }
        }

        /// <summary>Runs f for every item, 'degree' at a time on dedicated threads (not the thread pool). Result order = item order.
        /// A failed item gives default(T) and its exception is collected.</summary>
        internal static T[] ParallelMap<TIn, T>(IList<TIn> items, int degree, Func<TIn, T> f, List<Exception> errors)
        {
            var results = new T[items.Count];
            if (items.Count == 0) return results;
            int next = -1;
            var errs = new List<Exception>();
            ThreadStart work = () =>
            {
                while (true)
                {
                    int i = Interlocked.Increment(ref next);
                    if (i >= items.Count) return;
                    try { results[i] = f(items[i]); }
                    catch (Exception ex) { lock (errs) errs.Add(ex); }
                }
            };
            int n = Math.Max(1, Math.Min(degree, items.Count));
            if (n == 1) work();
            else
            {
                var threads = Enumerable.Range(0, n).Select(_ => new Thread(work) { IsBackground = true }).ToList();
                foreach (Thread th in threads) th.Start();
                foreach (Thread th in threads) th.Join();
            }
            if (errors != null) errors.AddRange(errs);
            return results;
        }
    }

    /// <summary>
    /// Registry reader on advapi32 (v2.0.1). The .NET RegistryKey class needs about three network round trips per value and
    /// reports most errors as "key does not exist"; in the v2.0.0 field test that made the applications step take a median of
    /// 86 s (34 servers hit the 300 s limit) and hid failed certificate reads. This reader queries only the values that are
    /// needed, with one call per value, reports real error codes, and reconnects once when the connection drops.
    /// </summary>
    internal sealed class NativeReader : IRegistryReader
    {
        private const int KEY_READ = 0x20019, KEY_WOW64_64KEY = 0x0100;
        private const int ERROR_SUCCESS = 0, ERROR_FILE_NOT_FOUND = 2, ERROR_MORE_DATA = 234, ERROR_NO_MORE_ITEMS = 259;
        // HKEY_LOCAL_MACHINE as Windows defines it on 64-bit: (HKEY)(LONG)0x80000002, sign-extended (as .NET passes it).
        private static readonly IntPtr HKLM = new IntPtr(unchecked((int)0x80000002));
        private static readonly int[] ConnectionErrors = { 6, 53, 64, 67, 1203, 1231, 1722, 1723, 1726, 1727, 1753, 1717, 1772, 1775 };

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int RegConnectRegistryW(string machine, IntPtr hKey, out SafeRegistryHandle result);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)] private static extern int RegOpenKeyExW(SafeRegistryHandle hKey, string subKey, int options, int sam, out SafeRegistryHandle result);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegQueryInfoKeyW(SafeRegistryHandle hKey, IntPtr cls, IntPtr clsLen, IntPtr reserved, out int subKeys, out int maxSubKeyLen, IntPtr maxClassLen,
            out int values, out int maxValueNameLen, out int maxValueLen, IntPtr secDesc, IntPtr lastWrite);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegEnumKeyExW(SafeRegistryHandle hKey, int index, StringBuilder name, ref int nameLen, IntPtr reserved, IntPtr cls, IntPtr clsLen, IntPtr lastWrite);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegEnumValueW(SafeRegistryHandle hKey, int index, StringBuilder name, ref int nameLen, IntPtr reserved, out int type, byte[] data, ref int dataLen);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
        private static extern int RegQueryValueExW(SafeRegistryHandle hKey, string name, IntPtr reserved, out int type, byte[] data, ref int dataLen);

        private readonly string machine;        // null = local
        private readonly Credential cred;
        private readonly object gate = new object();
        private SafeRegistryHandle root;
        private int generation;

        private NativeReader(string machine, Credential cred) { this.machine = machine; this.cred = cred; }

        public string Method { get { return machine == null ? "Local registry" : "Remote Registry service"; } }
        public int Parallelism { get { return machine == null ? 4 : 8; } }

        internal static NativeReader Local()
        {
            var r = new NativeReader(null, null);
            r.Connect();
            return r;
        }

        internal static NativeReader Remote(string computer, Credential cred)
        {
            var r = new NativeReader(computer, cred);
            r.Connect();
            // Proves the connection and the rights: SOFTWARE must be readable.
            r.Run(h => { SafeRegistryHandle k; int rc = RegOpenKeyExW(h, "SOFTWARE", 0, KEY_READ | KEY_WOW64_64KEY, out k); if (rc != 0) throw Fail(rc, "SOFTWARE"); k.Dispose(); return 0; });
            return r;
        }

        private void Connect()
        {
            if (machine == null) return;
            NetworkAccess.Run(cred, () =>
            {
                SafeRegistryHandle h;
                int rc = RegConnectRegistryW("\\\\" + machine, HKLM, out h);
                if (rc != 0) throw Fail(rc, null);
                lock (gate) { var old = root; root = h; generation++; if (old != null) old.Dispose(); }
            });
        }

        private static Exception Fail(int rc, string path)
        {
            string what = path == null ? "Connecting to the remote registry" : "Reading HKLM\\" + path;
            return new Win32Exception(rc, what + " failed: " + new Win32Exception(rc).Message + " (error " + rc + ")");
        }

        /// <summary>Runs an operation on the HKLM handle; reconnects once when the connection dropped.</summary>
        private T Run<T>(Func<SafeRegistryHandle, T> op)
        {
            for (int attempt = 0; ; attempt++)
            {
                SafeRegistryHandle h; int gen;
                lock (gate) { h = root; gen = generation; }
                try
                {
                    if (machine == null)
                    {
                        // The predefined HKLM handle, wrapped without ownership.
                        using (var local = new SafeRegistryHandle(HKLM, false)) return op(local);
                    }
                    return NetworkAccess.Run(cred, () => op(h));
                }
                catch (Win32Exception ex) when (attempt == 0 && machine != null && Array.IndexOf(ConnectionErrors, ex.NativeErrorCode) >= 0)
                {
                    lock (gate) { if (gen != generation) continue; }
                    Connect();
                }
            }
        }

        /// <summary>Opens a subkey; null when it does not exist. The caller disposes the handle.</summary>
        private static SafeRegistryHandle Open(SafeRegistryHandle h, string path)
        {
            SafeRegistryHandle k;
            int rc = RegOpenKeyExW(h, path, 0, KEY_READ | KEY_WOW64_64KEY, out k);
            if (rc == ERROR_FILE_NOT_FOUND) { if (k != null) k.Dispose(); return null; }
            if (rc != ERROR_SUCCESS) throw Fail(rc, path);
            return k;
        }

        public string[] SubKeys(string path)
        {
            return Run(h =>
            {
                using (SafeRegistryHandle k = Open(h, path))
                {
                    if (k == null) return new string[0];
                    int subKeys, maxLen, values, maxValueName, maxValue;
                    int rc = RegQueryInfoKeyW(k, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out subKeys, out maxLen, IntPtr.Zero, out values, out maxValueName, out maxValue, IntPtr.Zero, IntPtr.Zero);
                    if (rc != 0) throw Fail(rc, path);
                    var names = new List<string>(subKeys);
                    for (int i = 0; ; i++)
                    {
                        var sb = new StringBuilder(Math.Max(256, maxLen + 2));
                        int len = sb.Capacity;
                        rc = RegEnumKeyExW(k, i, sb, ref len, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                        if (rc == ERROR_NO_MORE_ITEMS) break;
                        if (rc != 0) throw Fail(rc, path);
                        names.Add(sb.ToString(0, len));
                    }
                    return names.ToArray();
                }
            });
        }

        public Dictionary<string, object> Values(string path, IList<string> names, bool stopIfFirstMissing = false)
        {
            return Run(h =>
            {
                var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                using (SafeRegistryHandle k = Open(h, path))
                {
                    if (k == null) return d;
                    byte[] buf = new byte[8192];
                    for (int i = 0; i < names.Count; i++)
                    {
                        int type, len = buf.Length;
                        int rc = RegQueryValueExW(k, names[i], IntPtr.Zero, out type, buf, ref len);
                        if (rc == ERROR_MORE_DATA) { buf = new byte[len + 2]; len = buf.Length; rc = RegQueryValueExW(k, names[i], IntPtr.Zero, out type, buf, ref len); }
                        if (rc == ERROR_FILE_NOT_FOUND) { if (i == 0 && stopIfFirstMissing) return d; continue; }
                        if (rc != 0) throw Fail(rc, path + "\\" + names[i]);
                        d[names[i]] = Decode(type, buf, len);
                    }
                }
                return d;
            });
        }

        public Dictionary<string, object> AllValues(string path)
        {
            return Run(h =>
            {
                var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                using (SafeRegistryHandle k = Open(h, path))
                {
                    if (k == null) return d;
                    int subKeys, maxLen, values, maxValueName, maxValue;
                    int rc = RegQueryInfoKeyW(k, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, out subKeys, out maxLen, IntPtr.Zero, out values, out maxValueName, out maxValue, IntPtr.Zero, IntPtr.Zero);
                    if (rc != 0) throw Fail(rc, path);
                    for (int i = 0; ; i++)
                    {
                        var sb = new StringBuilder(Math.Max(256, maxValueName + 2));
                        int nameLen = sb.Capacity, type, len = Math.Max(1024, maxValue + 2);
                        byte[] buf = new byte[len];
                        rc = RegEnumValueW(k, i, sb, ref nameLen, IntPtr.Zero, out type, buf, ref len);
                        if (rc == ERROR_NO_MORE_ITEMS) break;
                        if (rc != 0) throw Fail(rc, path);
                        d[sb.ToString(0, nameLen)] = Decode(type, buf, len);
                    }
                }
                return d;
            });
        }

        /// <summary>REG_SZ / EXPAND_SZ (not expanded, like DoNotExpandEnvironmentNames), MULTI_SZ, DWORD, QWORD, BINARY.</summary>
        internal static object Decode(int type, byte[] buf, int len)
        {
            len = Math.Max(0, Math.Min(len, buf.Length));
            switch (type)
            {
                case 1:
                case 2:
                    {
                        string s = Encoding.Unicode.GetString(buf, 0, len - (len % 2));
                        int z = s.IndexOf('\0');
                        return z >= 0 ? s.Substring(0, z) : s;
                    }
                case 7:
                    return Encoding.Unicode.GetString(buf, 0, len - (len % 2)).Split('\0').Where(x => x.Length > 0).ToArray();
                case 4: return len >= 4 ? (object)BitConverter.ToInt32(buf, 0) : null;
                case 11: return len >= 8 ? (object)BitConverter.ToInt64(buf, 0) : null;
                default:
                    {
                        var b = new byte[len];
                        Buffer.BlockCopy(buf, 0, b, 0, len);
                        return b;
                    }
            }
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
        public int Parallelism { get { return 4; } }

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

        /// <summary>ReturnValue 2 = not found; anything else that is not 0 is an error.</summary>
        private static bool Missing(ManagementBaseObject o, string path)
        {
            uint rv = Ret(o);
            if (rv == 0) return false;
            if (rv == 2) return true;
            throw new Win32Exception((int)rv, "Reading HKLM\\" + path + " through the WMI registry provider failed: " + new Win32Exception((int)rv).Message + " (error " + rv + ")");
        }

        public string[] SubKeys(string path)
        {
            ManagementBaseObject o = Call("EnumKey", path, null);
            if (Missing(o, path)) return new string[0];
            return (o["sNames"] as string[]) ?? new string[0];
        }

        private Dictionary<string, int> Types(string path)
        {
            var t = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            ManagementBaseObject e = Call("EnumValues", path, null);
            if (Missing(e, path)) return null;
            var names = (e["sNames"] as string[]) ?? new string[0];
            var types = new List<int>();
            var raw = e["Types"] as System.Collections.IEnumerable;
            if (raw != null) foreach (object x in raw) types.Add(Convert.ToInt32(x));
            for (int i = 0; i < names.Length && i < types.Count; i++) t[names[i] ?? string.Empty] = types[i];
            return t;
        }

        public Dictionary<string, object> Values(string path, IList<string> names, bool stopIfFirstMissing = false)
        {
            var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var types = Types(path);
            if (types == null) return d;
            for (int i = 0; i < names.Count; i++)
            {
                int ty;
                if (!types.TryGetValue(names[i], out ty)) { if (i == 0 && stopIfFirstMissing) return d; continue; }
                object v = Get(path, names[i], ty);
                if (v != null) d[names[i]] = v;
            }
            return d;
        }

        public Dictionary<string, object> AllValues(string path)
        {
            var d = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var types = Types(path);
            if (types == null) return d;
            foreach (var kv in types) d[kv.Key] = Get(path, kv.Key, kv.Value);
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
