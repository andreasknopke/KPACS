using System.Text;

namespace KPACS.DCMClasses;

public static class DicomCommunicationTrace
{
    private static readonly object SyncRoot = new();
    private static bool _enabled;
    private static string _logPath = string.Empty;

    public static bool IsEnabled
    {
        get
        {
            lock (SyncRoot)
            {
                return _enabled;
            }
        }
    }

    public static string LogPath
    {
        get
        {
            lock (SyncRoot)
            {
                return _logPath;
            }
        }
    }

    public static void Configure(bool enabled, string? logPath)
    {
        string normalizedPath = string.IsNullOrWhiteSpace(logPath) ? string.Empty : Path.GetFullPath(logPath.Trim());
        string? message = null;

        lock (SyncRoot)
        {
            bool wasEnabled = _enabled;
            string previousPath = _logPath;

            _enabled = enabled && !string.IsNullOrWhiteSpace(normalizedPath);
            _logPath = normalizedPath;

            if (_enabled)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_logPath) ?? AppContext.BaseDirectory);
                message = !wasEnabled
                    ? $"Trace enabled. Writing to {_logPath}."
                    : !string.Equals(previousPath, _logPath, StringComparison.OrdinalIgnoreCase)
                        ? $"Trace destination changed to {_logPath}."
                        : "Trace settings refreshed.";
            }
            else if (wasEnabled)
            {
                message = string.IsNullOrWhiteSpace(normalizedPath)
                    ? "Trace disabled because no log path is configured."
                    : "Trace disabled.";
            }
        }

        if (!string.IsNullOrWhiteSpace(message))
        {
            WriteInternal("TRACE", message);
        }
    }

    public static void Log(string source, string message)
    {
        if (string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        WriteInternal(source.Trim(), message.Trim());
    }

    public static void LogException(string source, string message, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string detail = string.IsNullOrWhiteSpace(message)
            ? exception.ToString()
            : $"{message}: {exception}";
        Log(source, detail);
    }

    private static void WriteInternal(string source, string message)
    {
        string? path;
        bool enabled;
        lock (SyncRoot)
        {
            enabled = _enabled;
            path = _logPath;
        }

        if (!enabled || string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            string sanitizedMessage = message.Replace("\r", " ").Replace("\n", " | ");
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{source}] {sanitizedMessage}{Environment.NewLine}";
            lock (SyncRoot)
            {
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch
        {
        }
    }
}
