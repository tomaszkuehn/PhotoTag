using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using PhotoTag.Services;

namespace PhotoTag.Models;

public sealed class PhotoItem : INotifyPropertyChanged
{
    private ImageSource? _thumb;

    public PhotoItem(string path, GeoPoint? gps, int orientation, DateTime? exifDate, bool error)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        Gps = gps;
        Orientation = orientation;
        GpsError = error;

        DateFromExif = exifDate is not null;
        var fileDate = System.IO.File.Exists(path) ? System.IO.File.GetLastWriteTimeUtc(path) : DateTime.MinValue;
        DateTaken = exifDate ?? (fileDate.Year > 1990 ? fileDate : DateTime.MinValue);
    }

    public string Path { get; }
    public string FileName { get; }
    public GeoPoint? Gps { get; }
    public int Orientation { get; }
    public bool GpsError { get; }
    public bool HasGps => Gps is not null;

    public bool DateFromExif { get; }
    public DateTime DateTaken { get; }
    public bool HasValidDate => DateTaken != DateTime.MinValue;
    public string DateText => !HasValidDate
        ? "brak daty"
        : DateTaken.ToLocalTime().ToString("yyyy-MM-dd HH:mm") + (DateFromExif ? "" : " (plik)");

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
        : HasGps ? $"GPS  {Gps!.Lat:F5}; {Gps.Lng:F5}"
        : "brak GPS";

    public Brush StatusBrush =>
        GpsError ? Brushes.Gray
        : HasGps ? Brushes.ForestGreen
        : Brushes.OrangeRed;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name!));
}