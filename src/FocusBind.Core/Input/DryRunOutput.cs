using FocusBind.Core.Coordinates;

namespace FocusBind.Core.Input;

/// <summary>Simulates output in memory. This class never calls a native input API.</summary>
public sealed class DryRunOutput(Action<string> trace, PixelPoint? initialCursor = null) : IInputOutput
{
    private PixelPoint _cursor = initialCursor ?? new PixelPoint(0, 0);
    private readonly HashSet<int> _keys = [];
    private readonly HashSet<int> _buttons = [];
    public int PressedCount => _keys.Count + _buttons.Count;
    public bool IsKeyDown(int key) => _keys.Contains(key);
    public void KeyDown(int key) { _keys.Add(key); trace($"키 누름 VK {key}"); }
    public void KeyUp(int key) { _keys.Remove(key); trace($"키 뗌 VK {key}"); }
    public void KeyTap(int key) { KeyDown(key); KeyUp(key); }
    public void MouseDown(int button) { _buttons.Add(button); trace($"마우스 누름 {button}"); }
    public void MouseUp(int button) { _buttons.Remove(button); trace($"마우스 뗌 {button}"); }
    public void MouseClick(int button) { MouseDown(button); MouseUp(button); }
    public void Wheel(int amount, bool horizontal = false) => trace($"{(horizontal ? "가로" : "세로")} 휠 {amount}");
    public PixelPoint? GetCursorPosition() => _cursor;
    public void MoveTo(PixelPoint point) { _cursor = point; trace($"커서 이동 {point.X}, {point.Y}"); }
    public void ReleaseAll()
    {
        foreach (var key in _keys.ToArray()) KeyUp(key);
        foreach (var button in _buttons.ToArray()) MouseUp(button);
        trace("합성 입력 해제 완료 (모의 실행)");
    }
}
