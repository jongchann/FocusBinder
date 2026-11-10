using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using FocusBind.Core.Input;
using FocusBind.Core.Profiles;

namespace FocusBind.App;

internal sealed class ActionListWindow : Window
{
    private sealed class Node(List<AutomationAction> container, AutomationAction action)
    {
        internal List<AutomationAction> Container { get; } = container;
        internal AutomationAction Action { get; set; } = action;
    }
    private readonly Profile _profile;
    private readonly TreeView _tree = new();
    private readonly StackPanel _form = new();
    private readonly ComboBox _addKind = new() { MinWidth = 180 };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 10, 0, 10) };
    private TreeViewItem? _selectedItem;
    private Node? _selected;
    private Func<AutomationAction>? _readDraft;
    private bool _refreshing;
    internal ActionSequence Sequence { get; }

    internal ActionListWindow(Profile profile, ActionSequence sequence)
    {
        _profile = profile;
        Sequence = JsonSerializer.Deserialize<ActionSequence>(JsonSerializer.Serialize(sequence))!;
        Title = "액션 목록 편집"; Width = 900; Height = 700; MinWidth = 700; MinHeight = 500;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = "위에서 아래로 실행합니다. 반복은 펼쳐서 내부 액션을 편집하세요. 적용 전까지 원본은 유지됩니다.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _tree.Margin = new Thickness(0, 0, 16, 0); body.Children.Add(_tree);
        var scroll = new ScrollViewer { Content = _form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        Grid.SetColumn(scroll, 1); body.Children.Add(scroll); Grid.SetRow(body, 1); grid.Children.Add(body);
        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        foreach (var kind in new[] { ActionKind.KeyTap, ActionKind.KeyDown, ActionKind.KeyUp, ActionKind.MouseClick,
            ActionKind.MouseDown, ActionKind.MouseUp, ActionKind.VerticalWheel, ActionKind.HorizontalWheel,
            ActionKind.CoordinateClick, ActionKind.Delay, ActionKind.Repeat })
            _addKind.Items.Add(new ComboBoxItem { Content = KindLabel(kind), Tag = kind });
        _addKind.SelectedIndex = 0; buttons.Children.Add(_addKind);
        AddButton(buttons, "추가", () => Add(false));
        AddButton(buttons, "반복 안에 추가", () => Add(true));
        AddButton(buttons, "삭제", Delete);
        AddButton(buttons, "↑", () => Move(-1));
        AddButton(buttons, "↓", () => Move(1));
        Grid.SetRow(buttons, 2); grid.Children.Add(buttons);
        Grid.SetRow(_status, 3); grid.Children.Add(_status);
        var footer = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right };
        AddButton(footer, "적용", () => { if (TryCommit() && ValidateSequence()) DialogResult = true; });
        var cancel = new Button { Content = "취소", IsCancel = true, Margin = new Thickness(6, 0, 0, 0) };
        footer.Children.Add(cancel); Grid.SetRow(footer, 4); grid.Children.Add(footer);
        Content = grid;
        _tree.SelectedItemChanged += (_, e) =>
        {
            if (_refreshing) return;
            var next = e.NewValue as TreeViewItem;
            if (!TryCommit())
            {
                _refreshing = true;
                if (_selectedItem is not null) _selectedItem.IsSelected = true;
                _refreshing = false;
                return;
            }
            _selectedItem = next;
            _selected = next?.Tag as Node;
            ShowForm();
        };
        RefreshTree(Sequence.Steps.FirstOrDefault());
    }

    private static void AddButton(Panel panel, string label, Action click)
    {
        var button = new Button { Content = label, Margin = new Thickness(6, 0, 0, 6), Padding = new Thickness(10, 5, 10, 5) };
        button.Click += (_, _) => click(); panel.Children.Add(button);
    }
    private void Add(bool inside)
    {
        if (!TryCommit()) return;
        var list = _selected?.Container ?? Sequence.Steps;
        if (inside)
        {
            if (_selected?.Action.Kind != ActionKind.Repeat) { _status.Text = "반복 액션을 먼저 선택하세요."; return; }
            list = _selected.Action.Steps;
        }
        var kind = (ActionKind)((ComboBoxItem)_addKind.SelectedItem).Tag;
        var action = new AutomationAction { Kind = kind, KeyCode = 65, MouseButton = 1, Amount = 120,
            DelayMilliseconds = 100, RepeatCount = 5 };
        if (kind == ActionKind.CoordinateClick) action.Coordinate = new CoordinateTarget
        { LayoutId = _profile.Layouts.Single(l => l.IsDefault).Id, X = 0.5, Y = 0.5 };
        if (kind == ActionKind.Repeat) action.Steps.Add(new AutomationAction { Kind = ActionKind.Delay, DelayMilliseconds = 100 });
        var insert = !inside && _selected is not null ? list.IndexOf(_selected.Action) + 1 : list.Count;
        list.Insert(insert, action); RefreshTree(action);
    }
    private void Delete()
    {
        if (_selected is null) return;
        var list = _selected.Container;
        var index = list.IndexOf(_selected.Action);
        list.RemoveAt(index);
        RefreshTree(list.Count == 0 ? null : list[Math.Min(index, list.Count - 1)]);
    }
    private void Move(int offset)
    {
        if (!TryCommit() || _selected is null) return;
        var list = _selected.Container; var action = _selected.Action;
        var index = list.IndexOf(action); var next = index + offset;
        if (next < 0 || next >= list.Count) return;
        list.RemoveAt(index); list.Insert(next, action); RefreshTree(action);
    }
    private void RefreshTree(AutomationAction? select)
    {
        _refreshing = true; _tree.Items.Clear(); _selected = null; _selectedItem = null; _readDraft = null;
        Build(_tree.Items, Sequence.Steps, 0);
        _refreshing = false; ShowForm();
        void Build(ItemCollection items, List<AutomationAction> actions, int depth)
        {
            if (depth > 12) return;
            foreach (var action in actions)
            {
                var node = new Node(actions, action);
                var item = new TreeViewItem { Header = Describe(action), Tag = node, IsExpanded = true };
                items.Add(item);
                if (action == select) { item.IsSelected = true; _selectedItem = item; _selected = node; }
                Build(item.Items, action.Steps, depth + 1);
                if (action.ElseSteps.Count > 0)
                {
                    var otherwise = new TreeViewItem { Header = "그 외 (현재 실행 미지원)", IsExpanded = true };
                    item.Items.Add(otherwise); Build(otherwise.Items, action.ElseSteps, depth + 1);
                }
            }
        }
    }
    private void ShowForm()
    {
        _form.Children.Clear(); _readDraft = null;
        if (_selected is null) { _form.Children.Add(new TextBlock { Text = "액션을 선택하거나 추가하세요." }); return; }
        var original = _selected.Action;
        var draft = JsonSerializer.Deserialize<AutomationAction>(JsonSerializer.Serialize(original))!;
        // Keep child list identities so tree nodes remain valid after committing a parent.
        draft.Steps = original.Steps; draft.ElseSteps = original.ElseSteps;
        _form.Children.Add(new TextBlock { Text = KindLabel(draft.Kind), FontSize = 19, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 0, 0, 12) });
        var readers = new List<Action>();
        switch (draft.Kind)
        {
            case ActionKind.KeyDown or ActionKind.KeyUp or ActionKind.KeyTap:
                Number("키 코드 VK (1~255)", draft.KeyCode, value => draft.KeyCode = Integer(value, 1, 255)); break;
            case ActionKind.MouseDown or ActionKind.MouseUp or ActionKind.MouseClick:
                Number("버튼 (1 왼쪽, 2 오른쪽, 3 가운데, 4 X1, 5 X2)", draft.MouseButton, value => draft.MouseButton = Integer(value, 1, 5)); break;
            case ActionKind.VerticalWheel or ActionKind.HorizontalWheel:
                Number("휠 양 (한 칸 120, 음수는 아래/왼쪽)", draft.Amount, value => draft.Amount = Integer(value, -12000, 12000)); break;
            case ActionKind.Delay:
                Number("대기 시간 (0~60000ms)", draft.DelayMilliseconds, value => draft.DelayMilliseconds = Integer(value, 0, 60000)); break;
            case ActionKind.Repeat:
                Number("반복 횟수 (1~1000)", draft.RepeatCount, value => draft.RepeatCount = Integer(value, 1, 1000)); break;
            case ActionKind.CoordinateClick when draft.Coordinate is { } coordinate:
                Choice("레이아웃", _profile.Layouts.Select(l => (l.Id, l.Name)), coordinate.LayoutId, value => coordinate.LayoutId = value);
                Choice("좌표 공간", new[] { (CoordinateSpace.ClientNormalized, "창 비율 (0~1)"),
                    (CoordinateSpace.ContentNormalized, "콘텐츠 비율 (0~1)"), (CoordinateSpace.AnchoredOffset, "앵커 기준 거리 (창 크기에 비례)"),
                    (CoordinateSpace.AnchoredPixelOffset, "앵커 기준 고정 거리 (px)"),
                    (CoordinateSpace.FixedClientPixel, "창 기준 고정 픽셀") }, coordinate.Space, value => coordinate.Space = value);
                _form.Children.Add(new TextBlock { Text = "3×3 앵커 (앵커 기준 거리에서 사용)" });
                var anchorGrid = new UniformGrid { Rows = 3, Columns = 3, Margin = new Thickness(0, 4, 0, 12) };
                var selectedAnchor = coordinate.Anchor;
                var anchorLabels = new[] { "좌상", "상", "우상", "좌", "중앙", "우", "좌하", "하", "우하" };
                foreach (var anchor in Enum.GetValues<Anchor>())
                {
                    var radio = new RadioButton { Content = anchorLabels[(int)anchor], GroupName = "CoordinateAnchor",
                        IsChecked = anchor == selectedAnchor, Margin = new Thickness(4) };
                    radio.Checked += (_, _) => selectedAnchor = anchor; anchorGrid.Children.Add(radio);
                }
                _form.Children.Add(anchorGrid); readers.Add(() => coordinate.Anchor = selectedAnchor);
                Number("X (정규화 공간은 0~1)", coordinate.X, value => coordinate.X = Real(value));
                Number("Y (정규화 공간은 0~1)", coordinate.Y, value => coordinate.Y = Real(value));
                Choice("클릭 방식", new[] { (CoordinateClickMode.Cursor, "커서 이동"), (CoordinateClickMode.WindowMessage, "창 메시지"),
                    (CoordinateClickMode.FastCursor, "빠른 이동·클릭·복원") }, coordinate.ClickMode, value => coordinate.ClickMode = value);
                var restore = new CheckBox { Content = "클릭 후 커서 원위치", IsChecked = coordinate.RestoreCursor, Margin = new Thickness(0, 8, 0, 8) };
                _form.Children.Add(restore); readers.Add(() => coordinate.RestoreCursor = restore.IsChecked == true); break;
            default:
                _form.Children.Add(new TextBlock { Text = "이 액션은 아직 실행을 지원하지 않습니다. 원문을 유지하며 삭제·순서 변경은 가능합니다.", TextWrapping = TextWrapping.Wrap }); break;
        }
        _readDraft = () => { foreach (var read in readers) read(); return draft; };
        void Number(string label, object value, Action<string> read)
        {
            _form.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap });
            var box = new TextBox { Text = Convert.ToString(value, CultureInfo.InvariantCulture), Margin = new Thickness(0, 4, 0, 12) };
            _form.Children.Add(box); readers.Add(() => read(box.Text ?? ""));
        }
        void Choice<T>(string label, IEnumerable<(T Value, string Label)> choices, T value, Action<T> read) where T : notnull
        {
            _form.Children.Add(new TextBlock { Text = label });
            var combo = new ComboBox { Margin = new Thickness(0, 4, 0, 12) };
            foreach (var choice in choices)
            {
                var item = new ComboBoxItem { Content = choice.Label, Tag = choice.Value };
                combo.Items.Add(item); if (Equals(choice.Value, value)) combo.SelectedItem = item;
            }
            _form.Children.Add(combo); readers.Add(() => read((T)((ComboBoxItem)combo.SelectedItem).Tag));
        }
    }
    private bool TryCommit()
    {
        if (_selected is null || _readDraft is null) return true;
        try
        {
            var draft = _readDraft();
            var errors = ValidateSteps([draft]);
            if (errors.Count > 0) { _status.Text = string.Join(Environment.NewLine, errors); return false; }
            var index = _selected.Container.IndexOf(_selected.Action);
            _selected.Container[index] = draft; _selected.Action = draft;
            if (_selectedItem is not null) _selectedItem.Header = Describe(draft);
            _status.Text = ""; return true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or InvalidOperationException)
        { _status.Text = ex.Message; return false; }
    }
    private IReadOnlyList<string> ValidateSteps(List<AutomationAction> steps) => ProfileValidator.Validate(new ProfileDocument
    {
        Profiles = [new Profile { Name = "액션 검증", Window = _profile.Window, Layouts = _profile.Layouts,
            Mappings = [new Mapping { Trigger = new InputTrigger { KeyCode = 65 }, Actions = new ActionSequence { Steps = steps } }] }]
    });
    private bool ValidateSequence()
    {
        var errors = ValidateSteps(Sequence.Steps);
        _status.Text = string.Join(Environment.NewLine, errors);
        return errors.Count == 0;
    }
    private static int Integer(string text, int min, int max) => int.TryParse(text, out var value) && value >= min && value <= max
        ? value : throw new FormatException($"정수 {min}~{max} 범위로 입력하세요.");
    private static double Real(string text) => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
        ? value : throw new FormatException("유한한 숫자를 입력하세요. 소수점은 . 을 사용합니다.");
    private static string Describe(AutomationAction action) => action.Kind switch
    {
        ActionKind.KeyDown or ActionKind.KeyUp or ActionKind.KeyTap => $"{KindLabel(action.Kind)} · VK {action.KeyCode}",
        ActionKind.Delay => $"대기 {action.DelayMilliseconds}ms",
        ActionKind.Repeat => $"반복 {action.RepeatCount}회",
        ActionKind.VerticalWheel or ActionKind.HorizontalWheel => $"{KindLabel(action.Kind)} {action.Amount}",
        _ => KindLabel(action.Kind)
    };
    private static string KindLabel(ActionKind kind) => kind switch
    {
        ActionKind.KeyDown => "키 누름", ActionKind.KeyUp => "키 뗌", ActionKind.KeyTap => "키 탭",
        ActionKind.MouseDown => "마우스 누름", ActionKind.MouseUp => "마우스 뗌", ActionKind.MouseClick => "마우스 클릭",
        ActionKind.VerticalWheel => "세로 휠", ActionKind.HorizontalWheel => "가로 휠",
        ActionKind.CoordinateClick => "좌표 클릭", ActionKind.Delay => "대기", ActionKind.Repeat => "반복",
        _ => $"{kind} (실행 미지원)"
    };
}
