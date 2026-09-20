using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using ScreenWatch.Core;

namespace ScreenWatch.Windows;

internal static class ScreenCapture
{
    private const int SampleSize = 96;

    public static bool IsRegionAvailable(CaptureRegion region) => Screen.AllScreens.Any(screen =>
        region.FitsInside(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height));

    public static void EnsureInteractiveDesktop()
    {
        IntPtr desktop = OpenInputDesktop(0, false, 0x0001);
        if (desktop == IntPtr.Zero) throw new InvalidOperationException("桌面不可访问（可能已锁屏或出现安全桌面），监控已停止。");
        try
        {
            var name = new StringBuilder(256);
            if (!GetUserObjectInformation(desktop, 2, name, name.Capacity * 2, out _)
                || !string.Equals(name.ToString(), "Default", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("当前不是可截图的普通桌面，监控已停止。");
        }
        finally { CloseDesktop(desktop); }
    }

    public static Bitmap Capture(CaptureRegion region)
    {
        if (!IsRegionAvailable(region)) throw new InvalidOperationException("监控区域已不在同一块显示器内，请重新框选。");
        EnsureInteractiveDesktop();
        var bitmap = new Bitmap(region.Width, region.Height, PixelFormat.Format24bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(region.X, region.Y, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
            EnsureInteractiveDesktop();
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    public static Bitmap LoadReference(string path, CaptureRegion region)
    {
        var file = new FileInfo(path);
        if (file.Length > 64 * 1024 * 1024) throw new InvalidDataException("图片不能超过 64 MB。");
        using var source = Image.FromFile(path);
        if (source.Width != region.Width || source.Height != region.Height)
            throw new InvalidDataException($"图片尺寸必须与区域一致：{region.Width} × {region.Height} 像素；当前为 {source.Width} × {source.Height}。");
        var bitmap = new Bitmap(source.Width, source.Height, PixelFormat.Format24bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.Clear(Color.White);
            graphics.DrawImageUnscaled(source, 0, 0);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    public static byte[] Sample(Bitmap image)
    {
        using var reduced = new Bitmap(SampleSize, SampleSize, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(reduced))
        {
            graphics.CompositingMode = CompositingMode.SourceCopy;
            graphics.InterpolationMode = InterpolationMode.HighQualityBilinear;
            graphics.PixelOffsetMode = PixelOffsetMode.HighQuality;
            graphics.DrawImage(image, new Rectangle(0, 0, SampleSize, SampleSize));
        }

        var bytes = new byte[SampleSize * SampleSize * 3];
        var data = reduced.LockBits(new Rectangle(0, 0, SampleSize, SampleSize), ImageLockMode.ReadOnly, PixelFormat.Format24bppRgb);
        try
        {
            // GDI+ stores BGR; comparison is invariant to the channel ordering.
            for (int y = 0; y < SampleSize; y++)
                Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), bytes, y * SampleSize * 3, SampleSize * 3);
        }
        finally { reduced.UnlockBits(data); }
        return bytes;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseDesktop(IntPtr desktop);
    [DllImport("user32.dll", EntryPoint = "GetUserObjectInformationW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetUserObjectInformation(IntPtr handle, int index, StringBuilder value, int length, out int needed);
}
