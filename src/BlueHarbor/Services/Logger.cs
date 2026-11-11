using System.Collections.Concurrent;
using System.Text;

namespace BlueHarbor.Services;

public sealed class Logger
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly ConcurrentQueue<string> _recent = new();
    private const int MaxRecent = 200;

    public Logger(string directory)
    {
        _directory = directory;
        Directory.CreateDirectory(_directory);
        CleanupOldLogs();
    }

    public string DirectoryPath => _directory;

    public event EventHandler<string>? LineWritten;

    public void Info(string message) => Write("INFO", message);
    public void Warn(string message) => Write("WARN", message);
    public void Error(string message) => Write("ERROR", message);

    public void Error(Exception ex, string message) =>
        Write("ERROR", $"{message}{Environment.NewLine}{ex}");

    public IReadOnlyList<string> RecentLines
    {
        get
        {
            lock (_gate)
            {
                return _recent.ToArray();
            }
        }
    }

    private void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        lock (_gate)
        {
            try
            {
                var path = Path.Combine(_directory, $"blueharbor-{DateTime.Now:yyyyMMdd}.log");
                var info = new FileInfo(path);
                if (info.Exists && info.Length > 2 * 1024 * 1024)
                {
                    var rotated = Path.Combine(_directory, $"blueharbor-{DateTime.Now:yyyyMMdd-HHmmss}.log");
                    File.Move(path, rotated, overwrite: true);
                }
                File.AppendAllText(path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // Logging must never take the receiver down.
            }

            _recent.Enqueue(line);
            while (_recent.Count > MaxRecent && _recent.TryDequeue(out _)) { }
        }

        try
        {
            LineWritten?.Invoke(this, line);
        }
        catch
        {
            // Ignore subscribers that fail.
        }
    }

    private void CleanupOldLogs()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_directory, "blueharbor-*.log"))
            {
                if (File.GetLastWriteTime(file) < DateTime.Now.AddDays(-14))
                {
                    File.Delete(file);
                }
            }
        }
        catch
        {
            // Best effort.
        }
    }
}
