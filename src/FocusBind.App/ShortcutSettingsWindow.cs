using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace FocusBind.App;

internal sealed class ShortcutSettingsWindow : Window
{
    internal List<ShortcutBinding> Bindings { get; }
    internal static string Label(ShortcutAction action) => action switch
    {
        ShortcutAction.MappingToggle => "매핑 시작 / 종료", ShortcutAction.VolumeUp => "대상 창 음량 +5%",
        ShortcutAction.VolumeDown => "대상 창 음량 -5%", _ => "대상 창 음소거 토글"
    };
    internal ShortcutSettingsWindow(List<ShortcutBinding> bindings, Action<List<ShortcutBinding>> apply)
    {
        Bindings = bindings.Select(b => new ShortcutBinding { Action = b.Action, KeyCode = b.KeyCode, Modifiers = b.Modifiers }).ToList();
        Title = "전역 기능 단축키 설정"; Width = 600; SizeToContent = SizeToContent.Height; ResizeMode = ResizeMode.CanMinimize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock { Text = "전역 단축키", FontSize = 24, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 9) });
        panel.Children.Add(new TextBlock { Text = "어느 앱에서든 빠르게 제어하세요.", Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush"), Margin = new Thickness(0, 0, 0, 20) });
        panel.Children.Add(new TextBlock { Text = "입력란에서 Ctrl·Alt·Shift·Win과 키를 함께 누르세요.\n소리는 단축키를 누를 때 포커스된 앱에 적용됩니다.", Margin = new Thickness(0, 0, 0, 20), TextWrapping = TextWrapping.Wrap });
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8), Foreground = (System.Windows.Media.Brush)FindResource("AccentBrush") };
        var boxes = new List<TextBox>();
        foreach (var binding in Bindings)
        {
            panel.Children.Add(new TextBlock { Text = Label(binding.Action) });
            var box = new TextBox { IsReadOnly = true, Text = KeyNames.Format(binding.KeyCode, binding.Modifiers), Margin = new Thickness(0, 4, 0, 12) };
            box.ToolTip = "선택한 뒤 원하는 보조키와 키를 함께 누르세요.";
            boxes.Add(box);
            box.PreviewKeyDown += (_, e) =>
            {
                if (e.Key == Key.Tab) return;
                e.Handled = true;
                var code = KeyInterop.VirtualKeyFromKey(e.Key == Key.System ? e.SystemKey : e.Key);
                if (code is 0 or 0x10 or 0x11 or 0x12 or 0x5B or 0x5C or >= 0xA0 and <= 0xA5) return;
                var modifiers = KeyNames.Current();
                if (modifiers == 0) { status.Text = "Ctrl·Alt·Shift·Win 중 하나 이상과 키를 함께 눌러 주세요."; return; }
                binding.KeyCode = code; binding.Modifiers = modifiers;
                box.Text = KeyNames.Format(code, modifiers);
                status.Text = $"{Label(binding.Action)} 단축키를 변경했습니다. 저장 및 적용을 눌러 주세요.";
            };
            panel.Children.Add(box);
        }
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
        panel.Children.Add(status);
        panel.Children.Add(save);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var reset = new Button { Content = "기본값 복원", Margin = new Thickness(0, 0, 8, 0) };
        reset.Click += (_, _) =>
        {
            var defaults = GlobalShortcuts.Defaults();
            for (var index = 0; index < Bindings.Count; index++)
            {
                var value = defaults.Single(b => b.Action == Bindings[index].Action);
                Bindings[index].KeyCode = value.KeyCode;
                Bindings[index].Modifiers = value.Modifiers;
                boxes[index].Text = KeyNames.Format(value.KeyCode, value.Modifiers);
            }
            status.Text = "기본값으로 변경했습니다. 저장 및 적용을 눌러 주세요.";
        };
        var cancel = new Button { Content = "취소", IsCancel = true };
        buttons.Children.Add(reset);
        buttons.Children.Add(cancel);
        panel.Children.Add(buttons);
        Content = panel;
    }
}
