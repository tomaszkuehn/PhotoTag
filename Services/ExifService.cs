using ExifLibrary;

namespace PhotoTag.Services;

public sealed record GeoPoint(double Lat, double Lng);

public static class ExifService
{
    private static readonly ExifTag[] GpsCoreTags =
    {
        ExifTag.GPSLatitude,
        ExifTag.GPSLongitude,
        ExifTag.GPSLatitudeRef,
        ExifTag.GPSLongitudeRef,
    };

    public static bool Supports(string extension) =>
        extension is ".jpg" or ".jpeg" or ".tif" or ".tiff";

    public static (GeoPoint? Gps, int Orientation, DateTime? DateTaken, bool Error) ReadInfo(string path)
    {
        try
        {
            var file = ImageFile.FromFile(path);
            var orientation = file.Properties.Get<ExifUShort>(ExifTag.Orientation)?.Value ?? 1;

            var dateTaken = ReadDateTaken(file);

            var lat = file.Properties.Get<GPSLatitudeLongitude>(ExifTag.GPSLatitude);
            var lon = file.Properties.Get<GPSLatitudeLongitude>(ExifTag.GPSLongitude);
            if (lat is null || lon is null)
                return (null, (int)orientation, dateTaken, false);

            double la = ToDecimal(lat);
            double lo = ToDecimal(lon);

            var latRef = file.Properties.Get<ExifEnumProperty<GPSLatitudeRef>>(ExifTag.GPSLatitudeRef);
            var lonRef = file.Properties.Get<ExifEnumProperty<GPSLongitudeRef>>(ExifTag.GPSLongitudeRef);
            if (latRef?.Value == GPSLatitudeRef.South) la = -la;
            if (lonRef?.Value == GPSLongitudeRef.West) lo = -lo;

            return (new GeoPoint(la, lo), (int)orientation, dateTaken, false);
        }
        catch
        {
            return (null, 1, null, true);
        }
    }

    private static DateTime? ReadDateTaken(ImageFile file)
    {
        var dt = file.Properties.Get<ExifDateTime>(ExifTag.DateTimeOriginal)?.Value;
        if (dt is null || dt.Value.Year < 1990)
            dt = file.Properties.Get<ExifDateTime>(ExifTag.DateTimeDigitized)?.Value;
        return dt is { Year: >= 1990 } ? dt : null;
    }

    public static void WriteGps(string source, string target, double lat, double lng)
    {
        var file = ImageFile.FromFile(source);

        foreach (var prop in file.Properties.Where(p => GpsCoreTags.Contains(p.Tag)).ToList())
            file.Properties.Remove(prop);

        file.Properties.Set(new ExifEnumProperty<GPSLatitudeRef>(
            ExifTag.GPSLatitudeRef, lat >= 0 ? GPSLatitudeRef.North : GPSLatitudeRef.South));
        file.Properties.Set(new ExifEnumProperty<GPSLongitudeRef>(
            ExifTag.GPSLongitudeRef, lng >= 0 ? GPSLongitudeRef.East : GPSLongitudeRef.West));

        var latDms = ToDms(lat);
        var lonDms = ToDms(lng);
        file.Properties.Set(new GPSLatitudeLongitude(ExifTag.GPSLatitude, latDms.d, latDms.m, latDms.s));
        file.Properties.Set(new GPSLatitudeLongitude(ExifTag.GPSLongitude, lonDms.d, lonDms.m, lonDms.s));

        file.Save(target);
    }

    private static double ToDecimal(GPSLatitudeLongitude v) =>
        (double)v.Degrees + (double)v.Minutes / 60.0 + (double)v.Seconds / 3600.0;

    private static (float d, float m, float s) ToDms(double value)
    {
        value = Math.Abs(value);
        double d = Math.Floor(value);
        double rest = Math.Max(0, value - d);
        double m = Math.Floor(rest * 60);
        double s = Math.Max(0, Math.Min(59.9999, (rest * 60 - m) * 60));
        return ((float)d, (float)m, (float)s);
    }
}