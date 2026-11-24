using System.Drawing;
using System.Windows.Forms;
using ServerDocumentationTool.Collect;

namespace ServerDocumentationTool
{
    /// <summary>Asks for alternate credentials (DOMAIN\user and password). Kept in memory for this session only, never saved.</summary>
    internal sealed class CredentialDialog : Form
    {
        private readonly TextBox txtUser = new TextBox();
        private readonly TextBox txtPassword = new TextBox();

        public CredentialDialog(string currentUser)
        {
            Text = "Alternate credentials";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(420, 170);
            Font = new Font("Segoe UI", 9F);

            var lblInfo = new Label { Text = "Account with administrator rights on the target servers:", Location = new Point(12, 12), Size = new Size(396, 20) };
            var lblUser = new Label { Text = "User name", Location = new Point(12, 44), Size = new Size(90, 22) };
            txtUser.Location = new Point(110, 41);
            txtUser.Size = new Size(296, 24);
            txtUser.Text = currentUser ?? string.Empty;
            var lblHint = new Label { Text = "DOMAIN\\user or user@domain", ForeColor = SystemColors.GrayText, Location = new Point(110, 66), Size = new Size(296, 18) };
            var lblPassword = new Label { Text = "Password", Location = new Point(12, 92), Size = new Size(90, 22) };
            txtPassword.Location = new Point(110, 89);
            txtPassword.Size = new Size(296, 24);
            txtPassword.UseSystemPasswordChar = true;

            var btnOk = new Button { Text = "OK", Location = new Point(250, 130), Size = new Size(75, 28), DialogResult = DialogResult.OK };
            var btnCancel = new Button { Text = "Cancel", Location = new Point(331, 130), Size = new Size(75, 28), DialogResult = DialogResult.Cancel };
            btnOk.Click += (s, e) =>
            {
                if (string.IsNullOrWhiteSpace(txtUser.Text))
                {
                    MessageBox.Show(this, "Enter a user name.", Text, MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    DialogResult = DialogResult.None;
                }
            };
            AcceptButton = btnOk;
            CancelButton = btnCancel;
            Controls.AddRange(new Control[] { lblInfo, lblUser, txtUser, lblHint, lblPassword, txtPassword, btnOk, btnCancel });
        }

        public Credential GetCredential()
        {
            Credential c = Credential.Parse(txtUser.Text, txtPassword.Text);
            txtPassword.Clear();
            return c;
        }
    }
}
