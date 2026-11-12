using System.Runtime.InteropServices;
using System.IO;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace LanChat;

internal static class RemoteDesktopCapture
{
    private const int SmCxVirtualScreen = 78;
    private const int SmCyVirtualScreen = 79;
    private const int SmXVirtualScreen = 76;
    private const int SmYVirtualScreen = 77;
    private const uint SrcCopy = 0x00CC0020;
    private const uint CaptureBlt = 0x40000000;

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr window);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr window, IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr dc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll", SetLastError = true)] private static extern bool StretchBlt(IntPtr destination, int x, int y, int width, int height, IntPtr source, int sourceX, int sourceY, int sourceWidth, int sourceHeight, uint operation);
    [DllImport("gdi32.dll")] private static extern int SetStretchBltMode(IntPtr dc, int mode);

    public sealed record CapturedBgraFrame(int Width, int Height, byte[] Pixels);

    public static CapturedBgraFrame CaptureBgra(int maxWidth = 1280)
    {
        var x = GetSystemMetrics(SmXVirtualScreen);
        var y = GetSystemMetrics(SmYVirtualScreen);
        var width = GetSystemMetrics(SmCxVirtualScreen);
        var height = GetSystemMetrics(SmCyVirtualScreen);
        if (width <= 0 || height <= 0) throw new InvalidOperationException("无法获取屏幕尺寸。");
        var scale = Math.Min(1d, (double)maxWidth / width);
        var outputWidth = Math.Max(2, (int)Math.Round(width * scale) & ~1);
        var outputHeight = Math.Max(2, (int)Math.Round(height * scale) & ~1);
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) throw new InvalidOperationException("无法获取屏幕设备上下文。");
        var memoryDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            memoryDc = CreateCompatibleDC(screenDc);
            bitmap = CreateCompatibleBitmap(screenDc, outputWidth, outputHeight);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero) throw new InvalidOperationException("无法创建屏幕捕获位图。");
            previous = SelectObject(memoryDc, bitmap);
            SetStretchBltMode(memoryDc, 4);
            if (!StretchBlt(memoryDc, 0, 0, outputWidth, outputHeight, screenDc, x, y, width, height, SrcCopy | CaptureBlt))
                throw new InvalidOperationException("屏幕捕获失败。");

            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            bgra.Freeze();
            var pixels = new byte[checked(outputWidth * outputHeight * 4)];
            bgra.CopyPixels(pixels, outputWidth * 4, 0);
            return new CapturedBgraFrame(outputWidth, outputHeight, pixels);
        }
        finally
        {
            if (previous != IntPtr.Zero && memoryDc != IntPtr.Zero) SelectObject(memoryDc, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    public static byte[] CaptureJpeg(int maxWidth = 1280, int quality = 50)
    {
        var x = GetSystemMetrics(SmXVirtualScreen);
        var y = GetSystemMetrics(SmYVirtualScreen);
        var width = GetSystemMetrics(SmCxVirtualScreen);
        var height = GetSystemMetrics(SmCyVirtualScreen);
        if (width <= 0 || height <= 0) throw new InvalidOperationException("无法获取屏幕尺寸。");
        var scale = Math.Min(1d, (double)maxWidth / width);
        var outputWidth = Math.Max(1, (int)Math.Round(width * scale));
        var outputHeight = Math.Max(1, (int)Math.Round(height * scale));
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) throw new InvalidOperationException("无法获取屏幕设备上下文。");
        var memoryDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;
        try
        {
            memoryDc = CreateCompatibleDC(screenDc);
            bitmap = CreateCompatibleBitmap(screenDc, outputWidth, outputHeight);
            if (memoryDc == IntPtr.Zero || bitmap == IntPtr.Zero) throw new InvalidOperationException("无法创建屏幕捕获位图。");
            previous = SelectObject(memoryDc, bitmap);
            SetStretchBltMode(memoryDc, 4); // HALFTONE scaling before JPEG encoding reduces CPU and frame size.
            if (!StretchBlt(memoryDc, 0, 0, outputWidth, outputHeight, screenDc, x, y, width, height, SrcCopy | CaptureBlt))
                throw new InvalidOperationException("屏幕捕获失败。");

            var source = Imaging.CreateBitmapSourceFromHBitmap(bitmap, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            source.Freeze();
            var encoder = new JpegBitmapEncoder { QualityLevel = quality };
            encoder.Frames.Add(BitmapFrame.Create(source));
            using var stream = new MemoryStream();
            encoder.Save(stream);
            return stream.ToArray();
        }
        finally
        {
            if (previous != IntPtr.Zero && memoryDc != IntPtr.Zero) SelectObject(memoryDc, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memoryDc != IntPtr.Zero) DeleteDC(memoryDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }
}
