using System.Diagnostics;
using LabSign.Core.Token;

namespace LabSign;

/// <summary>The notification-area icon: status at a glance, the log folder, autostart, exit.</summary>
internal sealed class TrayContext : ApplicationContext
{
    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _tokenItem = new() { Enabled = false };
    private readonly ToolStripMenuItem _autostartItem = new("Start with Windows") { CheckOnClick = true };
    private readonly ITokenProvider _token;

    public TrayContext(ITokenProvider token, string version, string dataFolder)
    {
        _token = token;
        var menu = new ContextMenuStrip();
        menu.Items.Add(new ToolStripMenuItem($"LabSign {version}") { Enabled = false });
        menu.Items.Add(_tokenItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open log and settings folder", null, (_, _) =>
            Process.Start(new ProcessStartInfo(dataFolder) { UseShellExecute = true }));
        menu.Items.Add(_autostartItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitThread());
        menu.Opening += (_, _) => Refresh();
        _autostartItem.CheckedChanged += (_, _) => WindowsIntegration.SetStartsWithWindows(_autostartItem.Checked);

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Shield,
            Text = "LabSign: report signing",
            ContextMenuStrip = menu,
            Visible = true,
        };
        Refresh();
    }

    private void Refresh()
    {
        var status = _token.GetStatus();
        _tokenItem.Text = status.Present
            ? $"Token: {status.Label} ({status.Subject})"
            : $"Token: {status.Error}";
        _autostartItem.Checked = WindowsIntegration.StartsWithWindows;
    }

    protected override void ExitThreadCore()
    {
        _icon.Visible = false;
        _icon.Dispose();
        base.ExitThreadCore();
    }
}
