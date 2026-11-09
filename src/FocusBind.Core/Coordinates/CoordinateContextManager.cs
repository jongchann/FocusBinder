using FocusBind.Core.Profiles;

namespace FocusBind.Core.Coordinates;

/// <summary>Publishes a new immutable context only after geometry has stayed unchanged for 200 ms.</summary>
public sealed class CoordinateContextManager(Profile profile)
{
    private const long StableMilliseconds = 200;
    private PixelRect _pendingBounds;
    private uint _pendingDpi;
    private long _changedAt;
    private bool _hasPending;
    private CoordinateContext? _current;

    public long Version { get; private set; }
    public CoordinateContext? Current => _current;

    public void Update(PixelRect bounds, uint dpi, bool visibleAndFocused, long timestampMilliseconds)
    {
        if (!visibleAndFocused || bounds.Width <= 0 || bounds.Height <= 0 || dpi == 0)
        {
            Invalidate();
            return;
        }
        if (_current is not null && !_hasPending && _current.Client == bounds && _current.Dpi == dpi) return;
        if (_hasPending && _pendingBounds == bounds && _pendingDpi == dpi) return;
        _pendingBounds = bounds;
        _pendingDpi = dpi;
        _changedAt = timestampMilliseconds;
        _hasPending = true;
        _current = null;
    }

    public bool TryGet(long timestampMilliseconds, out CoordinateContext? context)
    {
        if (_hasPending && timestampMilliseconds - _changedAt >= StableMilliseconds)
        {
            var layout = LayoutSelector.Select(profile, _pendingBounds.Width, _pendingBounds.Height, out _);
            _current = new CoordinateContext(_pendingBounds, _pendingDpi, layout);
            _hasPending = false;
            Version++;
        }
        context = _current;
        return context is not null;
    }

    public void Invalidate()
    {
        if (!_hasPending && _current is null) return;
        _hasPending = false;
        _current = null;
        Version++;
    }
}

public static class VirtualDesktopTransform
{
    public static bool TryToAbsolute(PixelPoint point, PixelRect desktop, out PixelPoint absolute)
    {
        absolute = default;
        if (desktop.Width <= 1 || desktop.Height <= 1 || !desktop.Contains(point.X, point.Y)) return false;
        absolute = new PixelPoint(
            (int)Math.Round((point.X - (double)desktop.X) * 65535 / (desktop.Width - 1)),
            (int)Math.Round((point.Y - (double)desktop.Y) * 65535 / (desktop.Height - 1)));
        return true;
    }
}
