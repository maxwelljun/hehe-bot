using YaxinMonitor.Core;

namespace YaxinMonitor.Windows;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, @"Local\YaxinMonitor.Desktop.v1", out bool firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("亚信全桌监控已在运行，请双击系统托盘图标打开。", "亚信全桌监控");
            return;
        }

        string root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenWatch", "yaxin");
        var store = new StateStore(root);
        try
        {
            YaxinSettings settings = store.LoadSettings();
            string version = typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
            var license = new LicenseClient(store, version);
            using (var login = new LoginForm(license))
            {
                if (login.ShowDialog() != DialogResult.OK) return;
            }
            Application.Run(new MainForm(store, settings, license));
        }
        catch (Exception exception)
        {
            MessageBox.Show($"程序无法启动：{exception.Message}\n\n数据目录：{root}",
                "亚信全桌监控", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
