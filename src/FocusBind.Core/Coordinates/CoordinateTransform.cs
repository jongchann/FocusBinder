using FocusBind.Core.Profiles;

namespace FocusBind.Core.Coordinates;

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public bool Contains(double x, double y) => Width > 0 && Height > 0 &&
        x >= X && y >= Y && x < (double)X + Width && y < (double)Y + Height;
}

public readonly record struct PixelPoint(int X, int Y);
public readonly record struct NormalizedPoint(double X, double Y);

/// <summary>Physical screen-pixel snapshot of one foreground client's current geometry.</summary>
public sealed record CoordinateContext(PixelRect Client, uint Dpi, LayoutVariant Layout)
{
    public PixelRect Content => new(
        Client.X + (int)Math.Round(Layout.Content.X * Client.Width),
        Client.Y + (int)Math.Round(Layout.Content.Y * Client.Height),
        (int)Math.Round(Layout.Content.Width * Client.Width),
        (int)Math.Round(Layout.Content.Height * Client.Height));
}

public static class LayoutSelector
{
    public static LayoutVariant Select(Profile profile, int clientWidth, int clientHeight, out bool outsideRange)
    {
        if (clientWidth <= 0 || clientHeight <= 0) throw new ArgumentOutOfRangeException(nameof(clientWidth));
        var aspect = (double)clientWidth / clientHeight;
        var selected = profile.Layouts
            .Where(l => aspect >= l.MinAspectRatio && aspect <= l.MaxAspectRatio)
            .OrderByDescending(l => l.Priority)
            .ThenBy(l => l.MaxAspectRatio - l.MinAspectRatio)
            .ThenBy(l => l.Id)
            .FirstOrDefault();
        outsideRange = selected is null;
        return selected ?? profile.Layouts.Single(l => l.IsDefault);
    }
}

public static class CoordinateTransform
{
    public static bool TryCapture(PixelRect client, PixelPoint screenPoint, out NormalizedPoint normalized)
    {
        normalized = default;
        if (client.Width <= 1 || client.Height <= 1 || !client.Contains(screenPoint.X, screenPoint.Y)) return false;
        normalized = new NormalizedPoint(
            (screenPoint.X - (double)client.X) / (client.Width - 1),
            (screenPoint.Y - (double)client.Y) / (client.Height - 1));
        return true;
    }

    public static bool TryResolve(CoordinateContext context, CoordinateTarget target, out PixelPoint point)
    {
        point = default;
        if (context.Client.Width <= 0 || context.Client.Height <= 0 || context.Layout.Id != target.LayoutId)
            return false;
        var client = context.Client;
        var content = context.Content;
        if (content.Width <= 0 || content.Height <= 0) return false;
        double x, y;
        switch (target.Space)
        {
            case CoordinateSpace.ClientNormalized:
                x = client.X + target.X * (client.Width - 1);
                y = client.Y + target.Y * (client.Height - 1);
                break;
            case CoordinateSpace.ContentNormalized:
                x = content.X + target.X * (content.Width - 1);
                y = content.Y + target.Y * (content.Height - 1);
                break;
            case CoordinateSpace.FixedClientPixel:
                x = client.X + target.X;
                y = client.Y + target.Y;
                break;
            case CoordinateSpace.AnchoredOffset or CoordinateSpace.AnchoredPixelOffset:
                var (anchorX, anchorY) = AnchorPosition(content, target.Anchor);
                if (!TryAnchorScale(context, target.Space, out var scale)) return false;
                x = anchorX + target.X * scale.X;
                y = anchorY + target.Y * scale.Y;
                break;
            default: return false;
        }
        if (!double.IsFinite(x) || !double.IsFinite(y) || !client.Contains(x, y) ||
            target.Space is CoordinateSpace.ContentNormalized or CoordinateSpace.AnchoredOffset or CoordinateSpace.AnchoredPixelOffset && !content.Contains(x, y))
            return false;
        point = new PixelPoint((int)Math.Round(x), (int)Math.Round(y));
        return client.Contains(point.X, point.Y) &&
            (target.Space is not (CoordinateSpace.ContentNormalized or CoordinateSpace.AnchoredOffset or CoordinateSpace.AnchoredPixelOffset) || content.Contains(point.X, point.Y));
    }

    /// <summary>Records a point without changing the target's coordinate space or anchor.</summary>
    public static bool TryCapture(CoordinateContext context, CoordinateTarget target, PixelPoint point)
    {
        if (context.Layout.Id != target.LayoutId || !context.Client.Contains(point.X, point.Y)) return false;
        double x, y;
        switch (target.Space)
        {
            case CoordinateSpace.ClientNormalized or CoordinateSpace.ContentNormalized:
                var region = target.Space == CoordinateSpace.ClientNormalized ? context.Client : context.Content;
                if (!TryCapture(region, point, out var normalized)) return false;
                x = normalized.X; y = normalized.Y;
                break;
            case CoordinateSpace.FixedClientPixel:
                x = point.X - (double)context.Client.X; y = point.Y - (double)context.Client.Y;
                break;
            case CoordinateSpace.AnchoredOffset or CoordinateSpace.AnchoredPixelOffset:
                if (!context.Content.Contains(point.X, point.Y) || !Enum.IsDefined(target.Anchor) ||
                    !TryAnchorScale(context, target.Space, out var scale)) return false;
                var anchor = AnchorPosition(context.Content, target.Anchor);
                x = (point.X - anchor.X) / scale.X; y = (point.Y - anchor.Y) / scale.Y;
                break;
            default: return false;
        }
        if (!double.IsFinite(x) || !double.IsFinite(y)) return false;
        target.X = x; target.Y = y;
        return true;
    }

    private static bool TryAnchorScale(CoordinateContext context, CoordinateSpace space, out (double X, double Y) scale)
    {
        scale = (1, 1);
        if (space == CoordinateSpace.AnchoredPixelOffset) return true;
        var width = context.Layout.ReferenceWidth * context.Layout.Content.Width;
        var height = context.Layout.ReferenceHeight * context.Layout.Content.Height;
        if (width <= 0 || height <= 0) return false;
        var x = context.Content.Width / width; var y = context.Content.Height / height;
        scale = context.Layout.Scale switch
        {
            ScalePolicy.Stretch => (x, y),
            ScalePolicy.UniformFit => (Math.Min(x, y), Math.Min(x, y)),
            ScalePolicy.UniformFill => (Math.Max(x, y), Math.Max(x, y)),
            ScalePolicy.NoScale => (1, 1),
            _ => (0, 0)
        };
        return double.IsFinite(scale.X) && double.IsFinite(scale.Y) && scale.X > 0 && scale.Y > 0;
    }

    private static (double X, double Y) AnchorPosition(PixelRect rect, Anchor anchor) => anchor switch
    {
        Anchor.TopLeft => (rect.X, rect.Y),
        Anchor.TopCenter => (rect.X + rect.Width / 2.0, rect.Y),
        Anchor.TopRight => (rect.X + rect.Width - 1, rect.Y),
        Anchor.MiddleLeft => (rect.X, rect.Y + rect.Height / 2.0),
        Anchor.Center => (rect.X + rect.Width / 2.0, rect.Y + rect.Height / 2.0),
        Anchor.MiddleRight => (rect.X + rect.Width - 1, rect.Y + rect.Height / 2.0),
        Anchor.BottomLeft => (rect.X, rect.Y + rect.Height - 1),
        Anchor.BottomCenter => (rect.X + rect.Width / 2.0, rect.Y + rect.Height - 1),
        Anchor.BottomRight => (rect.X + rect.Width - 1, rect.Y + rect.Height - 1),
        _ => throw new ArgumentOutOfRangeException(nameof(anchor))
    };
}
