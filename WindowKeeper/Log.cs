namespace WindowKeeper;

internal static class Log
{
    private static readonly object Sync = new();
    private static string? _path;

    public static void Init(string folder) => _path = Path.Combine(folder, "log.txt");

    public static void Write(string message)
    {
        if (_path == null) return;
        lock (Sync)
        {
            try
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > 1_000_000)
                    File.Move(_path, _path + ".old", overwrite: true);
                File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss}  {message}{Environment.NewLine}");
            }
            catch
            {
                // logging must never break the app
            }
        }
    }
}
