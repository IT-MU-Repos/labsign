using LabSign.Core.Signing;

namespace LabSign;

/// <summary>
/// LabSign's own PIN prompt, so the PIN never passes through the browser. Topmost because it is
/// opened by a click in the browser and would otherwise appear behind it.
/// </summary>
internal sealed class PinDialog : Form
{
    private readonly TextBox _pin = new() { UseSystemPasswordChar = true, Width = 260 };

    public string Pin => _pin.Text;

    public PinDialog(PinRequest request)
    {
        Text = "LabSign: enter token PIN";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        TopMost = true;
        ShowInTaskbar = true;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);
        Icon = SystemIcons.Shield;

        var layout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            WrapContents = false,
        };
        var reports = request.PdfCount == 1 ? "1 report" : $"{request.PdfCount} reports";
        layout.Controls.Add(new Label
        {
            Text = $"Sign {reports} with token \"{request.TokenLabel}\".\nEnter the token PIN:",
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 8),
        });
        if (request.Problem is not null)
        {
            layout.Controls.Add(new Label
            {
                Text = request.Problem,
                ForeColor = Color.Firebrick,
                AutoSize = true,
                Margin = new Padding(0, 0, 0, 8),
            });
        }
        layout.Controls.Add(_pin);

        var ok = new Button { Text = "Sign", DialogResult = DialogResult.OK, AutoSize = true };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, AutoSize = true };
        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            AutoSize = true,
            Dock = DockStyle.Fill,
            Margin = new Padding(0, 12, 0, 0),
        };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(ok);
        layout.Controls.Add(buttons);

        Controls.Add(layout);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        _pin.Focus();
    }
}
