using System;
using System.IO;
using System.Text;
using System.Windows.Forms;

namespace ServerDocumentationTool
{
    /// <summary>Writes to the log pane and, during a run, to &lt;run folder&gt;\ServerDocumentation_Run.log. Safe to call from any thread.</summary>
    internal sealed class RunLogger
    {
        internal const string FileName = "ServerDocumentation_Run.log";
        private readonly TextBox box;
        private readonly object fileLock = new object();
        private static readonly Encoding Utf8NoBom = new UTF8Encoding(false);
        private string filePath;

        public RunLogger(TextBox box)
        {
            this.box = box;
        }

        public void SetFile(string path)
        {
            lock (fileLock) { filePath = path; }
        }

        public void Log(string message)
        {
            string line = DateTime.Now.ToString("HH:mm:ss") + " " + (message ?? string.Empty) + Environment.NewLine;
            lock (fileLock)
            {
                if (filePath != null)
                {
                    try { File.AppendAllText(filePath, line, Utf8NoBom); } catch { }
                }
            }
            if (box == null) return;
            try
            {
                if (box.IsDisposed) return;
                if (box.InvokeRequired) box.BeginInvoke(new Action(() => Append(line)));
                else Append(line);
            }
            catch { }
        }

        private void Append(string line)
        {
            if (box.IsDisposed) return;
            // Keep the pane responsive on very long runs; the log file keeps everything.
            if (box.TextLength > 2000000) box.Clear();
            box.AppendText(line);
        }
    }
}
