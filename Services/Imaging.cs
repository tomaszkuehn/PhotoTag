using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhotoTag.Services;

namespace PhotoTag.Services;

public static class Imaging
{
    public static BitmapSource LoadBitmap(string path, int decodeWidth, int orientation)
    {
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
}