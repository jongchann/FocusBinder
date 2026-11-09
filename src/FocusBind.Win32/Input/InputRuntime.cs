using System.Collections.Concurrent;
using System.Threading.Channels;
using FocusBind.Core.Coordinates;
using FocusBind.Core.Input;
using FocusBind.Core.Profiles;

namespace FocusBind.Win32.Input;

/// <summary>Routes physical input only for the foreground window and runs mapped actions off the hook thread.</summary>
public sealed class InputRuntime : IDisposable
{
    private sealed record ActiveTarget(nint Handle, Profile Profile, Mapping[] Mappings,
        Mapping[] HookMappings, Mapping[] RawKeyMappings,
        CoordinateContextManager Coordinates, WindowMessageClicker MessageClicker,
        int Generation, CancellationTokenSource Cancellation);
    private sealed record RunRequest(ActiveTarget Target, Mapping Mapping);

    private readonly ProfileDocument _document;
    private readonly int? _ownIntegrityRid = ProcessIntegrityInspector.TryGetRid((uint)Environment.ProcessId);
    private readonly DiagnosticLog _diagnostics;
    private readonly HashSet<(TriggerKind Kind, int Code, WheelDirection Direction)> _triggerCodes;
    private readonly object _sync = new();
    private readonly TriggerStateMachine _triggers = new();
    private readonly TriggerStateMachine _rawTriggers = new();
    private readonly ConcurrentDictionary<(int Generation, Guid MappingId), byte> _queued = new();
    private readonly Channel<RunRequest> _queue = Channel.CreateBounded<RunRequest>(new BoundedChannelOptions(256)
    {
        SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.Wait
    });
    private readonly CancellationTokenSource _shutdown = new();
    private readonly List<CancellationTokenSource> _retired = [];
    private readonly SendInputOutput _output = new();
    private readonly InputActionRunner _runner;
    private readonly ForegroundWatcher _watcher;
    private readonly LowLevelInputHook _hooks;
    private readonly Task _worker;
    private ActiveTarget? _active;
    private int _generation;
    private int _hookGeneration;
    private int _paused;
    private long _matchedTriggers;
    private long _observedInputs;
    private long _observedKeyboardInputs;
    private long _observedMouseInputs;
    private long _rawKeyboardInputs;
    private int _rawKeyboardEnabled;
    private int _rawGeneration;
    private long _candidateInputs;
    private long _startedRuns;
    private long _completedRuns;
    private long _lastHealthTick;
    private bool _disposed;

    public Exception? LastError { get; private set; }
    public bool IsActive => Volatile.Read(ref _active) is not null;
    public bool IsPaused => Volatile.Read(ref _paused) != 0;
    public string? ActiveProfileName => Volatile.Read(ref _active)?.Profile.Name;
    public long MatchedTriggers => Interlocked.Read(ref _matchedTriggers);
    public long ObservedInputs => Interlocked.Read(ref _observedInputs);
    public long ObservedKeyboardInputs => Interlocked.Read(ref _observedKeyboardInputs);
    public long ObservedMouseInputs => Interlocked.Read(ref _observedMouseInputs);
    public long RawKeyboardInputs => Interlocked.Read(ref _rawKeyboardInputs);
    public long CandidateInputs => Interlocked.Read(ref _candidateInputs);
    public string? LastExternalWindowDiagnostic { get; private set; }
    public string? HookError => _hooks.LastCallbackError?.Message ?? _hooks.ThreadError?.Message;
    public bool HookThreadAlive => _hooks.IsThreadAlive;
    public long HookTimerTicks => _hooks.TimerTicks;
    public long HookCallbacks => _hooks.CallbackCount;
    public long KeyboardCallbacks => _hooks.KeyboardCallbacks;
    public long MouseCallbacks => _hooks.MouseCallbacks;
    public long StartedRuns => Interlocked.Read(ref _startedRuns);
    public long CompletedRuns => Interlocked.Read(ref _completedRuns);
    public string? LastCancellationReason { get; private set; }
    public string DiagnosticLogPath => _diagnostics.Path;
    public string? DiagnosticLogError => _diagnostics.LastError?.Message;

    public void EnableRawKeyboard()
    {
        Volatile.Write(ref _rawKeyboardEnabled, 1);
        _diagnostics.Write("raw keyboard fallback enabled for non-consuming key mappings");
    }

    public void HandleRawKeyboard(InputEvent input)
    {
        if (_disposed || Volatile.Read(ref _rawKeyboardEnabled) == 0 || input.Kind != TriggerKind.Key) return;
        Interlocked.Increment(ref _rawKeyboardInputs);
        if (input.IsDown && _triggerCodes.Contains((input.Kind, input.Code, input.WheelDirection)))
        {
            Interlocked.Increment(ref _candidateInputs);
            _diagnostics.Write($"raw candidate foreground={NativeMethods.GetForegroundWindow()} active={Volatile.Read(ref _active)?.Handle ?? 0}");
        }
        var target = Volatile.Read(ref _active);
        if (IsPaused || target is null || target.Cancellation.IsCancellationRequested ||
            NativeMethods.GetForegroundWindow() != target.Handle)
        {
            _rawTriggers.Reset();
            return;
        }
        if (_rawGeneration != target.Generation)
        {
            _rawTriggers.Reset();
            _rawGeneration = target.Generation;
        }
        var decision = _rawTriggers.Process(input, target.RawKeyMappings);
        if (decision.FiredMappings.Count == 0) return;
        Interlocked.Add(ref _matchedTriggers, decision.FiredMappings.Count);
        foreach (var mapping in decision.FiredMappings)
            _diagnostics.Write($"raw trigger fired mappingId={mapping.Id}");
        Queue(target, decision.FiredMappings);
    }

    public void PollRawKeyboard(long now)
    {
        if (_disposed || Volatile.Read(ref _rawKeyboardEnabled) == 0) return;
        var target = Volatile.Read(ref _active);
        if (IsPaused || target is null || target.Cancellation.IsCancellationRequested ||
            NativeMethods.GetForegroundWindow() != target.Handle) return;
        if (_rawGeneration != target.Generation)
        {
            _rawTriggers.Reset();
            _rawGeneration = target.Generation;
        }
        var fired = _rawTriggers.Poll(now, target.RawKeyMappings);
        if (fired.Count == 0) return;
        Interlocked.Add(ref _matchedTriggers, fired.Count);
        foreach (var mapping in fired) _diagnostics.Write($"raw long press fired mappingId={mapping.Id}");
        Queue(target, fired);
    }

    public void Pause()
    {
        if (_disposed || Interlocked.Exchange(ref _paused, 1) != 0) return;
        _diagnostics.Write("mapping paused");
        UpdateTarget(null);
    }

    public void Resume()
    {
        if (_disposed || Interlocked.Exchange(ref _paused, 0) == 0) return;
        _diagnostics.Write("mapping resumed");
        UpdateTarget(WindowInspector.GetForeground());
    }

    public InputRuntime(ProfileDocument document)
    {
        var errors = ProfileValidator.Validate(document);
        if (errors.Count > 0) throw new ProfileValidationException(errors);
        _document = document;
        _diagnostics = new DiagnosticLog();
        _triggerCodes = document.Profiles.SelectMany(profile => profile.Mappings)
            .Where(mapping => mapping.Enabled)
            .Select(mapping => (mapping.Trigger.Kind,
                mapping.Trigger.Kind == TriggerKind.Key ? mapping.Trigger.KeyCode : mapping.Trigger.MouseButton,
                mapping.Trigger.WheelDirection))
            .ToHashSet();
        _paused = document.Ui.StartPaused ? 1 : 0;
        _runner = new InputActionRunner(_output);
        _watcher = new ForegroundWatcher();
        _watcher.Changed += change =>
        {
            if (change.Kind == WindowChangeKind.Foreground) LogForeground(change.Window);
            UpdateTarget(change.Window);
        };
        try
        {
            _hooks = new LowLevelInputHook();
            _hooks.Handler = HandleInput;
            _hooks.Tick = HandleTick;
        }
        catch
        {
            _watcher.Dispose();
            _output.Dispose();
            _diagnostics.Dispose();
            throw;
        }
        _worker = Task.Run(ProcessQueueAsync);
        var foreground = WindowInspector.GetForeground();
        LogForeground(foreground);
        UpdateTarget(foreground);
        _ = ProbeHookAsync();
    }

    private async Task ProbeHookAsync()
    {
        try
        {
            var mouse = await _hooks.ProbeAsync();
            var keyboard = await _hooks.ProbeKeyboardAsync();
            _diagnostics.Write($"hook probe mouseCallbackReceived={mouse} keyboardCallbackReceived={keyboard}");
        }
        catch (Exception ex) { _diagnostics.Write($"hook probe error type={ex.GetType().Name} message={ex.Message}"); }
    }

    private void LogForeground(WindowSnapshot? snapshot)
    {
        if (snapshot is null) { _diagnostics.Write("foreground unavailable"); return; }
        _diagnostics.Write($"foreground pid={snapshot.ProcessId} process={snapshot.DisplayProcessName} " +
            $"class={snapshot.Identity.WindowClass} pathAvailable={!string.IsNullOrWhiteSpace(snapshot.Identity.ExecutablePath)} " +
            $"inputReady={snapshot.CanReceiveInput} integrityRid={ProcessIntegrityInspector.TryGetRid(snapshot.ProcessId)?.ToString() ?? "unknown"} " +
            $"ownIntegrityRid={_ownIntegrityRid?.ToString() ?? "unknown"}");
    }

    private void UpdateTarget(WindowSnapshot? snapshot)
    {
        if (_disposed) return;
        Profile? profile = null;
        if (snapshot is { CanReceiveInput: true } && snapshot.ProcessId != Environment.ProcessId)
            profile = ProfileResolver.Resolve(_document, snapshot.Identity);
        if (profile is not null && !profile.Mappings.Any(m => m.Enabled && m.Actions.Steps.Count > 0 && InputActionRunner.Supports(m.Actions, true)))
            profile = null;
        if (snapshot is not null && snapshot.ProcessId != Environment.ProcessId)
            LastExternalWindowDiagnostic = $"마지막 외부 창: {snapshot.DisplayProcessName} [{snapshot.Identity.WindowClass}] → " +
                (profile?.Name ?? (snapshot.CanReceiveInput ? "일치하는 프로필 없음" : "입력 불가 상태"));
        lock (_sync)
        {
            if (Volatile.Read(ref _paused) != 0) profile = null;
            var old = _active;
            if (profile is not null && old is not null && old.Handle == snapshot!.Handle && old.Profile.Id == profile.Id)
            {
                old.Coordinates.Update(ToRect(snapshot.ClientBounds), snapshot.Dpi, true, Environment.TickCount64);
                return;
            }
            if (old is not null)
            {
                old.Cancellation.Cancel();
                try { old.MessageClicker.Release(); }
                catch (Exception ex) { LastError = ex; }
                _retired.Add(old.Cancellation);
            }
            if (profile is null)
            {
                Volatile.Write(ref _active, null);
                if (old is not null || snapshot?.ProcessId != Environment.ProcessId)
                    _diagnostics.Write($"target inactive pid={snapshot?.ProcessId ?? 0} " +
                        $"reason={(IsPaused ? "paused" : snapshot is not { CanReceiveInput: true } ? "window not input ready" : "no supported matching profile")}");
            }
            else
            {
                var coordinates = new CoordinateContextManager(profile);
                coordinates.Update(ToRect(snapshot!.ClientBounds), snapshot.Dpi, true, Environment.TickCount64);
                var mappings = profile.Mappings.Where(m => m.Enabled && m.Actions.Steps.Count > 0 && InputActionRunner.Supports(m.Actions, true)).ToArray();
                var hookMappings = mappings.Where(m => m.Trigger.Kind != TriggerKind.Key || m.ConsumeOriginal).ToArray();
                var rawKeyMappings = mappings.Where(m => m.Trigger.Kind == TriggerKind.Key && !m.ConsumeOriginal).ToArray();
                Volatile.Write(ref _active, new ActiveTarget(snapshot.Handle, profile, mappings, hookMappings, rawKeyMappings,
                    coordinates, new WindowMessageClicker(snapshot.Handle),
                    Interlocked.Increment(ref _generation), new CancellationTokenSource()));
                _diagnostics.Write($"target active pid={snapshot.ProcessId} profileId={profile.Id} mappings={mappings.Length}");
            }
        }
        try { _output.ReleaseAll(); }
        catch (Exception ex) { LastError = ex; }
    }

    private bool HandleInput(InputEvent input)
    {
        Interlocked.Increment(ref _observedInputs);
        if (input.Kind == TriggerKind.Key) Interlocked.Increment(ref _observedKeyboardInputs);
        else Interlocked.Increment(ref _observedMouseInputs);
        if (input.IsDown && _triggerCodes.Contains((input.Kind, input.Code, input.WheelDirection)))
        {
            Interlocked.Increment(ref _candidateInputs);
            _diagnostics.Write($"candidate kind={input.Kind} foreground={NativeMethods.GetForegroundWindow()} active={Volatile.Read(ref _active)?.Handle ?? 0}");
        }
        var target = Volatile.Read(ref _active);
        if (IsPaused || target is null || target.Cancellation.IsCancellationRequested || NativeMethods.GetForegroundWindow() != target.Handle)
        {
            _triggers.Reset();
            return false;
        }
        if (_hookGeneration != target.Generation)
        {
            _triggers.Reset();
            _hookGeneration = target.Generation;
        }
        var decision = _triggers.Process(input,
            input.Kind == TriggerKind.Key && Volatile.Read(ref _rawKeyboardEnabled) != 0 ? target.HookMappings : target.Mappings);
        if (decision.FiredMappings.Count > 0) Interlocked.Add(ref _matchedTriggers, decision.FiredMappings.Count);
        foreach (var mapping in decision.FiredMappings)
            _diagnostics.Write($"trigger fired mappingId={mapping.Id} suppressed={decision.SuppressOriginal}");
        if (!Queue(target, decision.FiredMappings))
        {
            _triggers.Reset();
            return false;
        }
        return decision.SuppressOriginal;
    }

    private void HandleTick(long now)
    {
        if (now - _lastHealthTick >= 5000)
        {
            _lastHealthTick = now;
            _diagnostics.Write($"health callbacks={_hooks.CallbackCount} keyboardCallbacks={KeyboardCallbacks} rawKeyboardInputs={RawKeyboardInputs} " +
                $"mouseCallbacks={MouseCallbacks} keyboardInputs={ObservedKeyboardInputs} mouseInputs={ObservedMouseInputs} " +
                $"candidates={CandidateInputs} matched={MatchedTriggers} runs={StartedRuns}/{CompletedRuns} " +
                $"active={IsActive} hookError={HookError ?? "none"}");
        }
        var target = Volatile.Read(ref _active);
        if (IsPaused || target is null || target.Cancellation.IsCancellationRequested || NativeMethods.GetForegroundWindow() != target.Handle) return;
        if (_hookGeneration != target.Generation)
        {
            _triggers.Reset();
            _hookGeneration = target.Generation;
        }
        var fired = _triggers.Poll(now,
            Volatile.Read(ref _rawKeyboardEnabled) != 0 ? target.HookMappings : target.Mappings);
        if (fired.Count > 0) Interlocked.Add(ref _matchedTriggers, fired.Count);
        Queue(target, fired);
    }

    private bool Queue(ActiveTarget target, IReadOnlyList<Mapping> mappings)
    {
        foreach (var mapping in mappings)
        {
            var key = (target.Generation, mapping.Id);
            if (!_queued.TryAdd(key, 0)) continue; // Ignore a mapping already queued or running.
            if (_queue.Writer.TryWrite(new RunRequest(target, mapping))) continue;
            _queued.TryRemove(key, out _);
            return false;
        }
        return true;
    }

    private async Task ProcessQueueAsync()
    {
        try
        {
            await foreach (var request in _queue.Reader.ReadAllAsync(_shutdown.Token))
            {
                var key = (request.Target.Generation, request.Mapping.Id);
                try
                {
                    if (!CanAct(request.Target)) continue;
                    Interlocked.Increment(ref _startedRuns);
                    _diagnostics.Write($"run started mappingId={request.Mapping.Id}");
                    await _runner.RunAsync(request.Mapping.Actions, () => CanAct(request.Target),
                        request.Target.Profile.MaxRunMilliseconds, request.Target.Cancellation.Token,
                        coordinate => ResolveCoordinate(request.Target, coordinate),
                        message => _diagnostics.Write(message),
                        request.Target.MessageClicker.SetButton,
                        request.Target.MessageClicker.MoveTo);
                    Interlocked.Increment(ref _completedRuns);
                    _diagnostics.Write($"run completed mappingId={request.Mapping.Id}");
                }
                catch (OperationCanceledException ex) { LastCancellationReason = ex.Message; _diagnostics.Write($"run canceled reason={ex.Message}"); }
                catch (Exception ex) { LastError = ex; _diagnostics.Write($"run error type={ex.GetType().Name} message={ex.Message}"); }
                finally { _queued.TryRemove(key, out _); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private bool CanAct(ActiveTarget target)
    {
        if (IsPaused || target.Cancellation.IsCancellationRequested || Volatile.Read(ref _active)?.Generation != target.Generation ||
            NativeMethods.GetForegroundWindow() != target.Handle) return false;
        var snapshot = WindowInspector.TryGet(target.Handle);
        return snapshot is { CanReceiveInput: true } &&
            ProfileResolver.Resolve(_document, snapshot.Identity)?.Id == target.Profile.Id;
    }

    private PixelPoint? ResolveCoordinate(ActiveTarget target, CoordinateTarget coordinate)
    {
        if (!CanAct(target)) return null;
        var snapshot = WindowInspector.TryGet(target.Handle);
        if (snapshot is not { CanReceiveInput: true }) return null;
        lock (_sync)
        {
            if (Volatile.Read(ref _active)?.Generation != target.Generation) return null;
            target.Coordinates.Update(ToRect(snapshot.ClientBounds), snapshot.Dpi, true, Environment.TickCount64);
            if (!target.Coordinates.TryGet(Environment.TickCount64, out var context) || context is null ||
                !CoordinateTransform.TryResolve(context, coordinate, out var point)) return null;
            return point;
        }
    }

    private static PixelRect ToRect(WindowBounds bounds) => new(bounds.X, bounds.Y, bounds.Width, bounds.Height);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _hooks.Dispose();
        _watcher.Dispose();
        lock (_sync)
        {
            _active?.Cancellation.Cancel();
            try { _active?.MessageClicker.Release(); }
            catch (Exception ex) { LastError = ex; }
            if (_active is not null) _retired.Add(_active.Cancellation);
            Volatile.Write(ref _active, null);
        }
        _shutdown.Cancel();
        _queue.Writer.TryComplete();
        try { _worker.Wait(TimeSpan.FromSeconds(2)); }
        catch (AggregateException ex) { LastError = ex; }
        try { _output.Dispose(); }
        catch (Exception ex) { LastError = ex; }
        _diagnostics.Write($"session stopped callbacks={_hooks.CallbackCount} observed={ObservedInputs} candidates={CandidateInputs} matched={MatchedTriggers} runs={StartedRuns}/{CompletedRuns}");
        _diagnostics.Dispose();
        foreach (var source in _retired) source.Dispose();
        _shutdown.Dispose();
        GC.SuppressFinalize(this);
    }
}
