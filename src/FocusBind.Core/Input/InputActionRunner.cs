using FocusBind.Core.Profiles;
using FocusBind.Core.Coordinates;

namespace FocusBind.Core.Input;

public interface IInputOutput
{
    void KeyDown(int virtualKey);
    void KeyUp(int virtualKey);
    void KeyTap(int virtualKey);
    void MouseDown(int button);
    void MouseUp(int button);
    void MouseClick(int button);
    void Wheel(int amount, bool horizontal = false);
    PixelPoint? GetCursorPosition();
    void MoveTo(PixelPoint point);
    void ReleaseAll();
}

/// <summary>Runs basic actions off the hook thread. The guard must recheck foreground before every output.</summary>
public sealed class InputActionRunner(IInputOutput output)
{
    // Count every visited action, including Repeat itself, without expanding the tree.
    public const int MaxCommandsPerRun = 10000;
    private readonly SemaphoreSlim _serial = new(1, 1);

    public static bool Supports(ActionSequence sequence, bool allowCoordinates = false) =>
        SupportsSteps(sequence.Steps, allowCoordinates, 0);

    private static bool SupportsSteps(IReadOnlyList<AutomationAction> steps, bool allowCoordinates, int depth) =>
        depth <= 12 && steps.All(step => step.Kind switch
        {
            ActionKind.KeyDown or ActionKind.KeyUp or ActionKind.KeyTap or
            ActionKind.MouseDown or ActionKind.MouseUp or ActionKind.MouseClick or
            ActionKind.VerticalWheel or ActionKind.HorizontalWheel or ActionKind.Delay => true,
            ActionKind.CoordinateClick => allowCoordinates && step.Coordinate is not null,
            ActionKind.Repeat => step.RepeatCount is >= 1 and <= 1000 && step.Steps.Count > 0 &&
                SupportsSteps(step.Steps, allowCoordinates, depth + 1),
            _ => false
        });

    public async Task RunAsync(ActionSequence sequence, Func<bool> canAct, int maxRunMilliseconds,
        CancellationToken cancellationToken, Func<CoordinateTarget, PixelPoint?>? resolveCoordinate = null,
        Action<string>? trace = null, Action<PixelPoint, bool>? postCoordinateButton = null,
        Action<PixelPoint>? postCoordinateMove = null)
    {
        if (!Supports(sequence, resolveCoordinate is not null))
            throw new NotSupportedException("The sequence contains actions not yet implemented.");
        await Task.Yield(); // Never execute an action inline in a low-level hook callback.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(maxRunMilliseconds);
        await _serial.WaitAsync(timeout.Token);
        var executedCommands = 0;
        try
        {
            await ExecuteSteps(sequence.Steps);
        }
        finally
        {
            try { output.ReleaseAll(); }
            finally { _serial.Release(); }
        }

        async Task ExecuteSteps(IReadOnlyList<AutomationAction> steps)
        {
            foreach (var action in steps)
            {
                timeout.Token.ThrowIfCancellationRequested();
                if (!canAct()) throw new OperationCanceledException("Target window lost focus.", timeout.Token);
                if (executedCommands >= MaxCommandsPerRun)
                {
                    var message = $"Execution command limit exceeded ({MaxCommandsPerRun}).";
                    trace?.Invoke(message);
                    throw new InvalidOperationException(message);
                }
                executedCommands++;
                switch (action.Kind)
                {
                    case ActionKind.KeyDown: output.KeyDown(action.KeyCode); break;
                    case ActionKind.KeyUp: output.KeyUp(action.KeyCode); break;
                    case ActionKind.KeyTap: output.KeyTap(action.KeyCode); break;
                    case ActionKind.MouseDown: output.MouseDown(action.MouseButton); break;
                    case ActionKind.MouseUp: output.MouseUp(action.MouseButton); break;
                    case ActionKind.MouseClick: output.MouseClick(action.MouseButton); break;
                    case ActionKind.VerticalWheel: output.Wheel(action.Amount); break;
                    case ActionKind.HorizontalWheel: output.Wheel(action.Amount, horizontal: true); break;
                    case ActionKind.Delay: await Task.Delay(action.DelayMilliseconds, timeout.Token); break;
                    case ActionKind.Repeat:
                        for (var iteration = 0; iteration < action.RepeatCount; iteration++)
                            await ExecuteSteps(action.Steps);
                        break;
                    case ActionKind.CoordinateClick:
                        var target = action.Coordinate!;
                        var point = resolveCoordinate!(target);
                        if (point is null)
                        {
                            await Task.Delay(210, timeout.Token); // One wait for a resizing window to stabilize.
                            point = resolveCoordinate(target);
                        }
                        if (point is null) throw new OperationCanceledException("Coordinate context is unavailable.", timeout.Token);
                        if (target.ClickMode == CoordinateClickMode.WindowMessage)
                        {
                            if (postCoordinateButton is null || postCoordinateMove is null)
                                throw new NotSupportedException("Window message coordinate output is unavailable.");
                            postCoordinateMove(point.Value);
                            await Task.Delay(60, timeout.Token); // Let the client process mouse movement first.
                            if (!canAct() || resolveCoordinate(target) != point)
                                throw new OperationCanceledException("Target geometry changed before message click.", timeout.Token);
                            var postedDown = false;
                            try
                            {
                                postCoordinateButton(point.Value, true);
                                postedDown = true;
                                await Task.Delay(45, timeout.Token);
                            }
                            finally { if (postedDown) postCoordinateButton(point.Value, false); }
                            trace?.Invoke($"window message click posted target={point.Value.X},{point.Value.Y} moveLeadMs=60");
                            break;
                        }
                        var previous = output.GetCursorPosition();
                        var fastCursor = target.ClickMode == CoordinateClickMode.FastCursor;
                        trace?.Invoke($"coordinate click mode={target.ClickMode} moveLeadMs={(fastCursor ? 0 : 45)} " +
                            $"holdMs={(fastCursor ? 0 : 45)} restoreDelayMs={(fastCursor ? 20 : 35)}");
                        var mousePressed = false;
                        try
                        {
                            output.MoveTo(point.Value);
                            if (!fastCursor)
                                await Task.Delay(45, timeout.Token); // Let frame-based clients observe the new cursor position.
                            timeout.Token.ThrowIfCancellationRequested();
                            if (!canAct() || resolveCoordinate(target) != point)
                                throw new OperationCanceledException("Target geometry changed before click.", timeout.Token);
                            var actual = output.GetCursorPosition();
                            trace?.Invoke($"coordinate move target={point.Value.X},{point.Value.Y} " +
                                $"actual={(actual is null ? "unavailable" : $"{actual.Value.X},{actual.Value.Y}")}");
                            if (actual is null || Math.Abs(actual.Value.X - point.Value.X) > 2 ||
                                Math.Abs(actual.Value.Y - point.Value.Y) > 2)
                                throw new OperationCanceledException("Cursor did not reach the mapped coordinate.", timeout.Token);
                            timeout.Token.ThrowIfCancellationRequested();
                            output.MouseDown(1);
                            mousePressed = true;
                            if (!fastCursor)
                                await Task.Delay(45, timeout.Token); // Keep the press visible for at least one game frame.
                            timeout.Token.ThrowIfCancellationRequested();
                            output.MouseUp(1);
                            mousePressed = false;
                            trace?.Invoke("coordinate click released");
                            // Fast mode follows the old manager's immediate press/release, then 20ms before restoring.
                            await Task.Delay(fastCursor ? 20 : 35, timeout.Token);
                        }
                        finally
                        {
                            if (mousePressed) output.MouseUp(1);
                            var current = output.GetCursorPosition();
                            if (target.RestoreCursor && previous is not null && canAct() && current is not null &&
                                Math.Abs(current.Value.X - point.Value.X) <= 2 &&
                                Math.Abs(current.Value.Y - point.Value.Y) <= 2)
                                output.MoveTo(previous.Value);
                        }
                        break;
                }
            }
        }
    }
}
