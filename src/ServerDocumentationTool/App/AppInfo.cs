using System;
using System.IO;

namespace ServerDocumentationTool
{
    internal static class AppInfo
    {
        internal const string ProductName = "Server Documentation Tool";
        internal const string ExeName = "ServerDocumentationTool.exe";

        internal static Version Version
        {
            get
            {
                Version v = typeof(AppInfo).Assembly.GetName().Version;
                return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            }
        }

        internal static string VersionText { get { return "v" + Version.ToString(3); } }

        internal static string AppDirectory { get { return AppDomain.CurrentDomain.BaseDirectory; } }

        /// <summary>%ProgramData%\ServerDocumentationTool (settings, update staging).</summary>
        internal static string DataDirectory
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ServerDocumentationTool"); }
        }
    }
}
