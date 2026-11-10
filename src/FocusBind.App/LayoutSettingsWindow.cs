using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using FocusBind.Core.Profiles;

namespace FocusBind.App;

internal sealed class LayoutSettingsWindow : Window
{
    private readonly Profile _draft;
    private readonly ListBox _list = new() { DisplayMemberPath = "Name" };
    private readonly StackPanel _form = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) };
    private LayoutVariant? _selected;
    private Func<LayoutVariant>? _read;
    private bool _refreshing;
    internal List<LayoutVariant> Layouts => _draft.Layouts;
    internal LayoutSettingsWindow(Profile profile)
    {
        _draft = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(profile))!;
        Title = "레이아웃 설정"; Width = 850; Height = 730; MinWidth = 650; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = "콘텐츠 영역은 창 안의 비율(0~1)로 설정합니다. 레이아웃은 종횡비·우선순위에 따라 선택됩니다. 기본 레이아웃은 하나입니다.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(210) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _list.Margin = new Thickness(0, 0, 16, 0); body.Children.Add(_list);
        var scroll = new ScrollViewer { Content = _form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll, 1); body.Children.Add(scroll); Grid.SetRow(body, 1); grid.Children.Add(body);
        Grid.SetRow(_status, 2); grid.Children.Add(_status);
        var buttons = new WrapPanel();
        Button("추가", Add); Button("삭제", Delete); Button("기본으로 지정", SetDefault);
        Button("적용", () => { if (Commit() && Validate()) DialogResult = true; });
        buttons.Children.Add(new Button { Content = "취소", IsCancel = true, Margin = new Thickness(6, 0, 0, 6) });
        Grid.SetRow(buttons, 3); grid.Children.Add(buttons); Content = grid;
        _list.SelectionChanged += (_, _) =>
        {
            if (_refreshing) return;
            var next = _list.SelectedItem as LayoutVariant;
            if (!Commit()) { _refreshing = true; _list.SelectedItem = _selected; _refreshing = false; return; }
            _refreshing = true; _selected = next; _list.SelectedItem = next; _refreshing = false; ShowForm();
        };
        Refresh(Layouts.FirstOrDefault());
        void Button(string text, Action run)
        {
            var button = new Button { Content = text, Margin = new Thickness(0, 0, 6, 6) };
            button.Click += (_, _) => run(); buttons.Children.Add(button);
        }
    }
    private void Refresh(LayoutVariant? selected)
    {
        _refreshing = true; _list.ItemsSource = null; _list.ItemsSource = Layouts;
        _selected = selected; _list.SelectedItem = selected; _refreshing = false; ShowForm();
    }
    private void Add()
    {
        if (!Commit()) return;
        var layout = new LayoutVariant { Name = $"레이아웃 {Layouts.Count + 1}", IsDefault = false,
            Priority = Layouts.Max(l => l.Priority) < int.MaxValue ? Layouts.Max(l => l.Priority) + 1 : int.MaxValue };
        Layouts.Add(layout); Refresh(layout);
    }
    private void Delete()
    {
        if (_selected is null) return;
        if (_selected.IsDefault) { _status.Text = "기본 레이아웃은 삭제할 수 없습니다. 다른 레이아웃을 기본으로 지정하세요."; return; }
        if (_draft.Mappings.Any(m => Uses(m.Actions.Steps, _selected.Id)))
        { _status.Text = "이 레이아웃을 사용하는 좌표 액션이 있습니다. 액션의 레이아웃을 변경한 뒤 삭제하세요."; return; }
        Layouts.Remove(_selected); Refresh(Layouts.FirstOrDefault());
        static bool Uses(List<AutomationAction> actions, Guid id) => actions.Any(a =>
            a.Coordinate?.LayoutId == id || Uses(a.Steps, id) || Uses(a.ElseSteps, id));
    }
    private void SetDefault()
    {
        if (!Commit() || _selected is null) return;
        foreach (var layout in Layouts) layout.IsDefault = layout == _selected;
        ShowForm(); _status.Text = "기본 레이아웃을 변경했습니다.";
    }
    private void ShowForm()
    {
        _form.Children.Clear(); _read = null;
        if (_selected is null) return;
        var source = _selected;
        _form.Children.Add(new TextBlock { Text = source.IsDefault ? "기본 레이아웃" : "추가 레이아웃", FontSize = 20,
            FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        var name = Field("이름", source.Name);
        var priority = Field("우선순위 (높을수록 우선)", source.Priority);
        var width = Field("기준 가로 크기 (px)", source.ReferenceWidth);
        var height = Field("기준 세로 크기 (px)", source.ReferenceHeight);
        var minAspect = Field("최소 종횡비 (가로 / 세로)", source.MinAspectRatio);
        var maxAspect = Field("최대 종횡비", source.MaxAspectRatio);
        var x = Field("콘텐츠 X (0~1)", source.Content.X);
        var y = Field("콘텐츠 Y (0~1)", source.Content.Y);
        var w = Field("콘텐츠 가로 비율 (0 초과~1)", source.Content.Width);
        var h = Field("콘텐츠 세로 비율 (0 초과~1)", source.Content.Height);
        _form.Children.Add(new TextBlock { Text = "앵커 오프셋 크기 조정" });
        var scale = new ComboBox { Margin = new Thickness(0, 4, 0, 12) };
        foreach (var value in Enum.GetValues<ScalePolicy>())
        {
            var item = new ComboBoxItem { Content = value.ToString(), Tag = value }; scale.Items.Add(item);
            if (value == source.Scale) scale.SelectedItem = item;
        }
        _form.Children.Add(scale);
        _read = () => new LayoutVariant { Id = source.Id, IsDefault = source.IsDefault, Name = name.Text.Trim(),
            Priority = Int(priority), ReferenceWidth = Int(width), ReferenceHeight = Int(height),
            MinAspectRatio = Real(minAspect), MaxAspectRatio = Real(maxAspect),
            Content = new NormalizedRect { X = Real(x), Y = Real(y), Width = Real(w), Height = Real(h) },
            Scale = (ScalePolicy)((ComboBoxItem)scale.SelectedItem).Tag };
        TextBox Field(string text, object value)
        {
            _form.Children.Add(new TextBlock { Text = text });
            var box = new TextBox { Text = Convert.ToString(value, CultureInfo.InvariantCulture), Margin = new Thickness(0, 4, 0, 12) };
            _form.Children.Add(box); return box;
        }
    }
    private bool Commit()
    {
        if (_selected is null || _read is null) return true;
        try
        {
            var candidate = _read();
            var index = Layouts.IndexOf(_selected); var previous = _selected;
            Layouts[index] = candidate;
            if (!Validate()) { Layouts[index] = previous; return false; }
            _selected = candidate;
            _refreshing = true; _list.Items.Refresh(); _list.SelectedItem = candidate; _refreshing = false;
            // Rebuild the readers so repeated commits follow the current default flag.
            ShowForm(); return true;
        }
        catch (FormatException ex) { _status.Text = ex.Message; return false; }
    }
    private bool Validate()
    {
        var errors = ProfileValidator.Validate(new ProfileDocument { Profiles = [new Profile
            { Name = _draft.Name, Window = _draft.Window, Layouts = Layouts }] });
        _status.Text = string.Join(Environment.NewLine, errors); return errors.Count == 0;
    }
    private static int Int(TextBox box) => int.TryParse(box.Text, out var value) ? value : throw new FormatException("정수를 입력하세요.");
    private static double Real(TextBox box) => double.TryParse(box.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
        ? value : throw new FormatException("유한한 숫자를 입력하세요. 소수점은 . 을 사용합니다.");
}
