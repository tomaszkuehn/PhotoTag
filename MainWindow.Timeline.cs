using System.Globalization;
using System.Windows;
using PhotoTag.Models;
using PhotoTag.Services;

namespace PhotoTag;

/// <summary>Ręczne ustawianie czasu zdjęć: okno „Ułóż kolejność”, Alt+↑/↓, menu kontekstowe, cofanie.</summary>
public partial class MainWindow
{
    private static readonly TimeSpan PlacementStep = TimeSpan.FromSeconds(1);
    private const int TimeUndoLimit = 4;

    /// <summary>Stan sprzed ostatnich zmian czasu: ścieżka → poprzedni ręczny czas (null = brak).</summary>
    private readonly LinkedList<Dictionary<string, DateTime?>> _timeUndo = new();

    internal IReadOnlyList<PhotoItem> Photos => _items;
    internal int TimeUndoCount => _timeUndo.Count;

    // ---------------------------------------------------------------- umieszczanie na osi czasu

    /// <summary>
    /// Ustawia ręczny czas zdjęć <paramref name="moving"/> tuż przed/po <paramref name="anchor"/>
    /// (krok 1 s; gdy brak miejsca do sąsiedniego zdjęcia — równomiernie pomiędzy).
    /// </summary>
    internal bool PlaceInTimeline(List<PhotoItem> moving, PhotoItem anchor, bool after)
    {
        if (!anchor.HasValidDate)
        {
            StatusText.Text = "Nie można wstawić obok zdjęcia bez daty.";
            return false;
        }

        var others = _items.Where(i => !moving.Contains(i) && i.HasValidDate).ToList();
        var idx = others.IndexOf(anchor);
        if (idx < 0) return false;
        var k = moving.Count;

        DateTime lo, hi;
        if (after)
        {
            lo = anchor.TimeUtc;
            hi = lo + PlacementStep * (k + 1);
            if (idx + 1 < others.Count && others[idx + 1].TimeUtc < hi) hi = others[idx + 1].TimeUtc;
        }
        else
        {
            hi = anchor.TimeUtc;
            lo = hi - PlacementStep * (k + 1);
            if (idx > 0 && others[idx - 1].TimeUtc > lo) lo = others[idx - 1].TimeUtc;
        }
        var step = (hi - lo) / (k + 1);

        PushTimeUndo(moving);
        for (int j = 0; j < k; j++)
        {
            var item = moving[j];
            SetTimeOverride(item, item.LocalFor(lo + step * (j + 1)));
        }
        SettingsService.Save(_settings);

        AfterTimeChange(moving);
        StatusText.Text = (k == 1 ? $"{moving[0].FileName}: " : $"Przesunięto {k} zdjęć: ") +
                          $"czas ustawiony na {moving[0].Time:yyyy-MM-dd HH:mm:ss} " +
                          $"({(after ? "po" : "przed")} {anchor.FileName}).";
        return true;
    }

    /// <summary>Alt+↑/↓: przesuwa zaznaczone zdjęcia o jedną pozycję na liście.</summary>
    private void MoveInTimeline(bool later)
    {
        var moving = SelectedPhotos();
        if (moving.Count == 0) return;

        PhotoItem? anchor;
        bool after;
        if (later)
        {
            var last = _items.IndexOf(moving[^1]);
            anchor = _items.Skip(last + 1).FirstOrDefault(i => !moving.Contains(i) && i.HasValidDate);
            after = true;
        }
        else if (moving.All(i => !i.HasValidDate))
        {
            // z grupy „Bez daty” w górę: na koniec osi czasu
            anchor = _items.LastOrDefault(i => !moving.Contains(i) && i.HasValidDate);
            after = true;
        }
        else
        {
            var first = _items.IndexOf(moving[0]);
            anchor = _items.Take(first).LastOrDefault(i => !moving.Contains(i) && i.HasValidDate);
            after = false;
        }

        if (anchor is null)
        {
            StatusText.Text = later ? "Zdjęcie jest już na końcu osi czasu." : "Zdjęcie jest już na początku osi czasu.";
            return;
        }
        PlaceInTimeline(moving, anchor, after);
    }

    private void SetTimeOverride(PhotoItem item, DateTime? local)
    {
        item.TimeOverride = local;
        if (local is { } t)
            _settings.TimeOverrides[item.Path] = t;
        else
            _settings.TimeOverrides.Remove(item.Path);
    }

    private void AfterTimeChange(List<PhotoItem> changed)
    {
        RebuildList(_items.ToList(), changed[0]);
        if (changed.Count > 1)
            SelectItems(changed);
        SendPhotosToMap();
        SendTrackToMap();
    }

    // ---------------------------------------------------------------- cofanie (ostatnie 4 zmiany)

    private void PushTimeUndo(IEnumerable<PhotoItem> items)
    {
        _timeUndo.AddFirst(items.ToDictionary(i => i.Path, i => i.TimeOverride, StringComparer.OrdinalIgnoreCase));
        while (_timeUndo.Count > TimeUndoLimit)
            _timeUndo.RemoveLast();
    }

    /// <summary>Cofa ostatnią zmianę czasu (przesunięcie, ustawienie, przywrócenie daty).</summary>
    internal bool UndoTimeMove()
    {
        if (_timeUndo.First is not { } node) return false;
        _timeUndo.RemoveFirst();

        var changed = new List<PhotoItem>();
        foreach (var (path, previous) in node.Value)
        {
            if (_items.FirstOrDefault(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase)) is { } item)
            {
                SetTimeOverride(item, previous);
                changed.Add(item);
            }
            else if (previous is { } t)
                _settings.TimeOverrides[path] = t;
            else
                _settings.TimeOverrides.Remove(path);
        }
        SettingsService.Save(_settings);

        if (changed.Count > 0)
            AfterTimeChange(changed.OrderBy(i => _items.IndexOf(i)).ToList());
        StatusText.Text = $"Cofnięto zmianę czasu {node.Value.Count} zdjęć (pozostało cofnięć: {_timeUndo.Count}).";
        return true;
    }

    // ---------------------------------------------------------------- okno i menu kontekstowe

    private void OrderBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_items.Count == 0)
        {
            ShowInfo("Najpierw wybierz folder ze zdjęciami.");
            return;
        }
        new OrderWindow(this, _current) { Owner = this }.ShowDialog();
    }

    private void SetTimeMenu_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedPhotos();
        if (sel.Count == 0) return;
        var first = sel[0];
        var initial = first.HasValidDate ? first.Time : DateTime.Now;
        var text = PromptText(
            sel.Count == 1
                ? $"Data i godzina zdjęcia {first.FileName} (rrrr-mm-dd gg:mm:ss):"
                : $"Data i godzina pierwszego z {sel.Count} zdjęć (pozostałe zachowają odstępy):",
            initial.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
        if (text is null) return;
        if (!TryParseLocalTime(text, out var time))
        {
            ShowInfo($"Nie rozpoznano daty „{text}”. Użyj formatu 2024-07-15 14:30:00.");
            return;
        }

        PushTimeUndo(sel);
        var delta = time - initial;
        for (int n = 0; n < sel.Count; n++)
        {
            var item = sel[n];
            SetTimeOverride(item, item.HasValidDate ? item.Time + delta : time + PlacementStep * n);
        }
        SettingsService.Save(_settings);
        AfterTimeChange(sel);
        StatusText.Text = $"Ustawiono czas {sel.Count} zdjęć (pierwsze: {time:yyyy-MM-dd HH:mm:ss}).";
    }

    private void MoveEarlierMenu_Click(object sender, RoutedEventArgs e) => MoveInTimeline(false);

    private void MoveLaterMenu_Click(object sender, RoutedEventArgs e) => MoveInTimeline(true);

    private void UndoTimeMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!UndoTimeMove())
            StatusText.Text = "Brak zmian czasu do cofnięcia.";
    }

    private void ResetTimeMenu_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedPhotos().Where(i => i.TimeOverride is not null).ToList();
        if (sel.Count == 0)
        {
            StatusText.Text = "Zaznaczone zdjęcia nie mają ręcznie ustawionego czasu.";
            return;
        }
        PushTimeUndo(sel);
        foreach (var item in sel)
            SetTimeOverride(item, null);
        SettingsService.Save(_settings);
        AfterTimeChange(sel);
        StatusText.Text = $"Przywrócono datę z EXIF dla {sel.Count} zdjęć.";
    }

    private static bool TryParseLocalTime(string text, out DateTime time)
    {
        string[] formats = { "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd H:mm:ss", "yyyy-MM-dd HH:mm", "yyyy-MM-dd H:mm", "yyyy-MM-dd" };
        text = text.Trim();
        if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out time) ||
            DateTime.TryParse(text, CultureInfo.CurrentCulture, DateTimeStyles.None, out time))
        {
            time = DateTime.SpecifyKind(time, DateTimeKind.Unspecified);
            return true;
        }
        return false;
    }
}
