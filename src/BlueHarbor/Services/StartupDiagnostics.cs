namespace BlueHarbor.Services;

/// <summary>
/// Last-resort diagnostics for failures that happen before the normal logger
/// (or its data directory) is available. Always writes under %TEMP%, which is
/// writable even when the app is launched from a restricted context.
/// </summary>
public static class StartupDiagnostics
{
    public static string LogPath { get; } =
        Path.Combine(Path.GetTempPath(), "BlueHarbor-startup.log");

    public static void Write(string message)
    {
        try
        {
            File.AppendAllText(
                LogPath,
                $"{DateTime.Now:O} {message}{Environment.NewLine}");
        }
        catch
        {
            // Nothing more we can do.
        }
    }

    public static void Write(Exception exception, string context)
    {
        Write($"{context}: {exception}");
    }
}
