using System.IO;
using System.Text.Json;

namespace PhotoTag.Services;

public sealed record SavedPlace(string Name, double Lat, double Lng);

public sealed class AppSettings
{
    public string? LastFolder { get; set; }
    public List<string> ReferenceFolders { get; set; } = new();
    public List<string> TrackFiles { get; set; } = new();
    /// <summary>Przesunięcie zegara aparatu w sekundach (czas poprawny = czas EXIF + przesunięcie).</summary>
    public Dictionary<string, double> ClockOffsets { get; set; } = new();
    public List<SavedPlace> Favorites { get; set; } = new();
    public List<SavedPlace> Recents { get; set; } = new();
    public int SessionGapMinutes { get; set; } = 30;
    /// <summary>Ręcznie ustawiony czas lokalny zdjęcia (ścieżka → czas), np. po przesunięciu na liście.</summary>
    public Dictionary<string, DateTime> TimeOverrides
    {
        get => _timeOverrides;
        set => _timeOverrides = new Dictionary<string, DateTime>(value, StringComparer.OrdinalIgnoreCase);
    }
    private Dictionary<string, DateTime> _timeOverrides = new(StringComparer.OrdinalIgnoreCase);
}

public static class SettingsService
{
    private static readonly string Dir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PhotoTag");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch
        {
        }
        return new AppSettings();
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
        }
    }
}
