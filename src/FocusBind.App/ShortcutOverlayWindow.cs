using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using FocusBind.Core.Coordinates;
using FocusBind.Core.Overlay;
using FocusBind.Core.Profiles;

namespace FocusBind.App;

/// <summary>Non-activating, click-through layered window. No input handlers or injection.</summary>
internal sealed class ShortcutOverlayWindow : Window
{
    private readonly Canvas _canvas = new() { IsHitTestVisible = false, ClipToBounds = true };
    private HwndSource? _source;
    internal const int ExtendedStyles = 0x08000000 | 0x00000020 | 0x00000080; // NOACTIVATE | TRANSPARENT | TOOLWINDOW
    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLongW(nint window, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLongW(nint window, int index, int value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { internal int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out NativeRect rectangle);

    internal ShortcutOverlayWindow()
    {
        Title = "FocusBind 단축키 오버레이";
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ShowActivated = false; Focusable = false; IsHitTestVisible = false;
        ShowInTaskbar = false; Topmost = true; ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual; Width = 1; Height = 1;
        Content = _canvas;
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            // Layered + WS_EX_TRANSPARENT passes mouse input to windows underneath:
            // https://learn.microsoft.com/windows/win32/winmsg/window-features#layered-windows
            var styles = GetWindowLongW(handle, -20);
            Marshal.SetLastPInvokeError(0);
            if (SetWindowLongW(handle, -20, styles | ExtendedStyles) == 0 && Marshal.GetLastPInvokeError() != 0)
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "오버레이 입력 통과 설정 실패");
            _source = HwndSource.FromHwnd(handle); _source?.AddHook(Hook);
        };
        Closed += (_, _) => { _source?.RemoveHook(Hook); _source = null; };
    }

    internal void Present(string profileName, CoordinateContext context, IReadOnlyList<ShortcutOverlayHint> hints, double opacity)
    {
        if (!IsVisible) Show(); // ShowActivated=false and WS_EX_NOACTIVATE keep the target foreground.
        var bounds = context.Client;
        if (!SetWindowPos(new WindowInteropHelper(this).Handle, -1, bounds.X, bounds.Y, bounds.Width, bounds.Height, 0x0010 | 0x0200))
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "오버레이 위치 갱신 실패");
        RenderHints(profileName, context, hints, opacity);
    }

    internal bool MatchesBounds(PixelRect bounds) => IsVisible &&
        GetWindowRect(new WindowInteropHelper(this).Handle, out var actual) && actual.Left == bounds.X && actual.Top == bounds.Y &&
        actual.Right - actual.Left == bounds.Width && actual.Bottom - actual.Top == bounds.Height;

    internal void RenderHints(string profileName, CoordinateContext context, IReadOnlyList<ShortcutOverlayHint> hints, double opacity)
    {
        _canvas.Children.Clear(); _canvas.Opacity = Math.Clamp(opacity, 0, 1);
        var scale = context.Dpi / 96.0;
        var width = context.Client.Width / scale; var height = context.Client.Height / scale;
        foreach (var group in hints.Where(h => h.Position is not null).GroupBy(h => h.Position!.Value))
        {
            var point = group.Key;
            var x = (point.X - context.Client.X) / scale; var y = (point.Y - context.Client.Y) / scale;
            var dot = new Border { Width = 6, Height = 6, Background = Brushes.White,
                BorderBrush = Brushes.Black, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(3) };
            Canvas.SetLeft(dot, x - 3); Canvas.SetTop(dot, y - 3); _canvas.Children.Add(dot);
            var badge = Badge(string.Join(" / ", group.Select(h => TriggerLabel(h.Trigger)).Distinct()));
            badge.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            Canvas.SetLeft(badge, Math.Clamp(x + 9, 0, Math.Max(0, width - badge.DesiredSize.Width)));
            Canvas.SetTop(badge, Math.Clamp(y - badge.DesiredSize.Height / 2, 0, Math.Max(0, height - badge.DesiredSize.Height)));
            _canvas.Children.Add(badge);
        }
        var list = hints.Where(h => h.Position is null).ToList();
        if (list.Count > 0)
        {
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock { Text = $"단축키 · {profileName}", Foreground = Brushes.White,
                FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 5), TextWrapping = TextWrapping.Wrap });
            foreach (var hint in list)
                panel.Children.Add(new TextBlock { Text = $"{TriggerLabel(hint.Trigger)}   {hint.Name}", Foreground = Brushes.White,
                    TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 2), FontSize = 12 });
            var box = new Border { Child = panel, Background = new SolidColorBrush(Color.FromRgb(22, 36, 52)),
                Padding = new Thickness(10), CornerRadius = new CornerRadius(7), MaxWidth = Math.Max(1, Math.Min(300, width - 16)) };
            Canvas.SetLeft(box, 8); Canvas.SetTop(box, 8); _canvas.Children.Add(box);
        }
    }

    private static Border Badge(string text) => new()
    {
        Background = new SolidColorBrush(Color.FromRgb(22, 36, 52)), BorderBrush = Brushes.White,
        BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Padding = new Thickness(7, 4, 7, 4),
        Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold,
            MaxWidth = 210, TextTrimming = TextTrimming.CharacterEllipsis }
    };

    internal static string TriggerLabel(InputTrigger trigger)
    {
        string label;
        if (trigger.Kind == TriggerKind.Key) label = KeyNames.Format(trigger.KeyCode, trigger.Modifiers, false);
        else
        {
            var modifiers = new List<string>();
            if (trigger.Modifiers.HasFlag(FocusBind.Core.Profiles.ModifierKeys.Control)) modifiers.Add("Ctrl");
            if (trigger.Modifiers.HasFlag(FocusBind.Core.Profiles.ModifierKeys.Shift)) modifiers.Add("Shift");
            if (trigger.Modifiers.HasFlag(FocusBind.Core.Profiles.ModifierKeys.Alt)) modifiers.Add("Alt");
            if (trigger.Modifiers.HasFlag(FocusBind.Core.Profiles.ModifierKeys.Windows)) modifiers.Add("Win");
            modifiers.Add(trigger.Kind == TriggerKind.MouseButton ? $"마우스 {trigger.MouseButton}" :
                trigger.Kind == TriggerKind.VerticalWheel ? (trigger.WheelDirection == WheelDirection.Positive ? "휠 ↑" : "휠 ↓") :
                trigger.WheelDirection == WheelDirection.Positive ? "휠 →" : "휠 ←");
            label = string.Join(" + ", modifiers);
        }
        return label + (trigger.Phase == TriggerPhase.Release ? " (뗌)" : trigger.Phase == TriggerPhase.LongPress
            ? $" ({trigger.LongPressMilliseconds}ms)" : "");
    }

    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0084) { handled = true; return -1; } // HTTRANSPARENT
        if (message == 0x0021) { handled = true; return 3; } // MA_NOACTIVATE
        return 0;
    }
}
