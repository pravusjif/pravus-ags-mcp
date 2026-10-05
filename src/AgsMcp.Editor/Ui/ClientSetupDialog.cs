using System.Drawing;
using System.Windows.Forms;

namespace AgsMcp.Editor.Ui
{
    /// <summary>Shows the client setup snippets in a selectable text box, with a button that copies them all.</summary>
    internal sealed class ClientSetupDialog : Form
    {
        public ClientSetupDialog(string url)
        {
            Text = "AGS MCP - client setup";
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(760, 400);
            MinimizeBox = false;
            MaximizeBox = false;
            ShowInTaskbar = false;

            var text = new TextBox
            {
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Both,
                WordWrap = false,
                Dock = DockStyle.Fill,
                Font = new Font(FontFamily.GenericMonospace, 9f),
                Text = ClientSetup.Text(url),
            };
            text.Select(0, 0);

            var copy = new Button { Text = "Copy all", AutoSize = true };
            copy.Click += (s, e) => Clipboard.SetText(text.Text);
            var close = new Button { Text = "Close", AutoSize = true, DialogResult = DialogResult.OK };

            var buttons = new FlowLayoutPanel
            {
                Dock = DockStyle.Bottom,
                FlowDirection = FlowDirection.RightToLeft,
                AutoSize = true,
                Padding = new Padding(6),
            };
            buttons.Controls.Add(close);
            buttons.Controls.Add(copy);

            Controls.Add(text);
            Controls.Add(buttons);
            AcceptButton = close;
            CancelButton = close;
        }
    }
}
