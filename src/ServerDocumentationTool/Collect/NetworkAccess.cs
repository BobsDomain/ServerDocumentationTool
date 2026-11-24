using System;
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
    /// </summary>
    internal static class NetworkAccess
    {
        private const int LOGON32_LOGON_NEW_CREDENTIALS = 9;
        private const int LOGON32_PROVIDER_WINNT50 = 3;

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool LogonUser(string user, string domain, string password, int logonType, int logonProvider, out SafeAccessTokenHandle token);

        internal static T Run<T>(Credential cred, Func<T> work)
        {
            if (cred == null) return work();
            SafeAccessTokenHandle token;
            string domain = string.IsNullOrEmpty(cred.Domain) ? null : cred.Domain;
            if (!LogonUser(cred.User, domain, cred.Password, LOGON32_LOGON_NEW_CREDENTIALS, LOGON32_PROVIDER_WINNT50, out token))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The alternate credentials could not be prepared for network access");
            using (token)
            {
                T result = default(T);
                WindowsIdentity.RunImpersonated(token, () => { result = work(); });
                return result;
            }
        }

        internal static void Run(Credential cred, Action work)
        {
            Run<bool>(cred, () => { work(); return true; });
        }
    }
}
