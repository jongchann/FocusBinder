using System.IO;
using System.Globalization;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Interop;
using FocusBind.Core.Profiles;
using FocusBind.Core.Input;
using FocusBind.Core.Coordinates;
using FocusBind.Win32;
using FocusBind.Win32.Input;
using BindingModifiers = FocusBind.Core.Profiles.ModifierKeys;

namespace FocusBind.App;

public partial class BindingEditorWindow : Window
{
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private readonly WindowSnapshot _targetWindow;
    private readonly ProfileStore _store = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusBind", "profiles.json"));
    private ProfileDocument _document = new();
    private Profile? _selectedProfile;
    private Mapping? _selectedMapping;
    private bool _updatingInspector;
    private bool _recordingTrigger;
    private double _previewWidth = 640;
    private double _previewHeight = 360;
    private WindowSnapshot? _previewSnapshot;
    private bool _capturingPreview;
    private HwndSource? _messageSource;
    private readonly HashSet<Guid> _editableProfileIds = [];

    public BindingEditorWindow(WindowSnapshot targetWindow)
    {
        _targetWindow = targetWindow;
        InitializeComponent();
        InputMethod.SetIsInputMethodEnabled(KeyBox, false);
        SourceInitialized += (_, _) =>
        {
            _messageSource = HwndSource.FromHwnd(new WindowInteropHelper(this).Handle);
            _messageSource?.AddHook(WindowMessageHook);
        };
        Closed += (_, _) => { _messageSource?.RemoveHook(WindowMessageHook); _messageSource = null; };
        var aspect = (double)Math.Max(1, targetWindow.ClientBounds.Width) / Math.Max(1, targetWindow.ClientBounds.Height);
        if (aspect >= 640.0 / 360) _previewHeight = Math.Min(360, 640 / aspect);
        else _previewWidth = Math.Min(640, 360 * aspect);
        PreviewSurface.Width = _previewWidth;
        PreviewSurface.Height = _previewHeight;
        GridLineX1.X1 = GridLineX1.X2 = _previewWidth / 3;
        GridLineX2.X1 = GridLineX2.X2 = _previewWidth * 2 / 3;
        GridLineX1.Y2 = GridLineX2.Y2 = _previewHeight;
        GridLineY1.Y1 = GridLineY1.Y2 = _previewHeight / 3;
        GridLineY2.Y1 = GridLineY2.Y2 = _previewHeight * 2 / 3;
        GridLineY1.X2 = GridLineY2.X2 = _previewWidth;
        TargetText.Text = $"대상: {targetWindow.DisplayTitle} · {targetWindow.ClientBounds.Width}×{targetWindow.ClientBounds.Height}";
        Loaded += async (_, _) => { await LoadAsync(); if (IsVisible) await CapturePreviewAsync(); };
    }

    private async void RefreshPreview_Click(object sender, RoutedEventArgs e) => await CapturePreviewAsync();

    private async Task CapturePreviewAsync()
    {
        if (_capturingPreview) return;
        _capturingPreview = true;
        RefreshPreviewButton.IsEnabled = false;
        StopRecording();
        try
        {
            var target = WindowInspector.TryGet(_targetWindow.Handle);
            if (target is null || target.ProcessId != _targetWindow.ProcessId)
                throw new InvalidOperationException("대상 창이 닫혔습니다.");
            Hide();
            if (target.IsMinimized) WindowPreviewCapture.ShowWindow(target.Handle, 9);
            if (!WindowPreviewCapture.SetForegroundWindow(target.Handle))
                throw new InvalidOperationException("대상 창을 앞으로 가져오지 못했습니다.");
            await Task.Delay(350);
            target = WindowInspector.TryGet(target.Handle);
            if (target is null || target.ProcessId != _targetWindow.ProcessId)
                throw new InvalidOperationException("대상 창이 닫혔습니다.");
            var image = WindowPreviewCapture.Capture(target);
            TargetPreviewImage.Source = image;
            _previewSnapshot = target;
            var aspect = (double)target.ClientBounds.Width / target.ClientBounds.Height;
            _previewWidth = aspect >= 640.0 / 360 ? 640 : 360 * aspect;
            _previewHeight = aspect >= 640.0 / 360 ? 640 / aspect : 360;
            PreviewSurface.Width = _previewWidth;
            PreviewSurface.Height = _previewHeight;
            GridLineX1.X1 = GridLineX1.X2 = _previewWidth / 3;
            GridLineX2.X1 = GridLineX2.X2 = _previewWidth * 2 / 3;
            GridLineX1.Y2 = GridLineX2.Y2 = _previewHeight;
            GridLineY1.Y1 = GridLineY1.Y2 = _previewHeight / 3;
            GridLineY2.Y1 = GridLineY2.Y2 = _previewHeight * 2 / 3;
            GridLineY1.X2 = GridLineY2.X2 = _previewWidth;
            PreviewPlaceholder.Visibility = Visibility.Collapsed;
            DrawMarkers();
            UpdateCoordinateText();
            StatusText.Text = $"화면 캡처 {DateTime.Now:HH:mm:ss} · {image.PixelWidth}×{image.PixelHeight} · 십자 중심이 클릭 위치입니다.";
        }
        catch (Exception ex) { StatusText.Text = $"화면 캡처 실패: {ex.Message}"; }
        finally
        {
            Show();
            Activate();
            RefreshPreviewButton.IsEnabled = true;
            _capturingPreview = false;
        }
    }

    private CoordinateContext? PreviewContext(CoordinateTarget target)
    {
        var snapshot = _previewSnapshot ?? _targetWindow;
        var layout = _selectedProfile?.Layouts.FirstOrDefault(l => l.Id == target.LayoutId);
        return layout is null ? null : new CoordinateContext(new PixelRect(0, 0,
            snapshot.ClientBounds.Width, snapshot.ClientBounds.Height), snapshot.Dpi, layout);
    }

    private void PlaceCoordinate(CoordinateTarget target, double x, double y)
    {
        var context = PreviewContext(target);
        if (context is null) return;
        var point = new PixelPoint((int)Math.Round(Math.Clamp(x / _previewWidth, 0, 1) * (context.Client.Width - 1)),
            (int)Math.Round(Math.Clamp(y / _previewHeight, 0, 1) * (context.Client.Height - 1)));
        if (!CoordinateTransform.TryCapture(context, target, point)) return;
        UpdateCoordinateText();
    }

    private void MarkerCanvas_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_recordingTrigger || _selectedMapping is null || e.OriginalSource != MarkerCanvas) return;
        var target = FindCoordinateAction(_selectedMapping.Actions.Steps)?.Coordinate;
        if (target is null) return;
        var position = e.GetPosition(MarkerCanvas);
        PlaceCoordinate(target, position.X, position.Y);
        DrawMarkers();
        e.Handled = true;
    }

    private async Task LoadAsync()
    {
        try
        {
            _document = await _store.LoadAsync();
            var matching = _document.Profiles.Where(p => ProfileResolver.Matches(p.Window, _targetWindow.Identity)).ToList();
            if (matching.Count == 0)
            {
                var created = CreateProfile();
                _document.Profiles.Add(created);
                matching.Add(created);
                StatusText.Text = "새 프로필을 만들었습니다. 저장을 누르면 파일에 기록됩니다.";
            }
            else StatusText.Text = $"프로필 {matching.Count}개를 불러왔습니다.";
            ProfilesList.ItemsSource = matching;
            foreach (var profile in matching) _editableProfileIds.Add(profile.Id);
            ProfilesList.SelectedItem = ProfileResolver.Resolve(_document, _targetWindow.Identity) ?? matching[0];
        }
        catch (Exception ex)
        {
            StatusText.Text = $"프로필 로드 실패: {ex.Message}";
            MessageBox.Show(this, "설정 파일을 읽지 못했습니다. 기존 파일을 덮어쓰지 않도록 편집을 중단합니다.\n\n" + ex.Message,
                "FocusBind", MessageBoxButton.OK, MessageBoxImage.Error);
            Close();
        }
    }

    private Profile CreateProfile()
    {
        var path = _targetWindow.Identity.ExecutablePath;
        var profile = new Profile
        {
            Name = string.IsNullOrWhiteSpace(path)
                ? Path.GetFileNameWithoutExtension(_targetWindow.Identity.ProcessName) ?? "새 창 프로필"
                : Path.GetFileNameWithoutExtension(path),
            Window = new WindowMatcher
            {
                ExecutablePath = string.IsNullOrWhiteSpace(path) ? null : path,
                ExecutableName = string.IsNullOrWhiteSpace(path) ? _targetWindow.Identity.ProcessName : Path.GetFileName(path),
                WindowClass = _targetWindow.Identity.WindowClass
            }
        };
        profile.Layouts[0].ReferenceWidth = Math.Max(1, _targetWindow.ClientBounds.Width);
        profile.Layouts[0].ReferenceHeight = Math.Max(1, _targetWindow.ClientBounds.Height);
        return profile;
    }

    private void AddProfile_Click(object sender, RoutedEventArgs e)
    {
        var profile = CreateProfile();
        profile.Name += $" {ProfilesList.Items.Count + 1}";
        _document.Profiles.Add(profile);
        RefreshProfiles(profile);
        StatusText.Text = "프로필을 추가했습니다.";
    }

    private void DuplicateProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null) return;
        var copy = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(_selectedProfile))!;
        copy.Id = Guid.NewGuid();
        copy.Name += " 복사본";
        var ids = new Dictionary<Guid, Guid>();
        foreach (var layout in copy.Layouts)
        {
            var previous = layout.Id;
            layout.Id = Guid.NewGuid();
            ids[previous] = layout.Id;
        }
        foreach (var mapping in copy.Mappings)
        {
            mapping.Id = Guid.NewGuid();
            RemapCoordinates(mapping.Actions.Steps, ids);
        }
        _document.Profiles.Add(copy);
        RefreshProfiles(copy);
        StatusText.Text = "프로필을 복제했습니다.";
    }

    private static void RemapCoordinates(IEnumerable<AutomationAction> actions, Dictionary<Guid, Guid> ids)
    {
        foreach (var action in actions)
        {
            if (action.Coordinate is not null && ids.TryGetValue(action.Coordinate.LayoutId, out var id))
                action.Coordinate.LayoutId = id;
            RemapCoordinates(action.Steps, ids);
            RemapCoordinates(action.ElseSteps, ids);
        }
    }

    private void DeleteProfile_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null) return;
        _document.Profiles.Remove(_selectedProfile);
        _document.WindowSelections.RemoveAll(s => s.ProfileId == _selectedProfile.Id);
        RefreshProfiles(null);
        StatusText.Text = "프로필을 삭제했습니다. 저장하면 반영됩니다.";
    }

    private void RefreshProfiles(Profile? select)
    {
        if (select is not null) _editableProfileIds.Add(select.Id);
        ProfilesList.ItemsSource = _document.Profiles.Where(p =>
            _editableProfileIds.Contains(p.Id) || ProfileResolver.Matches(p.Window, _targetWindow.Identity)).ToList();
        ProfilesList.SelectedItem = select ?? ProfilesList.Items.Cast<Profile>().FirstOrDefault();
    }

    private void ProfilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedProfile = ProfilesList.SelectedItem as Profile;
        MappingsList.ItemsSource = _selectedProfile?.Mappings;
        MappingsList.SelectedIndex = _selectedProfile?.Mappings.Count > 0 ? 0 : -1;
        DrawMarkers();
        UpdateConflictStatus();
    }

    private void AddMapping_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null) return;
        var mapping = new Mapping
        {
            Name = $"클릭 {_selectedProfile.Mappings.Count + 1}",
            Trigger = new InputTrigger { Kind = TriggerKind.Key, KeyCode = 65 },
            Actions = new ActionSequence { Steps = [new AutomationAction
            {
                Kind = ActionKind.CoordinateClick,
                Coordinate = new CoordinateTarget { LayoutId = _selectedProfile.Layouts.Single(l => l.IsDefault).Id,
                    Space = CoordinateSpace.ContentNormalized, X = 0.5, Y = 0.5 }
            }] }
        };
        _selectedProfile.Mappings.Add(mapping);
        MappingsList.Items.Refresh();
        MappingsList.SelectedItem = mapping;
        DrawMarkers();
        UpdateConflictStatus();
        if (string.IsNullOrEmpty(ConflictText.Text))
            StatusText.Text = "매핑을 추가했습니다. 마커를 드래그해 클릭 위치를 정하세요.";
    }

    private void DeleteMapping_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null || _selectedMapping is null) return;
        _selectedProfile.Mappings.Remove(_selectedMapping);
        MappingsList.Items.Refresh();
        MappingsList.SelectedIndex = _selectedProfile.Mappings.Count > 0 ? 0 : -1;
        DrawMarkers();
        UpdateConflictStatus();
    }

    private void MappingsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => RefreshMappingInspector();

    private void RefreshMappingInspector()
    {
        StopRecording();
        _selectedMapping = MappingsList.SelectedItem as Mapping;
        _updatingInspector = true;
        MappingNameBox.Text = _selectedMapping?.Name ?? string.Empty;
        MappingEnabledBox.IsChecked = _selectedMapping?.Enabled ?? false;
        TriggerTypeCombo.SelectedIndex = _selectedMapping is null ? 0 : TriggerOptionIndex(_selectedMapping.Trigger);
        KeyCapturePanel.Visibility = TriggerTypeCombo.SelectedIndex == 0 ? Visibility.Visible : Visibility.Collapsed;
        KeyBox.Text = _selectedMapping is null ? string.Empty : FormatTrigger(_selectedMapping.Trigger);
        RefreshTriggerModifiers();
        UpdateTriggerPhaseInspector();
        ConsumeBox.IsChecked = _selectedMapping?.ConsumeOriginal ?? false;
        var repeat = _selectedMapping?.Actions.Steps.FirstOrDefault(a => a.Kind == ActionKind.Repeat);
        var wheel = SelectedWheelAction();
        ActionModeCombo.SelectedIndex = BasicActionMode(_selectedMapping?.Actions);
        RepeatSettings.Visibility = ActionModeCombo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        WheelSettings.Visibility = wheel is null ? Visibility.Collapsed : Visibility.Visible;
        CoordinateSettings.Visibility = wheel is null ? Visibility.Visible : Visibility.Collapsed;
        WheelDirectionCombo.SelectedIndex = wheel?.Kind == ActionKind.HorizontalWheel
            ? (wheel.Amount < 0 ? 3 : 2) : (wheel?.Amount < 0 ? 1 : 0);
        WheelAmountBox.Text = Math.Abs((long)(wheel?.Amount ?? 120)).ToString();
        var wheelModifiers = _selectedMapping is not null && WheelChord.TryRead(_selectedMapping.Actions.Steps, out _, out var modifiers)
            ? modifiers : BindingModifiers.None;
        SetModifierControls(wheelModifiers, WheelCtrlBox, WheelAltBox, WheelShiftBox, WheelWinBox);
        RepeatCountBox.Text = (repeat?.RepeatCount ?? 5).ToString();
        RepeatIntervalBox.Text = (repeat?.Steps.FirstOrDefault(a => a.Kind == ActionKind.Delay)?.DelayMilliseconds ?? 100).ToString();
        var coordinate = _selectedMapping is null ? null : FindCoordinateAction(_selectedMapping.Actions.Steps)?.Coordinate;
        ClickModeCombo.IsEnabled = coordinate is not null;
        ClickModeCombo.SelectedIndex = coordinate is null ? 0 : (int)coordinate.ClickMode;
        RestoreCursorBox.IsEnabled = coordinate is not null && coordinate.ClickMode != CoordinateClickMode.WindowMessage;
        RestoreCursorBox.IsChecked = coordinate?.RestoreCursor ?? false;
        UpdateClickModeWarning();
        _updatingInspector = false;
        UpdateCoordinateText();
        DrawMarkers();
        UpdateConflictStatus();
        UpdateMappingStatus();
    }

    private static int BasicActionMode(ActionSequence? sequence)
    {
        if (sequence is null || sequence.Steps.Count == 0) return 3;
        if (WheelChord.TryRead(sequence.Steps, out _, out _)) return 2;
        if (sequence.Steps.Count != 1) return 3;
        var step = sequence.Steps[0];
        if (step.Kind == ActionKind.CoordinateClick) return 0;
        if (step.Kind is ActionKind.VerticalWheel or ActionKind.HorizontalWheel) return 2;
        return step.Kind == ActionKind.Repeat && step.Steps.Count == 2 &&
            step.Steps[0].Kind == ActionKind.CoordinateClick && step.Steps[1].Kind == ActionKind.Delay ? 1 : 3;
    }

    private void UpdateMappingStatus()
    {
        MappingStatusText.Text = _selectedMapping is null ? "매핑을 선택하세요." : !_selectedMapping.Enabled
            ? "비활성 매핑입니다." : _selectedMapping.Actions.Steps.Count == 0 ? "실행할 액션이 없습니다."
            : InputActionRunner.GetUnsupportedReason(_selectedMapping.Actions, true) ?? "";
    }

    private void MappingEnabled_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingInspector || _selectedMapping is null) return;
        _selectedMapping.Enabled = MappingEnabledBox.IsChecked == true;
        UpdateMappingStatus();
    }

    private void ProfileSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null) return;
        StopRecording();
        var dialog = new ProfileSettingsWindow(_selectedProfile, _targetWindow.Identity) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _selectedProfile.Name = dialog.ProfileName;
        _selectedProfile.Window = dialog.Matcher;
        ProfilesList.Items.Refresh();
        StatusText.Text = "창 조건을 적용했습니다. 변경사항 저장을 누르면 파일에 기록됩니다.";
    }

    private void LayoutSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null) return;
        CommitInspector(); StopRecording();
        var dialog = new LayoutSettingsWindow(_selectedProfile) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _selectedProfile.Layouts = dialog.Layouts;
        RefreshMappingInspector();
        StatusText.Text = "레이아웃을 적용했습니다. 오른쪽 클릭 위치 설정에서 좌표 기준과 3×3 기준점을 선택하세요. 변경사항을 저장하세요.";
    }

    private void MoveMappingUp_Click(object sender, RoutedEventArgs e) => MoveMapping(-1);
    private void MoveMappingDown_Click(object sender, RoutedEventArgs e) => MoveMapping(1);
    private void MoveMapping(int direction)
    {
        if (_selectedProfile is null || _selectedMapping is null) return;
        var index = _selectedProfile.Mappings.IndexOf(_selectedMapping);
        var next = index + direction;
        if (next < 0 || next >= _selectedProfile.Mappings.Count) return;
        var selected = _selectedMapping;
        _selectedProfile.Mappings.RemoveAt(index);
        _selectedProfile.Mappings.Insert(next, selected);
        MappingsList.Items.Refresh();
        MappingsList.SelectedItem = selected;
    }

    private void EditActions_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null || _selectedMapping is null) return;
        CommitInspector(); StopRecording();
        var dialog = new ActionListWindow(_selectedProfile, _selectedMapping.Actions) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        _selectedMapping.Actions = dialog.Sequence;
        RefreshMappingInspector();
        StatusText.Text = "액션 목록을 적용했습니다. 변경사항 저장을 누르면 파일에 기록됩니다.";
    }

    private void DryRun_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null || _selectedMapping is null) return;
        CommitInspector(); StopRecording();
        var snapshot = WindowInspector.TryGet(_targetWindow.Handle) ?? _previewSnapshot ?? _targetWindow;
        new DryRunWindow(_selectedProfile, _selectedMapping, snapshot) { Owner = this }.ShowDialog();
    }

    private void CommitInspector()
    {
        CommitLongPressMilliseconds(); CommitWheelAmount(); CommitCoordinateOffsets();
        RepeatSettings_LostFocus(this, new RoutedEventArgs());
        if (_selectedMapping is not null) _selectedMapping.Name = MappingNameBox.Text.Trim();
    }

    private static int TriggerOptionIndex(InputTrigger trigger) => trigger.Kind switch
    {
        TriggerKind.Key => 0,
        TriggerKind.MouseButton => Math.Clamp(trigger.MouseButton, 1, 5),
        TriggerKind.VerticalWheel => trigger.WheelDirection == WheelDirection.Positive ? 6 : 7,
        TriggerKind.HorizontalWheel => trigger.WheelDirection == WheelDirection.Positive ? 8 : 9,
        _ => 0
    };

    private void TriggerTypeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingInspector || _selectedMapping is null) return;
        var trigger = _selectedMapping.Trigger;
        var index = TriggerTypeCombo.SelectedIndex;
        trigger.Kind = index switch
        {
            0 => TriggerKind.Key,
            >= 1 and <= 5 => TriggerKind.MouseButton,
            6 or 7 => TriggerKind.VerticalWheel,
            8 or 9 => TriggerKind.HorizontalWheel,
            _ => TriggerKind.Key
        };
        trigger.KeyCode = trigger.Kind == TriggerKind.Key ? (trigger.KeyCode is >= 1 and <= 255 ? trigger.KeyCode : 65) : 0;
        trigger.MouseButton = trigger.Kind == TriggerKind.MouseButton ? index : 0;
        trigger.WheelDirection = index is 6 or 8 ? WheelDirection.Positive :
            index is 7 or 9 ? WheelDirection.Negative : WheelDirection.None;
        if (trigger.Kind == TriggerKind.Key) trigger.Modifiers &= ~WheelChord.FlagForKey(trigger.KeyCode);
        if (trigger.Kind is TriggerKind.VerticalWheel or TriggerKind.HorizontalWheel)
            trigger.Phase = TriggerPhase.Press;
        KeyCapturePanel.Visibility = index == 0 ? Visibility.Visible : Visibility.Collapsed;
        KeyBox.Text = FormatTrigger(trigger);
        RefreshTriggerModifiers();
        UpdateTriggerPhaseInspector();
        UpdateConflictStatus();
        MappingsList.Items.Refresh();
    }

    private void RecordTriggerButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedMapping is null) return;
        _recordingTrigger = true;
        RecordTriggerButton.Content = "기록 중 · Esc 취소";
        StatusText.Text = "편집기 안에서 다음 키, 마우스 버튼 또는 수직·수평 휠을 입력하세요.";
    }

    private static BindingModifiers ReadModifierControls(CheckBox ctrl, CheckBox alt, CheckBox shift, CheckBox win) =>
        (ctrl.IsChecked == true ? BindingModifiers.Control : BindingModifiers.None) |
        (alt.IsChecked == true ? BindingModifiers.Alt : BindingModifiers.None) |
        (shift.IsChecked == true ? BindingModifiers.Shift : BindingModifiers.None) |
        (win.IsChecked == true ? BindingModifiers.Windows : BindingModifiers.None);

    private static void SetModifierControls(BindingModifiers modifiers, CheckBox ctrl, CheckBox alt, CheckBox shift, CheckBox win)
    {
        ctrl.IsChecked = modifiers.HasFlag(BindingModifiers.Control); alt.IsChecked = modifiers.HasFlag(BindingModifiers.Alt);
        shift.IsChecked = modifiers.HasFlag(BindingModifiers.Shift); win.IsChecked = modifiers.HasFlag(BindingModifiers.Windows);
    }

    private void RefreshTriggerModifiers()
    {
        var previous = _updatingInspector; _updatingInspector = true;
        SetModifierControls(_selectedMapping?.Trigger.Modifiers ?? BindingModifiers.None, TriggerCtrlBox, TriggerAltBox, TriggerShiftBox, TriggerWinBox);
        TriggerSummaryText.Text = _selectedMapping is null ? "" : FormatTrigger(_selectedMapping.Trigger);
        _updatingInspector = previous;
    }

    private void TriggerModifiers_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingInspector || _selectedMapping is null) return;
        var trigger = _selectedMapping.Trigger;
        trigger.Modifiers = ReadModifierControls(TriggerCtrlBox, TriggerAltBox, TriggerShiftBox, TriggerWinBox);
        if (trigger.Kind == TriggerKind.Key) trigger.Modifiers &= ~WheelChord.FlagForKey(trigger.KeyCode);
        RefreshTriggerInspector(); UpdateConflictStatus();
    }

    private void WheelModifiers_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingInspector || _selectedMapping is null || SelectedWheelAction() is not { } wheel) return;
        CommitWheelAmount();
        _selectedMapping.Actions.Steps = WheelChord.Wrap(wheel,
            ReadModifierControls(WheelCtrlBox, WheelAltBox, WheelShiftBox, WheelWinBox));
        RefreshMappingInspector();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recordingTrigger) return;
        var key = KeyNames.ResolveKey(e);
        var code = KeyInterop.VirtualKeyFromKey(key);
        if (key == Key.Escape)
        {
            StopRecording();
            StatusText.Text = "입력 기록을 취소했습니다.";
        }
        else if (code is not (0x10 or 0xA0 or 0xA1 or 0x11 or 0xA2 or 0xA3 or
                     0x12 or 0xA4 or 0xA5 or 0x5B or 0x5C)) CaptureKey(key);
        e.Handled = true;
    }

    private void Window_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (!_recordingTrigger || _selectedMapping is null) return;
        var button = e.ChangedButton switch
        {
            MouseButton.Left => 1,
            MouseButton.Right => 2,
            MouseButton.Middle => 3,
            MouseButton.XButton1 => 4,
            MouseButton.XButton2 => 5,
            _ => 0
        };
        if (button == 0) return;
        var trigger = _selectedMapping.Trigger;
        trigger.Kind = TriggerKind.MouseButton;
        trigger.MouseButton = button;
        trigger.KeyCode = 0;
        trigger.WheelDirection = WheelDirection.None;
        trigger.Modifiers = CurrentKeyboardModifiers();
        RefreshTriggerInspector();
        StopRecording();
        StatusText.Text = "마우스 버튼을 기록했습니다.";
        UpdateConflictStatus();
        e.Handled = true;
    }

    private void Window_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_recordingTrigger || _selectedMapping is null || e.Delta == 0) return;
        CaptureWheel(TriggerKind.VerticalWheel, e.Delta);
        e.Handled = true;
    }

    private nint WindowMessageHook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        const int WmMouseHorizontalWheel = 0x020E;
        if (message != WmMouseHorizontalWheel || !_recordingTrigger || _selectedMapping is null) return 0;
        var delta = unchecked((short)((wParam.ToInt64() >> 16) & 0xffff));
        if (delta == 0) return 0;
        CaptureWheel(TriggerKind.HorizontalWheel, delta);
        handled = true;
        return 0;
    }

    private void CaptureWheel(TriggerKind kind, int delta)
    {
        if (_selectedMapping is null) return;
        var trigger = _selectedMapping.Trigger;
        trigger.Kind = kind;
        trigger.WheelDirection = delta > 0 ? WheelDirection.Positive : WheelDirection.Negative;
        trigger.KeyCode = 0;
        trigger.MouseButton = 0;
        trigger.Phase = TriggerPhase.Press;
        trigger.Modifiers = CurrentKeyboardModifiers();
        RefreshTriggerInspector();
        StopRecording();
        StatusText.Text = kind == TriggerKind.HorizontalWheel
            ? "수평 휠 방향을 기록했습니다." : "수직 휠 방향을 기록했습니다.";
        UpdateConflictStatus();
    }

    private void RefreshTriggerInspector()
    {
        if (_selectedMapping is null) return;
        _updatingInspector = true;
        TriggerTypeCombo.SelectedIndex = TriggerOptionIndex(_selectedMapping.Trigger);
        KeyCapturePanel.Visibility = _selectedMapping.Trigger.Kind == TriggerKind.Key ? Visibility.Visible : Visibility.Collapsed;
        KeyBox.Text = FormatTrigger(_selectedMapping.Trigger);
        RefreshTriggerModifiers();
        UpdateTriggerPhaseInspector();
        _updatingInspector = false;
        MappingsList.Items.Refresh();
    }

    private void UpdateTriggerPhaseInspector()
    {
        var previous = _updatingInspector;
        _updatingInspector = true;
        var trigger = _selectedMapping?.Trigger;
        TriggerPhaseCombo.IsEnabled = trigger is { Kind: TriggerKind.Key or TriggerKind.MouseButton };
        TriggerPhaseCombo.SelectedIndex = trigger is null ? 0 : (int)trigger.Phase;
        LongPressSettings.Visibility = trigger?.Phase == TriggerPhase.LongPress
            ? Visibility.Visible : Visibility.Collapsed;
        LongPressMillisecondsBox.Text = (trigger?.LongPressMilliseconds ?? 500).ToString();
        _updatingInspector = previous;
    }

    private void TriggerPhaseCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingInspector || _selectedMapping is null || TriggerPhaseCombo.SelectedIndex < 0) return;
        CommitLongPressMilliseconds();
        _selectedMapping.Trigger.Phase = _selectedMapping.Trigger.Kind is TriggerKind.VerticalWheel or TriggerKind.HorizontalWheel
            ? TriggerPhase.Press : (TriggerPhase)TriggerPhaseCombo.SelectedIndex;
        UpdateTriggerPhaseInspector();
        UpdateConflictStatus();
    }

    private void LongPressMillisecondsBox_LostFocus(object sender, RoutedEventArgs e) => CommitLongPressMilliseconds();

    private void CommitLongPressMilliseconds()
    {
        if (_updatingInspector || _selectedMapping is null) return;
        var trigger = _selectedMapping.Trigger;
        trigger.LongPressMilliseconds = int.TryParse(LongPressMillisecondsBox.Text, out var value)
            ? Math.Clamp(value, 50, 60000) : trigger.LongPressMilliseconds;
        LongPressMillisecondsBox.Text = trigger.LongPressMilliseconds.ToString();
    }

    private void StopRecording()
    {
        _recordingTrigger = false;
        RecordTriggerButton.Content = "다음 입력 기록";
    }

    private void ActionModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingInspector || _selectedMapping is null) return;
        if (ActionModeCombo.SelectedIndex == 3)
        {
            RepeatSettings.Visibility = WheelSettings.Visibility = Visibility.Collapsed;
            return;
        }
        if (ActionModeCombo.SelectedIndex == 2)
        {
            _selectedMapping.Actions.Steps = [new AutomationAction { Kind = ActionKind.VerticalWheel, Amount = 120 }];
        }
        else
        {
            var click = FindCoordinateAction(_selectedMapping.Actions.Steps) ?? new AutomationAction
            {
                Kind = ActionKind.CoordinateClick,
                Coordinate = new CoordinateTarget { LayoutId = _selectedProfile!.Layouts.Single(l => l.IsDefault).Id,
                    Space = CoordinateSpace.ContentNormalized, X = 0.5, Y = 0.5 }
            };
            if (ActionModeCombo.SelectedIndex == 1)
            {
                if (_selectedMapping.Actions.Steps.FirstOrDefault() is not { Kind: ActionKind.Repeat })
                    _selectedMapping.Actions.Steps = [new AutomationAction
                    {
                        Kind = ActionKind.Repeat,
                        RepeatCount = 5,
                        Steps = [click, new AutomationAction { Kind = ActionKind.Delay, DelayMilliseconds = 100 }]
                    }];
            }
            else _selectedMapping.Actions.Steps = [click];
        }
        RefreshMappingInspector();
    }

    private AutomationAction? SelectedWheelAction() => _selectedMapping is not null &&
        WheelChord.TryRead(_selectedMapping.Actions.Steps, out var wheel, out _) ? wheel : null;

    private void WheelDirectionCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingInspector || SelectedWheelAction() is not { } wheel) return;
        wheel.Kind = WheelDirectionCombo.SelectedIndex >= 2 ? ActionKind.HorizontalWheel : ActionKind.VerticalWheel;
        var amount = (int)Math.Clamp(Math.Abs((long)wheel.Amount), 1, 12000);
        wheel.Amount = WheelDirectionCombo.SelectedIndex is 1 or 3 ? -amount : amount;
        CommitWheelAmount();
    }

    private void WheelAmountBox_LostFocus(object sender, RoutedEventArgs e) => CommitWheelAmount();

    private void CommitWheelAmount()
    {
        if (_updatingInspector || SelectedWheelAction() is not { } wheel) return;
        var amount = int.TryParse(WheelAmountBox.Text, out var value) ? Math.Clamp(value, 1, 12000)
            : (int)Math.Clamp(Math.Abs((long)wheel.Amount), 1, 12000);
        wheel.Amount = WheelDirectionCombo.SelectedIndex is 1 or 3 ? -amount : amount;
        WheelAmountBox.Text = amount.ToString();
    }

    private void RepeatSettings_LostFocus(object sender, RoutedEventArgs e)
    {
        if (ActionModeCombo.SelectedIndex != 1) return;
        var repeat = _selectedMapping?.Actions.Steps.FirstOrDefault(a => a.Kind == ActionKind.Repeat);
        if (repeat is null) return;
        repeat.RepeatCount = int.TryParse(RepeatCountBox.Text, out var count) ? Math.Clamp(count, 1, 1000) : 5;
        var delay = repeat.Steps.FirstOrDefault(a => a.Kind == ActionKind.Delay);
        if (delay is not null)
            delay.DelayMilliseconds = int.TryParse(RepeatIntervalBox.Text, out var interval) ? Math.Clamp(interval, 0, 60000) : 100;
        RepeatCountBox.Text = repeat.RepeatCount.ToString();
        RepeatIntervalBox.Text = (delay?.DelayMilliseconds ?? 100).ToString();
    }

    private static AutomationAction? FindCoordinateAction(IEnumerable<AutomationAction> actions)
    {
        foreach (var action in actions)
        {
            if (action.Kind == ActionKind.CoordinateClick) return action;
            var nested = FindCoordinateAction(action.Steps);
            if (nested is not null) return nested;
        }
        return null;
    }

    private static string FormatTrigger(InputTrigger trigger) => KeyNames.FormatTrigger(trigger);

    private void UpdateConflictStatus()
    {
        if (ConflictText is null) return;
        var conflict = _selectedProfile is null ? null : TriggerConflictDetector.Find(_selectedProfile).FirstOrDefault();
        ConflictText.Text = conflict is null ? string.Empty :
            $"트리거 충돌: '{conflict.First.Name}'와 '{conflict.Second.Name}'";
    }

    private void MappingNameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (_selectedMapping is null) return;
        _selectedMapping.Name = MappingNameBox.Text.Trim();
        MappingsList.Items.Refresh();
        DrawMarkers();
    }

    private void KeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (_selectedMapping is null) return;
        CaptureKey(KeyNames.ResolveKey(e));
        e.Handled = true;
    }

    private void CaptureKey(Key key)
    {
        if (_selectedMapping is null) return;
        var code = KeyInterop.VirtualKeyFromKey(key);
        if (code is < 1 or > 255) return;
        var modifiers = CurrentKeyboardModifiers();
        if (code is 0x10 or 0xA0 or 0xA1) modifiers &= ~BindingModifiers.Shift;
        if (code is 0x11 or 0xA2 or 0xA3) modifiers &= ~BindingModifiers.Control;
        if (code is 0x12 or 0xA4 or 0xA5) modifiers &= ~BindingModifiers.Alt;
        if (code is 0x5B or 0x5C) modifiers &= ~BindingModifiers.Windows;
        _selectedMapping.Trigger.Kind = TriggerKind.Key;
        _selectedMapping.Trigger.KeyCode = code;
        _selectedMapping.Trigger.MouseButton = 0;
        _selectedMapping.Trigger.WheelDirection = WheelDirection.None;
        _selectedMapping.Trigger.Modifiers = modifiers;
        RefreshTriggerInspector();
        StopRecording();
        StatusText.Text = "트리거 키를 변경했습니다. 저장을 누르면 반영됩니다.";
        UpdateConflictStatus();
    }

    private static BindingModifiers CurrentKeyboardModifiers()
    {
        var modifiers = BindingModifiers.None;
        if (Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift)) modifiers |= BindingModifiers.Shift;
        if (Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control)) modifiers |= BindingModifiers.Control;
        if (Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Alt)) modifiers |= BindingModifiers.Alt;
        if (Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Windows)) modifiers |= BindingModifiers.Windows;
        return modifiers;
    }

    private void ConsumeBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_selectedMapping is not null) _selectedMapping.ConsumeOriginal = ConsumeBox.IsChecked == true;
    }

    private void RestoreCursorBox_Changed(object sender, RoutedEventArgs e)
    {
        if (_updatingInspector || _selectedMapping is null) return;
        var coordinate = FindCoordinateAction(_selectedMapping.Actions.Steps)?.Coordinate;
        if (coordinate is not null) coordinate.RestoreCursor = RestoreCursorBox.IsChecked == true;
    }

    private void ClickModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingInspector || _selectedMapping is null) return;
        var coordinate = FindCoordinateAction(_selectedMapping.Actions.Steps)?.Coordinate;
        if (coordinate is null) return;
        if (ClickModeCombo.SelectedIndex is < 0 or > 2) return;
        coordinate.ClickMode = (CoordinateClickMode)ClickModeCombo.SelectedIndex;
        RestoreCursorBox.IsEnabled = coordinate.ClickMode != CoordinateClickMode.WindowMessage;
        UpdateClickModeWarning();
    }

    private void UpdateClickModeWarning() => ClickModeWarning.Visibility =
        ClickModeCombo.SelectedIndex == 1 &&
        WindowMessageClicker.IsKnownIncompatible(_targetWindow.Identity.WindowClass)
            ? Visibility.Visible : Visibility.Collapsed;

    private void DrawMarkers()
    {
        if (MarkerCanvas is null) return;
        MarkerCanvas.Children.Clear();
        if (_selectedProfile is null) return;
        foreach (var mapping in _selectedProfile.Mappings)
        {
            var target = FindCoordinateAction(mapping.Actions.Steps)?.Coordinate;
            if (target is null) continue;
            var marker = new Thumb
            {
                Style = (Style)FindResource("MarkerStyle"),
                Tag = mapping.Name,
                ToolTip = mapping.Name + " · 중심이 클릭 위치",
                Background = mapping == _selectedMapping ? Brushes.DarkOrange : Brushes.Teal
            };
            var context = PreviewContext(target);
            if (context is null || !CoordinateTransform.TryResolve(context, target, out var point)) continue;
            Canvas.SetLeft(marker, (double)point.X / Math.Max(1, context.Client.Width - 1) * _previewWidth - marker.Width / 2);
            Canvas.SetTop(marker, (double)point.Y / Math.Max(1, context.Client.Height - 1) * _previewHeight - marker.Height / 2);
            marker.DragCompleted += (_, _) => { MappingsList.SelectedItem = mapping; DrawMarkers(); };
            marker.DragDelta += (_, change) =>
            {
                var x = Math.Clamp(Canvas.GetLeft(marker) + change.HorizontalChange, -marker.Width / 2, _previewWidth - marker.Width / 2);
                var y = Math.Clamp(Canvas.GetTop(marker) + change.VerticalChange, -marker.Height / 2, _previewHeight - marker.Height / 2);
                Canvas.SetLeft(marker, x);
                Canvas.SetTop(marker, y);
                PlaceCoordinate(target, x + marker.Width / 2, y + marker.Height / 2);
            };
            MarkerCanvas.Children.Add(marker);
        }
    }

    private void UpdateCoordinateText()
    {
        var coordinate = _selectedMapping is null ? null : FindCoordinateAction(_selectedMapping.Actions.Steps)?.Coordinate;
        var previous = _updatingInspector; _updatingInspector = true;
        CoordinateSpaceCombo.IsEnabled = coordinate is not null;
        CoordinateSpaceCombo.SelectedItem = CoordinateSpaceCombo.Items.Cast<ComboBoxItem>()
            .FirstOrDefault(item => int.Parse(item.Tag.ToString()!) == (int)(coordinate?.Space ?? CoordinateSpace.ContentNormalized));
        AnchorSettings.Visibility = coordinate is not null && IsAnchored(coordinate.Space) ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in CoordinateAnchorGrid.Children.OfType<RadioButton>())
            button.IsChecked = int.Parse(button.Tag.ToString()!) == (int)(coordinate?.Anchor ?? Anchor.Center);
        CoordinateOffsetXBox.Text = coordinate?.X.ToString("0.########", CultureInfo.InvariantCulture) ?? "";
        CoordinateOffsetYBox.Text = coordinate?.Y.ToString("0.########", CultureInfo.InvariantCulture) ?? "";
        CoordinateAnchorHint.Text = coordinate?.Space == CoordinateSpace.AnchoredPixelOffset
            ? "X: 오른쪽 + / 왼쪽 − · Y: 아래 + / 위 −\n예: 우하 기준 X −80, Y −40 → 오른쪽·아래에서 80px·40px 유지"
            : "기준 레이아웃의 거리(px)이며 창 크기·레이아웃 배율에 따라 조정됩니다.";
        _updatingInspector = previous;
        CoordinateText.Text = coordinate is null ? "좌표 액션 없음" : IsAnchored(coordinate.Space)
            ? $"기준점 거리 X {coordinate.X:0.##} · Y {coordinate.Y:0.##} px"
            : coordinate.Space == CoordinateSpace.FixedClientPixel ? $"X {coordinate.X:0.##} · Y {coordinate.Y:0.##} px" : $"X {coordinate.X:P1} · Y {coordinate.Y:P1}";
        if (coordinate is not null && PreviewContext(coordinate) is { } context &&
            CoordinateTransform.TryResolve(context, coordinate, out var point))
            CoordinateText.Text += $"\n창 상대 {point.X}, {point.Y} px · {context.Client.Width}×{context.Client.Height}";
    }

    private static bool IsAnchored(CoordinateSpace space) => space is CoordinateSpace.AnchoredOffset or CoordinateSpace.AnchoredPixelOffset;

    private void CoordinateSpaceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingInspector || CoordinateSpaceCombo.SelectedItem is not ComboBoxItem item || _selectedMapping is null) return;
        var coordinate = FindCoordinateAction(_selectedMapping.Actions.Steps)?.Coordinate;
        if (coordinate is null) return;
        ChangeCoordinateBasis(coordinate, (CoordinateSpace)int.Parse(item.Tag.ToString()!), coordinate.Anchor, true);
    }

    private void CoordinateAnchor_Checked(object sender, RoutedEventArgs e)
    {
        if (_updatingInspector || sender is not RadioButton button || _selectedMapping is null) return;
        var coordinate = FindCoordinateAction(_selectedMapping.Actions.Steps)?.Coordinate;
        if (coordinate is null || !IsAnchored(coordinate.Space)) return;
        ChangeCoordinateBasis(coordinate, coordinate.Space, (Anchor)int.Parse(button.Tag.ToString()!), false);
    }

    private void ChangeCoordinateBasis(CoordinateTarget coordinate, CoordinateSpace space, Anchor anchor, bool autoAnchor)
    {
        var context = PreviewContext(coordinate);
        if (context is null || !CoordinateTransform.TryResolve(context, coordinate, out var point))
        {
            StatusText.Text = "현재 클릭 위치가 영역 밖입니다. 먼저 마커나 화면에서 유효한 지점을 선택하세요.";
            UpdateCoordinateText(); return;
        }
        if (autoAnchor && IsAnchored(space) && !IsAnchored(coordinate.Space))
        {
            var content = context.Content;
            var column = Math.Clamp((int)((point.X - (double)content.X) / Math.Max(1, content.Width) * 3), 0, 2);
            var row = Math.Clamp((int)((point.Y - (double)content.Y) / Math.Max(1, content.Height) * 3), 0, 2);
            anchor = (Anchor)(row * 3 + column);
        }
        var converted = new CoordinateTarget { LayoutId = coordinate.LayoutId, Space = space, Anchor = anchor };
        if (CoordinateTransform.TryCapture(context, converted, point))
        {
            coordinate.Space = space; coordinate.Anchor = anchor;
            coordinate.X = converted.X; coordinate.Y = converted.Y;
            StatusText.Text = "클릭 위치를 유지하며 좌표 기준을 바꿨습니다. 변경사항을 저장하세요.";
        }
        else StatusText.Text = "현재 위치를 선택한 좌표 기준으로 변환할 수 없습니다.";
        UpdateCoordinateText(); DrawMarkers();
    }

    private void CoordinateOffsets_LostFocus(object sender, RoutedEventArgs e) => CommitCoordinateOffsets();

    private void CommitCoordinateOffsets()
    {
        if (_updatingInspector || _selectedMapping is null) return;
        var coordinate = FindCoordinateAction(_selectedMapping.Actions.Steps)?.Coordinate;
        if (coordinate is null || !IsAnchored(coordinate.Space)) return;
        if (double.TryParse(CoordinateOffsetXBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) && double.IsFinite(x) &&
            double.TryParse(CoordinateOffsetYBox.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var y) && double.IsFinite(y))
        {
            coordinate.X = x; coordinate.Y = y;
            if (PreviewContext(coordinate) is { } context && !CoordinateTransform.TryResolve(context, coordinate, out _))
                StatusText.Text = "입력한 거리는 현재 창 영역 밖입니다. X·Y를 조정하세요. 영역 밖 클릭은 실행하지 않습니다.";
            else StatusText.Text = "기준점 거리를 적용했습니다. 변경사항을 저장하세요.";
        }
        else StatusText.Text = "거리는 유한한 숫자로 입력하세요. 소수점은 . 을 사용합니다.";
        UpdateCoordinateText(); DrawMarkers();
    }

    private void CaptureCoordinate_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedProfile is null || _selectedMapping is null) return;
        var coordinate = FindCoordinateAction(_selectedMapping.Actions.Steps)?.Coordinate;
        if (coordinate is null) return;
        var snapshot = WindowInspector.TryGet(_targetWindow.Handle);
        if (snapshot is null || !snapshot.IsVisible || snapshot.IsMinimized || snapshot.ClientBounds.IsEmpty)
        {
            StatusText.Text = "대상 창이 보이지 않습니다. 복원한 뒤 다시 선택하세요.";
            return;
        }
        var layout = _selectedProfile.Layouts.FirstOrDefault(l => l.Id == coordinate.LayoutId);
        if (layout is null) { StatusText.Text = "좌표 레이아웃을 찾지 못했습니다."; return; }
        var overlay = new CoordinateCaptureWindow(snapshot) { Owner = this };
        if (overlay.ShowForTarget() != true || overlay.SelectedPoint is not { } point)
        {
            StatusText.Text = overlay.CaptureError ?? "좌표 선택을 취소했습니다.";
            return;
        }
        var context = new CoordinateContext(new PixelRect(snapshot.ClientBounds.X, snapshot.ClientBounds.Y,
            snapshot.ClientBounds.Width, snapshot.ClientBounds.Height), snapshot.Dpi, layout);
        if (!CoordinateTransform.TryCapture(context, coordinate, point))
        {
            StatusText.Text = "선택 지점이 현재 콘텐츠 영역 밖입니다.";
            return;
        }
        DrawMarkers();
        UpdateCoordinateText();
        StatusText.Text = "대상 창에서 클릭 지점을 선택했습니다. 저장을 누르면 반영됩니다.";
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        CommitInspector();
        try
        {
            var conflicts = _document.Profiles.SelectMany(TriggerConflictDetector.Find).ToList();
            if (conflicts.Count > 0)
            {
                StatusText.Text = $"저장 전 트리거 충돌을 해결하세요: {conflicts[0].First.Name} / {conflicts[0].Second.Name}";
                return;
            }
            if (_selectedProfile is not null && ProfileResolver.Matches(_selectedProfile.Window, _targetWindow.Identity))
            {
                ProfileSelections.Select(_document, _selectedProfile.Id, _targetWindow.Identity);
            }
            await _store.SaveAsync(_document);
            StatusText.Text = $"저장 완료: {_store.Path}";
        }
        catch (Exception ex) { StatusText.Text = $"저장 실패: {ex.Message}"; }
    }
}
