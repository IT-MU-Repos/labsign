using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using LabSign.Core.Signing;

namespace LabSign.Desktop;

/// <summary>
/// LabSign's own PIN prompt, so the PIN never passes through the browser. Topmost because it is
/// opened by a click in the browser and would otherwise appear behind it.
/// </summary>
internal sealed class PinWindow : Window
{
    private readonly TextBox _pin = new() { PasswordChar = '•', Width = 280 };
    private readonly TaskCompletionSource<string?> _result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _submitted;

    public Task<string?> Result => _result.Task;

    public PinWindow(PinRequest request)
    {
        Title = "LabSign: enter token PIN";
        Icon = new WindowIcon(new MemoryStream(AppIcon.Png(64)));
        Topmost = true;
        CanResize = false;
        ShowInTaskbar = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var documents = request.PdfCount == 1 ? "1 report" : $"{request.PdfCount} reports";
        var panel = new StackPanel { Margin = new Thickness(20), Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = $"Sign {documents} with token \"{request.TokenLabel}\".\nEnter the token PIN:",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 320,
        });
        if (request.Problem is not null)
            panel.Children.Add(new TextBlock { Text = request.Problem, Foreground = Brushes.Firebrick, MaxWidth = 320, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(_pin);

        var sign = new Button { Content = "Sign", IsDefault = true };
        var cancel = new Button { Content = "Cancel", IsCancel = true };
        sign.Click += (_, _) => Submit();
        cancel.Click += (_, _) => Close();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
        };
        buttons.Children.Add(sign);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;

        _pin.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) Submit();
        };
        Opened += (_, _) =>
        {
            Activate();
            _pin.Focus();
        };
        Closed += (_, _) => _result.TrySetResult(_submitted ? _pin.Text ?? "" : null);
    }

    private void Submit()
    {
        _submitted = true;
        Close();
    }
}
