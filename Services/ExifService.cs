using System.Globalization;
using System.Text;
using ExifLibrary;

namespace PhotoTag.Services;

public sealed record GeoPoint(double Lat, double Lng);

public sealed record PhotoExif(
    GeoPoint? Gps,
    int Orientation,
    DateTime? DateTaken,
    TimeSpan? UtcOffset,
    string? Camera,
    double? Direction,
    GpsSource GpsSource,
    bool Error,
    bool TimeWritten = false);

/// <summary>Skąd pochodzi pozycja GPS zdjęcia.</summary>
public enum GpsSource
{
    None,
    /// <summary>Zapisana przez urządzenie w chwili zrobienia zdjęcia (są znaczniki czasu/wysokości/metody GPS).</summary>
    Device,
    /// <summary>Dopisana później (PhotoTag lub plik _LOCPT).</summary>
    Added,
    /// <summary>Same współrzędne, bez śladów urządzenia — prawdopodobnie dopisane innym programem.</summary>
    Unknown,
}

public static class ExifService
{
    // OffsetTimeOriginal (0x9011) nie ma w enumie ExifLibNet; IFD EXIF ma bazę 200000.
    private const ExifTag OffsetTimeOriginal = (ExifTag)(200000 + 0x9011);

    /// <summary>Wartość GPSProcessingMethod oznaczająca pozycję wpisaną ręcznie (PhotoTag).</summary>
    private const string ManualMethod = "MANUAL";

    /// <summary>
    /// Software po zapisie czasu przez PhotoTag: data w pliku ma już korektę zegara aparatu
    /// (<see cref="PhotoExif.TimeWritten"/>), więc nie wolno jej doliczać drugi raz.
    /// </summary>
    public const string TimeWrittenMarker = "PhotoTag";

    // Znaczniki zapisywane przez aparat/telefon razem z pozycją, a zwykle nie przez programy do geotagowania.
    private static readonly ExifTag[] DeviceGpsTags =
    {
        ExifTag.GPSTimeStamp,
        ExifTag.GPSDateStamp,
        ExifTag.GPSAltitude,
        ExifTag.GPSDOP,
        ExifTag.GPSSatellites,
        ExifTag.GPSMeasureMode,
    };

    private static readonly ExifTag[] GpsCoreTags =
    {
        ExifTag.GPSLatitude,
        ExifTag.GPSLongitude,
        ExifTag.GPSLatitudeRef,
        ExifTag.GPSLongitudeRef,
        ExifTag.GPSImgDirection,
        ExifTag.GPSImgDirectionRef,
        ExifTag.GPSProcessingMethod,
        ExifTag.GPSVersionID,
    };

    /// <summary>Zdjęcia (EXIF) i filmy (MP4/MOV — <see cref="VideoService"/>).</summary>
    public static bool Supports(string extension) =>
        extension.ToLowerInvariant() is ".jpg" or ".jpeg" or ".tif" or ".tiff" || VideoService.Supports(extension);

    public static PhotoExif ReadInfo(string path)
    {
        if (VideoService.IsVideo(path))
            return VideoService.ReadInfo(path);
        try
        {
            var file = ImageFile.FromFile(path);
            var orientation = file.Properties.Get<ExifUShort>(ExifTag.Orientation)?.Value ?? 1;
            var dateTaken = ReadDateTaken(file);
            var offset = ReadUtcOffset(file);
            var camera = ReadCamera(file);
            var direction = file.Properties.Get<ExifURational>(ExifTag.GPSImgDirection)?.Value is { } dv
                && dv.Denominator != 0
                ? (double)dv.Numerator / dv.Denominator
                : (double?)null;
            var timeWritten = file.Properties.Get<ExifAscii>(ExifTag.Software)?.Value?.Trim('\0', ' ') == TimeWrittenMarker;

            var lat = file.Properties.Get<GPSLatitudeLongitude>(ExifTag.GPSLatitude);
            var lon = file.Properties.Get<GPSLatitudeLongitude>(ExifTag.GPSLongitude);
            if (lat is null || lon is null)
                return new PhotoExif(null, (int)orientation, dateTaken, offset, camera, null, GpsSource.None, false, timeWritten);

            double la = ToDecimal(lat);
            double lo = ToDecimal(lon);

            var latRef = file.Properties.Get<ExifEnumProperty<GPSLatitudeRef>>(ExifTag.GPSLatitudeRef);
            var lonRef = file.Properties.Get<ExifEnumProperty<GPSLongitudeRef>>(ExifTag.GPSLongitudeRef);
            if (latRef?.Value == GPSLatitudeRef.South) la = -la;
            if (lonRef?.Value == GPSLongitudeRef.West) lo = -lo;

            var method = ReadProcessingMethod(file);
            var source = string.Equals(method, ManualMethod, StringComparison.OrdinalIgnoreCase)
                    || System.IO.Path.GetFileNameWithoutExtension(path).EndsWith("_LOCPT", StringComparison.OrdinalIgnoreCase)
                ? GpsSource.Added
                : method is not null || file.Properties.Any(p => DeviceGpsTags.Contains(p.Tag))
                    ? GpsSource.Device
                    : GpsSource.Unknown;

            return new PhotoExif(new GeoPoint(la, lo), (int)orientation, dateTaken, offset, camera, direction, source, false, timeWritten);
        }
        catch
        {
            return new PhotoExif(null, 1, null, null, null, null, GpsSource.None, true);
        }
    }

    private static DateTime? ReadDateTaken(ImageFile file)
    {
        var dt = file.Properties.Get<ExifDateTime>(ExifTag.DateTimeOriginal)?.Value;
        if (dt is null || dt.Value.Year < 1990)
            dt = file.Properties.Get<ExifDateTime>(ExifTag.DateTimeDigitized)?.Value;
        return dt is { Year: >= 1990 } ? DateTime.SpecifyKind(dt.Value, DateTimeKind.Unspecified) : null;
    }

    private static TimeSpan? ReadUtcOffset(ImageFile file)
    {
        var prop = file.Properties.FirstOrDefault(p => p.Tag == OffsetTimeOriginal);
        var text = prop switch
        {
            ExifAscii a => a.Value,
            { Value: byte[] b } => Encoding.ASCII.GetString(b),
            _ => null,
        };
        text = text?.Trim('\0', ' ');
        if (string.IsNullOrEmpty(text)) return null;
        var sign = text[0] == '-' ? -1 : 1;
        return TimeSpan.TryParseExact(text.TrimStart('+', '-'), @"hh\:mm", CultureInfo.InvariantCulture, out var ts)
            ? sign * ts
            : null;
    }

    private static string? ReadProcessingMethod(ImageFile file)
    {
        var prop = file.Properties.FirstOrDefault(p => p.Tag == ExifTag.GPSProcessingMethod);
        var text = prop switch
        {
            null => null,
            ExifEncodedString e => e.Value,
            ExifAscii a => a.Value,
            // UNDEFINED: 8 bajtów nagłówka kodowania + tekst
            { Value: byte[] b } => Encoding.ASCII.GetString(b, Math.Min(8, b.Length), Math.Max(0, b.Length - 8)),
            _ => prop.Value?.ToString(),
        };
        text = text?.Trim('\0', ' ');
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string? ReadCamera(ImageFile file)
    {
        var make = file.Properties.Get<ExifAscii>(ExifTag.Make)?.Value?.Trim('\0', ' ');
        var model = file.Properties.Get<ExifAscii>(ExifTag.Model)?.Value?.Trim('\0', ' ');
        if (string.IsNullOrEmpty(model)) return string.IsNullOrEmpty(make) ? null : make;
        if (string.IsNullOrEmpty(make) || model.StartsWith(make, StringComparison.OrdinalIgnoreCase)) return model;
        return $"{make} {model}";
    }

    /// <summary>Zapisuje współrzędne (i kierunek); <paramref name="time"/> — czas lokalny po korekcie do DateTimeOriginal/Digitized/DateTime.</summary>
    public static void WriteGps(string source, string target, double lat, double lng, double? direction = null, DateTime? time = null)
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

        file.Properties.Set(new ExifByteArray(ExifTag.GPSVersionID, new byte[] { 2, 3, 0, 0 }));
        file.Properties.Set(new ExifEncodedString(ExifTag.GPSProcessingMethod, ManualMethod, Encoding.ASCII));

        if (direction is { } dir)
        {
            dir = ((dir % 360) + 360) % 360;
            file.Properties.Set(new ExifEnumProperty<GPSDirectionRef>(
                ExifTag.GPSImgDirectionRef, GPSDirectionRef.TrueDirection));
            file.Properties.Set(new ExifURational(ExifTag.GPSImgDirection, (uint)Math.Round(dir * 100), 100));
        }

        if (time is { } t)
        {
            t = DateTime.SpecifyKind(t, DateTimeKind.Unspecified);
            file.Properties.Set(new ExifDateTime(ExifTag.DateTimeOriginal, t));
            file.Properties.Set(new ExifDateTime(ExifTag.DateTimeDigitized, t));
            file.Properties.Set(new ExifDateTime(ExifTag.DateTime, t));
            file.Properties.Set(new ExifAscii(ExifTag.Software, TimeWrittenMarker, Encoding.ASCII));
        }

        file.Save(target);
        TiffFixer.Fix(target, lat, lng);
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
