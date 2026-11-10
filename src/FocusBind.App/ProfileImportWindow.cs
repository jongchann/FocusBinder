using System.Windows;
using System.Windows.Controls;
using FocusBind.Core.Profiles;

namespace FocusBind.App;

internal sealed class ProfileImportWindow : Window
{
    private sealed record MergeTarget(Guid Id, string Label)
    {
        public override string ToString() => Label;
    }
    internal Dictionary<Guid, ProfileImportChoice> Choices { get; } = [];
    private readonly List<Action> _readChoices = [];
    internal void ReadChoices() { foreach (var read in _readChoices) read(); }

    internal ProfileImportWindow(ProfileDocument current, ProfileDocument imported)
    {
        Title = "프로파일 가져오기 충돌 해결";
        Width = 700; Height = 600; MinWidth = 540; MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid { Margin = new Thickness(24) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition());
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = "같은 ID 또는 이름의 프로파일이 있습니다.\n병합: 기존 프로파일 설정과 같은 ID·단축키의 매핑을 유지하고 새로운 매핑을 추가합니다. 필요한 레이아웃도 함께 가져옵니다.\n복사본 추가: 별도 프로파일로 보관합니다. 건너뛰기: 해당 프로파일을 가져오지 않습니다.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16)
        });
        var rows = new StackPanel();
        foreach (var incoming in imported.Profiles)
        {
            var conflicts = ProfileTransfer.Conflicts(current, incoming);
            if (conflicts.Count == 0) continue;
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };
            row.Children.Add(new TextBlock { Text = $"가져올 프로파일: {incoming.Name} · 매핑 {incoming.Mappings.Count}개", TextWrapping = TextWrapping.Wrap });
            var mode = new ComboBox { Margin = new Thickness(0, 6, 0, 6), ItemsSource = new[] { "복사본 추가", "기존 프로파일에 병합", "건너뛰기" }, SelectedIndex = 0 };
            var target = new ComboBox { ItemsSource = conflicts.Select(p => new MergeTarget(p.Id, $"{p.Name} · {p.Window.ExecutableName ?? p.Window.ExecutablePath} · {p.Id.ToString()[..8]}")).ToArray(),
                SelectedValuePath = "Id", SelectedIndex = 0, IsEnabled = false };
            mode.SelectionChanged += (_, _) => target.IsEnabled = mode.SelectedIndex == 1;
            row.Children.Add(mode); row.Children.Add(target); rows.Children.Add(row);
            _readChoices.Add(() => Choices[incoming.Id] = new ProfileImportChoice(mode.SelectedIndex switch
            {
                1 => ProfileImportMode.Merge, 2 => ProfileImportMode.Skip, _ => ProfileImportMode.Copy
            }, mode.SelectedIndex == 1 ? (Guid)target.SelectedValue : null));
        }
        var scroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 1); grid.Children.Add(scroll);
        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var apply = new Button { Content = "선택대로 가져오기", Padding = new Thickness(12, 8, 12, 8), IsDefault = true };
        apply.Click += (_, _) => { ReadChoices(); DialogResult = true; };
        var cancel = new Button { Content = "전체 취소", IsCancel = true, Margin = new Thickness(8, 0, 0, 0), Padding = new Thickness(12, 8, 12, 8) };
        footer.Children.Add(apply); footer.Children.Add(cancel);
        Grid.SetRow(footer, 2); grid.Children.Add(footer); Content = grid;
    }
}
