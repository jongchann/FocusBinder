using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using FocusBind.Core.Coordinates;
using FocusBind.Win32;

namespace FocusBind.App;

/// <summary>Temporary overlay aligned to the target's physical client rectangle.</summary>
public sealed class CoordinateCaptureWindow : Window
{
    private readonly WindowSnapshot _target;
    private readonly TextBlock _positionText;
    private const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint hwnd);

    public PixelPoint? SelectedPoint { get; private set; }
    public string? CaptureError { get; private set; }

    public CoordinateCaptureWindow(WindowSnapshot target)
    {
        _target = target;
        Title = "FocusBind 좌표 선택";
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = 640;
        Height = 360;
        Focusable = true;

        var root = new Grid { Background = new SolidColorBrush(Color.FromArgb(115, 12, 26, 43)) };
        root.Children.Add(new Border
        {
            BorderBrush = Brushes.DeepSkyBlue,
            BorderThickness = new Thickness(3),
            IsHitTestVisible = false
        });
        var heading = new TextBlock
        {
            Text = "클릭 지점을 선택하세요 · Esc 또는 오른쪽 클릭: 취소",
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(210, 20, 37, 56)),
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Padding = new Thickness(15, 9, 15, 9),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 16, 0, 0),
            IsHitTestVisible = false
        };
        root.Children.Add(heading);
        _positionText = new TextBlock
        {
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(210, 20, 37, 56)),
            FontSize = 14,
            Padding = new Thickness(10, 6, 10, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, 20),
            IsHitTestVisible = false
        };
        root.Children.Add(_positionText);
        Content = root;

        SourceInitialized += (_, _) => AlignToClient();
        Loaded += (_, _) => Focus();
        MouseMove += (_, e) => ShowPosition(e.GetPosition(this));
        MouseLeftButtonDown += (_, e) => Capture(e.GetPosition(this));
        MouseRightButtonDown += (_, _) => DialogResult = false;
        KeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) DialogResult = false; };
    }

    public bool? ShowForTarget()
    {
        if (!SetForegroundWindow(_target.Handle))
        {
            CaptureError = "대상 창을 앞으로 가져오지 못했습니다. 창을 직접 활성화한 뒤 다시 시도하세요.";
            return false;
        }
        return ShowDialog();
    }

    private void AlignToClient()
    {
        var bounds = _target.ClientBounds;
        var handle = new WindowInteropHelper(this).Handle;
        if (SetWindowPos(handle, new nint(-1), bounds.X, bounds.Y, bounds.Width, bounds.Height, SwpShowWindow)) return;
        CaptureError = "캡처 오버레이를 대상 창 위치에 배치하지 못했습니다.";
        Dispatcher.BeginInvoke(Close);
    }

    private PixelPoint ToScreenPoint(Point local)
    {
        var physical = PointToScreen(local);
        return new PixelPoint((int)Math.Round(physical.X), (int)Math.Round(physical.Y));
    }

    private void ShowPosition(Point local)
    {
        var point = ToScreenPoint(local);
        var bounds = _target.ClientBounds;
        _positionText.Text = $"창 상대: {point.X - bounds.X}, {point.Y - bounds.Y} px";
    }

    private void Capture(Point local)
    {
        var current = WindowInspector.TryGet(_target.Handle);
        if (current is null || current.ProcessId != _target.ProcessId || !current.IsVisible || current.IsMinimized ||
            current.ClientBounds != _target.ClientBounds)
        {
            CaptureError = "대상 창 위치나 크기가 바뀌었습니다. 다시 선택하세요.";
            DialogResult = false;
            return;
        }
        var point = ToScreenPoint(local);
        var rect = new PixelRect(current.ClientBounds.X, current.ClientBounds.Y,
            current.ClientBounds.Width, current.ClientBounds.Height);
        if (!CoordinateTransform.TryCapture(rect, point, out _)) return;
        SelectedPoint = point;
        DialogResult = true;
    }
}
