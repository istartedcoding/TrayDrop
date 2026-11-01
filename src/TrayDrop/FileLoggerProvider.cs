using Microsoft.Extensions.Logging;

namespace TrayDrop;

internal sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly object _gate = new();
    private int _failureReported;

    public FileLoggerProvider(string dataDirectory)
    {
        var directory = Path.Combine(dataDirectory, "Logs");
        Directory.CreateDirectory(directory);
        LogPath = Path.Combine(directory, "TrayDrop.log");
    }

    public string LogPath { get; }
    public event Action<Exception>? WriteFailed;
    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);
    public void Dispose() { }

    private sealed class EmptyScope : IDisposable
    {
        public static EmptyScope Instance { get; } = new();
        public void Dispose() { }
    }

    private sealed class FileLogger(FileLoggerProvider owner, string category) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => EmptyScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Information;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;
            var entry = $"{DateTimeOffset.Now:O} [{logLevel}] {category}: {formatter(state, exception)}" +
                        (exception is null ? "" : Environment.NewLine + exception) + Environment.NewLine;
            try
            {
                lock (owner._gate)
                {
                    if (File.Exists(owner.LogPath) && new FileInfo(owner.LogPath).Length > 5 * 1024 * 1024)
                        File.Move(owner.LogPath, owner.LogPath + ".previous", overwrite: true);
                    File.AppendAllText(owner.LogPath, entry);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                System.Diagnostics.Trace.TraceError("TrayDrop log write failed: {0}\n{1}", error, entry);
                if (Interlocked.Exchange(ref owner._failureReported, 1) == 0)
                    owner.WriteFailed?.Invoke(error);
            }
        }
    }
}
