using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FocusBind.App;

internal sealed class ShortcutSettingsWindow : Window
{
    internal List<ShortcutBinding> Bindings { get; }
    private readonly List<ShortcutEditorRow> _rows = [];
    internal static string Label(ShortcutAction action) => action switch
    {
        ShortcutAction.MappingToggle => "매핑 시작 / 종료", ShortcutAction.VolumeUp => "대상 창 음량 +5%",
        ShortcutAction.VolumeDown => "대상 창 음량 -5%", ShortcutAction.MuteToggle => "대상 창 음소거 토글",
        _ => "대상 프로세스 오버레이 켜기 / 끄기"
    };
    internal ShortcutSettingsWindow(List<ShortcutBinding> bindings, Action<List<ShortcutBinding>> apply)
    {
        Bindings = bindings.Select(b => new ShortcutBinding { Action = b.Action, KeyCode = b.KeyCode, Modifiers = b.Modifiers }).ToList();
        Title = "전역 기능 단축키 설정"; Width = 680; Height = 760; MinWidth = 560; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid { Margin = new Thickness(24) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "전역 단축키", FontSize = 24, FontWeight = FontWeights.SemiBold });
        header.Children.Add(new TextBlock { Text = "보조키를 체크하고 키 목록에서 A~Z·숫자·F키를 직접 선택하거나, 오른쪽 기록란에서 조합을 누르세요. 소리는 누르는 순간 포커스된 앱에 적용됩니다.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 18) });
        grid.Children.Add(header);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8), Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush") };
        var rows = new StackPanel();
        foreach (var binding in Bindings)
        {
            var row = new ShortcutEditorRow(binding, message => status.Text = message);
            _rows.Add(row); rows.Children.Add(row.View);
        }
        var scroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        var save = new Button { Content = "저장 및 적용", Padding = new Thickness(12, 9, 12, 9), Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(0, 8, 0, 0) };
        save.Click += (_, _) =>
        {
            try
            {
                GlobalShortcuts.Validate(Bindings);
                apply(Bindings);
                DialogResult = true;
            }
            catch (Exception ex) { status.Text = $"적용 실패: {ex.Message}"; }
        };
        Grid.SetRow(status, 2); grid.Children.Add(status);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var reset = new Button { Content = "기본값 복원", Margin = new Thickness(0, 0, 8, 0) };
        reset.Click += (_, _) =>
        {
            var defaults = GlobalShortcuts.Defaults();
            foreach (var row in _rows)
            {
                var value = defaults.Single(b => b.Action == row.Binding.Action);
                row.SetChord(value.KeyCode, value.Modifiers, false);
            }
            status.Text = "기본값으로 변경했습니다. 저장 및 적용을 눌러 주세요.";
        };
        var cancel = new Button { Content = "취소", IsCancel = true };
        buttons.Children.Add(reset);
        buttons.Children.Add(save);
        buttons.Children.Add(cancel);
        save.Margin = new Thickness(0, 0, 8, 0);
        Grid.SetRow(buttons, 3); grid.Children.Add(buttons);
        Content = grid;
    }
}
