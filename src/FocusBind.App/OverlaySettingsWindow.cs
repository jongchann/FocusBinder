using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using FocusBind.Core.Overlay;
using FocusBind.Win32;

namespace FocusBind.App;

internal sealed class OverlaySettingsWindow : Window
{
    private sealed record ProcessOption(string Label, OverlayProcessTarget? Target)
    {
        public override string ToString() => Label;
    }
    private readonly CheckBox _enabled = new() { Content = "대상 프로세스에 단축키 오버레이 표시" };
    private readonly ComboBox _process = new() { DisplayMemberPath = "Label", Margin = new Thickness(0, 5, 0, 16) };
    private readonly Slider _transparency = new() { Minimum = 0, Maximum = 100, TickFrequency = 1, IsSnapToTickEnabled = true };
    internal OverlaySettingsWindow(ShortcutOverlaySettings settings, Action<ShortcutOverlaySettings> apply,
        IReadOnlyList<WindowSnapshot> windows)
    {
        Title = "단축키 오버레이 설정"; Width = 600; Height = 570; MinWidth = 460; MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = "단축키 오버레이", FontSize = 23, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(new TextBlock { Text = "선택한 실행 파일의 포커스된 창에 표시합니다. 프로세스 재시작 후에도 적용되며 매핑 실행과 독립적으로 켜고 끌 수 있습니다. 클릭과 포커스는 대상 창으로 전달합니다.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 20) });
        panel.Children.Add(new TextBlock { Text = "대상 프로세스 (실행 파일 기준)" });
        var options = new List<ProcessOption> { new("대상 프로세스를 선택하세요", null) };
        foreach (var group in windows.Where(w => w.ProcessId != Environment.ProcessId &&
            (!string.IsNullOrWhiteSpace(w.Identity.ExecutablePath) || !string.IsNullOrWhiteSpace(w.Identity.ProcessName)))
            .GroupBy(w => string.IsNullOrWhiteSpace(w.Identity.ExecutablePath) ? w.Identity.ProcessName : w.Identity.ExecutablePath, StringComparer.OrdinalIgnoreCase))
        {
            var window = group.First(); var target = OverlayProcessTarget.FromWindow(window.Identity);
            if (string.IsNullOrWhiteSpace(target.ExecutableName)) continue;
            options.Add(new ProcessOption($"{target.ExecutableName} · PID {window.ProcessId}" +
                (string.IsNullOrWhiteSpace(target.ExecutablePath) ? " · 경로 접근 불가" : $" · {target.ExecutablePath}"), target));
        }
        var selected = options.FirstOrDefault(o => o.Target is not null && settings.TargetProcess is { } saved &&
            string.Equals(o.Target.ExecutableName, saved.ExecutableName, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(o.Target.ExecutablePath, saved.ExecutablePath, StringComparison.OrdinalIgnoreCase));
        if (selected is null && settings.TargetProcess is { } stored)
        {
            selected = new ProcessOption($"{stored.ExecutableName} · 저장된 대상 (현재 창 없음)", stored);
            options.Add(selected);
        }
        _process.ItemsSource = options; _process.SelectedItem = selected ?? options[0]; panel.Children.Add(_process);
        _enabled.IsChecked = settings.Enabled; panel.Children.Add(_enabled);
        var value = new TextBlock { Margin = new Thickness(0, 16, 0, 8) }; panel.Children.Add(value);
        _transparency.Value = settings.TransparencyPercent; panel.Children.Add(_transparency);
        panel.Children.Add(new TextBlock { Text = "0% = 불투명 · 100% = 완전 투명", FontSize = 11, Margin = new Thickness(0, 8, 0, 12) });
        var preview = new Border { Background = new SolidColorBrush(Color.FromRgb(22, 36, 52)), Padding = new Thickness(12),
            CornerRadius = new CornerRadius(6), Child = new TextBlock { Text = "Ctrl + A  ·  단축키 표시 예시", Foreground = Brushes.White } };
        panel.Children.Add(preview);
        void Update() { value.Text = $"투명도 {Math.Round(_transparency.Value)}%"; preview.Opacity = 1 - _transparency.Value / 100.0; }
        _transparency.ValueChanged += (_, _) => Update(); Update();
        var error = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 12, 0, 12) }; panel.Children.Add(error);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        var save = new Button { Content = "저장 및 적용", Margin = new Thickness(0, 0, 8, 0) };
        save.Click += (_, _) =>
        {
            try
            {
                var target = (_process.SelectedItem as ProcessOption)?.Target;
                if (_enabled.IsChecked == true && target is null) throw new InvalidOperationException("대상 프로세스를 선택하세요.");
                apply(new ShortcutOverlaySettings { Enabled = _enabled.IsChecked == true,
                    TransparencyPercent = (int)Math.Round(_transparency.Value), TargetProcess = target });
                DialogResult = true;
            }
            catch (Exception ex) { error.Text = $"오버레이 설정 저장 실패: {ex.Message}"; }
        };
        buttons.Children.Add(save); buttons.Children.Add(new Button { Content = "취소", IsCancel = true }); panel.Children.Add(buttons);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }
}
