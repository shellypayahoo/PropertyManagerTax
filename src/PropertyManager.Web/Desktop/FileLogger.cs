namespace PropertyManager.Web.Desktop;

/// <summary>
/// Writes the app's log to a file in the data folder (logs/app.log, with the previous one kept as app.old.log), since an
/// installed copy has no window to show it in. Only warnings and errors from the framework, and the app's own messages.
/// </summary>
public sealed class FileLoggerProvider(string folder) : ILoggerProvider
{
    private const long MaxBytes = 5 * 1024 * 1024;
    private readonly object gate = new();
    private readonly string path = Path.Combine(folder, "app.log");

    public string LogPath => path;

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
    }

    internal void Write(string line)
    {
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(folder);
                if (File.Exists(path) && new FileInfo(path).Length > MaxBytes)
                {
                    File.Move(path, Path.Combine(folder, "app.old.log"), overwrite: true);
                }
                File.AppendAllText(path, line + Environment.NewLine);
            }
            catch (IOException)
            {
                // Logging must never stop the app.
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= (category.StartsWith("Microsoft", StringComparison.Ordinal) || category.StartsWith("System", StringComparison.Ordinal) ? LogLevel.Warning : LogLevel.Information);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }
            var line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss} {logLevel,-11} {category}: {formatter(state, exception)}";
            provider.Write(exception is null ? line : $"{line}{Environment.NewLine}{exception}");
        }
    }
}
