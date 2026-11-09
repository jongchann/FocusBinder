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
        var appDirectory = Path.Combine(root, "src/FocusBind.App/bin/Debug/net10.0-windows");
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
        ((TextBlock)main.FindName("ShortcutSummaryText")).Text = "매핑 시작 / 종료   Ctrl + Alt + F6 [VK 117 / 0x75]\n대상 창 음량 +5%   Ctrl + Alt + F7 [VK 118 / 0x76]\n대상 창 음량 -5%   Ctrl + Alt + F8 [VK 119 / 0x77]\n대상 창 음소거 토글   Ctrl + Alt + F9 [VK 120 / 0x78]";
        var outputDirectory = Path.Combine(root, "design-preview");
        Directory.CreateDirectory(outputDirectory);
        Render(main, Path.Combine(outputDirectory, "main.png"), 1140, 850);
        Render(main, Path.Combine(outputDirectory, "main-small.png"), 1000, 720);
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
        editor.Close(); main.Close(); app.Shutdown();
        Console.WriteLine("Rendered main, editor, and global shortcut settings.");
    }
    private static void IgnoreBindings<T>(List<T> bindings) { }
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
