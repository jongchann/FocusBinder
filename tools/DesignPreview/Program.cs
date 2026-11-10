using System.IO;
using System.Reflection;
using System.Runtime.Loader;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Xml.Linq;
using System.Windows.Markup;
using FocusBind.Core.Profiles;
using FocusBind.Win32;
using FocusBind.Core.Overlay;
using FocusBind.Core.Coordinates;
using System.Windows.Interop;
using System.Runtime.InteropServices;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        try { Run(args); }
        catch (Exception ex) { Console.Error.WriteLine(ex); Environment.Exit(1); }
    }
    private static void Run(string[] args)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
        var assets = Path.Combine(root, "src/FocusBind.App/Assets");
        Directory.CreateDirectory(assets);
        if (args.Contains("--icon"))
        {
            var sizes = new[] { 16, 24, 32, 48, 64, 128, 256 };
            var images = sizes.Select(IconPng).ToArray();
            using var output = new BinaryWriter(File.Create(Path.Combine(assets, "FocusBind.ico")));
            output.Write((ushort)0); output.Write((ushort)1); output.Write((ushort)sizes.Length);
            var offset = 6 + sizes.Length * 16;
            for (var i = 0; i < sizes.Length; i++)
            {
                output.Write((byte)(sizes[i] == 256 ? 0 : sizes[i])); output.Write((byte)(sizes[i] == 256 ? 0 : sizes[i]));
                output.Write((byte)0); output.Write((byte)0); output.Write((ushort)1); output.Write((ushort)32);
                output.Write(images[i].Length); output.Write(offset); offset += images[i].Length;
            }
            foreach (var bytes in images) output.Write(bytes);
            File.WriteAllBytes(Path.Combine(assets, "FocusBind.png"), images[^1]);
            Console.WriteLine("Generated icon: 16–256 px");
            return;
        }
        var appDirectory = args.FirstOrDefault(arg => arg.StartsWith("--app-directory=", StringComparison.Ordinal))?["--app-directory=".Length..]
            ?? Path.Combine(root, "src/FocusBind.App/bin/Debug/net10.0-windows");
        AssemblyLoadContext.Default.Resolving += (_, name) => File.Exists(Path.Combine(appDirectory, name.Name + ".dll"))
            ? AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(appDirectory, name.Name + ".dll")) : null;
        var assembly = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(appDirectory, "FocusBind.App.dll"));
        var app = new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        XNamespace ns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        var resources = XDocument.Load(Path.Combine(root, "src/FocusBind.App/App.xaml")).Root!.Element(ns + "Application.Resources")!;
        var dictionary = new XElement(ns + "ResourceDictionary", new XAttribute(XNamespace.Xmlns + "x", "http://schemas.microsoft.com/winfx/2006/xaml"), resources.Elements());
        app.Resources = (ResourceDictionary)XamlReader.Parse(dictionary.ToString());
        var main = ReadWindow(Path.Combine(root, "src/FocusBind.App/MainWindow.xaml"));
        ((ListView)main.FindName("WindowsList")).ItemsSource = new[] {
            new { DisplayTitle = "대상 게임", DisplayProcessName = "Game.exe", ClientSize = "1920×1080", DisplayState = "표시 중" },
            new { DisplayTitle = "브라우저", DisplayProcessName = "Browser.exe", ClientSize = "1440×900", DisplayState = "표시 중" } };
        ((TextBlock)main.FindName("ResultCountText")).Text = "2개의 창";
        ((TextBlock)main.FindName("ShortcutSummaryText")).Text = "매핑 시작 / 종료   Ctrl + Alt + Z [VK 90 / 0x5A]\n대상 창 음량 +5%   Ctrl + Alt + F7 [VK 118 / 0x76]\n대상 창 음량 -5%   Ctrl + Alt + F8 [VK 119 / 0x77]\n대상 창 음소거 토글   Ctrl + Alt + P [VK 80 / 0x50]\n대상 프로세스 오버레이   Ctrl + Alt + O [VK 79 / 0x4F]";
        var outputDirectory = Path.Combine(root, "design-preview");
        Directory.CreateDirectory(outputDirectory);
        Render(main, Path.Combine(outputDirectory, "main.png"), 1140, 850);
        Render(main, Path.Combine(outputDirectory, "main-small.png"), 1000, 720);
        if (((CheckBox)main.FindName("DiagnosticLoggingBox")).IsChecked == true) throw new Exception("Logging must default off in the UI.");
        ((Expander)main.FindName("DiagnosticsExpander")).IsExpanded = true;
        Render(main, Path.Combine(outputDirectory, "main-logging.png"), 1140, 850);
        ((Expander)main.FindName("DiagnosticsExpander")).IsExpanded = false;
        var shortcutsType = assembly.GetType("FocusBind.App.GlobalShortcuts")!;
        var bindings = shortcutsType.GetMethod("Defaults", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
        var callbackType = typeof(Action<>).MakeGenericType(bindings.GetType());
        var callback = typeof(Program).GetMethod(nameof(IgnoreBindings), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(assembly.GetType("FocusBind.App.ShortcutBinding")!).CreateDelegate(callbackType);
        var shortcutWindow = (Window)Activator.CreateInstance(assembly.GetType("FocusBind.App.ShortcutSettingsWindow")!,
            BindingFlags.Instance | BindingFlags.NonPublic, null, [bindings, callback], null)!;
        Render(shortcutWindow, Path.Combine(outputDirectory, "shortcuts.png"), 600, 710);
        shortcutWindow.Close();
        var editor = ReadWindow(Path.Combine(root, "src/FocusBind.App/BindingEditorWindow.xaml"));
        ((ListBox)editor.FindName("ProfilesList")).ItemsSource = new[] { new { Name = "기본 컨트롤" }, new { Name = "전투 프리셋" } };
        ((ListBox)editor.FindName("ProfilesList")).SelectedIndex = 0;
        ((TextBox)editor.FindName("MappingNameBox")).Text = "기본 공격";
        ((TextBox)editor.FindName("KeyBox")).Text = "Ctrl + A [VK 65 / 0x41]";
        ((TextBlock)editor.FindName("TargetText")).Text = "대상: 게임 화면 · 1920×1080";
        ((TextBlock)editor.FindName("CoordinateText")).Text = "창 상대 1343, 540 px · 1920×1080";
        foreach (var name in new[] { "TriggerTypeCombo", "TriggerPhaseCombo", "ActionModeCombo", "ClickModeCombo" }) ((ComboBox)editor.FindName(name)).SelectedIndex = 0;
        Render(editor, Path.Combine(outputDirectory, "editor.png"), 1360, 820);
        Render(editor, Path.Combine(outputDirectory, "editor-small.png"), 1080, 650);
        VerifyWheelEditor(assembly, outputDirectory);
        VerifyAdvancedEditors(assembly, outputDirectory);
        VerifyShortcutOverlay(assembly, outputDirectory);
        VerifyShortcutRecording(assembly, outputDirectory);
        VerifyProfileImport(assembly, outputDirectory);
        VerifyAnchorEditor(assembly, outputDirectory);
        editor.Close(); main.Close(); app.Shutdown();
        Console.WriteLine("Rendered main, editor, and global shortcut settings.");
    }
    private static void IgnoreBindings<T>(List<T> bindings) { }
    private static void VerifyAnchorEditor(Assembly assembly, string outputDirectory)
    {
        var snapshot = new WindowSnapshot(0, 0, 0, new WindowIdentity(@"C:\Games\Game.exe", "모서리 좌표 테스트", "UnityWndClass"),
            new WindowBounds(0, 0, 1280, 720), 96, true, false, false);
        var window = (Window)Activator.CreateInstance(assembly.GetType("FocusBind.App.BindingEditorWindow")!, snapshot)!;
        var profile = new Profile { Name = "모서리 좌표", Window = new WindowMatcher { ExecutableName = "Game.exe" } };
        var coordinate = new CoordinateTarget { LayoutId = profile.Layouts[0].Id, X = 0.9, Y = 0.9 };
        var mapping = new Mapping { Name = "우하 버튼", Trigger = new InputTrigger { KeyCode = 65 },
            Actions = new ActionSequence { Steps = [new AutomationAction { Kind = ActionKind.CoordinateClick, Coordinate = coordinate }] } };
        profile.Mappings.Add(mapping);
        var profiles = (ListBox)window.FindName("ProfilesList"); profiles.ItemsSource = new[] { profile }; profiles.SelectedIndex = 0;
        var mode = (ComboBox)window.FindName("CoordinateSpaceCombo");
        var grid = (System.Windows.Controls.Primitives.UniformGrid)window.FindName("CoordinateAnchorGrid");
        var xBox = (TextBox)window.FindName("CoordinateOffsetXBox");
        var yBox = (TextBox)window.FindName("CoordinateOffsetYBox");
        var context = new CoordinateContext(new PixelRect(0, 0, 1280, 720), 96, profile.Layouts[0]);
        void Check(bool condition) { if (!condition) throw new Exception("Anchor editor regression failed."); }
        void Call(string name, params object[] args) => window.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, args);
        Check(CoordinateTransform.TryResolve(context, coordinate, out var initial));
        mode.SelectedIndex = 1;
        Check(coordinate.Space == CoordinateSpace.AnchoredPixelOffset && coordinate.Anchor == Anchor.BottomRight);
        Check(CoordinateTransform.TryResolve(context, coordinate, out var converted) && initial == converted);
        foreach (var button in grid.Children.OfType<RadioButton>())
        {
            button.IsChecked = true;
            Check(CoordinateTransform.TryResolve(context, coordinate, out converted) && initial == converted);
        }
        xBox.Text = "-80"; yBox.Text = "-40";
        xBox.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
        Check(coordinate.X == -80 && coordinate.Y == -40 && coordinate.Anchor == Anchor.BottomRight);
        Call("PlaceCoordinate", coordinate, 600.0, 320.0);
        Check(coordinate.Space == CoordinateSpace.AnchoredPixelOffset && coordinate.Anchor == Anchor.BottomRight);
        Check(CoordinateTransform.TryResolve(context, coordinate, out var moved) && moved.X > 1100 && moved.Y > 600);
        var mappings = (ListBox)window.FindName("MappingsList"); mappings.SelectedIndex = -1; mappings.SelectedIndex = 0;
        Check(mode.SelectedIndex == 1 && ((RadioButton)grid.Children[8]).IsChecked == true);
        mode.SelectedIndex = 2;
        Check(coordinate.Space == CoordinateSpace.AnchoredOffset && CoordinateTransform.TryResolve(context, coordinate, out converted) && converted == moved);
        Call("PlaceCoordinate", coordinate, 580.0, 300.0);
        Check(coordinate.Space == CoordinateSpace.AnchoredOffset && coordinate.Anchor == Anchor.BottomRight);
        mode.SelectedIndex = 1;
        xBox.Text = "bad"; xBox.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
        Check(xBox.Text != "bad" && double.IsFinite(coordinate.X));
        xBox.Text = "-80"; yBox.Text = "-40";
        Call("CommitInspector");
        Check(coordinate.X == -80 && coordinate.Y == -40 && coordinate.Space == CoordinateSpace.AnchoredPixelOffset);
        Render(window, Path.Combine(outputDirectory, "editor-anchor.png"), 1080, 750);
        ((StackPanel)window.FindName("AnchorSettings")).BringIntoView();
        window.UpdateLayout();
        Render(window, Path.Combine(outputDirectory, "editor-anchor.png"), 1080, 750);
        window.Close();
        Console.WriteLine("PASS corner anchor UI: position-preserving mode/anchor changes, offsets, drag, reselection and save (offline).");
    }
    private static void VerifyProfileImport(Assembly assembly, string outputDirectory)
    {
        Profile Profile() => new() { Name = "동일 이름 프로파일", Window = new WindowMatcher { ExecutableName = "Game.exe" } };
        var current = new ProfileDocument { Profiles = [Profile(), Profile()] };
        var imported = new ProfileDocument { Profiles = [Profile(), Profile(), Profile()] };
        var type = assembly.GetType("FocusBind.App.ProfileImportWindow")!;
        var window = (Window)Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.NonPublic, null, [current, imported], null)!;
        var grid = (Grid)window.Content;
        var rows = (StackPanel)grid.Children.OfType<ScrollViewer>().Single().Content;
        var choices = (Dictionary<Guid, ProfileImportChoice>)type.GetProperty("Choices", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        if (choices.Count != 0 || rows.Children.Count != 3) throw new Exception("Import must start with an isolated draft.");
        for (var i = 0; i < 3; i++)
        {
            var combos = ((StackPanel)rows.Children[i]).Children.OfType<ComboBox>().ToArray();
            if (combos[0].SelectedIndex != 0 || combos[1].IsEnabled) throw new Exception("Import must default to copy.");
            combos[0].SelectedIndex = i;
            if (combos[1].IsEnabled != (i == 1)) throw new Exception("Merge target enabled state is incorrect.");
            if (i == 1) combos[1].SelectedIndex = 1;
        }
        Render(window, Path.Combine(outputDirectory, "profile-import.png"), 700, 600);
        type.GetMethod("ReadChoices", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        if (choices[imported.Profiles[0].Id].Mode != ProfileImportMode.Copy ||
            choices[imported.Profiles[1].Id] != new ProfileImportChoice(ProfileImportMode.Merge, current.Profiles[1].Id) ||
            choices[imported.Profiles[2].Id].Mode != ProfileImportMode.Skip) throw new Exception("Import choices or selected merge target were lost.");
        window.Close();
        Console.WriteLine("PASS profile import conflict UI: copy default, merge target selection, skip and draft isolation (offline).");
    }
    private static void VerifyShortcutRecording(Assembly assembly, string outputDirectory)
    {
        var shortcutType = assembly.GetType("FocusBind.App.GlobalShortcuts")!;
        var defaults = shortcutType.GetMethod("Defaults", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, null)!;
        var callbackType = typeof(Action<>).MakeGenericType(defaults.GetType());
        var callback = typeof(Program).GetMethod(nameof(IgnoreBindings), BindingFlags.Static | BindingFlags.NonPublic)!
            .MakeGenericMethod(assembly.GetType("FocusBind.App.ShortcutBinding")!).CreateDelegate(callbackType);
        var dialog = (Window)Activator.CreateInstance(assembly.GetType("FocusBind.App.ShortcutSettingsWindow")!,
            BindingFlags.Instance | BindingFlags.NonPublic, null, [defaults, callback], null)!;
        var rows = (System.Collections.IList)dialog.GetType().GetField("_rows", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
        var bindings = dialog.GetType().GetProperty("Bindings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
        void Check(bool value) { if (!value) throw new Exception("Global shortcut recording regression failed."); }
        T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(obj)!;
        object Binding(object row) => row.GetType().GetProperty("Binding", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(row)!;
        int Code(object row) => (int)Binding(row).GetType().GetProperty("KeyCode")!.GetValue(Binding(row))!;
        var mods = ModifierKeys.Control | ModifierKeys.Alt;
        using var source = new HwndSource(new HwndSourceParameters("FocusBind shortcut event verification") { WindowStyle = 0, Width = 1, Height = 1 });
        foreach (var row in rows.Cast<object>())
        {
            var record = row.GetType().GetMethod("Record", BindingFlags.Instance | BindingFlags.NonPublic)!;
            Check(!System.Windows.Input.InputMethod.GetIsInputMethodEnabled(Field<TextBox>(row, "CaptureBox")));
            foreach (var code in Enumerable.Range(0x41, 26))
            foreach (var marker in new[] { "MarkImeProcessed", "MarkSystem", "MarkDeadCharProcessed" })
            {
                var key = System.Windows.Input.KeyInterop.KeyFromVirtualKey(code);
                var e = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, Environment.TickCount, key)
                    { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
                typeof(System.Windows.Input.KeyEventArgs).GetMethod(marker, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(e, null);
                record.Invoke(row, [e, mods]);
                Check(Code(row) == code && e.Handled && Field<TextBox>(row, "CaptureBox").Text.Contains("Ctrl") &&
                    Field<TextBox>(row, "CaptureBox").Text.Contains("Alt"));
            }
            var before = Code(row);
            var bare = new System.Windows.Input.KeyEventArgs(System.Windows.Input.Keyboard.PrimaryDevice, source, 0, System.Windows.Input.Key.M)
                { RoutedEvent = System.Windows.Input.Keyboard.PreviewKeyDownEvent };
            record.Invoke(row, [bare, ModifierKeys.None]); Check(Code(row) == before);
            Field<CheckBox>(row, "CtrlBox").IsChecked = true;
            Field<CheckBox>(row, "AltBox").IsChecked = true;
        }
        var codes = new[] { 0x52, 0x55, 0x44, 0x4D, 0x4F };
        for (var index = 0; index < rows.Count; index++)
        {
            var combo = Field<ComboBox>(rows[index]!, "KeyCombo");
            combo.SelectedItem = combo.Items.Cast<ComboBoxItem>().Single(item => (int)item.Tag == codes[index]);
            Check(Code(rows[index]!) == codes[index]);
        }
        shortcutType.GetMethod("Validate", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [bindings]);
        var native = (uint)shortcutType.GetMethod("NativeModifiers", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [mods])!;
        Check(native == 0x4003);
        var validation = shortcutType.GetMethod("Validate", BindingFlags.Static | BindingFlags.NonPublic)!;
        var duplicateCombo = Field<ComboBox>(rows[0]!, "KeyCombo");
        duplicateCombo.SelectedItem = duplicateCombo.Items.Cast<ComboBoxItem>().Single(item => (int)item.Tag == 0x4D);
        try { validation.Invoke(null, [bindings]); throw new Exception("Duplicate chord was accepted."); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { }
        duplicateCombo.SelectedItem = duplicateCombo.Items.Cast<ComboBoxItem>().Single(item => (int)item.Tag == codes[0]);
        validation.Invoke(null, [bindings]);
        var restored = (System.Collections.IList)shortcutType.GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [System.Text.Json.JsonSerializer.Serialize(bindings)])!;
        Check((int)restored[3]!.GetType().GetProperty("KeyCode")!.GetValue(restored[3])! == 0x4D);
        var legacyJson = System.Text.Json.JsonSerializer.Serialize(bindings).Replace("\"KeyCode\":82", "\"KeyCode\":229");
        var legacy = (System.Collections.IList)shortcutType.GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [legacyJson])!;
        Check((int)legacy[0]!.GetType().GetProperty("KeyCode")!.GetValue(legacy[0])! == 229 &&
            (int)legacy[3]!.GetType().GetProperty("KeyCode")!.GetValue(legacy[3])! == 0x4D);
        try { validation.Invoke(null, [legacy]); throw new Exception("IME process key was registered."); }
        catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { }
        Check(((System.Collections.IList)defaults).Cast<object>().Select(b => (int)b.GetType().GetProperty("KeyCode")!.GetValue(b)!).SequenceEqual(new[] { 90, 118, 119, 80, 79 }));
        var footer = ((Grid)dialog.Content).Children.OfType<StackPanel>().Last();
        footer.Children.OfType<Button>().Single(b => b.Content.ToString() == "기본값 복원").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(rows.Cast<object>().Select(Code).SequenceEqual(new[] { 90, 118, 119, 80, 79 }));
        Check(rows.Cast<object>().All(row => Field<CheckBox>(row, "CtrlBox").IsChecked == true && Field<CheckBox>(row, "AltBox").IsChecked == true));
        Render(dialog, Path.Combine(outputDirectory, "shortcuts.png"), 680, 760);
        Render(dialog, Path.Combine(outputDirectory, "shortcuts-small.png"), 560, 500);
        dialog.Close();
        Console.WriteLine("PASS all global actions: Ctrl+Alt+A-Z IME/System/dead-key recording, direct selection, native flags, JSON round trip, draft isolation.");
    }
    [DllImport("user32.dll")] private static extern int GetWindowLongW(nint window, int index);
    private static void VerifyShortcutOverlay(Assembly assembly, string outputDirectory)
    {
        Window Create(string name, params object[] args) => (Window)Activator.CreateInstance(assembly.GetType("FocusBind.App." + name)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, args, null)!;
        void Check(bool value) { if (!value) throw new Exception("Shortcut overlay regression failed."); }
        var settings = new ShortcutOverlaySettings { Enabled = true, TransparencyPercent = 35 };
        var applied = false;
        var processWindow = new WindowSnapshot(1, 1234, 1, new WindowIdentity(@"C:\Games\Langrisser.exe", "Game", "UnityWndClass"),
            new WindowBounds(0, 0, 1280, 720), 96, true, false, true);
        var dialog = Create("OverlaySettingsWindow", settings, (Action<ShortcutOverlaySettings>)(_ => applied = true), new[] { processWindow });
        var processCombo = (ComboBox)dialog.GetType().GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
        processCombo.SelectedIndex = 1;
        Check(processCombo.SelectedItem.ToString()!.StartsWith("Langrisser.exe") && settings.TargetProcess is null);
        var slider = (Slider)dialog.GetType().GetField("_transparency", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
        slider.Value = 60;
        Check(!applied && settings.TransparencyPercent == 35);
        Render(dialog, Path.Combine(outputDirectory, "overlay-settings.png"), 600, 570);
        dialog.Close();
        var savedTarget = new ShortcutOverlaySettings { Enabled = true, TargetProcess = OverlayProcessTarget.FromWindow(processWindow.Identity) };
        var noProcessDialog = Create("OverlaySettingsWindow", savedTarget, (Action<ShortcutOverlaySettings>)(_ => { }), Array.Empty<WindowSnapshot>());
        var savedCombo = (ComboBox)noProcessDialog.GetType().GetField("_process", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(noProcessDialog)!;
        Check(savedCombo.SelectedIndex == 1 && savedCombo.SelectedItem.ToString()!.Contains("저장된 대상"));
        noProcessDialog.Close();
        var shortcutType = assembly.GetType("FocusBind.App.GlobalShortcuts")!;
        var parse = shortcutType.GetMethod("Parse", BindingFlags.Static | BindingFlags.NonPublic)!;
        const string four = "[{\"Action\":0,\"KeyCode\":121,\"Modifiers\":6},{\"Action\":1,\"KeyCode\":118,\"Modifiers\":6},{\"Action\":2,\"KeyCode\":119,\"Modifiers\":6},{\"Action\":3,\"KeyCode\":120,\"Modifiers\":6}]";
        var migrated = (System.Collections.IList)parse.Invoke(null, [four])!;
        Check(migrated.Count == 5);
        var last = migrated[4]!;
        Check(Convert.ToInt32(last.GetType().GetProperty("Action")!.GetValue(last)) == 5);
        Check((int)last.GetType().GetProperty("KeyCode")!.GetValue(last)! == 79); // Use the configured default O.
        var occupied = four.Replace("\"KeyCode\":120", "\"KeyCode\":79");
        var fallback = (System.Collections.IList)parse.Invoke(null, [occupied])!;
        Check((int)fallback[4]!.GetType().GetProperty("KeyCode")!.GetValue(fallback[4])! == 122); // O and F10 occupied: choose F11.
        var legacy = four[..^1] + ",{\"Action\":4,\"KeyCode\":122,\"Modifiers\":6}]";
        var legacyMigrated = (System.Collections.IList)parse.Invoke(null, [legacy])!;
        Check(legacyMigrated.Count == 5 && !legacyMigrated.Cast<object>().Any(b => Convert.ToInt32(b.GetType().GetProperty("Action")!.GetValue(b)) == 4));
        var restored = (System.Collections.IList)parse.Invoke(null, [System.Text.Json.JsonSerializer.Serialize(migrated)])!;
        Check(restored.Count == 5 && (int)restored[4]!.GetType().GetProperty("KeyCode")!.GetValue(restored[4])! == 79);
        var profile = new Profile { Name = "전투 프로필" };
        var layout = profile.Layouts[0];
        profile.Mappings.Add(new Mapping { Name = "공격", Trigger = new InputTrigger { KeyCode = 65, Modifiers = ModifierKeys.Control },
            Actions = new ActionSequence { Steps = [new AutomationAction { Kind = ActionKind.CoordinateClick,
                Coordinate = new CoordinateTarget { LayoutId = layout.Id, Space = CoordinateSpace.ClientNormalized, X = 0.5, Y = 0.5 } }] } });
        profile.Mappings.Add(new Mapping { Name = "확대", Trigger = new InputTrigger { KeyCode = 0x6B },
            Actions = new ActionSequence { Steps = [new AutomationAction { Kind = ActionKind.VerticalWheel, Amount = 120 }] } });
        profile.Mappings.Add(new Mapping { Name = "취소", Trigger = new InputTrigger { KeyCode = 0x1B, Phase = TriggerPhase.Release },
            Actions = new ActionSequence { Steps = [new AutomationAction { Kind = ActionKind.CoordinateClick,
                Coordinate = new CoordinateTarget { LayoutId = layout.Id, Space = CoordinateSpace.ClientNormalized, X = 0.9, Y = 0.85 } }] } });
        var context = new CoordinateContext(new PixelRect(-1920, 100, 1200, 675), 144, layout);
        var hints = ShortcutOverlayLayout.Build(profile, context);
        var overlay = Create("ShortcutOverlayWindow");
        Check(!overlay.ShowActivated && !overlay.Focusable && !overlay.ShowInTaskbar && !overlay.IsHitTestVisible && overlay.AllowsTransparency);
        var render = overlay.GetType().GetMethod("RenderHints", BindingFlags.Instance | BindingFlags.NonPublic)!;
        render.Invoke(overlay, [profile.Name, context, hints, 0.65]);
        var canvas = (Canvas)overlay.Content;
        Check(canvas.Opacity == 0.65 && !canvas.IsHitTestVisible);
        var dots = canvas.Children.OfType<Border>().Where(b => b.Width == 6).ToArray();
        Check(dots.Any(b => Math.Abs(Canvas.GetLeft(b) - 397) < 1 && Math.Abs(Canvas.GetTop(b) - 222.33) < 1));
        Render(overlay, Path.Combine(outputDirectory, "overlay-preview.png"), 800, 450);
        Roots[overlay].Background = new SolidColorBrush(Color.FromRgb(64, 86, 96));
        Render(overlay, Path.Combine(outputDirectory, "overlay-preview.png"), 800, 450);
        overlay.Close();
        // Hidden HWND only: verify native passthrough/non-activation flags without displaying a window.
        var native = Create("ShortcutOverlayWindow");
        var handle = new WindowInteropHelper(native).EnsureHandle();
        var styles = GetWindowLongW(handle, -20);
        Check((styles & (0x08000000 | 0x00000020 | 0x00000080 | 0x00080000)) ==
            (0x08000000 | 0x00000020 | 0x00000080 | 0x00080000));
        native.Close();
        Console.WriteLine("PASS overlay process draft, shortcut migration/fallback/roundtrip, DPI render, and hidden native click-through styles.");
    }
    private static void VerifyAdvancedEditors(Assembly assembly, string outputDirectory)
    {
        var profile = new Profile { Name = "전투 설정", Window = new WindowMatcher { ExecutableName = "example.exe", WindowClass = "TestWindow" } };
        var mapping = new Mapping { Name = "연속 동작", Trigger = new InputTrigger { KeyCode = 65 },
            Actions = new ActionSequence { Steps = [new AutomationAction { Kind = ActionKind.KeyTap, KeyCode = 65 },
                new AutomationAction { Kind = ActionKind.Repeat, RepeatCount = 2, Steps = [new AutomationAction { Kind = ActionKind.Delay, DelayMilliseconds = 100 }] }] } };
        profile.Mappings.Add(mapping);
        Window Create(string name, params object[] args) => (Window)Activator.CreateInstance(assembly.GetType("FocusBind.App." + name)!,
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, args, null)!;
        object? Call(object obj, string method, params object[] args) => obj.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(obj, args);
        T Field<T>(object obj, string name) => (T)obj.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(obj)!;
        void Check(bool value) { if (!value) throw new Exception("Advanced editor regression failed."); }
        var identity = new WindowIdentity(@"C:\Apps\example.exe", "Battle", "TestWindow");
        var conditions = Create("ProfileSettingsWindow", profile, identity);
        Field<TextBox>(conditions, "_title").Text = "[";
        Check((bool)Call(conditions, "ValidateDraft")! == false && profile.Window.TitlePattern is null);
        Field<TextBox>(conditions, "_title").Text = "^Battle$";
        Check((bool)Call(conditions, "ValidateDraft")!);
        Render(conditions, Path.Combine(outputDirectory, "profile-conditions.png"), 600, 640);
        conditions.Close();
        var actions = Create("ActionListWindow", profile, mapping.Actions);
        var sequence = (ActionSequence)actions.GetType().GetProperty("Sequence", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(actions)!;
        Call(actions, "Add", false);
        Check(sequence.Steps.Count == 3 && mapping.Actions.Steps.Count == 2);
        Call(actions, "Move", -1);
        Check(sequence.Steps[0].Kind == ActionKind.KeyTap && sequence.Steps[0].KeyCode == 65);
        Call(actions, "Delete"); Check(sequence.Steps.Count == 2);
        var tree = Field<TreeView>(actions, "_tree");
        ((TreeViewItem)tree.Items[1]).IsSelected = true;
        var addKind = Field<ComboBox>(actions, "_addKind"); addKind.SelectedIndex = 6; // vertical wheel
        Call(actions, "Add", true);
        Check(sequence.Steps[1].Steps.Count == 2 && sequence.Steps[1].Steps[1].Kind == ActionKind.VerticalWheel);
        var box = Field<StackPanel>(actions, "_form").Children.OfType<TextBox>().Single();
        box.Text = "bad"; Check((bool)Call(actions, "TryCommit")! == false);
        box.Text = "-360"; Check((bool)Call(actions, "TryCommit")!);
        Check(sequence.Steps[1].Steps[1].Amount == -360 && mapping.Actions.Steps[1].Steps.Count == 1);
        Check((bool)Call(actions, "ValidateSequence")!);
        Render(actions, Path.Combine(outputDirectory, "action-list.png"), 900, 700);
        addKind.SelectedIndex = 8; // coordinate click
        Call(actions, "Add", false);
        var coordinateForm = Field<StackPanel>(actions, "_form");
        coordinateForm.Children.OfType<ComboBox>().ElementAt(1).SelectedIndex = 2;
        var anchorGrid = coordinateForm.Children.OfType<System.Windows.Controls.Primitives.UniformGrid>().Single();
        ((RadioButton)anchorGrid.Children[8]).IsChecked = true;
        var coordinates = coordinateForm.Children.OfType<TextBox>().ToArray();
        coordinates[0].Text = "-20"; coordinates[1].Text = "-30";
        Check((bool)Call(actions, "TryCommit")!);
        Check(sequence.Steps[1].Steps.Last().Coordinate is { Space: CoordinateSpace.AnchoredOffset, Anchor: Anchor.BottomRight, X: -20, Y: -30 });
        Render(actions, Path.Combine(outputDirectory, "action-coordinate.png"), 900, 700);
        actions.Close();
        var layoutProfile = new Profile { Name = "레이아웃", Window = profile.Window };
        layoutProfile.Mappings.Add(new Mapping { Trigger = new InputTrigger { KeyCode = 65 },
            Actions = new ActionSequence { Steps = [new AutomationAction { Kind = ActionKind.CoordinateClick,
                Coordinate = new CoordinateTarget { LayoutId = layoutProfile.Layouts[0].Id, X = 0.5, Y = 0.5 } }] } });
        var layoutsWindow = Create("LayoutSettingsWindow", layoutProfile);
        var layouts = (List<LayoutVariant>)layoutsWindow.GetType().GetProperty("Layouts", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(layoutsWindow)!;
        var layoutForm = Field<StackPanel>(layoutsWindow, "_form");
        layoutForm.Children.OfType<TextBox>().ElementAt(8).Text = "1.2";
        Check((bool)Call(layoutsWindow, "Commit")! == false && layoutProfile.Layouts[0].Content.Width == 1);
        layoutForm.Children.OfType<TextBox>().ElementAt(8).Text = "0.8";
        Check((bool)Call(layoutsWindow, "Commit")! && layouts[0].Content.Width == 0.8);
        Call(layoutsWindow, "Add"); Call(layoutsWindow, "SetDefault");
        Check(layouts.Count == 2 && layouts.Count(l => l.IsDefault) == 1 && layouts[1].IsDefault && layoutProfile.Layouts.Count == 1);
        var layoutList = Field<ListBox>(layoutsWindow, "_list");
        layoutList.SelectedIndex = 0;
        Check(layoutList.SelectedIndex == 0);
        Call(layoutsWindow, "Delete"); Check(layouts.Count == 2); // referenced layout is protected
        layoutList.SelectedIndex = 1;
        Call(layoutsWindow, "Delete"); Check(layouts.Count == 2); // default is protected
        Render(layoutsWindow, Path.Combine(outputDirectory, "layouts.png"), 850, 730);
        layoutsWindow.Close();
        var snapshot = new WindowSnapshot(0, 0, 0, identity, new WindowBounds(0, 0, 1280, 720), 96, true, false, false);
        var editor = Create("BindingEditorWindow", snapshot);
        var profiles = (ListBox)editor.FindName("ProfilesList"); profiles.ItemsSource = new[] { profile }; profiles.SelectedIndex = 0;
        Check(((ComboBox)editor.FindName("ActionModeCombo")).SelectedIndex == 3);
        Call(editor, "CommitInspector"); Check(mapping.Actions.Steps.Count == 2 && mapping.Actions.Steps[1].RepeatCount == 2);
        var second = new Mapping { Name = "미지원 동작", Trigger = new InputTrigger { KeyCode = 66 },
            Actions = new ActionSequence { Steps = [new AutomationAction { Kind = ActionKind.Lua, LuaSource = "return" }] } };
        profile.Mappings.Add(second);
        var mappings = (ListBox)editor.FindName("MappingsList"); mappings.Items.Refresh(); mappings.SelectedItem = second;
        Check(((TextBlock)editor.FindName("MappingStatusText")).Text.Contains("Lua"));
        Call(editor, "MoveMapping", -1);
        Check(profile.Mappings[0] == second && mappings.SelectedItem == second);
        ((CheckBox)editor.FindName("MappingEnabledBox")).IsChecked = false;
        Check(!second.Enabled && ((TextBlock)editor.FindName("MappingStatusText")).Text.Contains("비활성"));
        editor.Close();
        var dry = Create("DryRunWindow", profile, mapping, snapshot);
        var previousContext = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        try
        {
            dry.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            PumpUntil(() => Field<bool>(dry, "_finished"));
            Check(Field<TextBox>(dry, "_log").Text.Contains("모의 실행 완료"));
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        Render(dry, Path.Combine(outputDirectory, "dry-run.png"), 760, 600);
        dry.Close();
        var cancelProfile = new Profile { Name = "취소 검사", Window = profile.Window };
        var cancelMapping = new Mapping { Trigger = new InputTrigger { KeyCode = 65 }, Actions = new ActionSequence { Steps = [
            new AutomationAction { Kind = ActionKind.KeyDown, KeyCode = 65 },
            new AutomationAction { Kind = ActionKind.Delay, DelayMilliseconds = 60000 }
        ] } }; cancelProfile.Mappings.Add(cancelMapping);
        var canceledDry = Create("DryRunWindow", cancelProfile, cancelMapping, snapshot);
        SynchronizationContext.SetSynchronizationContext(new System.Windows.Threading.DispatcherSynchronizationContext());
        try
        {
            canceledDry.RaiseEvent(new RoutedEventArgs(FrameworkElement.LoadedEvent));
            PumpUntil(() => Field<TextBox>(canceledDry, "_log").Text.Contains("키 누름"));
            canceledDry.Close();
            PumpUntil(() => Field<bool>(canceledDry, "_finished"));
            Check(Field<TextBox>(canceledDry, "_log").Text.Contains("키 뗌 VK 65"));
        }
        finally { SynchronizationContext.SetSynchronizationContext(previousContext); }
        Console.WriteLine("PASS conditions/actions/layouts editors, draft isolation, custom sequence preservation, mapping order/status, dry-run completion/close cancellation (offline).");
    }
    private static void PumpUntil(Func<bool> done)
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(10) };
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var timedOut = false;
        timer.Tick += (_, _) =>
        {
            if (done()) frame.Continue = false;
            else if (elapsed.Elapsed > TimeSpan.FromSeconds(5)) { timedOut = true; frame.Continue = false; }
        };
        timer.Start();
        try { System.Windows.Threading.Dispatcher.PushFrame(frame); }
        finally { timer.Stop(); }
        if (timedOut) throw new TimeoutException("Offline window did not finish.");
    }
    private static void VerifyWheelEditor(Assembly assembly, string outputDirectory)
    {
        // Construct without showing: no Loaded handler, live capture, input injection, or settings writes.
        var snapshot = new WindowSnapshot(0, 0, 0, new WindowIdentity("C:\\Apps\\example.exe", "휠 매핑 테스트", "TestWindow"),
            new WindowBounds(0, 0, 1280, 720), 96, true, false, false);
        var window = (Window)Activator.CreateInstance(assembly.GetType("FocusBind.App.BindingEditorWindow")!, snapshot)!;
        var profile = new Profile { Name = "휠 테스트", Window = new WindowMatcher { ExecutableName = "example.exe" } };
        var mapping = new Mapping { Name = "확대 / 스크롤", Trigger = new InputTrigger { KeyCode = 65 },
            Actions = new ActionSequence { Steps = [new AutomationAction { Kind = ActionKind.VerticalWheel, Amount = -360 }] } };
        profile.Mappings.Add(mapping);
        var profiles = (ListBox)window.FindName("ProfilesList");
        profiles.ItemsSource = new[] { profile }; profiles.SelectedIndex = 0;
        var mode = (ComboBox)window.FindName("ActionModeCombo");
        var direction = (ComboBox)window.FindName("WheelDirectionCombo");
        var amount = (TextBox)window.FindName("WheelAmountBox");
        void Check(bool value) { if (!value) throw new Exception("Wheel editor regression failed."); }
        void Commit(string text)
        {
            amount.Text = text;
            amount.RaiseEvent(new RoutedEventArgs(UIElement.LostFocusEvent));
        }
        Check(mode.SelectedIndex == 2 && direction.SelectedIndex == 1 && amount.Text == "360");
        for (var index = 0; index < 4; index++)
        {
            direction.SelectedIndex = index;
            Commit("240");
            var action = mapping.Actions.Steps.Single();
            Check(action.Kind == (index < 2 ? ActionKind.VerticalWheel : ActionKind.HorizontalWheel));
            Check(action.Amount == (index is 1 or 3 ? -240 : 240));
        }
        Commit("0"); Check(mapping.Actions.Steps.Single().Amount == -1);
        Commit("999999"); Check(mapping.Actions.Steps.Single().Amount == -12000);
        Commit("invalid"); Check(amount.Text == "12000");
        mode.SelectedIndex = 0;
        Check(mapping.Actions.Steps.Single().Kind == ActionKind.CoordinateClick);
        mode.SelectedIndex = 1;
        Check(mapping.Actions.Steps.Single().Kind == ActionKind.Repeat);
        mode.SelectedIndex = 2;
        Check(mapping.Actions.Steps.Single().Kind == ActionKind.VerticalWheel);
        direction.SelectedIndex = 1; Commit("360");
        var mappings = (ListBox)window.FindName("MappingsList");
        mappings.SelectedIndex = -1; mappings.SelectedIndex = 0;
        Check(mode.SelectedIndex == 2 && direction.SelectedIndex == 1 && amount.Text == "360");
        Check(((StackPanel)window.FindName("CoordinateSettings")).Visibility == Visibility.Collapsed);
        Render(window, Path.Combine(outputDirectory, "editor-wheel.png"), 1080, 650);
        DependencyObject? parent = (StackPanel)window.FindName("WheelSettings");
        while (parent is not null && parent is not ScrollViewer) parent = VisualTreeHelper.GetParent(parent);
        ((ScrollViewer?)parent)?.ScrollToEnd();
        Render(window, Path.Combine(outputDirectory, "editor-wheel.png"), 1080, 650);
        var triggerType = (ComboBox)window.FindName("TriggerTypeCombo");
        triggerType.SelectedIndex = 6;
        ((CheckBox)window.FindName("TriggerCtrlBox")).IsChecked = true;
        Check(mapping.Trigger.Modifiers == ModifierKeys.Control && ((TextBlock)window.FindName("TriggerSummaryText")).Text.Contains("Ctrl + 휠 ↑"));
        triggerType.SelectedIndex = 7;
        Check(mapping.Trigger.Modifiers == ModifierKeys.Control && mapping.Trigger.WheelDirection == WheelDirection.Negative);
        ((CheckBox)window.FindName("TriggerShiftBox")).IsChecked = true;
        Check(mapping.Trigger.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift));
        ((CheckBox)window.FindName("WheelCtrlBox")).IsChecked = true;
        ((CheckBox)window.FindName("WheelShiftBox")).IsChecked = true;
        direction.SelectedIndex = 3;
        mappings.SelectedIndex = -1; mappings.SelectedIndex = 0;
        Check(mode.SelectedIndex == 2 && ((CheckBox)window.FindName("WheelCtrlBox")).IsChecked == true &&
            ((CheckBox)window.FindName("WheelShiftBox")).IsChecked == true && ((CheckBox)window.FindName("TriggerCtrlBox")).IsChecked == true);
        Check(FocusBind.Core.Input.WheelChord.TryRead(mapping.Actions.Steps, out var chordWheel, out var chordModifiers) &&
            chordWheel!.Kind == ActionKind.HorizontalWheel && chordWheel.Amount == -360 && chordModifiers == (ModifierKeys.Control | ModifierKeys.Shift));
        window.GetType().GetMethod("CommitInspector", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null);
        Check(mapping.Actions.Steps.Count == 5);
        ((ScrollViewer?)parent)?.ScrollToTop();
        Render(window, Path.Combine(outputDirectory, "editor-trigger-chord.png"), 1080, 650);
        ((ScrollViewer?)parent)?.ScrollToEnd();
        Render(window, Path.Combine(outputDirectory, "editor-wheel-chord.png"), 1080, 650);
        mode.SelectedIndex = 0;
        var clickMode = (ComboBox)window.FindName("ClickModeCombo");
        var restore = (CheckBox)window.FindName("RestoreCursorBox");
        clickMode.SelectedIndex = 2;
        restore.IsChecked = false;
        var coordinate = mapping.Actions.Steps.Single().Coordinate!;
        Check(coordinate.ClickMode == CoordinateClickMode.FastCursor && !coordinate.RestoreCursor && restore.IsEnabled);
        mappings.SelectedIndex = -1; mappings.SelectedIndex = 0;
        Check(clickMode.SelectedIndex == 2 && restore.IsChecked == false && restore.IsEnabled);
        clickMode.SelectedIndex = 1;
        Check(coordinate.ClickMode == CoordinateClickMode.WindowMessage && !restore.IsEnabled);
        clickMode.SelectedIndex = 0;
        Check(coordinate.ClickMode == CoordinateClickMode.Cursor && restore.IsEnabled);
        clickMode.SelectedIndex = 2;
        restore.IsChecked = true;
        ((ScrollViewer?)parent)?.ScrollToEnd();
        Render(window, Path.Combine(outputDirectory, "editor-fast-click.png"), 1080, 650);
        window.Close();
        Console.WriteLine("PASS wheel and click mode editing, option preservation, and reselection (offline).");
    }
    private static Window ReadWindow(string path)
    {
        var xml = XDocument.Load(path);
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";
        xml.Root!.Attribute(x + "Class")?.Remove();
        var events = new HashSet<string> { "Click", "Checked", "Unchecked", "SelectionChanged", "TextChanged", "MouseDoubleClick", "PreviewKeyDown", "PreviewMouseDown", "PreviewMouseWheel", "MouseLeftButtonDown", "LostFocus" };
        foreach (var attribute in xml.Descendants().Attributes().Where(a => events.Contains(a.Name.LocalName)).ToArray()) attribute.Remove();
        foreach (var local in xml.Descendants().Attributes().Where(a => a.IsNamespaceDeclaration && a.Value == "clr-namespace:FocusBind.App"))
            local.Value = "clr-namespace:FocusBind.App;assembly=FocusBind.App";
        foreach (var element in xml.Descendants().Where(e => e.Name.NamespaceName == "clr-namespace:FocusBind.App"))
            element.Name = XName.Get(element.Name.LocalName, "clr-namespace:FocusBind.App;assembly=FocusBind.App");
        return (Window)XamlReader.Parse(xml.ToString());
    }
    private static byte[] IconPng(int size)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(size / 256.0, size / 256.0));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(24, 43, 61)), null, new Rect(0, 0, 256, 256), 54, 54);
            var teal = new Pen(new SolidColorBrush(Color.FromRgb(92, 220, 202)), 13) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
            foreach (var path in new[] { "M 56,94 L 56,59 L 92,59", "M 164,59 L 200,59 L 200,94", "M 56,162 L 56,197 L 92,197", "M 164,197 L 200,197 L 200,162" })
                dc.DrawGeometry(null, teal, Geometry.Parse(path));
            var link = new Pen(Brushes.White, 15) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
            dc.DrawLine(link, new Point(101, 155), new Point(155, 101));
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(24, 43, 61)), new Pen(Brushes.White, 12), new Point(96, 160), 18, 18);
            dc.DrawEllipse(new SolidColorBrush(Color.FromRgb(92, 220, 202)), null, new Point(160, 96), 21, 21);
            dc.Pop();
        }
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private static readonly Dictionary<Window, Border> Roots = [];
    private static void Render(Window window, string path, int width, int height)
    {
        if (!Roots.TryGetValue(window, out var root))
        {
            var content = (UIElement)window.Content; window.Content = null;
            root = new Border { Background = new SolidColorBrush(Color.FromRgb(243,246,249)), Child = content };
            Roots[window] = root;
        }
        root.Measure(new Size(width, height)); root.Arrange(new Rect(0, 0, width, height)); root.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(root);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path); encoder.Save(stream);
    }
}
