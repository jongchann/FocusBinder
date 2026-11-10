using System.Threading.Channels;

namespace FocusBind.Win32.Input;

/// <summary>Opt-in session trace. Disabled instances allocate no writer and touch no files.</summary>
public sealed class DiagnosticLog : IDisposable
{
    private sealed class Session(string path)
    {
        internal string Path { get; } = path;
        internal Channel<string> Lines { get; } = Channel.CreateBounded<string>(new BoundedChannelOptions(2048)
        { SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.DropWrite });
        internal CancellationTokenSource Stop { get; } = new();
        internal Task Writer { get; set; } = Task.CompletedTask;
    }
    private static readonly object PolicyGate = new();
    private static readonly HashSet<DiagnosticLog> Instances = [];
    private static int _enabled; // Off by default in both Debug and Release.
    private readonly object _gate = new();
    private readonly string? _requestedPath;
    private Session? _session;
    private bool _disposed;
    private int _sessionCount;
    public static bool IsEnabled => Volatile.Read(ref _enabled) != 0;
    public string? Path { get { lock (_gate) return _session?.Path; } }
    public Exception? LastError { get; private set; }

    public DiagnosticLog(string? path = null)
    {
        _requestedPath = path;
        lock (PolicyGate) { Instances.Add(this); if (IsEnabled) ApplyEnabled(true); }
    }

    /// <summary>Updates existing and future loggers without restarting input hooks.</summary>
    public static void Configure(bool enabled)
    {
        lock (PolicyGate)
        {
            if (IsEnabled == enabled) return;
            Volatile.Write(ref _enabled, enabled ? 1 : 0);
            foreach (var log in Instances) log.ApplyEnabled(enabled);
        }
    }

    private void ApplyEnabled(bool enabled)
    {
        Session? old;
        lock (_gate)
        {
            if (_disposed) return;
            if (enabled)
            {
                if (_session is not null) return;
                var path = _requestedPath;
                if (path is null) path = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FocusBind", $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.log");
                else if (_sessionCount > 0) path = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!,
                    $"{System.IO.Path.GetFileNameWithoutExtension(path)}-{Guid.NewGuid():N}.log");
                _sessionCount++;
                LastError = null;
                var session = new Session(path);
                session.Lines.Writer.TryWrite($"{DateTimeOffset.Now:O} session started");
                _session = session;
                session.Writer = Task.Run(() => WriteAsync(session));
                return;
            }
            old = _session; _session = null;
        }
        CloseSession(old, drain: false);
    }

    public void Write(string message)
    {
        if (!IsEnabled) return;
        lock (_gate)
        {
            if (_disposed || !IsEnabled || _session is null) return;
            _session.Lines.Writer.TryWrite($"{DateTimeOffset.Now:O} {message}");
        }
    }

    private async Task WriteAsync(Session session)
    {
        try
        {
            session.Stop.Token.ThrowIfCancellationRequested();
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(session.Path))!);
            await using var stream = new FileStream(session.Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                4096, FileOptions.Asynchronous);
            await using var writer = new StreamWriter(stream) { AutoFlush = true };
            await foreach (var line in session.Lines.Reader.ReadAllAsync(session.Stop.Token))
            {
                session.Stop.Token.ThrowIfCancellationRequested();
                await writer.WriteLineAsync(line);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { LastError = ex; }
    }

    private void CloseSession(Session? session, bool drain)
    {
        if (session is null) return;
        if (!drain) session.Stop.Cancel();
        session.Lines.Writer.TryComplete();
        try { session.Writer.GetAwaiter().GetResult(); }
        catch (Exception ex) { LastError = ex; }
        finally { session.Stop.Dispose(); }
    }

    public void Dispose()
    {
        Session? session;
        lock (PolicyGate)
        {
            Instances.Remove(this);
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true; session = _session; _session = null;
            }
        }
        CloseSession(session, drain: true);
    }
}
