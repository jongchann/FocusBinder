using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Interop;
using System.Windows.Input;
using FocusBind.Core.Profiles;
using ModifierKeys = FocusBind.Core.Profiles.ModifierKeys;

namespace FocusBind.App;

internal enum ShortcutAction { MappingToggle = 0, VolumeUp = 1, VolumeDown = 2, MuteToggle = 3, OverlayToggle = 5 } // 4 was legacy mute-off.
internal sealed class ShortcutBinding
{
    public ShortcutAction Action { get; set; }
    public int KeyCode { get; set; }
    public ModifierKeys Modifiers { get; set; } = ModifierKeys.Control | ModifierKeys.Alt;
}

internal sealed class GlobalShortcuts : IDisposable
{
    private readonly HwndSource _source;
    private readonly List<int> _registered = [];
    internal event Action<ShortcutAction>? Invoked;
    internal static string SettingsPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusBind", "shortcuts.json");
    internal static List<ShortcutBinding> Defaults() =>
    [
        new() { Action = ShortcutAction.MappingToggle, KeyCode = 0x5A },
        new() { Action = ShortcutAction.VolumeUp, KeyCode = 0x76 },
        new() { Action = ShortcutAction.VolumeDown, KeyCode = 0x77 },
        new() { Action = ShortcutAction.MuteToggle, KeyCode = 0x50 },
        new() { Action = ShortcutAction.OverlayToggle, KeyCode = 0x4F }
    ];
    internal static List<ShortcutBinding> Load()
    {
        if (!File.Exists(SettingsPath)) return Defaults();
        return Parse(File.ReadAllText(SettingsPath));
    }
    internal static List<ShortcutBinding> Parse(string json)
    {
        var bindings = JsonSerializer.Deserialize<List<ShortcutBinding>>(json)
            ?? throw new InvalidDataException("단축키 설정이 비어 있습니다.");
        if (bindings.Any(b => b is null)) throw new InvalidDataException("단축키 설정에 빈 항목이 있습니다.");
        // The original numeric action 3 (mute on) becomes toggle; retire action 4 (mute off).
        bindings.RemoveAll(b => (int)b.Action == 4);
        if (!bindings.Any(b => b.Action == ShortcutAction.OverlayToggle))
        {
            // Preserve all user bindings; pick a free fallback if the new default is already taken.
            var key = new[] { Defaults().Single(b => b.Action == ShortcutAction.OverlayToggle).KeyCode }
                .Concat(Enumerable.Range(0x79, 15)).First(code => !bindings.Any(b => b.KeyCode == code &&
                b.Modifiers == (ModifierKeys.Control | ModifierKeys.Alt)));
            bindings.Add(new ShortcutBinding { Action = ShortcutAction.OverlayToggle, KeyCode = key });
        }
        ValidateStructure(bindings);
        // Old recorders could persist VK_PROCESSKEY under an IME. Keep other user bindings
        // intact so the affected row can be repaired in the editor; Apply still rejects it.
        if (bindings.Any(b => b.KeyCode != 0xE5 && !IsShortcutKey(b.KeyCode))) Validate(bindings);
        return bindings;
    }
    internal static void Save(List<ShortcutBinding> bindings)
    {
        Validate(bindings);
        Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
        var temporary = SettingsPath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(bindings, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, SettingsPath, true);
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hwnd, int id);
    internal GlobalShortcuts(HwndSource source) { _source = source; source.AddHook(Hook); }
    internal static bool IsShortcutKey(int code) => code is >= 8 and <= 255 &&
        code is not (0x10 or 0x11 or 0x12 or 0x5B or 0x5C or 0xA0 or 0xA1 or 0xA2 or 0xA3 or 0xA4 or 0xA5 or 0xE5) &&
        KeyInterop.KeyFromVirtualKey(code) != Key.None;

    internal static uint NativeModifiers(ModifierKeys modifiers) =>
        (modifiers.HasFlag(ModifierKeys.Alt) ? 1u : 0) |
        (modifiers.HasFlag(ModifierKeys.Control) ? 2u : 0) |
        (modifiers.HasFlag(ModifierKeys.Shift) ? 4u : 0) |
        (modifiers.HasFlag(ModifierKeys.Windows) ? 8u : 0) | 0x4000;
    internal static void Validate(List<ShortcutBinding> bindings)
    {
        ValidateStructure(bindings);
        if (bindings.FirstOrDefault(b => !IsShortcutKey(b.KeyCode)) is { } invalid)
            throw new InvalidDataException($"{ShortcutSettingsWindow.Label(invalid.Action)}: 기록할 수 없는 키입니다. 설정창의 키 목록에서 다시 선택하세요.");
    }

    private static void ValidateStructure(List<ShortcutBinding> bindings)
    {
        if (bindings.Count != 5 || bindings.Select(b => b.Action).Distinct().Count() != 5 ||
            bindings.Any(b => !Enum.IsDefined(b.Action) || b.KeyCode is < 1 or > 255 || b.Modifiers == 0 || ((int)b.Modifiers & ~15) != 0) ||
            bindings.Select(b => (b.KeyCode, b.Modifiers)).Distinct().Count() != bindings.Count)
            throw new InvalidDataException("각 동작에 중복되지 않는 보조키 조합을 지정하세요.");
    }
    internal void Apply(List<ShortcutBinding> bindings)
    {
        Validate(bindings);
        Clear();
        try
        {
            foreach (var binding in bindings)
            {
                var native = NativeModifiers(binding.Modifiers);
                var id = 0x5100 + (int)binding.Action;
                if (!RegisterHotKey(_source.Handle, id, native, (uint)binding.KeyCode))
                    throw new InvalidOperationException($"등록할 수 없는 단축키: {KeyNames.Format(binding.KeyCode, binding.Modifiers)} (다른 앱 사용 여부 확인)");
                _registered.Add(id);
            }
        }
        catch { Clear(); throw; }
    }
    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x0312 && _registered.Contains(wParam.ToInt32()))
        { handled = true; Invoked?.Invoke((ShortcutAction)(wParam.ToInt32() - 0x5100)); }
        return 0;
    }
    internal void Clear() { foreach (var id in _registered) UnregisterHotKey(_source.Handle, id); _registered.Clear(); }
    public void Dispose() { Clear(); _source.RemoveHook(Hook); }
}
