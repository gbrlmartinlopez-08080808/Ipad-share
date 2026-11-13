using System.Collections.Concurrent;
using System.Text;

namespace IpadShare.App.App;

/// <summary>Thread-safe logger: rolling file in %LOCALAPPDATA%\iPadShare\logs plus an in-memory tail for Diagnostics.</summary>
internal static class Log
{
    private static readonly ConcurrentQueue<string> s_tail = new();
    private static readonly BlockingCollection<string> s_queue = new(new ConcurrentQueue<string>(), 10000);
    private static string? s_path;
    private static Thread? s_writer;
    private const int TailLines = 400;
    private const long MaxBytes = 2 * 1024 * 1024;

    public static string Directory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "iPadShare", "logs");

    public static string? FilePath => s_path;

    public static void Init()
    {
        try
        {
            System.IO.Directory.CreateDirectory(Directory);
            s_path = Path.Combine(Directory, "ipadshare.log");
            if (File.Exists(s_path) && new FileInfo(s_path).Length > MaxBytes)
                File.Move(s_path, Path.Combine(Directory, "ipadshare.1.log"), overwrite: true);
        }
        catch { s_path = null; }
        s_writer = new Thread(WriteLoop) { IsBackground = true, Name = "iPadShare log", Priority = ThreadPriority.BelowNormal };
        s_writer.Start();
        Info($"iPad Share {typeof(Log).Assembly.GetName().Version} starting on {Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture})");
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);
    public static void Error(string message, Exception? ex = null) =>
        Write("ERROR", ex == null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}");

    private static void Write(string level, string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {level,-5} {message}";
        s_tail.Enqueue(line);
        while (s_tail.Count > TailLines && s_tail.TryDequeue(out _)) { }
        s_queue.TryAdd(line);   // never block the caller (hooks run on hot paths)
    }

    public static string Tail()
    {
        var sb = new StringBuilder();
        foreach (var l in s_tail) sb.AppendLine(l);
        return sb.ToString();
    }

    private static void WriteLoop()
    {
        foreach (var line in s_queue.GetConsumingEnumerable())
        {
            if (s_path == null) continue;
            try { File.AppendAllText(s_path, line + Environment.NewLine); } catch { }
        }
    }

    public static void Flush()
    {
        var deadline = DateTime.UtcNow.AddSeconds(1);
        while (s_queue.Count > 0 && DateTime.UtcNow < deadline) Thread.Sleep(10);
    }
}
