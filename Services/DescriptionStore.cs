using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace PhotoTag.Services;

/// <summary>
/// Opisy zdjęć w pliku <see cref="FileName"/> w folderze ze zdjęciami: { "nazwa pliku": "opis" }.
/// Plik …_LOCPT dzieli opis z oryginałem (szukane są obie nazwy), więc opis przetrwa zapis lokalizacji.
/// </summary>
public sealed class DescriptionStore
{
    public const string FileName = "opisy.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // polskie znaki czytelne w pliku zamiast ą
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _path;
    private readonly SortedDictionary<string, string> _map = new(StringComparer.OrdinalIgnoreCase);

    private DescriptionStore(string folder) => _path = Path.Combine(folder, FileName);

    public static DescriptionStore Load(string folder)
    {
        var store = new DescriptionStore(folder);
        try
        {
            if (File.Exists(store._path) &&
                JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(store._path)) is { } data)
                foreach (var (name, text) in data)
                    if (!string.IsNullOrWhiteSpace(text))
                        store._map[name] = text;
        }
        catch
        {
        }
        return store;
    }

    public string? Get(string fileName) =>
        _map.TryGetValue(fileName, out var text) || _map.TryGetValue(OtherName(fileName), out text) ? text : null;

    /// <summary>Ustawia opis (pusty = usuwa) i zapisuje plik.</summary>
    public void Set(string fileName, string? text)
    {
        _map.Remove(OtherName(fileName));
        text = text?.Trim();
        if (string.IsNullOrEmpty(text))
            _map.Remove(fileName);
        else
            _map[fileName] = text;
        Save();
    }

    /// <summary>Przenosi opis na nową nazwę pliku (np. oryginał → …_LOCPT po zapisie lokalizacji).</summary>
    public void Rename(string from, string to)
    {
        if (string.Equals(from, to, StringComparison.OrdinalIgnoreCase) || Get(from) is not { } text) return;
        _map.Remove(from);
        _map.Remove(OtherName(from));
        _map[to] = text;
        Save();
    }

    private void Save()
    {
        if (_map.Count == 0)
        {
            if (File.Exists(_path)) File.Delete(_path);
            return;
        }
        var tmp = _path + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(_map, JsonOptions));
        File.Move(tmp, _path, overwrite: true);
    }

    /// <summary>IMG_1.jpg ↔ IMG_1_LOCPT.jpg</summary>
    private static string OtherName(string fileName)
    {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        return stem.EndsWith("_LOCPT", StringComparison.OrdinalIgnoreCase)
            ? stem[..^"_LOCPT".Length] + ext
            : stem + "_LOCPT" + ext;
    }
}
