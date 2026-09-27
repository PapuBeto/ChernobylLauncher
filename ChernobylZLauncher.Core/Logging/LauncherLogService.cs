namespace ChernobylZLauncher.Core.Logging;

public enum LogLevel
{
    Info,
    Success,
    Warning,
    Error
}

public class LogEntry
{
    public DateTime Timestamp { get; set; } = DateTime.Now;
    public LogLevel Level { get; set; }
    public string Message { get; set; } = string.Empty;
}

public class LauncherLogService
{
    private readonly List<LogEntry> _history = new();
    private readonly object _lock = new();

    public event Action<LogEntry>? OnLogAdded;

    public IReadOnlyList<LogEntry> History
    {
        get
        {
            lock (_lock)
            {
                return _history.ToList();
            }
        }
    }

    public void Log(LogLevel level, string message)
    {
        var entry = new LogEntry
        {
            Level = level,
            Message = message
        };

        lock (_lock)
        {
            _history.Add(entry);
        }

        OnLogAdded?.Invoke(entry);
    }

    public void Info(string message) => Log(LogLevel.Info, message);
    public void Success(string message) => Log(LogLevel.Success, message);
    public void Warning(string message) => Log(LogLevel.Warning, message);
    public void Error(string message) => Log(LogLevel.Error, message);

    public void Clear()
    {
        lock (_lock)
        {
            _history.Clear();
        }
    }
}