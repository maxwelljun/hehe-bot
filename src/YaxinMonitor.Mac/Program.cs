using System.Diagnostics;
using YaxinMonitor.Core;

namespace YaxinMonitor.Windows;

internal static class Program
{
    private const string DefaultControlUrl = "http://127.0.0.1:17868/";

    public static async Task<int> Main()
    {
        string controlUrl = Environment.GetEnvironmentVariable("YAXIN_MONITOR_CONTROL_URL") ?? DefaultControlUrl;
        if (await ControlServer.IsAlreadyRunningAsync(controlUrl).ConfigureAwait(false))
        {
            MacShell.Open(controlUrl);
            return 0;
        }

        string dataDirectory = Environment.GetEnvironmentVariable("YAXIN_MONITOR_DATA_DIR")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "YaxinMonitor");
        var store = new StateStore(dataDirectory);
        try
        {
            YaxinSettings settings = store.LoadSettings();
            await using var server = new ControlServer(controlUrl, store, settings);
            Console.CancelKeyPress += (_, args) =>
            {
                args.Cancel = true;
                server.RequestShutdown();
            };
            await server.RunAsync().ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception)
        {
            try { store.Log("程序无法启动：" + exception); } catch { }
            MacShell.ShowError("亚信全桌监控无法启动", exception.Message + "\n\n数据目录：" + dataDirectory);
            return 1;
        }
    }
}

internal static class MacShell
{
    public static void Open(string target) => Start("/usr/bin/open", target);

    public static void ShowError(string title, string message)
    {
        string script = $"display alert {AppleScriptString(title)} message {AppleScriptString(message)} as critical";
        Start("/usr/bin/osascript", "-e", script);
    }

    private static string AppleScriptString(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\r", "").Replace("\n", "\\n") + "\"";

    private static void Start(string executable, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo(executable) { UseShellExecute = false };
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        _ = Process.Start(startInfo);
    }
}
