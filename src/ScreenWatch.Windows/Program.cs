using ScreenWatch.Core;

namespace ScreenWatch.Windows;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        using var mutex = new Mutex(true, @"Local\ScreenWatch.Desktop.v1", out bool firstInstance);
        if (!firstInstance)
        {
            MessageBox.Show("ScreenWatch 已在运行，请在系统托盘中双击图标打开。", "ScreenWatch");
            return;
        }

        var store = new ProfileStore(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ScreenWatch"));
        try
        {
            var settings = store.Load();
            Application.Run(new MainForm(store, settings));
        }
        catch (Exception exception)
        {
            MessageBox.Show($"程序无法继续运行：{exception.Message}\n\n配置目录：{store.DirectoryPath}\n如果配置损坏，请先备份再移走 settings.json 后重试。",
                "ScreenWatch", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
