using System;
using System.Net;
using System.Windows.Forms;

namespace IABB.App
{
    internal static class Program
    {
        [STAThread]
        private static void Main()
        {
            // GitHub requires TLS 1.2; older Windows builds do not enable it by default for .NET Framework apps.
            try { ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12; } catch { }

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (s, e) => ShowError(e.Exception);
            AppDomain.CurrentDomain.UnhandledException += (s, e) => ShowError(e.ExceptionObject as Exception);
            Application.Run(new MainForm());
        }

        internal static void ShowError(Exception ex)
        {
            string text = ex == null ? "Unknown error." : ex.Message;
            try
            {
                MessageBox.Show("An unexpected error occurred:" + Environment.NewLine + Environment.NewLine + text,
                    AppInfo.ProductName, MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            catch { }
        }
    }

    internal static class AppInfo
    {
        internal const string ProductName = "Infrastructure As-Built Builder";

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

        internal static string EngineDirectory { get { return System.IO.Path.Combine(AppDirectory, "Engine"); } }
    }
}
