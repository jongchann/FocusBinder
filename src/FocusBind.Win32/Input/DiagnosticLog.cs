using System.Threading.Channels;

namespace FocusBind.Win32.Input;

/// <summary>Best-effort, bounded session trace. Hook callbacks only enqueue short messages.</summary>
public sealed class DiagnosticLog : IDisposable
{
    private readonly Channel<string> _lines = Channel.CreateBounded<string>(new BoundedChannelOptions(2048)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.DropWrite
    });
    private readonly Task _writer;
    private int _disposed;

    public string Path { get; }
    public Exception? LastError { get; private set; }

    public DiagnosticLog(string? path = null)
    {
        Path = path ?? System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FocusBind",
            $"diagnostics-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
        _writer = Task.Run(WriteAsync);
        Write("session started");
    }

    public void Write(string message)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        _lines.Writer.TryWrite($"{DateTimeOffset.Now:O} {message}");
    }

    private async Task WriteAsync()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            await using var stream = new FileStream(Path, FileMode.CreateNew, FileAccess.Write, FileShare.Read,
                4096, FileOptions.Asynchronous);
            await using var writer = new StreamWriter(stream) { AutoFlush = true };
            await foreach (var line in _lines.Reader.ReadAllAsync())
                await writer.WriteLineAsync(line);
        }
        catch (Exception ex) { LastError = ex; }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        _lines.Writer.TryComplete();
        try { _writer.Wait(TimeSpan.FromSeconds(3)); }
        catch (AggregateException ex) { LastError = ex; }
    }
}
