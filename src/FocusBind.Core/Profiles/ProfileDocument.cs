namespace FocusBind.Core.Profiles;

public sealed class ProfileDocument
{
    public const int CurrentSchemaVersion = 1;
    public int SchemaVersion { get; set; } = CurrentSchemaVersion;
    public List<Profile> Profiles { get; set; } = [];
    public List<WindowSelection> WindowSelections { get; set; } = [];
    public UiSettings Ui { get; set; } = new();
}

public sealed class UiSettings
{
    public bool StartPaused { get; set; }
    public bool MinimizeToTray { get; set; } = true;
}

public sealed class WindowSelection
{
    public Guid ProfileId { get; set; }
    public WindowMatcher Window { get; set; } = new();
}

public sealed class Profile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New profile";
    public int Priority { get; set; }
    public WindowMatcher Window { get; set; } = new();
    public int MaxConcurrentRuns { get; set; } = 1;
    public int MaxRunMilliseconds { get; set; } = 30000;
    public List<Mapping> Mappings { get; set; } = [];
    public List<LayoutVariant> Layouts { get; set; } = [new()];
}

public sealed class WindowMatcher
{
    public string? ExecutablePath { get; set; }
    public string? ExecutableName { get; set; }
    public string? TitlePattern { get; set; }
    public string? WindowClass { get; set; }
}

public sealed class Mapping
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "New mapping";
    public bool Enabled { get; set; } = true;
    public bool ConsumeOriginal { get; set; }
    public InputTrigger Trigger { get; set; } = new();
    public ActionSequence Actions { get; set; } = new();
}

public enum TriggerKind { Key, MouseButton, VerticalWheel, HorizontalWheel }
public enum TriggerPhase { Press, Release, LongPress }
public enum WheelDirection { None, Positive, Negative }
[Flags] public enum ModifierKeys { None = 0, Shift = 1, Control = 2, Alt = 4, Windows = 8 }

public sealed class InputTrigger
{
    public TriggerKind Kind { get; set; }
    public TriggerPhase Phase { get; set; } = TriggerPhase.Press;
    public ModifierKeys Modifiers { get; set; }
    public int KeyCode { get; set; }
    public int MouseButton { get; set; }
    public WheelDirection WheelDirection { get; set; }
    public int LongPressMilliseconds { get; set; } = 500;
}

public enum ConcurrencyPolicy { IgnoreWhileRunning, Restart }
public sealed class ActionSequence
{
    public ConcurrencyPolicy Concurrency { get; set; } = ConcurrencyPolicy.IgnoreWhileRunning;
    public List<AutomationAction> Steps { get; set; } = [];
}

public enum ActionKind { KeyDown, KeyUp, KeyTap, MouseDown, MouseUp, MouseClick, VerticalWheel, HorizontalWheel, CoordinateClick, Delay, Conditional, Repeat, CallSequence, Lua }
public sealed class AutomationAction
{
    public ActionKind Kind { get; set; }
    public int KeyCode { get; set; }
    public int MouseButton { get; set; }
    public int Amount { get; set; }
    public int DelayMilliseconds { get; set; }
    public int RepeatCount { get; set; }
    public string? Condition { get; set; }
    public string? SequenceName { get; set; }
    public string? LuaSource { get; set; }
    public CoordinateTarget? Coordinate { get; set; }
    public List<AutomationAction> Steps { get; set; } = [];
    public List<AutomationAction> ElseSteps { get; set; } = [];
}

public enum CoordinateSpace { ClientNormalized, ContentNormalized, AnchoredOffset, FixedClientPixel }
public enum CoordinateClickMode { Cursor = 0, WindowMessage = 1, FastCursor = 2 }
public enum Anchor { TopLeft, TopCenter, TopRight, MiddleLeft, Center, MiddleRight, BottomLeft, BottomCenter, BottomRight }
public enum ScalePolicy { Stretch, UniformFit, UniformFill, NoScale }

public sealed class CoordinateTarget
{
    public Guid LayoutId { get; set; }
    public CoordinateSpace Space { get; set; } = CoordinateSpace.ContentNormalized;
    public Anchor Anchor { get; set; } = Anchor.Center;
    public double X { get; set; }
    public double Y { get; set; }
    public bool RestoreCursor { get; set; } = true;
    public CoordinateClickMode ClickMode { get; set; } = CoordinateClickMode.Cursor;
}

public sealed class LayoutVariant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Default";
    public bool IsDefault { get; set; } = true;
    public int Priority { get; set; }
    public int ReferenceWidth { get; set; } = 1600;
    public int ReferenceHeight { get; set; } = 900;
    public double MinAspectRatio { get; set; } = 0.1;
    public double MaxAspectRatio { get; set; } = 10;
    public NormalizedRect Content { get; set; } = new();
    public ScalePolicy Scale { get; set; } = ScalePolicy.Stretch;
}

public sealed class NormalizedRect
{
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; } = 1;
    public double Height { get; set; } = 1;
}
