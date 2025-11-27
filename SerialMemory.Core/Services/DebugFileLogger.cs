namespace SerialMemory.Core.Services;

/// <summary>
/// Static file logger for debugging MCP servers where STDOUT/STDERR may not be visible.
/// Writes to %TEMP%\serialmemory_debug.log
/// </summary>
public static class DebugFileLogger
{
    private static readonly string LogFilePath = Path.Combine(Path.GetTempPath(), "serialmemory_debug.log");
    private static readonly object Lock = new();

    public static void Log(string message)
    {
        try
        {
            lock (Lock)
            {
                File.AppendAllText(LogFilePath, $"[{DateTime.UtcNow:O}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Ignore file logging errors
        }
    }

    public static void Log(string category, string message) => Log($"[{category}] {message}");

    public static void Clear()
    {
        try
        {
            if (File.Exists(LogFilePath))
                File.Delete(LogFilePath);
        }
        catch
        {
            // Ignore
        }
    }

    public static string GetLogFilePath() => LogFilePath;
}
