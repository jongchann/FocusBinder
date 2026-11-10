using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using FocusBind.Core.Coordinates;
using FocusBind.Core.Input;
using FocusBind.Core.Profiles;
using FocusBind.Win32;

namespace FocusBind.App;

internal sealed class DryRunWindow : Window
{
    private readonly CancellationTokenSource _cancel = new();
    private bool _finished;
    private readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalContentAlignment = VerticalAlignment.Top };
    internal DryRunWindow(Profile profile, Mapping mapping, WindowSnapshot snapshot)
    {
        // Work from a snapshot. Editing or closing the parent cannot change the running test.
        var copy = JsonSerializer.Deserialize<Profile>(JsonSerializer.Serialize(profile))!;
        var selected = copy.Mappings.Single(m => m.Id == mapping.Id);
        Title = "매핑 테스트 · 실제 입력 없음"; Width = 760; Height = 600; MinWidth = 500; MinHeight = 350;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var grid = new Grid { Margin = new Thickness(20) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock { Text = "실제 키·마우스 입력 없이 선택한 매핑을 모의 실행합니다. 대상 창의 입력 수용 여부는 확인하지 않습니다.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) });
        Grid.SetRow(_log, 1); grid.Children.Add(_log);
        var stop = new Button { Content = "실행 취소", Margin = new Thickness(0, 12, 0, 0) };
        Grid.SetRow(stop, 2); grid.Children.Add(stop);
        stop.Click += (_, _) => _cancel.Cancel();
        Content = grid;
        Closed += (_, _) => { if (!_finished) _cancel.Cancel(); };
        Loaded += async (_, _) =>
        {
            try
            {
                Trace($"트리거: {TriggerLabel(selected.Trigger)} · 단계 {selected.Trigger.Phase}");
                Trace($"대상 크기 {snapshot.ClientBounds.Width}×{snapshot.ClientBounds.Height} · DPI {snapshot.Dpi}");
                if (!selected.Enabled) Trace("비활성 매핑입니다. 이 창에서만 모의 실행합니다.");
                var errors = ProfileValidator.Validate(new ProfileDocument { Profiles = [copy] });
                if (errors.Count > 0) throw new InvalidOperationException(string.Join(Environment.NewLine, errors));
                if (InputActionRunner.GetUnsupportedReason(selected.Actions, true) is { } reason)
                    throw new NotSupportedException(reason);
                var output = new DryRunOutput(Trace);
                var rect = new PixelRect(snapshot.ClientBounds.X, snapshot.ClientBounds.Y,
                    snapshot.ClientBounds.Width, snapshot.ClientBounds.Height);
                var layout = LayoutSelector.Select(copy, rect.Width, rect.Height, out _);
                PixelPoint? Resolve(CoordinateTarget target)
                {
                    var context = new CoordinateContext(rect, snapshot.Dpi, layout);
                    if (!CoordinateTransform.TryResolve(context, target, out var point))
                    { Trace("좌표를 계산할 수 없습니다. 레이아웃·영역·크기를 확인하세요."); return null; }
                    Trace($"계산 좌표 {point.X}, {point.Y}");
                    return point;
                }
                await Task.Run(() => new InputActionRunner(output).RunAsync(selected.Actions, () => !_cancel.IsCancellationRequested,
                    copy.MaxRunMilliseconds, _cancel.Token, Resolve, Trace,
                    (point, down) => Trace($"창 메시지 {(down ? "누름" : "뗌")} {point.X}, {point.Y} (모의)"),
                    point => Trace($"창 메시지 이동 {point.X}, {point.Y} (모의)")));
                Trace("모의 실행 완료. 실제 클릭 성공을 의미하지 않습니다.");
            }
            catch (OperationCanceledException) { Trace("모의 실행 중단: 취소·시간 제한 또는 잘못된 좌표"); }
            catch (Exception ex) { Trace($"실행 불가: {ex.Message}"); }
            finally { stop.IsEnabled = false; _finished = true; _cancel.Dispose(); }
        };
    }
    private void Trace(string line)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Trace(line)); return; }
        _log.AppendText(line + Environment.NewLine); _log.ScrollToEnd();
    }
    private static string TriggerLabel(InputTrigger trigger) => trigger.Kind == TriggerKind.Key
        ? KeyNames.Format(trigger.KeyCode, trigger.Modifiers)
        : $"{trigger.Modifiers} · {trigger.Kind} · 버튼 {trigger.MouseButton} · {trigger.WheelDirection}";
}
