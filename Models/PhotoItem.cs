using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using PhotoTag.Services;

namespace PhotoTag.Models;

public sealed class PhotoItem : INotifyPropertyChanged
{
    public const string UnknownCamera = "(nieznany aparat)";

    private ImageSource? _thumb;
    private TimeSpan _clockOffset;
    private string _sessionLabel = "";

    public PhotoItem(string path, PhotoExif exif)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        Gps = exif.Gps;
        GpsSource = exif.GpsSource;
        Direction = exif.Direction;
        Orientation = exif.Orientation;
        GpsError = exif.Error;
        UtcOffset = exif.UtcOffset;
        Camera = exif.Camera ?? UnknownCamera;

        DateFromExif = exif.DateTaken is not null;
        var fileDate = System.IO.File.Exists(path) ? System.IO.File.GetLastWriteTime(path) : DateTime.MinValue;
        DateTaken = exif.DateTaken ?? (fileDate.Year > 1990
            ? DateTime.SpecifyKind(fileDate, DateTimeKind.Unspecified)
            : DateTime.MinValue);
    }

    public string Path { get; }
    public string FileName { get; }
    public GeoPoint? Gps { get; }
    public double? Direction { get; }
    public int Orientation { get; }
    public bool GpsError { get; }
    public bool HasGps => Gps is not null;
    public GpsSource GpsSource { get; }
    /// <summary>Pozycja zapisana przez aparat/telefon w chwili zrobienia zdjęcia.</summary>
    public bool GpsFromDevice => HasGps && GpsSource == GpsSource.Device;
    public string Camera { get; }

    /// <summary>Strefa czasowa z EXIF (OffsetTimeOriginal), jeśli zapisana.</summary>
    public TimeSpan? UtcOffset { get; }

    public bool DateFromExif { get; }
    /// <summary>Czas lokalny z EXIF (lub data pliku), bez korekty zegara.</summary>
    public DateTime DateTaken { get; }
    public bool HasValidDate => DateTaken != DateTime.MinValue;

    public TimeSpan ClockOffset
    {
        get => _clockOffset;
        set
        {
            if (_clockOffset == value) return;
            _clockOffset = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DateText));
        }
    }

    /// <summary>Czas lokalny po korekcie zegara aparatu.</summary>
    public DateTime Time => HasValidDate ? DateTaken + ClockOffset : DateTime.MinValue;

    /// <summary>Czas UTC po korekcie; bez strefy w EXIF przyjmowana strefa systemowa.</summary>
    public DateTime TimeUtc => !HasValidDate ? DateTime.MinValue : UtcFor(Time);

    public DateTime UtcFor(DateTime local)
    {
        if (UtcOffset is { } off)
            return DateTime.SpecifyKind(local - off, DateTimeKind.Utc);
        try
        {
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), TimeZoneInfo.Local);
        }
        catch (ArgumentException)
        {
            // czas nieistniejący (zmiana na czas letni)
            return DateTime.SpecifyKind(local - TimeZoneInfo.Local.BaseUtcOffset, DateTimeKind.Utc);
        }
    }

    public string DateText
    {
        get
        {
            if (!HasValidDate) return "brak daty";
            var text = Time.ToString("yyyy-MM-dd HH:mm:ss") + (DateFromExif ? "" : " (plik)");
            if (ClockOffset != TimeSpan.Zero)
                text += $" (korekta {FormatOffset(ClockOffset)})";
            return text;
        }
    }

    public string SessionLabel
    {
        get => _sessionLabel;
        set
        {
            if (_sessionLabel == value) return;
            _sessionLabel = value;
            OnPropertyChanged();
        }
    }

    public ImageSource? Thumb
    {
        get => _thumb;
        set
        {
            if (!ReferenceEquals(_thumb, value))
            {
                _thumb = value;
                OnPropertyChanged(nameof(Thumb));
            }
        }
    }

    public string StatusText =>
        GpsError ? "błąd odczytu EXIF"
        : HasGps ? SourcePrefix + $"  {Gps!.Lat:F5}; {Gps.Lng:F5}" + (Direction is { } d ? $"  ↗{d:0}°" : "")
        : "brak GPS";

    private string SourcePrefix => GpsSource switch
    {
        GpsSource.Device => "📷 GPS z aparatu",
        GpsSource.Added => "GPS dodany",
        _ => "GPS",
    };

    public string GpsSourceText => GpsSource switch
    {
        GpsSource.Device => "z aparatu (zapisany w chwili zdjęcia)",
        GpsSource.Added => "dopisany później",
        GpsSource.Unknown => "źródło nieznane (prawdopodobnie dopisany innym programem)",
        _ => "",
    };

    public Brush StatusBrush =>
        GpsError ? Brushes.Gray
        : GpsFromDevice ? Brushes.DarkGoldenrod
        : HasGps ? Brushes.ForestGreen
        : Brushes.OrangeRed;

    public static string FormatOffset(TimeSpan ts) =>
        (ts < TimeSpan.Zero ? "-" : "+") + ts.Duration().ToString(ts.Duration().TotalDays >= 1 ? @"d\.hh\:mm\:ss" : @"hh\:mm\:ss");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
}
