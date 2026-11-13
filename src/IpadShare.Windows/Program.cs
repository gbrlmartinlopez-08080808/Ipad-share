using IpadShare.App.App;
using IpadShare.App.Bluetooth;
using IpadShare.App.Input;
using IpadShare.App.UI;
using IpadShare.Core.Settings;

namespace IpadShare.App;

internal static class Program
{
    public static string SettingsPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "iPadShare", "settings.json");

    [STAThread]
    private static int Main(string[] args)
    {
        Log.Init();

        if (args.Contains("--diagnose", StringComparer.OrdinalIgnoreCase))
            return QuickDiagnose();
        if (args.Contains("--reset", StringComparer.OrdinalIgnoreCase))
        {
            try { File.Delete(SettingsPath); } catch { }
        }

        using var single = new SingleInstance(() => TrayApp.Current?.ShowMainFromAnyThread());
        if (!single.IsFirst) return 0;

        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => Log.Error("UI exception", e.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log.Error("Fatal exception", e.ExceptionObject as Exception);
            Log.Flush();
        };
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            Log.Error("Background task exception", e.Exception);
            e.SetObserved();
        };

        var app = new AppController(new SettingsStore(SettingsPath));
        bool background = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        Application.Run(new TrayApp(app, background));
        Log.Flush();
        return 0;
    }

    /// <summary>`iPadShare.exe --diagnose`: prints capabilities to a text file next to the logs and opens it.</summary>
    private static int QuickDiagnose()
    {
        var lines = new List<string> { $"iPad Share quick diagnosis — {DateTime.Now}", $"Windows {Environment.OSVersion.Version}" };
        try
        {
            var (ok, msg) = BleHidPeripheral.ProbeAsync().GetAwaiter().GetResult();
            lines.Add($"Built-in Bluetooth: {(ok ? "OK" : "NOT USABLE")} — {msg}");
        }
        catch (Exception ex) { lines.Add("Bluetooth probe failed: " + ex.Message); }
        lines.Add("USB bridge ports: " + string.Join(", ", BridgeTransport.FindBridgePorts().DefaultIfEmpty("none")));
        try
        {
            foreach (var t in PrecisionTouchpad.Enumerate(m => lines.Add(m))) { lines.Add("Touchpad: " + t.Describe()); t.Dispose(); }
        }
        catch (Exception ex) { lines.Add("Touchpad enumeration failed: " + ex.Message); }
        foreach (var m in SystemInfo.Monitors())
            lines.Add($"Monitor {m.Device}: {m.Bounds.Width}x{m.Bounds.Height}, {m.PhysicalMm.Width:0}x{m.PhysicalMm.Height:0} mm ({(m.PhysicalKnown ? "EDID" : "assumed")})");
        var path = Path.Combine(Log.Directory, "diagnose.txt");
        try
        {
            Directory.CreateDirectory(Log.Directory);
            File.WriteAllLines(path, lines);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
        }
        catch { Console.WriteLine(string.Join(Environment.NewLine, lines)); }
        return 0;
    }
}
