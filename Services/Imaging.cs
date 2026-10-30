using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace PhotoTag.Services;

public static class Imaging
{
    public static BitmapSource LoadBitmap(string path, int decodeWidth, int orientation)
    {
        if (VideoService.IsVideo(path))
            return ShellThumbnail(path, decodeWidth);

        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.UriSource = new Uri(path);
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.DecodePixelWidth = decodeWidth;
        bmp.EndInit();
        bmp.Freeze();
        return ApplyOrientation(bmp, orientation);
    }

    private static BitmapSource ApplyOrientation(BitmapSource source, int o)
    {
        if (o <= 1 || o > 8) return source;
        Transform t = o switch
        {
            2 => new ScaleTransform(-1, 1),
            3 => new RotateTransform(180),
            4 => new ScaleTransform(1, -1),
            5 => new TransformGroup { Children = { new ScaleTransform(1, -1), new RotateTransform(90) } },
            6 => new RotateTransform(90),
            7 => new TransformGroup { Children = { new ScaleTransform(1, -1), new RotateTransform(270) } },
            8 => new RotateTransform(270),
            _ => Transform.Identity,
        };
        if (t == Transform.Identity) return source;
        var tb = new TransformedBitmap(source, t);
        tb.Freeze();
        return tb;
    }

    // ---------------------------------------------------------------- miniatury filmów (Windows Shell)

    /// <summary>Klatka filmu z systemowego dostawcy miniatur (to samo co w Eksploratorze; obrót już uwzględniony).</summary>
    private static BitmapSource ShellThumbnail(string path, int size)
    {
        BitmapSource? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                result = ShellThumbnailCore(path, size);
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        thread.Join();
        if (error is not null) throw error;
        return result!;
    }

    private static BitmapSource ShellThumbnailCore(string path, int size)
    {
        var iid = typeof(IShellItemImageFactory).GUID;
        // parser powłoki nie akceptuje „/” w ścieżce
        SHCreateItemFromParsingName(System.IO.Path.GetFullPath(path), IntPtr.Zero, ref iid, out var factory);
        try
        {
            var sz = new NativeSize { Width = size, Height = size };
            // najpierw prawdziwa miniatura; gdy brak kodeka — ikona pliku
            if (factory.GetImage(sz, SiigbfThumbnailOnly | SiigbfBiggerSizeOk, out var hbm) != 0 &&
                factory.GetImage(sz, SiigbfBiggerSizeOk, out hbm) != 0)
                throw new InvalidOperationException("Brak miniatury filmu.");
            try
            {
                var bmp = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bmp.Freeze();
                return bmp;
            }
            finally
            {
                DeleteObject(hbm);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    private const int SiigbfBiggerSizeOk = 0x1;
    private const int SiigbfThumbnailOnly = 0x8;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);
}
