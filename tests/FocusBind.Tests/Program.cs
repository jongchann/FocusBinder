using FocusBind.Core.Profiles;
using FocusBind.Core.Input;
using FocusBind.Core.Coordinates;
using FocusBind.Win32.Input;
using FocusBind.Win32;

if (args.Contains("--probe-audio"))
{
    try { Console.WriteLine($"audio enumeration OK, unmatched sessions changed={ProcessAudio.Adjust(uint.MaxValue, 0, null, trace: Console.WriteLine)}"); return 0; }
    catch (Exception ex) { Console.Error.WriteLine(ex); return 2; }
}

if (args.Contains("--list-windows"))
{
    foreach (var window in WindowInspector.ListTopLevelWindows())
        Console.WriteLine($"{window.ProcessId}\t{window.Identity.WindowClass}\t{window.Identity.Title}\t{window.Identity.ExecutablePath}");
    return 0;
}

if (args.Length == 2 && args[0] == "--integrity" && uint.TryParse(args[1], out var targetPid))
{
    Console.WriteLine($"current={ProcessIntegrityInspector.TryGetRid((uint)Environment.ProcessId)?.ToString() ?? "unavailable"} " +
        $"target={ProcessIntegrityInspector.TryGetRid(targetPid)?.ToString() ?? "unavailable"}");
    return 0;
}

if (args.Contains("--probe-hooks"))
{
    if (WindowInspector.ListTopLevelWindows().Count == 0)
    {
        Console.WriteLine("SKIP hook probe: this shell cannot see an interactive desktop.");
        return 0;
    }
    using var hooks = new LowLevelInputHook();
    var before = hooks.CallbackCount;
    var mouseReceived = await hooks.ProbeAsync();
    var keyboardReceived = await hooks.ProbeKeyboardAsync();
    Console.WriteLine($"threadAlive={hooks.IsThreadAlive} timerTicks={hooks.TimerTicks} " +
        $"callbacksBefore={before} callbacksAfter={hooks.CallbackCount} mouseCallbackReceived={mouseReceived} " +
        $"keyboardCallbackReceived={keyboardReceived} " +
        $"callbackError={hooks.LastCallbackError?.Message ?? "none"}");
    return mouseReceived && keyboardReceived ? 0 : 2;
}

var tests = new (string Name, Func<Task> Run)[]
{
    ("round trip", RoundTrip),
    ("invalid save preserves file", InvalidSave),
    ("invalid schema rejected", InvalidSchema),
    ("profile resolution", Resolution),
    ("protected window selections remain isolated", ProtectedSelections),
    ("profile import clones and preserves existing settings", ProfileImport),
    ("untitled window has a readable label", UntitledWindowLabel),
    ("trigger state and paired suppression", TriggerState),
    ("release and custom long press timing", TriggerPhases),
    ("modifier combinations remain distinct", ModifierCombinations),
    ("audio COM enumeration leaves other processes unchanged", AudioEnumeration),
    ("duplicate trigger detection", TriggerConflicts),
    ("coordinate scaling and bounds", Coordinates),
    ("coordinate stabilization and virtual desktop", CoordinateStabilization),
    ("basic action execution and cancellation", BasicActions),
    ("nested repeats obey per-run command budget", CommandBudget),
    ("saved wheel mappings preserve direction and amount", WheelMappingPipeline),
    ("fast cursor mode round trips and restores optionally", FastCursorRoundTrip),
    ("fast cursor mode cancels safely and respects user movement", FastCursorSafety),
    ("timeout releases held input", ActionTimeout),
    ("canceled waiting run leaves active input alone", WaitingCancellation),
    ("invalid and changing coordinates stop before click", InvalidCoordinates),
    ("focus loss releases held input", ActionFocusLoss),
    ("Unity message clicks are marked incompatible", UnityMessageClickCompatibility),
    ("saved mapping pipeline resolves trigger and click", MappingPipeline),
    ("raw keyboard packets decode press and release", RawKeyboardPackets),
    ("Win32 window inspection and event hook", Win32Smoke),
    ("diagnostic log writes and flushes", DiagnosticLogRoundTrip)
};
var failed = 0;
foreach (var test in tests)
{
    try { await test.Run(); Console.WriteLine("PASS " + test.Name); }
    catch (Exception ex) { failed++; Console.Error.WriteLine("FAIL " + test.Name + ": " + ex); }
}
return failed == 0 ? 0 : 1;

static Task DiagnosticLogRoundTrip()
{
    var path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"focusbind-log-{Guid.NewGuid():N}.log");
    try
    {
        using (var log = new DiagnosticLog(path)) log.Write("health callbacks=2");
        Assert(File.Exists(path));
        var lines = File.ReadAllLines(path);
        Assert(lines.Length == 2 && lines[0].Contains("session started") && lines[1].Contains("health callbacks=2"));
    }
    finally { if (File.Exists(path)) File.Delete(path); }
    return Task.CompletedTask;
}

static async Task MappingPipeline()
{
    var profile = NewProfile("Pipeline");
    profile.Window = new WindowMatcher { ExecutablePath = @"C:\Games\game.exe", ExecutableName = "game.exe", WindowClass = "GameWindow" };
    var mapping = profile.Mappings.Single();
    mapping.Trigger = new InputTrigger { Kind = TriggerKind.Key, KeyCode = 49 };
    mapping.Actions = new ActionSequence { Steps = [new AutomationAction
    {
        Kind = ActionKind.CoordinateClick,
        Coordinate = new CoordinateTarget { LayoutId = profile.Layouts.Single().Id,
            Space = CoordinateSpace.ClientNormalized, X = 0.25, Y = 0.5 }
    }] };
    var document = new ProfileDocument { Profiles = [profile] };
    Assert(ProfileValidator.Validate(document).Count == 0);
    var identity = new WindowIdentity("", "Game", "GameWindow", "game.exe");
    var selected = ProfileResolver.Resolve(document, identity);
    Assert(selected?.Id == profile.Id);
    var decision = new TriggerStateMachine().Process(
        new InputEvent(TriggerKind.Key, 49, true, WheelDirection.None, 100), selected!.Mappings);
    Assert(decision.FiredMappings.Single().Id == mapping.Id);
    var output = new RecordingOutput();
    var context = new CoordinateContext(new PixelRect(100, 200, 801, 601), 96, profile.Layouts.Single());
    await new InputActionRunner(output).RunAsync(mapping.Actions, () => true, 1000, CancellationToken.None,
        target => CoordinateTransform.TryResolve(context, target, out var point) ? point : null);
    Assert(output.Events.Contains("move 300,500"));
    Assert(output.Events.Contains("mouse down 1") && output.Events.Contains("mouse up 1"));
}

static Task RawKeyboardPackets()
{
    var data = new byte[40];
    BitConverter.TryWriteBytes(data.AsSpan(0), 1U); // RIM_TYPEKEYBOARD
    BitConverter.TryWriteBytes(data.AsSpan(24 + 6), (ushort)49);
    BitConverter.TryWriteBytes(data.AsSpan(24 + 8), 0x0100U);
    Assert(RawKeyboardDecoder.TryDecode(data, 24, 100, out var down));
    Assert(down.Kind == TriggerKind.Key && down.Code == 49 && down.IsDown);
    BitConverter.TryWriteBytes(data.AsSpan(24 + 2), (ushort)1); // RI_KEY_BREAK
    BitConverter.TryWriteBytes(data.AsSpan(24 + 8), 0x0101U);
    Assert(RawKeyboardDecoder.TryDecode(data, 24, 110, out var up) && !up.IsDown);
    Assert(!RawKeyboardDecoder.TryDecode(data[..20], 24, 120, out _));
    return Task.CompletedTask;
}

static Profile NewProfile(string name = "Example") => new()
{
    Name = name,
    Window = new WindowMatcher { ExecutableName = "example.exe" },
    Mappings = [new Mapping
    {
        Trigger = new InputTrigger { Kind = TriggerKind.Key, KeyCode = 65 },
        Actions = new ActionSequence { Steps = [new AutomationAction { Kind = ActionKind.KeyTap, KeyCode = 66 }] }
    }]
};

static async Task RoundTrip()
{
    var path = TempPath();
    try
    {
        var profile = NewProfile();
        profile.Mappings.Single().Actions.Steps.Add(new AutomationAction
        {
            Kind = ActionKind.CoordinateClick,
            Coordinate = new CoordinateTarget { LayoutId = profile.Layouts.Single().Id,
                ClickMode = CoordinateClickMode.WindowMessage, X = 0.3, Y = 0.7 }
        });
        var store = new ProfileStore(path);
        await store.SaveAsync(new ProfileDocument
        {
            Profiles = [profile],
            WindowSelections = [new WindowSelection { ProfileId = profile.Id, Window = new WindowMatcher { ExecutableName = "example.exe" } }]
        });
        var loaded = await store.LoadAsync();
        Assert(loaded.Profiles.Single().Id == profile.Id);
        Assert(loaded.WindowSelections.Single().ProfileId == profile.Id);
        var steps = loaded.Profiles.Single().Mappings.Single().Actions.Steps;
        Assert(steps[0].Kind == ActionKind.KeyTap);
        Assert(steps[1].Coordinate?.ClickMode == CoordinateClickMode.WindowMessage);
    }
    finally { File.Delete(path); }
}

static async Task InvalidSave()
{
    var path = TempPath();
    try
    {
        var store = new ProfileStore(path);
        var document = new ProfileDocument { Profiles = [NewProfile()] };
        await store.SaveAsync(document);
        var original = await File.ReadAllTextAsync(path);
        document.Profiles[0].Layouts.Clear();
        await Throws<ProfileValidationException>(() => store.SaveAsync(document));
        Assert(await File.ReadAllTextAsync(path) == original);
    }
    finally { File.Delete(path); }
}

static async Task InvalidSchema()
{
    var path = TempPath();
    try
    {
        var store = new ProfileStore(path);
        await File.WriteAllTextAsync(path, "{\"profiles\":[]}");
        await Throws<ProfileValidationException>(() => store.LoadAsync());
        await File.WriteAllTextAsync(path, "{\"schemaVersion\":999,\"profiles\":[]}");
        await Throws<ProfileValidationException>(() => store.LoadAsync());
        await File.WriteAllTextAsync(path, "{broken json");
        await Throws<System.Text.Json.JsonException>(() => store.LoadAsync());
    }
    finally { File.Delete(path); }
}

static async Task ProfileImport()
{
    var path = TempPath();
    try
    {
        var profile = NewProfile("Same name");
        var current = new ProfileDocument { Profiles = [profile], Ui = new UiSettings { StartPaused = true } };
        var window = new WindowIdentity(@"C:\Games\example.exe", "Game", "GameWindow");
        ProfileSelections.Select(current, profile.Id, window);
        var imported = new ProfileDocument { Profiles = [profile] };
        ProfileSelections.Select(imported, profile.Id, window);
        var result = ProfileTransfer.Merge(current, imported);
        Assert(current.Profiles.Count == 1 && imported.Profiles.Single().Id == profile.Id);
        Assert(result.Profiles.Count == 2 && result.Ui.StartPaused);
        Assert(result.Profiles[1].Id != profile.Id);
        Assert(result.Profiles[1].Mappings[0].Id != profile.Mappings[0].Id);
        Assert(result.WindowSelections.Count == 1 && ProfileResolver.Resolve(result, window)?.Id == profile.Id);
        result.Profiles[1].Mappings[0].Name = "Changed copy";
        Assert(profile.Mappings[0].Name != "Changed copy");
        var store = new ProfileStore(path);
        await store.SaveAsync(result);
        Assert((await store.LoadAsync()).Profiles.Count == 2);
        var before = await File.ReadAllTextAsync(path);
        imported.SchemaVersion = 999;
        await Throws<ProfileValidationException>(() => Task.FromResult(ProfileTransfer.Merge(current, imported)));
        Assert(await File.ReadAllTextAsync(path) == before && current.Profiles.Count == 1);
    }
    finally { File.Delete(path); }
}

static Task ProtectedSelections()
{
    var first = NewProfile("First");
    first.Window = new WindowMatcher { ExecutableName = "first.exe", WindowClass = "UnityWndClass" };
    var second = NewProfile("Second");
    second.Window = new WindowMatcher { ExecutableName = "second.exe", WindowClass = "UnityWndClass" };
    var alternative = NewProfile("First alternative");
    alternative.Window = new WindowMatcher { ExecutableName = "first.exe", WindowClass = "UnityWndClass" };
    var document = new ProfileDocument { Profiles = [first, second, alternative] };
    var firstWindow = new WindowIdentity("", "Game", "UnityWndClass", "first.exe");
    var secondWindow = firstWindow with { ProcessName = "second.exe" };
    ProfileSelections.Select(document, first.Id, firstWindow);
    ProfileSelections.Select(document, second.Id, secondWindow);
    ProfileSelections.Select(document, alternative.Id, firstWindow with { ProcessName = "FIRST.EXE" });
    Assert(document.WindowSelections.Count == 2);
    Assert(ProfileResolver.Resolve(document, firstWindow)?.Id == alternative.Id);
    Assert(ProfileResolver.Resolve(document, secondWindow)?.Id == second.Id);
    return Task.CompletedTask;
}

static Task Resolution()
{
    var general = NewProfile("General");
    var specific = NewProfile("Specific");
    specific.Window.WindowClass = "GameWindow";
    var document = new ProfileDocument { Profiles = [general, specific] };
    var window = new WindowIdentity("C:\\Games\\example.exe", "Game", "GameWindow");
    Assert(ProfileResolver.Resolve(document, window)?.Id == specific.Id);
    general.Priority = 1;
    Assert(ProfileResolver.Resolve(document, window)?.Id == general.Id);
    document.WindowSelections.Add(new WindowSelection { ProfileId = specific.Id, Window = new WindowMatcher { ExecutableName = "example.exe" } });
    Assert(ProfileResolver.Resolve(document, window)?.Id == specific.Id);
    Assert(ProfileResolver.Resolve(document, new WindowIdentity("C:\\Games\\other.exe", "Other", "GameWindow")) is null);
    var protectedWindow = new WindowIdentity("", "Game", "GameWindow", "example.exe");
    Assert(ProfileResolver.Resolve(document, protectedWindow)?.Id == specific.Id);
    Assert(ProfileResolver.Resolve(document, protectedWindow with { ProcessName = "other.exe" }) is null);
    Assert(ProfileResolver.Resolve(document, protectedWindow with { WindowClass = "OtherClass" }) is null);
    Assert(!ProfileResolver.Matches(new WindowMatcher { ExecutableName = "example.exe" }, protectedWindow));
    return Task.CompletedTask;
}

static async Task Win32Smoke()
{
    if (!OperatingSystem.IsWindows()) return;
    using var watcher = new ForegroundWatcher();
    Assert(ProcessIntegrityInspector.TryGetRid((uint)Environment.ProcessId) is >= 4096);
    using var hooks = new LowLevelInputHook(); // No handler: all input passes through.
    await Task.Delay(100);
    Assert(hooks.IsThreadAlive && hooks.TimerTicks > 0);
    using var runtime = new InputRuntime(new ProfileDocument()); // Empty profiles must never intercept input.
    Assert(!runtime.IsActive);
    var foreground = WindowInspector.GetForeground();
    if (foreground is not null)
    {
        Assert(foreground.Handle != 0);
        Assert(foreground.ProcessId != 0);
        Assert(foreground.Dpi >= 96);
        Assert(foreground.ClientBounds.Width >= 0 && foreground.ClientBounds.Height >= 0);
        Assert(WindowInspector.TryGet(foreground.Handle) is not null);
    }
}

static Task UntitledWindowLabel()
{
    var window = new WindowSnapshot(1, 42, 1,
        new WindowIdentity(@"C:\Apps\example.exe", "", "GameWindow"),
        new WindowBounds(0, 0, 800, 600), 96, true, false, true);
    Assert(window.DisplayTitle.Contains("example.exe"));
    Assert(window.DisplayTitle.Contains("GameWindow"));
    Assert(!window.IsAuxiliary && window.ClientSize == "800×600");
    Assert((window with { ClientBounds = new WindowBounds(0, 0, 1, 1) }).IsAuxiliary);
    return Task.CompletedTask;
}

static Task TriggerState()
{
    var state = new TriggerStateMachine();
    var keyMapping = new Mapping
    {
        ConsumeOriginal = true,
        Trigger = new InputTrigger { Kind = TriggerKind.Key, KeyCode = 65, Modifiers = ModifierKeys.Control }
    };
    var wheelMapping = new Mapping
    {
        Trigger = new InputTrigger { Kind = TriggerKind.HorizontalWheel, WheelDirection = WheelDirection.Negative }
    };
    var longMapping = new Mapping
    {
        Trigger = new InputTrigger { Kind = TriggerKind.MouseButton, MouseButton = 4,
            Phase = TriggerPhase.LongPress, LongPressMilliseconds = 500 }
    };
    Mapping[] mappings = [keyMapping, wheelMapping, longMapping];
    InputDecision Send(TriggerKind kind, int code, bool down, long time, WheelDirection direction = WheelDirection.None) =>
        state.Process(new InputEvent(kind, code, down, direction, time), mappings);
    Assert(!Send(TriggerKind.Key, 0x11, true, 0).SuppressOriginal);
    var press = Send(TriggerKind.Key, 65, true, 10);
    Assert(press.SuppressOriginal && press.FiredMappings.Single().Id == keyMapping.Id);
    Assert(Send(TriggerKind.Key, 65, true, 20).FiredMappings.Count == 0); // auto-repeat
    Assert(Send(TriggerKind.Key, 65, false, 30).SuppressOriginal);
    Assert(!Send(TriggerKind.Key, 0x11, false, 40).SuppressOriginal);
    Assert(Send(TriggerKind.HorizontalWheel, 0, true, 50, WheelDirection.Negative)
        .FiredMappings.Single().Id == wheelMapping.Id);
    Send(TriggerKind.MouseButton, 4, true, 100);
    Assert(state.Poll(599, mappings).Count == 0);
    Assert(state.Poll(600, mappings).Single().Id == longMapping.Id);
    Assert(state.Poll(700, mappings).Count == 0);
    Send(TriggerKind.MouseButton, 4, false, 710);
    state.Reset();
    Assert(!Send(TriggerKind.Key, 65, false, 720).SuppressOriginal);
    return Task.CompletedTask;
}

static Task AudioEnumeration()
{
    if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
    var traces = new List<string>();
    Assert(ProcessAudio.Adjust(uint.MaxValue, 0, null, trace: traces.Add) == 0);
    Assert(traces.Any(line => line.StartsWith("audio devices=")));
    Assert(!traces.Any(line => line.Contains("match=True")));
    return Task.CompletedTask;
}

static Task ModifierCombinations()
{
    var state = new TriggerStateMachine();
    var plain = new Mapping { Trigger = new InputTrigger { KeyCode = 65 } };
    var control = new Mapping { Trigger = new InputTrigger { KeyCode = 65, Modifiers = ModifierKeys.Control } };
    var combined = new Mapping { Trigger = new InputTrigger { KeyCode = 65, Modifiers = ModifierKeys.Control | ModifierKeys.Shift } };
    Mapping[] mappings = [plain, control, combined];
    InputDecision Send(int code, bool down) => state.Process(new InputEvent(TriggerKind.Key, code, down, WheelDirection.None, 0), mappings);
    Assert(Send(65, true).FiredMappings.Single() == plain);
    Send(65, false);
    Send(0xA2, true);
    Assert(Send(65, true).FiredMappings.Single() == control);
    Send(65, false);
    Send(0xA0, true);
    Assert(Send(65, true).FiredMappings.Single() == combined);
    Send(65, false);
    Send(0xA2, false);
    Assert(Send(65, true).FiredMappings.Count == 0);
    return Task.CompletedTask;
}

static Task TriggerPhases()
{
    var state = new TriggerStateMachine();
    var release = new Mapping { ConsumeOriginal = true, Trigger = new InputTrigger
        { Kind = TriggerKind.Key, KeyCode = 65, Phase = TriggerPhase.Release } };
    var hold = new Mapping { Trigger = new InputTrigger
        { Kind = TriggerKind.Key, KeyCode = 66, Phase = TriggerPhase.LongPress, LongPressMilliseconds = 750 } };
    Mapping[] mappings = [release, hold];
    InputDecision Send(int key, bool down, long time) =>
        state.Process(new InputEvent(TriggerKind.Key, key, down, WheelDirection.None, time), mappings);
    var down = Send(65, true, 0);
    Assert(down.SuppressOriginal && down.FiredMappings.Count == 0);
    var up = Send(65, false, 100);
    Assert(up.SuppressOriginal && up.FiredMappings.Single() == release);
    Assert(Send(65, false, 110).FiredMappings.Count == 0);
    Send(66, true, 200);
    Assert(state.Poll(949, mappings).Count == 0);
    Assert(state.Poll(950, mappings).Single() == hold);
    Assert(state.Poll(1000, mappings).Count == 0);
    Send(66, false, 1010);
    Send(66, true, 1100);
    Send(66, false, 1200);
    Assert(state.Poll(2000, mappings).Count == 0);
    return Task.CompletedTask;
}

static Task TriggerConflicts()
{
    var profile = NewProfile();
    profile.Mappings.Add(new Mapping
    {
        Trigger = new InputTrigger { Kind = TriggerKind.Key, KeyCode = 65 },
        Actions = new ActionSequence { Steps = [new AutomationAction { Kind = ActionKind.KeyTap, KeyCode = 67 }] }
    });
    Assert(TriggerConflictDetector.Find(profile).Count == 1);
    Assert(ProfileValidator.Validate(new ProfileDocument { Profiles = [profile] })
        .Any(error => error.Contains("duplicate trigger")));
    profile.Mappings[1].Trigger.KeyCode = 68;
    Assert(TriggerConflictDetector.Find(profile).Count == 0);
    profile.Mappings[1].Trigger.Kind = TriggerKind.VerticalWheel;
    profile.Mappings[1].Trigger.WheelDirection = WheelDirection.Positive;
    profile.Mappings[1].Trigger.Phase = TriggerPhase.Release;
    Assert(ProfileValidator.Validate(new ProfileDocument { Profiles = [profile] })
        .Any(error => error.Contains("wheel triggers support press")));
    return Task.CompletedTask;
}

static Task Coordinates()
{
    var layout = new LayoutVariant { ReferenceWidth = 1600, ReferenceHeight = 900 };
    var context = new CoordinateContext(new PixelRect(-1280, 100, 1280, 720), 144, layout);
    var center = new CoordinateTarget { LayoutId = layout.Id, Space = CoordinateSpace.ClientNormalized, X = 0.5, Y = 0.5 };
    Assert(CoordinateTransform.TryResolve(context, center, out var point));
    Assert(point.X == -640 && point.Y == 460);
    var corner = new CoordinateTarget { LayoutId = layout.Id, Space = CoordinateSpace.ClientNormalized, X = 1, Y = 1 };
    Assert(CoordinateTransform.TryResolve(context, corner, out point) && point.X == -1 && point.Y == 819);
    Assert(CoordinateTransform.TryCapture(context.Client, point, out var captured)
        && captured == new NormalizedPoint(1, 1));
    Assert(!CoordinateTransform.TryCapture(context.Client, new PixelPoint(0, 819), out _));
    var anchored = new CoordinateTarget { LayoutId = layout.Id, Space = CoordinateSpace.AnchoredOffset,
        Anchor = Anchor.TopRight, X = -100, Y = 100 };
    Assert(CoordinateTransform.TryResolve(context, anchored, out point) && point.X == -81 && point.Y == 180);
    anchored.X = 200;
    Assert(!CoordinateTransform.TryResolve(context, anchored, out _));
    var narrow = new LayoutVariant { IsDefault = false, Priority = 1, MinAspectRatio = 1.7, MaxAspectRatio = 1.8 };
    var profile = NewProfile();
    profile.Layouts.Add(narrow);
    Assert(LayoutSelector.Select(profile, 1920, 1080, out var outside).Id == narrow.Id && !outside);
    Assert(LayoutSelector.Select(profile, 800, 600, out outside).IsDefault && !outside);
    return Task.CompletedTask;
}

static Task CoordinateStabilization()
{
    var profile = NewProfile();
    var manager = new CoordinateContextManager(profile);
    manager.Update(new PixelRect(-1920, 0, 1280, 720), 144, true, 0);
    Assert(!manager.TryGet(199, out _));
    manager.Update(new PixelRect(-1920, 0, 1600, 900), 144, true, 100);
    Assert(!manager.TryGet(299, out _));
    Assert(manager.TryGet(300, out var context) && context!.Client.Width == 1600);
    var version = manager.Version;
    manager.Update(new PixelRect(-1920, 0, 1600, 900), 144, true, 400);
    Assert(manager.Version == version && manager.TryGet(400, out _));
    manager.Update(new PixelRect(-1920, 0, 0, 0), 144, false, 500);
    Assert(!manager.TryGet(800, out _));
    var desktop = new PixelRect(-1920, -1080, 3840, 2160);
    Assert(VirtualDesktopTransform.TryToAbsolute(new PixelPoint(-1920, -1080), desktop, out var topLeft)
        && topLeft == new PixelPoint(0, 0));
    Assert(VirtualDesktopTransform.TryToAbsolute(new PixelPoint(1919, 1079), desktop, out var bottomRight)
        && bottomRight == new PixelPoint(65535, 65535));
    Assert(!VirtualDesktopTransform.TryToAbsolute(new PixelPoint(1920, 0), desktop, out _));
    return Task.CompletedTask;
}

static Task UnityMessageClickCompatibility()
{
    Assert(WindowMessageClicker.IsKnownIncompatible("UnityWndClass"));
    Assert(WindowMessageClicker.IsKnownIncompatible("unitywndclass"));
    Assert(!WindowMessageClicker.IsKnownIncompatible("Notepad"));
    return Task.CompletedTask;
}

static async Task BasicActions()
{
    var output = new RecordingOutput();
    var runner = new InputActionRunner(output);
    var sequence = new ActionSequence { Steps =
    [
        new AutomationAction { Kind = ActionKind.KeyTap, KeyCode = 65 },
        new AutomationAction { Kind = ActionKind.HorizontalWheel, Amount = -120 },
        new AutomationAction { Kind = ActionKind.Delay, DelayMilliseconds = 1 }
    ] };
    await runner.RunAsync(sequence, () => true, 1000, CancellationToken.None);
    Assert(output.Events.SequenceEqual(["key tap 65", "horizontal wheel -120", "release"]));
    output.Events.Clear();
    await Throws<OperationCanceledException>(() => runner.RunAsync(sequence, () => false, 1000, CancellationToken.None));
    Assert(output.Events.SequenceEqual(["release"]));
    Assert(!InputActionRunner.Supports(new ActionSequence { Steps = [new AutomationAction { Kind = ActionKind.Lua }] }));
    output.Events.Clear();
    var target = new CoordinateTarget { LayoutId = Guid.NewGuid(), X = 0.5, Y = 0.5 };
    var click = new ActionSequence { Steps = [new AutomationAction { Kind = ActionKind.CoordinateClick, Coordinate = target }] };
    await runner.RunAsync(click, () => true, 1000, CancellationToken.None, _ => new PixelPoint(100, 200));
    Assert(output.Events.SequenceEqual(["move 100,200", "mouse down 1", "mouse up 1", "move 10,10", "release"]));
    output.Events.Clear();
    output.IgnoreMove = true;
    await Throws<OperationCanceledException>(() => runner.RunAsync(click, () => true, 1000,
        CancellationToken.None, _ => new PixelPoint(100, 200)));
    Assert(!output.Events.Any(item => item.StartsWith("mouse down")));
    output.IgnoreMove = false;
    output.Events.Clear();
    using (var cancelDuringPress = new CancellationTokenSource())
    {
        output.OnMouseDown = cancelDuringPress.Cancel;
        await Throws<OperationCanceledException>(() => runner.RunAsync(click, () => true, 1000,
            cancelDuringPress.Token, _ => new PixelPoint(100, 200)));
        var upIndex = output.Events.IndexOf("mouse up 1");
        Assert(upIndex >= 0 && output.Events.LastIndexOf("move 10,10") > upIndex);
        output.OnMouseDown = null;
    }
    output.Events.Clear();
    target.ClickMode = CoordinateClickMode.WindowMessage;
    var posted = new List<string>();
    await runner.RunAsync(click, () => true, 1000, CancellationToken.None,
        _ => new PixelPoint(100, 200), postCoordinateButton: (point, down) =>
            posted.Add($"{(down ? "down" : "up")} {point.X},{point.Y}"),
        postCoordinateMove: point => posted.Add($"move {point.X},{point.Y}"));
    Assert(posted.SequenceEqual(["move 100,200", "down 100,200", "up 100,200"]));
    Assert(!output.Events.Any(item => item.StartsWith("move") || item.StartsWith("mouse down")));
    target.ClickMode = CoordinateClickMode.Cursor;
    output.Events.Clear();
    var repeated = new ActionSequence { Steps = [new AutomationAction
    {
        Kind = ActionKind.Repeat, RepeatCount = 3,
        Steps = [new AutomationAction { Kind = ActionKind.KeyTap, KeyCode = 66 }]
    }] };
    await runner.RunAsync(repeated, () => true, 1000, CancellationToken.None);
    Assert(output.Events.SequenceEqual(["key tap 66", "key tap 66", "key tap 66", "release"]));
}

static string TempPath() => System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"focusbind-test-{Guid.NewGuid():N}.json");
static async Task FastCursorRoundTrip()
{
    Assert((int)CoordinateClickMode.Cursor == 0 && (int)CoordinateClickMode.WindowMessage == 1);
    Assert(System.Text.Json.JsonSerializer.Deserialize<CoordinateTarget>("{}")!.ClickMode == CoordinateClickMode.Cursor);
    var profile = NewProfile("Fast cursor");
    var target = new CoordinateTarget { LayoutId = profile.Layouts.Single().Id,
        Space = CoordinateSpace.ClientNormalized, X = 0.5, Y = 0.5, ClickMode = CoordinateClickMode.FastCursor };
    profile.Mappings.Single().Actions = new ActionSequence { Steps = [
        new AutomationAction { Kind = ActionKind.CoordinateClick, Coordinate = target }
    ] };
    var path = TempPath();
    try
    {
        var store = new ProfileStore(path);
        foreach (var restore in new[] { true, false })
        {
            target.RestoreCursor = restore;
            await store.SaveAsync(new ProfileDocument { Profiles = [profile] });
            var actions = (await store.LoadAsync()).Profiles.Single().Mappings.Single().Actions;
            Assert(actions.Steps.Single().Coordinate is { ClickMode: CoordinateClickMode.FastCursor } saved
                && saved.RestoreCursor == restore);
            var output = new RecordingOutput();
            var traces = new List<string>();
            await new InputActionRunner(output).RunAsync(actions, () => true, 2000, CancellationToken.None,
                _ => new PixelPoint(100, 200), traces.Add);
            Assert(output.Events.SequenceEqual(restore
                ? ["move 100,200", "mouse down 1", "mouse up 1", "move 10,10", "release"]
                : new[] { "move 100,200", "mouse down 1", "mouse up 1", "release" }));
            Assert(traces.Any(line => line.Contains("mode=FastCursor moveLeadMs=0 holdMs=0 restoreDelayMs=20")));
            Assert(output.PressedButtons.Count == 0);
        }
    }
    finally { if (File.Exists(path)) File.Delete(path); }
}

static async Task FastCursorSafety()
{
    var target = new CoordinateTarget { ClickMode = CoordinateClickMode.FastCursor, RestoreCursor = true };
    var actions = new ActionSequence { Steps = [new AutomationAction { Kind = ActionKind.CoordinateClick, Coordinate = target }] };
    var output = new RecordingOutput { IgnoreMove = true };
    var runner = new InputActionRunner(output);
    await Throws<OperationCanceledException>(() => runner.RunAsync(actions, () => true, 2000,
        CancellationToken.None, _ => new PixelPoint(100, 200)));
    Assert(!output.Events.Contains("mouse down 1") && output.Events.Last() == "release");
    output = new RecordingOutput();
    using (var cancel = new CancellationTokenSource())
    {
        output.OnMouseDown = cancel.Cancel;
        await Throws<OperationCanceledException>(() => new InputActionRunner(output).RunAsync(actions, () => true, 2000,
            cancel.Token, _ => new PixelPoint(100, 200)));
        Assert(output.Events.SequenceEqual(["move 100,200", "mouse down 1", "mouse up 1", "move 10,10", "release"]));
        Assert(output.PressedButtons.Count == 0);
    }
    output = new RecordingOutput();
    var focused = true;
    output.OnMouseDown = () => focused = false;
    var afterFocusLoss = new AutomationAction { Kind = ActionKind.KeyTap, KeyCode = 66 };
    actions.Steps.Add(afterFocusLoss);
    await Throws<OperationCanceledException>(() => new InputActionRunner(output).RunAsync(actions, () => focused, 2000,
        CancellationToken.None, _ => new PixelPoint(100, 200)));
    Assert(output.Events.SequenceEqual(["move 100,200", "mouse down 1", "mouse up 1", "release"]));
    Assert(output.PressedButtons.Count == 0);
    actions.Steps.Remove(afterFocusLoss);
    output = new RecordingOutput();
    output.OnMouseDown = () => output.MoveTo(new PixelPoint(300, 400)); // User takes over the cursor.
    await new InputActionRunner(output).RunAsync(actions, () => true, 2000, CancellationToken.None,
        _ => new PixelPoint(100, 200));
    Assert(output.GetCursorPosition() == new PixelPoint(300, 400));
    Assert(!output.Events.Contains("move 10,10") && output.PressedButtons.Count == 0);
}

static async Task WheelMappingPipeline()
{
    var profile = NewProfile("Wheel controls");
    profile.Mappings.Clear();
    var expected = new[] { "vertical wheel 120", "vertical wheel -360", "horizontal wheel 240", "horizontal wheel -12000" };
    var kinds = new[] { ActionKind.VerticalWheel, ActionKind.VerticalWheel, ActionKind.HorizontalWheel, ActionKind.HorizontalWheel };
    var amounts = new[] { 120, -360, 240, -12000 };
    for (var i = 0; i < kinds.Length; i++)
        profile.Mappings.Add(new Mapping { Name = $"Wheel {i}", ConsumeOriginal = true,
            Trigger = new InputTrigger { Kind = TriggerKind.Key, KeyCode = 65 + i },
            Actions = new ActionSequence { Steps = [new AutomationAction { Kind = kinds[i], Amount = amounts[i] }] } });
    var path = TempPath();
    try
    {
        var store = new ProfileStore(path);
        await store.SaveAsync(new ProfileDocument { Profiles = [profile] });
        var saved = (await store.LoadAsync()).Profiles.Single();
        var state = new TriggerStateMachine();
        var output = new RecordingOutput();
        var runner = new InputActionRunner(output);
        for (var i = 0; i < kinds.Length; i++)
        {
            var decision = state.Process(new InputEvent(TriggerKind.Key, 65 + i, true, WheelDirection.None, i * 10), saved.Mappings);
            Assert(decision.SuppressOriginal && decision.FiredMappings.Count == 1);
            var actions = decision.FiredMappings.Single().Actions;
            Assert(InputActionRunner.Supports(actions));
            output.Events.Clear();
            await runner.RunAsync(actions, () => true, 1000, CancellationToken.None);
            Assert(output.Events.SequenceEqual([expected[i], "release"]));
            output.Events.Clear();
            await Throws<OperationCanceledException>(() => runner.RunAsync(actions, () => false, 1000, CancellationToken.None));
            Assert(output.Events.SequenceEqual(["release"]));
            state.Process(new InputEvent(TriggerKind.Key, 65 + i, false, WheelDirection.None, i * 10 + 1), saved.Mappings);
        }
    }
    finally { if (File.Exists(path)) File.Delete(path); }
}

static async Task CommandBudget()
{
    var output = new RecordingOutput();
    var runner = new InputActionRunner(output);
    var trace = new List<string>();
    // Repeat nodes also consume a command: 1 + 10 * 1000 + 1 exceeds the budget.
    var nested = new ActionSequence { Steps = [
        new AutomationAction { Kind = ActionKind.KeyDown, KeyCode = 65 },
        new AutomationAction { Kind = ActionKind.Repeat, RepeatCount = 10, Steps = [
            new AutomationAction { Kind = ActionKind.Repeat, RepeatCount = 999, Steps = [
                new AutomationAction { Kind = ActionKind.KeyTap, KeyCode = 66 }
            ] }
        ] }
    ] };
    await Throws<InvalidOperationException>(() => runner.RunAsync(nested, () => true, 5000,
        CancellationToken.None, trace: trace.Add));
    Assert(output.Events.Count(e => e == "key tap 66") == 9988);
    Assert(output.PressedKeys.Count == 0 && output.Events.Last() == "release");
    Assert(trace.Single().Contains("command limit"));
    // Exactly the limit succeeds; a fresh invocation receives a fresh budget.
    var boundary = new ActionSequence { Steps = Enumerable.Range(0, InputActionRunner.MaxCommandsPerRun)
        .Select(_ => new AutomationAction { Kind = ActionKind.KeyTap, KeyCode = 67 }).ToList() };
    output.Events.Clear();
    await runner.RunAsync(boundary, () => true, 5000, CancellationToken.None);
    Assert(output.Events.Count == InputActionRunner.MaxCommandsPerRun + 1);
    boundary.Steps.Add(new AutomationAction { Kind = ActionKind.KeyTap, KeyCode = 68 });
    output.Events.Clear();
    await Throws<InvalidOperationException>(() => runner.RunAsync(boundary, () => true, 5000, CancellationToken.None));
    Assert(!output.Events.Contains("key tap 68") && output.Events.Last() == "release");
}

static ActionSequence HeldInputSequence() => new() { Steps = [
    new AutomationAction { Kind = ActionKind.KeyDown, KeyCode = 65 },
    new AutomationAction { Kind = ActionKind.MouseDown, MouseButton = 2 },
    new AutomationAction { Kind = ActionKind.Delay, DelayMilliseconds = 60000 },
    new AutomationAction { Kind = ActionKind.KeyTap, KeyCode = 66 }
] };

static async Task ActionTimeout()
{
    var output = new RecordingOutput();
    await Throws<OperationCanceledException>(() => new InputActionRunner(output)
        .RunAsync(HeldInputSequence(), () => true, 150, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)));
    Assert(output.Events.SequenceEqual(["key down 65", "mouse down 2", "release"]));
    Assert(output.PressedKeys.Count == 0 && output.PressedButtons.Count == 0);
}

static async Task WaitingCancellation()
{
    var output = new RecordingOutput();
    var runner = new InputActionRunner(output);
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    output.OnMouseDown = () => started.TrySetResult();
    using var activeCancel = new CancellationTokenSource();
    var active = runner.RunAsync(HeldInputSequence(), () => true, 5000, activeCancel.Token);
    try
    {
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        using var waitingCancel = new CancellationTokenSource();
        var waiting = runner.RunAsync(HeldInputSequence(), () => true, 5000, waitingCancel.Token);
        waitingCancel.Cancel();
        await Throws<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert(output.Events.SequenceEqual(["key down 65", "mouse down 2"]));
        Assert(output.PressedKeys.Contains(65) && output.PressedButtons.Contains(2));
    }
    finally
    {
        activeCancel.Cancel();
        await Throws<OperationCanceledException>(() => active.WaitAsync(TimeSpan.FromSeconds(2)));
    }
    Assert(output.PressedKeys.Count == 0 && output.PressedButtons.Count == 0);
    Assert(output.Events.Last() == "release");
    output.OnMouseDown = null;
    await runner.RunAsync(new ActionSequence(), () => true, 1000, CancellationToken.None);
}

static async Task InvalidCoordinates()
{
    var target = new CoordinateTarget();
    var sequence = new ActionSequence { Steps = [
        new AutomationAction { Kind = ActionKind.KeyDown, KeyCode = 65 },
        new AutomationAction { Kind = ActionKind.CoordinateClick, Coordinate = target }
    ] };
    foreach (var mode in Enum.GetValues<CoordinateClickMode>())
    {
        target.ClickMode = mode;
        foreach (var changing in new[] { false, true })
        {
            var output = new RecordingOutput();
            var resolutions = 0;
            var postedButtons = 0;
            await Throws<OperationCanceledException>(() => new InputActionRunner(output).RunAsync(sequence,
                () => true, 2000, CancellationToken.None,
                _ => !changing ? null : new PixelPoint(++resolutions == 1 ? 100 : 101, 200),
                postCoordinateButton: (_, _) => postedButtons++, postCoordinateMove: _ => { }));
            Assert(!output.Events.Any(e => e.StartsWith("mouse down")) && postedButtons == 0);
            Assert(output.PressedKeys.Count == 0 && output.Events.Last() == "release");
        }
    }
}

static async Task ActionFocusLoss()
{
    var output = new RecordingOutput();
    var focused = true;
    output.OnMouseDown = () => focused = false;
    await Throws<OperationCanceledException>(() => new InputActionRunner(output)
        .RunAsync(HeldInputSequence(), () => focused, 2000, CancellationToken.None));
    Assert(output.Events.SequenceEqual(["key down 65", "mouse down 2", "release"]));
    Assert(output.PressedKeys.Count == 0 && output.PressedButtons.Count == 0);
}

static void Assert(bool condition) { if (!condition) throw new Exception("Assertion failed."); }
static async Task Throws<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}.");
}

sealed class RecordingOutput : IInputOutput
{
    private PixelPoint _cursor = new(10, 10);
    public bool IgnoreMove { get; set; }
    public Action? OnMouseDown { get; set; }
    public List<string> Events { get; } = [];
    public HashSet<int> PressedKeys { get; } = [];
    public HashSet<int> PressedButtons { get; } = [];
    public void KeyDown(int virtualKey) { PressedKeys.Add(virtualKey); Events.Add($"key down {virtualKey}"); }
    public void KeyUp(int virtualKey) { PressedKeys.Remove(virtualKey); Events.Add($"key up {virtualKey}"); }
    public void KeyTap(int virtualKey) => Events.Add($"key tap {virtualKey}");
    public void MouseDown(int button)
    {
        Events.Add($"mouse down {button}");
        PressedButtons.Add(button);
        OnMouseDown?.Invoke();
    }
    public void MouseUp(int button) { PressedButtons.Remove(button); Events.Add($"mouse up {button}"); }
    public void MouseClick(int button) => Events.Add($"mouse click {button}");
    public void Wheel(int amount, bool horizontal = false) => Events.Add($"{(horizontal ? "horizontal" : "vertical")} wheel {amount}");
    public PixelPoint? GetCursorPosition() => _cursor;
    public void MoveTo(PixelPoint point)
    {
        Events.Add($"move {point.X},{point.Y}");
        if (!IgnoreMove) _cursor = point;
    }
    public void ReleaseAll() { PressedKeys.Clear(); PressedButtons.Clear(); Events.Add("release"); }
}
