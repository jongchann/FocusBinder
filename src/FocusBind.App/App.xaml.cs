using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using FocusBind.Core.Coordinates;
using FocusBind.Core.Profiles;
using FocusBind.Win32;
using FocusBind.Win32.Input;

namespace FocusBind.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        if (e.Args.FirstOrDefault() != "--verify-package")
        {
            base.OnStartup(e);
            MainWindow = new MainWindow(); MainWindow.Show();
            return;
        }
        // Packaging smoke test: no main window, hooks, settings reads/writes, or input injection.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        if (e.Args.Length != 2) { Shutdown(64); return; }
        var success = false;
        string? error = null;
        try
        {
            if (RuntimeInformation.ProcessArchitecture != Architecture.X64) throw new InvalidOperationException("Expected x64 process.");
            if (DiagnosticLog.IsEnabled) throw new InvalidOperationException("Diagnostic logging must default to off.");
            var disabledLogPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(e.Args[1]))!, $"disabled-{Guid.NewGuid():N}.log");
            using (var disabledLog = new DiagnosticLog(disabledLogPath))
            {
                disabledLog.Write("must not create a file");
                if (disabledLog.Path is not null || File.Exists(disabledLogPath)) throw new InvalidOperationException("Disabled logger touched the filesystem.");
            }
            if (!Resources.Contains("PrimaryButton")) throw new InvalidOperationException("Application resources missing.");
            using var icon = GetResourceStream(new Uri("/FocusBind.App;component/Assets/FocusBind.ico", UriKind.Relative))?.Stream
                ?? throw new InvalidOperationException("Application icon missing.");
            if (ProfileValidator.Validate(new ProfileDocument()).Count != 0) throw new InvalidOperationException("Core validation failed.");
            var layout = new LayoutVariant();
            var context = new CoordinateContext(new PixelRect(0, 0, 1280, 720), 96, layout);
            if (!CoordinateTransform.TryResolve(context, new CoordinateTarget { LayoutId = layout.Id, X = 0.5, Y = 0.5 }, out _))
                throw new InvalidOperationException("Coordinate calculation failed.");
            var editor = new BindingEditorWindow(new WindowSnapshot(0, 0, 0,
                new WindowIdentity(@"C:\PackageVerification\target.exe", "Verification", "TestWindow"),
                new WindowBounds(0, 0, 1280, 720), 96, true, false, false));
            editor.Close(); // Never show: Loaded does not run and no live capture/settings access occurs.
            success = true;
        }
        catch (Exception ex) { error = ex.ToString(); }
        try
        {
            File.WriteAllText(e.Args[1], JsonSerializer.Serialize(new
            {
                Success = success, Version = Assembly.GetExecutingAssembly().GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion,
                Runtime = RuntimeInformation.FrameworkDescription, Architecture = RuntimeInformation.ProcessArchitecture.ToString(),
                CoreLibraryPath = typeof(object).Assembly.Location, DesktopLibraryPath = typeof(Window).Assembly.Location, Error = error
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { success = false; }
        Shutdown(success ? 0 : 1);
    }
}
