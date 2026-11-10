using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using BindingModifiers = FocusBind.Core.Profiles.ModifierKeys;

namespace FocusBind.App;

internal sealed class ShortcutEditorRow
{
    internal ShortcutBinding Binding { get; }
    internal StackPanel View { get; } = new() { Margin = new Thickness(0, 0, 0, 16) };
    internal readonly CheckBox CtrlBox = new() { Content = "Ctrl", Margin = new Thickness(0, 0, 12, 0) };
    internal readonly CheckBox AltBox = new() { Content = "Alt", Margin = new Thickness(0, 0, 12, 0) };
    internal readonly CheckBox ShiftBox = new() { Content = "Shift", Margin = new Thickness(0, 0, 12, 0) };
    internal readonly CheckBox WinBox = new() { Content = "Win" };
    internal readonly ComboBox KeyCombo = new() { MinWidth = 110, Margin = new Thickness(0, 0, 10, 0), IsTextSearchEnabled = true };
    internal readonly TextBox CaptureBox = new() { IsReadOnly = true, ToolTip = "이 칸에서 Ctrl·Alt·Shift·Win과 키를 함께 눌러 기록" };
    private readonly Action<string> _status;
    private bool _updating;

    internal ShortcutEditorRow(ShortcutBinding binding, Action<string> status)
    {
        Binding = binding; _status = status;
        View.Children.Add(new TextBlock { Text = ShortcutSettingsWindow.Label(binding.Action), FontWeight = FontWeights.SemiBold });
        var modifiers = new WrapPanel { Margin = new Thickness(0, 6, 0, 6) };
        foreach (var box in new[] { CtrlBox, AltBox, ShiftBox, WinBox })
        { modifiers.Children.Add(box); box.Checked += ManualChanged; box.Unchecked += ManualChanged; }
        View.Children.Add(modifiers);
        var keys = Enumerable.Range(0x41, 26).Concat(Enumerable.Range(0x30, 10)).Concat(Enumerable.Range(0x70, 24))
            .Concat(Enumerable.Range(8, 248)).Distinct().Where(GlobalShortcuts.IsShortcutKey);
        foreach (var code in keys)
            KeyCombo.Items.Add(new ComboBoxItem { Content = KeyNames.Format(code, BindingModifiers.None, false), Tag = code });
        KeyCombo.SelectionChanged += (_, _) => ManualChanged(this, new RoutedEventArgs());
        InputMethod.SetIsInputMethodEnabled(CaptureBox, false);
        CaptureBox.PreviewKeyDown += (_, e) => Record(e, KeyNames.Current());
        var keyGrid = new Grid();
        keyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(145) });
        keyGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        keyGrid.Children.Add(KeyCombo); Grid.SetColumn(CaptureBox, 1); keyGrid.Children.Add(CaptureBox); View.Children.Add(keyGrid);
        SetChord(binding.KeyCode, binding.Modifiers, false);
        if (!GlobalShortcuts.IsShortcutKey(binding.KeyCode))
            _status($"{ShortcutSettingsWindow.Label(binding.Action)}: 기존 키를 사용할 수 없습니다. 키 목록에서 다시 선택하세요. 다른 설정은 유지됩니다.");
    }

    internal void SetChord(int code, BindingModifiers modifiers, bool announce = true)
    {
        _updating = true;
        Binding.KeyCode = code; Binding.Modifiers = modifiers;
        CtrlBox.IsChecked = modifiers.HasFlag(BindingModifiers.Control); AltBox.IsChecked = modifiers.HasFlag(BindingModifiers.Alt);
        ShiftBox.IsChecked = modifiers.HasFlag(BindingModifiers.Shift); WinBox.IsChecked = modifiers.HasFlag(BindingModifiers.Windows);
        KeyCombo.SelectedItem = KeyCombo.Items.Cast<ComboBoxItem>().FirstOrDefault(item => (int)item.Tag == code);
        CaptureBox.Text = KeyNames.Format(code, modifiers); _updating = false;
        if (announce) Changed();
    }

    internal void Record(KeyEventArgs e, BindingModifiers modifiers)
    {
        var key = KeyNames.ResolveKey(e);
        if (key == Key.Tab && modifiers is BindingModifiers.None or BindingModifiers.Shift) return;
        e.Handled = true;
        var code = KeyInterop.VirtualKeyFromKey(key);
        if (!GlobalShortcuts.IsShortcutKey(code)) return;
        if (modifiers == BindingModifiers.None) { _status("Ctrl·Alt·Shift·Win 중 하나 이상을 함께 지정하세요."); return; }
        SetChord(code, modifiers);
    }

    private void ManualChanged(object sender, RoutedEventArgs e)
    {
        if (_updating || KeyCombo.SelectedItem is not ComboBoxItem item) return;
        var modifiers = (CtrlBox.IsChecked == true ? BindingModifiers.Control : BindingModifiers.None) |
            (AltBox.IsChecked == true ? BindingModifiers.Alt : BindingModifiers.None) |
            (ShiftBox.IsChecked == true ? BindingModifiers.Shift : BindingModifiers.None) |
            (WinBox.IsChecked == true ? BindingModifiers.Windows : BindingModifiers.None);
        SetChord((int)item.Tag, modifiers);
    }
    private void Changed() => _status($"{ShortcutSettingsWindow.Label(Binding.Action)}: {KeyNames.Format(Binding.KeyCode, Binding.Modifiers, false)} · 저장 및 적용을 눌러 주세요.");
}
