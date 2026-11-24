using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace ServerDocumentationTool.Collect
{
    /// <summary>
    /// Runs code with the alternate credentials for NETWORK access only (LogonUser LOGON32_LOGON_NEW_CREDENTIALS, the
    /// same as "runas /netonly"). Used for remote registry, the admin share (IIS config) and the DHCP API. Local access
    /// keeps the signed-in user. Without alternate credentials the code simply runs as the signed-in user.
    /// v2.0.1: one logon token per credential, reused by every call (the registry is now read on several threads).
    /// </summary>
    internal static class NetworkAccess
    {
        private const int LOGON32_LOGON_NEW_CREDENTIALS = 9;
        private const int LOGON32_PROVIDER_WINNT50 = 3;
        private static readonly Dictionary<Credential, SafeAccessTokenHandle> Tokens = new Dictionary<Credential, SafeAccessTokenHandle>();

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LogonUser(string user, string domain, string password, int logonType, int logonProvider, out SafeAccessTokenHandle token);

        private static SafeAccessTokenHandle Token(Credential cred)
        {
            lock (Tokens)
            {
                SafeAccessTokenHandle token;
                if (Tokens.TryGetValue(cred, out token)) return token;
                string domain = string.IsNullOrEmpty(cred.Domain) ? null : cred.Domain;
                if (!LogonUser(cred.User, domain, cred.Password, LOGON32_LOGON_NEW_CREDENTIALS, LOGON32_PROVIDER_WINNT50, out token))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "The alternate credentials could not be prepared for network access");
                Tokens[cred] = token;
                return token;
            }
        }

        internal static T Run<T>(Credential cred, Func<T> work)
        {
            if (cred == null) return work();
            T result = default(T);
            WindowsIdentity.RunImpersonated(Token(cred), () => { result = work(); });
            return result;
        }

        internal static void Run(Credential cred, Action work)
        {
            Run<bool>(cred, () => { work(); return true; });
        }
    }
}
