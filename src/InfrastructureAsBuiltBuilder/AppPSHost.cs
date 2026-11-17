using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Host;
using System.Security;

namespace IABB.App
{
    /// <summary>
    /// Minimal PowerShell host for the engine's runspace pool (v1.0.2).
    /// A runspace pool created with an InitialSessionState needs a host; without one, Windows PowerShell 5.1 fails when the
    /// pool opens ("Cannot process argument because the value of argument 'host' is null" - the v1.0.0/v1.0.1 start-up error).
    /// The engine never prompts; host output is ignored because progress reaches the app through the Information stream.
    /// </summary>
    internal sealed class AppPSHost : PSHost
    {
        private readonly Guid instanceId = Guid.NewGuid();
        private readonly CultureInfo culture = CultureInfo.CurrentCulture;
        private readonly CultureInfo uiCulture = CultureInfo.CurrentUICulture;
        private readonly AppPSHostUI ui = new AppPSHostUI();

        public override CultureInfo CurrentCulture { get { return culture; } }
        public override CultureInfo CurrentUICulture { get { return uiCulture; } }
        public override Guid InstanceId { get { return instanceId; } }
        public override string Name { get { return "ServerDocumentationTool"; } }
        public override PSHostUserInterface UI { get { return ui; } }
        public override Version Version { get { return AppInfo.Version; } }
        public override void EnterNestedPrompt() { throw new NotSupportedException("Nested prompts are not supported by the Server Documentation Tool host."); }
        public override void ExitNestedPrompt() { throw new NotSupportedException("Nested prompts are not supported by the Server Documentation Tool host."); }
        public override void NotifyBeginApplication() { }
        public override void NotifyEndApplication() { }
        public override void SetShouldExit(int exitCode) { }
    }

    internal sealed class AppPSHostUI : PSHostUserInterface
    {
        private readonly AppPSHostRawUI rawUI = new AppPSHostRawUI();

        public override PSHostRawUserInterface RawUI { get { return rawUI; } }
        public override string ReadLine() { return string.Empty; }
        public override SecureString ReadLineAsSecureString() { return new SecureString(); }
        public override void Write(string value) { }
        public override void Write(ConsoleColor foregroundColor, ConsoleColor backgroundColor, string value) { }
        public override void WriteLine(string value) { }
        public override void WriteErrorLine(string value) { }
        public override void WriteDebugLine(string message) { }
        public override void WriteProgress(long sourceId, ProgressRecord record) { }
        public override void WriteVerboseLine(string message) { }
        public override void WriteWarningLine(string message) { }

        public override Dictionary<string, PSObject> Prompt(string caption, string message, Collection<FieldDescription> descriptions)
        {
            throw new NotSupportedException("Interactive prompts are not supported by the Server Documentation Tool host.");
        }

        public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName)
        {
            throw new NotSupportedException("Credential prompts are not supported by the Server Documentation Tool host. Use 'Use alternate credentials' in the app.");
        }

        public override PSCredential PromptForCredential(string caption, string message, string userName, string targetName, PSCredentialTypes allowedCredentialTypes, PSCredentialUIOptions options)
        {
            throw new NotSupportedException("Credential prompts are not supported by the Server Documentation Tool host. Use 'Use alternate credentials' in the app.");
        }

        public override int PromptForChoice(string caption, string message, Collection<ChoiceDescription> choices, int defaultChoice)
        {
            return defaultChoice;
        }
    }

    internal sealed class AppPSHostRawUI : PSHostRawUserInterface
    {
        private Size bufferSize = new Size(250, 3000);
        private Size windowSize = new Size(250, 50);

        public override ConsoleColor BackgroundColor { get; set; }
        public override Size BufferSize { get { return bufferSize; } set { bufferSize = value; } }
        public override Coordinates CursorPosition { get; set; }
        public override int CursorSize { get; set; }
        public override ConsoleColor ForegroundColor { get; set; }
        public override bool KeyAvailable { get { return false; } }
        public override Size MaxPhysicalWindowSize { get { return windowSize; } }
        public override Size MaxWindowSize { get { return windowSize; } }
        public override Coordinates WindowPosition { get; set; }
        public override Size WindowSize { get { return windowSize; } set { windowSize = value; } }
        public override string WindowTitle { get; set; }

        public override void FlushInputBuffer() { }
        public override BufferCell[,] GetBufferContents(Rectangle rectangle) { throw new NotSupportedException(); }
        public override KeyInfo ReadKey(ReadKeyOptions options) { throw new NotSupportedException(); }
        public override void ScrollBufferContents(Rectangle source, Coordinates destination, Rectangle clip, BufferCell fill) { }
        public override void SetBufferContents(Coordinates origin, BufferCell[,] contents) { }
        public override void SetBufferContents(Rectangle rectangle, BufferCell fill) { }
    }
}
