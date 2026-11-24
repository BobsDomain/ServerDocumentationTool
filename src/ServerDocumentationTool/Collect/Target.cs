using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace ServerDocumentationTool.Collect
{
    /// <summary>Alternate credentials typed into the app (DOMAIN\user or user@domain). Kept in memory only.</summary>
    internal sealed class Credential
    {
        public string Domain;   // empty for a UPN
        public string User;
        public string Password;

        public string FullUser { get { return string.IsNullOrEmpty(Domain) ? User : Domain + "\\" + User; } }

        public static Credential Parse(string userName, string password)
        {
            if (string.IsNullOrWhiteSpace(userName)) return null;
            string u = userName.Trim();
            var c = new Credential { Password = password ?? string.Empty };
            int i = u.IndexOf('\\');
            if (i > 0) { c.Domain = u.Substring(0, i); c.User = u.Substring(i + 1); }
            else { c.Domain = string.Empty; c.User = u; }
            return c;
        }
    }

    /// <summary>The server being collected, how to reach it and the time limits.</summary>
    internal sealed class Target
    {
        public string ComputerName;
        public bool IsLocal;
        public Credential Credential;     // null = signed-in user; never used for the local server
        public int ConnectTimeoutSec = 30;
        public int OperationTimeoutSec = 180;
        public int SectionTimeoutSec = 300;

        /// <summary>Credential to use for this target (null for the local server or when none was given).</summary>
        public Credential RemoteCredential { get { return IsLocal ? null : Credential; } }

        /// <summary>Host name used in UNC paths and API calls ("." becomes the local machine name).</summary>
        public string HostName { get { return IsLocal ? Environment.MachineName : ComputerName; } }
    }

    internal static class LocalNames
    {
        private static List<string> names;
        private static readonly object Gate = new object();

        internal static List<string> All
        {
            get
            {
                lock (Gate)
                {
                    if (names != null) return names;
                    string cn = Environment.MachineName.ToLowerInvariant();
                    var n = new List<string> { ".", "localhost", "127.0.0.1", "::1", cn };
                    try
                    {
                        string dom = (IPGlobalProperties.GetIPGlobalProperties().DomainName ?? string.Empty).Trim().ToLowerInvariant();
                        if (dom.Length > 0) n.Add(cn + "." + dom);
                    }
                    catch { }
                    try
                    {
                        string hn = (Dns.GetHostEntry(Environment.MachineName).HostName ?? string.Empty).Trim().ToLowerInvariant();
                        if (hn.Length > 0) n.Add(hn);
                    }
                    catch { }
                    names = n.Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
                    return names;
                }
            }
        }

        /// <summary>Port of Test-IABBIsLocal (v1.0.0: "." is checked before the trailing dot is trimmed).</summary>
        internal static bool IsLocal(string computerName)
        {
            if (string.IsNullOrWhiteSpace(computerName)) return true;
            string n = computerName.Trim().ToLowerInvariant();
            if (n != ".") n = n.TrimEnd('.');
            return All.Contains(n);
        }
    }

    internal sealed class HardTimeoutException : TimeoutException
    {
        public readonly int Seconds;
        public HardTimeoutException(string what, int seconds) : base(what + " did not finish within " + seconds + " seconds.") { Seconds = seconds; }
    }

    /// <summary>
    /// Hard time limits. The work runs on its own thread; when the limit is reached the caller continues and the
    /// thread is abandoned (a hung WMI/RPC call cannot be cancelled safely). Its eventual result is discarded.
    /// </summary>
    internal static class Hard
    {
        internal static T Run<T>(Func<T> work, int seconds, string what)
        {
            var task = Task.Factory.StartNew(work, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            bool done;
            try { done = task.Wait(TimeSpan.FromSeconds(Math.Max(1, seconds))); }
            catch (AggregateException ae)
            {
                Exception inner = ae.Flatten().InnerExceptions.FirstOrDefault() ?? ae;
                ExceptionDispatchInfo.Capture(inner).Throw();
                throw;
            }
            if (!done)
            {
                task.ContinueWith(t => { var ignored = t.Exception; }, TaskContinuationOptions.OnlyOnFaulted);
                throw new HardTimeoutException(what, seconds);
            }
            return task.Result;
        }

        internal static void Run(Action work, int seconds, string what)
        {
            Run<bool>(() => { work(); return true; }, seconds, what);
        }
    }
}
