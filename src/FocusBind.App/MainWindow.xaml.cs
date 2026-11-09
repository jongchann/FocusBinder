using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.IO;
using System.Diagnostics;
using System.Windows.Threading;
using System.Windows.Interop;
using FocusBind.Win32;
using FocusBind.Win32.Input;
using FocusBind.Core.Input;
using FocusBind.Core.Profiles;

namespace FocusBind.App;

public partial class MainWindow : Window
{
    private readonly ForegroundWatcher _watcher;
    private readonly int? _ownIntegrityRid = ProcessIntegrityInspector.TryGetRid((uint)Environment.ProcessId);
    private InputRuntime? _runtime;
    private RawKeyboardInputSink? _rawKeyboard;
    private string? _rawKeyboardError;
    private GlobalShortcuts? _shortcuts;
    private readonly DiagnosticLog _shortcutLog = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusBind",
        $"shortcuts-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log"));
    private List<ShortcutBinding> _shortcutBindings = GlobalShortcuts.Defaults();
    private bool _startingRuntime;
    private int _openEditors;
    private bool _transferringProfiles;
    private readonly DispatcherTimer _runtimeStatusTimer;
    private readonly ProfileStore _store = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusBind", "profiles.json"));

    public MainWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => InitializeShortcuts();
        _watcher = new ForegroundWatcher();
        _watcher.Changed += OnWindowChanged;
        _runtimeStatusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _runtimeStatusTimer.Tick += (_, _) => RefreshRuntimeStatus();
        _runtimeStatusTimer.Start();
        Activated += (_, _) => RefreshWindows();
        Closed += (_, _) => { _shortcuts?.Dispose(); _shortcutLog.Dispose(); _runtimeStatusTimer.Stop(); _rawKeyboard?.Dispose(); _runtime?.Dispose(); _watcher.Dispose(); };
        ShowSnapshot(WindowInspector.GetForeground(), "현재 포커스");
        RefreshWindows();
    }

    private void OnWindowChanged(WindowChange change)
    {
        Dispatcher.BeginInvoke(() => ShowSnapshot(change.Window,
            change.Kind == WindowChangeKind.Foreground ? "포커스 변경" : "위치·크기 변경"));
    }

    private void ShowSnapshot(WindowSnapshot? window, string reason)
    {
        EventText.Text = reason;
        if (window is null)
        {
            TitleText.Text = "활성 창 없음";
            ProcessText.Text = ClassText.Text = BoundsText.Text = DpiText.Text = string.Empty;
            return;
        }
        TitleText.Text = $"제목: {window.DisplayTitle}";
        ProcessText.Text = string.IsNullOrWhiteSpace(window.Identity.ExecutablePath)
            ? $"실행 파일 경로 접근 불가 · 프로세스: {window.DisplayProcessName} (PID {window.ProcessId})"
            : $"실행 파일: {window.Identity.ExecutablePath} (PID {window.ProcessId})";
        ClassText.Text = $"클래스: {window.Identity.WindowClass}";
        BoundsText.Text = $"클라이언트: {window.ClientBounds.X}, {window.ClientBounds.Y} / {window.ClientBounds.Width}×{window.ClientBounds.Height}";
        DpiText.Text = $"DPI: {window.Dpi} · 표시: {window.IsVisible} · 최소화: {window.IsMinimized} · 입력 가능: {window.CanReceiveInput}";
        if (window.ProcessId != Environment.ProcessId) ShowElevationWarning(window);
    }

    private void ShowElevationWarning(WindowSnapshot window)
    {
        var targetRid = ProcessIntegrityInspector.TryGetRid(window.ProcessId);
        ElevationPanel.Visibility = targetRid > _ownIntegrityRid ? Visibility.Visible : Visibility.Collapsed;
    }

    private void WindowsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WindowsList?.SelectedItem is WindowSnapshot selected)
        {
            ShowElevationWarning(selected);
        }
    }

    private void InitializeShortcuts()
    {
        _shortcuts = new GlobalShortcuts(HwndSource.FromHwnd(new WindowInteropHelper(this).Handle)!);
        _shortcuts.Invoked += HandleShortcut;
        ShortcutStatusText.ToolTip = $"단축키 진단 로그: {_shortcutLog.Path}";
        try { _shortcutBindings = GlobalShortcuts.Load(); _shortcuts.Apply(_shortcutBindings); ShowShortcutSummary(); _shortcutLog.Write("shortcuts registered count=4"); }
        catch (Exception ex) { _shortcutLog.Write($"registration failed {ex}"); ShortcutStatusText.Text = $"단축키 설정/등록 실패: {ex.Message}"; }
    }

    private void ShowShortcutSummary()
    {
        ShortcutSummaryText.Text = string.Join(Environment.NewLine, _shortcutBindings.Select(b =>
            $"{ShortcutSettingsWindow.Label(b.Action)}   {KeyNames.Format(b.KeyCode, b.Modifiers)}"));
        ShortcutStatusText.Text = string.Empty;
    }

    private void ShortcutSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_shortcuts is null) return;
        _shortcuts.Clear();
        try
        {
            var dialog = new ShortcutSettingsWindow(_shortcutBindings, bindings =>
            {
                try
                {
                    _shortcuts.Apply(bindings);
                    GlobalShortcuts.Save(bindings);
                    _shortcutBindings = bindings;
                }
                catch
                {
                    _shortcuts.Clear(); // Keep recording available while the dialog remains open.
                    throw;
                }
            }) { Owner = this };
            if (dialog.ShowDialog() != true) _shortcuts.Apply(_shortcutBindings);
            ShowShortcutSummary();
        }
        catch (Exception ex)
        {
            try { _shortcuts.Apply(_shortcutBindings); }
            catch (Exception restore) { ShortcutStatusText.Text = $"단축키 복원 실패: {restore.Message}"; return; }
            ShortcutStatusText.Text = $"단축키 적용 실패(이전 설정 유지): {ex.Message}";
        }
    }

    private void HandleShortcut(ShortcutAction action)
    {
        _shortcutLog.Write($"shortcut received action={action}");
        if (action == ShortcutAction.MappingToggle) { InputToggle_Click(this, new RoutedEventArgs()); return; }
        try
        {
            var target = WindowInspector.GetForeground();
            if (target is null)
                throw new InvalidOperationException("현재 포커스된 앱을 확인할 수 없습니다.");
            AudioTargetText.Text = $"최근 소리 조절 대상: {target.DisplayProcessName} · PID {target.ProcessId}";
            _shortcutLog.Write($"audio target pid={target.ProcessId} class={target.Identity.WindowClass}");
            var count = ProcessAudio.Adjust(target.ProcessId, action == ShortcutAction.VolumeUp ? 0.05f : -0.05f,
                null, toggleMute: action == ShortcutAction.MuteToggle, trace: _shortcutLog.Write);
            _shortcutLog.Write($"audio completed matchedSessions={count}");
            ShortcutStatusText.Text = count == 0 ? "대상 앱의 오디오 세션이 없습니다. 앱에서 소리를 재생한 후 다시 시도하세요." :
                $"{target.DisplayProcessName}: {ShortcutSettingsWindow.Label(action)} · {count}개 오디오 세션 적용";
        }
        catch (Exception ex)
        {
            _shortcutLog.Write($"audio failed hresult=0x{ex.HResult:X8} {ex}");
            ShortcutStatusText.Text = $"소리 조절 실패: {ex.Message} · 로그: {_shortcutLog.Path}";
        }
    }

    private void RestartElevated_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("실행 파일 경로를 찾지 못했습니다.");
            var start = new ProcessStartInfo(executable) { UseShellExecute = true, Verb = "runas" };
            if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
                start.ArgumentList.Add(System.Reflection.Assembly.GetEntryAssembly()?.Location
                    ?? throw new InvalidOperationException("앱 DLL 경로를 찾지 못했습니다."));
            var process = Process.Start(start);
            if (process is null) throw new InvalidOperationException("관리자 권한으로 실행하지 못했습니다.");
            Close();
        }
        catch (Exception ex) { RuntimeStatusText.Text = $"관리자 권한 재시작 취소/실패: {ex.Message}"; }
    }

    private void RefreshWindows_Click(object sender, RoutedEventArgs e) => RefreshWindows();
    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OpenEditor_Click(object sender, RoutedEventArgs e)
    {
        if (_transferringProfiles) return;
        if (_runtime is not null) StopRuntime("편집 중에는 매핑을 중지합니다. 저장 후 다시 시작하세요.");
        if (WindowsList.SelectedItem is not WindowSnapshot selected)
        {
            MessageBox.Show(this, "먼저 목록에서 대상 창을 선택하세요.", "FocusBind");
            return;
        }
        var current = WindowInspector.TryGet(selected.Handle);
        if (current is null)
        {
            MessageBox.Show(this, "선택한 창이 종료되었습니다. 목록을 새로고침하세요.", "FocusBind");
            return;
        }
        var editor = new BindingEditorWindow(current) { Owner = this };
        _openEditors++;
        editor.Closed += (_, _) => _openEditors--;
        editor.Show();
    }

    private async void InputToggle_Click(object sender, RoutedEventArgs e)
    {
        if (_startingRuntime || _transferringProfiles) return;
        if (_runtime is not null)
        {
            StopRuntime("입력 매핑: 중지됨");
            return;
        }
        if (_openEditors > 0) { RuntimeStatusText.Text = "키 바인딩 편집기를 닫은 뒤 매핑을 시작하세요."; return; }
        _startingRuntime = true;
        try
        {
            var document = await _store.LoadAsync();
            if (document.Profiles.Count == 0 || !document.Profiles.Any(p => p.Mappings.Any(m =>
                m.Enabled && m.Actions.Steps.Count > 0 && InputActionRunner.Supports(m.Actions, true))))
            {
                RuntimeStatusText.Text = "저장된 활성 매핑이 없습니다. 대상 창을 선택해 편집하세요.";
                return;
            }
            _runtime = new InputRuntime(document);
            try
            {
                _rawKeyboard = new RawKeyboardInputSink(this);
                _rawKeyboard.KeyboardInput += _runtime.HandleRawKeyboard;
                _rawKeyboard.Tick += _runtime.PollRawKeyboard;
                _runtime.EnableRawKeyboard();
                _rawKeyboardError = null;
            }
            catch (Exception ex) { _rawKeyboardError = ex.Message; }
            DiagnosticPathBox.Text = $"진단 로그: {_runtime.DiagnosticLogPath}";
            DiagnosticPathBox.Visibility = Visibility.Visible;
            InputToggleButton.Content = "매핑 중지";
            PauseButton.IsEnabled = true;
            PauseButton.Content = _runtime.IsPaused ? "재개" : "일시정지";
            RuntimeStatusText.Text = _runtime.IsPaused ? "입력 매핑: 일시정지" :
                "입력 매핑: 활성화 · 포커스된 대상 창에서만 실행";
        }
        catch (Exception ex) { RuntimeStatusText.Text = $"매핑 시작 실패: {ex.Message}"; }
        finally { _startingRuntime = false; }
    }

    private void StopRuntime(string status)
    {
        _rawKeyboard?.Dispose();
        _rawKeyboard = null;
        _runtime?.Dispose();
        _runtime = null;
        InputToggleButton.Content = "매핑 시작";
        PauseButton.IsEnabled = false;
        PauseButton.Content = "일시정지";
        RuntimeStatusText.Text = status;
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (_runtime is null) return;
        if (_runtime.IsPaused) _runtime.Resume();
        else _runtime.Pause();
        PauseButton.Content = _runtime.IsPaused ? "재개" : "일시정지";
        RuntimeStatusText.Text = _runtime.IsPaused ? "입력 매핑: 일시정지" :
            "입력 매핑: 활성화 · 포커스된 대상 창에서만 실행";
    }

    private void RefreshRuntimeStatus()
    {
        var runtime = _runtime;
        if (runtime is null) return;
        if (runtime.IsPaused) { RuntimeStatusText.Text = "입력 매핑: 일시정지"; return; }
        var target = runtime.ActiveProfileName;
        var status = target is null ? "입력 매핑: 활성화 · 현재 창에 일치하는 프로필 없음" :
            $"입력 매핑: {target}";
        status += $" · 훅 입력 {runtime.ObservedInputs} · Raw 키 {runtime.RawKeyboardInputs} · 후보 입력 {runtime.CandidateInputs} · 트리거 {runtime.MatchedTriggers} · 실행 {runtime.StartedRuns} · 완료 {runtime.CompletedRuns}";
        status += $"\n훅 스레드: {(runtime.HookThreadAlive ? "실행 중" : "종료됨")} · 타이머 {runtime.HookTimerTicks} · 콜백 {runtime.HookCallbacks}";
        status += $" (키보드 {runtime.KeyboardCallbacks}, 마우스 {runtime.MouseCallbacks})";
        if (runtime.LastExternalWindowDiagnostic is { } diagnostic) status += $"\n{diagnostic}";
        if (runtime.HookError is { } hookError) status += $" · 훅 오류: {hookError}";
        if (runtime.DiagnosticLogError is { } logError) status += $" · 로그 오류: {logError}";
        if (_rawKeyboardError is { } rawError) status += $" · Raw 입력 오류: {rawError}";
        if (_rawKeyboard?.LastError is { } rawFailure) status += $" · Raw 입력 오류: {rawFailure.Message}";
        if (runtime.LastError is { } error) status += $" · 오류: {error.Message}";
        else if (runtime.LastCancellationReason is { } reason && runtime.StartedRuns > runtime.CompletedRuns)
            status += $" · 마지막 중단: {reason}";
        RuntimeStatusText.Text = status;
    }
    private void RefreshWindows()
    {
        var selectedHandle = (WindowsList.SelectedItem as WindowSnapshot)?.Handle;
        WindowsList.ItemsSource = WindowInspector.ListTopLevelWindows();
        ApplySearch();
        if (selectedHandle is not null)
            WindowsList.SelectedItem = WindowsList.Items.Cast<WindowSnapshot>()
                .FirstOrDefault(window => window.Handle == selectedHandle);
    }

    private void WindowSearchBox_TextChanged(object sender, TextChangedEventArgs e) => ApplySearch();
    private void ShowAuxiliaryBox_Changed(object sender, RoutedEventArgs e) => ApplySearch();

    private void ApplySearch()
    {
        if (WindowsList is null || WindowSearchBox is null || WindowsList.ItemsSource is null) return;
        var terms = WindowSearchBox.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var view = CollectionViewSource.GetDefaultView(WindowsList.ItemsSource);
        view.Filter = item => item is WindowSnapshot window &&
            (ShowAuxiliaryBox.IsChecked == true || !window.IsAuxiliary || window.IsMinimized)
            && terms.All(term =>
            window.DisplayTitle.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            window.Identity.ExecutablePath.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            window.Identity.WindowClass.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            window.ProcessId.ToString().Contains(term, StringComparison.OrdinalIgnoreCase));
        view.Refresh();
        ResultCountText.Text = $"{WindowsList.Items.Count}개 / 전체 {TotalWindowCount()}개";
    }

    private int TotalWindowCount() => (WindowsList.ItemsSource as IReadOnlyList<WindowSnapshot>)?.Count ?? 0;

    private async void ExportProfiles_Click(object sender, RoutedEventArgs e)
    {
        if (_transferringProfiles || _startingRuntime) return;
        if (_openEditors > 0) { RuntimeStatusText.Text = "편집 내용을 저장하고 편집기를 닫은 뒤 내보내세요."; return; }
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "저장된 전체 프로필 내보내기", Filter = "FocusBind JSON (*.json)|*.json",
            FileName = $"FocusBind-profiles-{DateTime.Now:yyyyMMdd}.json", DefaultExt = ".json"
        };
        if (dialog.ShowDialog(this) != true) return;
        _transferringProfiles = true;
        try
        {
            if (string.Equals(Path.GetFullPath(dialog.FileName), _store.Path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("현재 설정 파일과 다른 내보내기 경로를 선택하세요.");
            var document = await _store.LoadAsync();
            await new ProfileStore(dialog.FileName).SaveAsync(document);
            RuntimeStatusText.Text = $"프로필 {document.Profiles.Count}개 내보내기 완료: {dialog.FileName}";
        }
        catch (Exception ex) { RuntimeStatusText.Text = $"내보내기 실패: {ex.Message}"; }
        finally { _transferringProfiles = false; }
    }

    private async void ImportProfiles_Click(object sender, RoutedEventArgs e)
    {
        if (_transferringProfiles || _startingRuntime) return;
        if (_openEditors > 0) { RuntimeStatusText.Text = "편집 내용을 저장하고 편집기를 닫은 뒤 가져오세요."; return; }
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = "프로필 가져오기 (기존 프로필에 복사본 추가)", Filter = "FocusBind JSON (*.json)|*.json", CheckFileExists = true };
        if (dialog.ShowDialog(this) != true) return;
        _transferringProfiles = true;
        try
        {
            if (string.Equals(Path.GetFullPath(dialog.FileName), _store.Path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("현재 설정 파일과 다른 가져오기 파일을 선택하세요.");
            if (!File.Exists(dialog.FileName)) throw new FileNotFoundException("가져올 파일을 찾을 수 없습니다.");
            var imported = await new ProfileStore(dialog.FileName).LoadAsync();
            if (imported.Profiles.Count == 0) throw new InvalidOperationException("가져올 프로필이 없습니다.");
            ProfileDocument result;
            try { result = ProfileTransfer.Merge(await _store.LoadAsync(), imported); }
            catch (Exception ex) when (ex is ProfileValidationException or System.Text.Json.JsonException)
            {
                if (MessageBox.Show(this,
                    "현재 설정 파일이 손상되어 읽을 수 없습니다. 현재 파일을 백업하고 선택한 파일로 복구할까요?\n\n" + ex.Message,
                    "프로필 복구", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
                result = imported;
            }
            string? backup = null;
            if (File.Exists(_store.Path))
            {
                backup = _store.Path + $".backup-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.json";
                File.Copy(_store.Path, backup, false);
            }
            if (_runtime is not null) StopRuntime("프로필을 가져오는 동안 매핑을 중지합니다.");
            await _store.SaveAsync(result);
            RuntimeStatusText.Text = $"프로필 {imported.Profiles.Count}개 가져오기 완료 · 매핑은 다시 시작하세요." +
                (backup is null ? "" : $"\n이전 설정 백업: {backup}");
        }
        catch (Exception ex) { RuntimeStatusText.Text = $"가져오기 실패 (기존 설정 유지): {ex.Message}"; }
        finally { _transferringProfiles = false; }
    }

    private void WindowsList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (WindowsList.SelectedItem is WindowSnapshot selected)
            ShowSnapshot(WindowInspector.TryGet(selected.Handle), "선택한 창");
    }
}
