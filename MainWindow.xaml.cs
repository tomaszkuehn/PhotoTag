using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using PhotoTag.Models;
using PhotoTag.Services;

namespace PhotoTag;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<PhotoItem> _items = new();
    private readonly SemaphoreSlim _thumbLock = new(4, 4);
    private GeoPoint? _pending;
    private PhotoItem? _suggestionSource;

    private sealed record EstimateInfo(GeoPoint Point, PhotoItem Before, PhotoItem After);
    private EstimateInfo? _estimate;
    private string? _folder;

    public MainWindow()
    {
        InitializeComponent();
        PhotoList.ItemsSource = _items;
        PhotoList.SelectionChanged += PhotoList_SelectionChanged;
        Loaded += OnLoaded;
        PreviewKeyDown += Window_PreviewKeyDown;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await MapView.EnsureCoreWebView2Async();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Nie udało się uruchomić mapy (brak WebView2 Runtime?): " + ex.Message;
            return;
        }

        var assets = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets");
        MapView.CoreWebView2.SetVirtualHostNameToFolderMapping(
            "maphost", assets, CoreWebView2HostResourceAccessKind.Allow);
        MapView.CoreWebView2.WebMessageReceived += OnWebMessage;
        MapView.CoreWebView2.Navigate("https://maphost/map.html");
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var type = doc.RootElement.GetProperty("type").GetString();
            if (type == "pick")
            {
                _pending = new GeoPoint(
                    doc.RootElement.GetProperty("lat").GetDouble(),
                    doc.RootElement.GetProperty("lng").GetDouble());
                UpdateStatus(PhotoList.SelectedItem as PhotoItem);
            }
            else if (type == "streetview")
            {
                var p = new GeoPoint(
                    doc.RootElement.GetProperty("lat").GetDouble(),
                    doc.RootElement.GetProperty("lng").GetDouble());
                _ = ShowStreetViewAsync(p);
            }
        }
        catch
        {
        }
    }

    private void PickFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Wybierz folder ze zdjęciami" };
        if (dlg.ShowDialog(this) != true) return;
        FolderBox.Text = dlg.FolderName;
        _folder = dlg.FolderName;
        _ = LoadFolderAsync(_folder);
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_folder is not null)
            _ = LoadFolderAsync(_folder);
    }

    private async Task LoadFolderAsync(string folder)
    {
        if (!Directory.Exists(folder)) return;

        StatusText.Text = "Skanowanie folderu…";
        var extensions = new[] { ".jpg", ".jpeg", ".tif", ".tiff" };
        var previousName = (PhotoList.SelectedItem as PhotoItem)?.FileName;

        var found = await Task.Run(() =>
            Directory.EnumerateFiles(folder)
                .Where(f => extensions.Contains(System.IO.Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .Select(f =>
                {
                    var (gps, orientation, dateTaken, error) = ExifService.ReadInfo(f);
                    return new PhotoItem(f, gps, orientation, dateTaken, error);
                })
                .ToList());

        found = found
            .OrderBy(i => i.HasValidDate ? i.DateTaken : DateTime.MaxValue)
            .ThenBy(i => i.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var locptPaths = new HashSet<string>(
            found
                .Where(i => System.IO.Path.GetFileNameWithoutExtension(i.Path)
                    .EndsWith("_LOCPT", StringComparison.OrdinalIgnoreCase))
                .Select(i => i.Path),
            StringComparer.OrdinalIgnoreCase);
        found.RemoveAll(i =>
            System.IO.Path.GetFileNameWithoutExtension(i.Path)
                .EndsWith("_LOCPT", StringComparison.OrdinalIgnoreCase) is false &&
            locptPaths.Contains(ResolveTargetPath(i.Path)));

        _items.Clear();
        PreviewImage.Source = null;
        foreach (var item in found)
            _items.Add(item);
        foreach (var item in found)
            _ = LoadThumbAsync(item);

        UpdateCounts();
        var restored = previousName is null
            ? null
            : _items.FirstOrDefault(i => string.Equals(i.FileName, previousName, StringComparison.OrdinalIgnoreCase));
        MaintainSelection(restored);
        StatusText.Text = $"Załadowano {found.Count} zdjęć; {found.Count(i => !i.HasGps && !i.GpsError)} bez GPS. " +
                          "Wybierz zdjęcie, kliknij lokalizację na mapie, a następnie użyj przycisku zapisu.";
    }

    private void MaintainSelection(PhotoItem? item)
    {
        PhotoList.SelectedItem = item;
        if (item is not null)
            PhotoList.ScrollIntoView(item);
    }

    private async Task LoadThumbAsync(PhotoItem item)
    {
        await _thumbLock.WaitAsync();
        try
        {
            var data = await Task.Run(() => Imaging.LoadBitmap(item.Path, 140, item.Orientation));
            item.Thumb = data;
        }
        catch
        {
        }
        finally
        {
            _thumbLock.Release();
        }
    }

    private async void PhotoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (PhotoList.SelectedItem is not PhotoItem item) return;
        PreviewImage.Source = null;
        try
        {
            PreviewImage.Source = await Task.Run(() => Imaging.LoadBitmap(item.Path, 1400, item.Orientation));
        }
        catch
        {
        }
        if (item.HasGps)
        {
            _estimate = null;
            _suggestionSource = null;
            SendMapSetView(item.Gps!, 13);
        }
        else if (GetTimeEstimate(item) is { } est)
        {
            _estimate = est;
            _suggestionSource = null;
            SendMapEstimate(est);
        }
        else if (FindNearestTagged(item) is { } sugg)
        {
            _estimate = null;
            _suggestionSource = sugg.Source;
            SendMapSetView(sugg.Source.Gps!, sugg.Zoom);
        }
        else
        {
            _estimate = null;
            _suggestionSource = null;
        }

        UpdateStatus(item);
    }

    private void PreviewBorder_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        try
        {
            if (EnlargedOverlay.Visibility == Visibility.Visible)
            {
                CloseEnlarged();
                e.Handled = true;
                return;
            }
            if (PreviewImage.Source is null)
            {
                StatusText.Text = "Brak podglądu — najpierw kliknij miniaturę na liście.";
                return;
            }
            StatusText.Text = "Klik podglądu: otwieram duży podgląd…";
            ShowEnlarged();
            e.Handled = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.ToString(), "PhotoTag — błąd podglądu");
        }
    }

    private void PhotoList_MouseDoubleCick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (PreviewImage.Source is not null)
            ShowEnlarged();
    }

    private async void ShowEnlarged()
    {
        CloseStreetView();
        var item = PhotoList.SelectedItem as PhotoItem;
        MapView.Visibility = Visibility.Collapsed;
        PreviewBorder.Visibility = Visibility.Collapsed;
        EnlargedImage.Source = PreviewImage.Source;
        EnlargedOverlay.Visibility = Visibility.Visible;
        Keyboard.Focus(EnlargedOverlay);
        if (item is null) return;
        try
        {
            EnlargedImage.Source = await Task.Run(() => Imaging.LoadBitmap(item.Path, 2400, item.Orientation));
        }
        catch
        {
        }
    }

    private void CloseEnlarged()
    {
        EnlargedOverlay.Visibility = Visibility.Collapsed;
        EnlargedImage.Source = null;
        MapView.Visibility = Visibility.Visible;
        PreviewBorder.Visibility = Visibility.Visible;
    }

    private async Task ShowStreetViewAsync(GeoPoint point)
    {
        CloseEnlarged();
        try
        {
            await StreetView.EnsureCoreWebView2Async();
        }
        catch (Exception ex)
        {
            StatusText.Text = "Nie udało się uruchomić Street View: " + ex.Message;
            return;
        }

        var lat = point.Lat.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        var lng = point.Lng.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
        StreetView.CoreWebView2.Navigate(
            $"https://www.google.com/maps/@?api=1&map_action=pano&viewpoint={lat},{lng}");

        MapView.Visibility = Visibility.Collapsed;
        StreetViewBorder.Visibility = Visibility.Visible;
        StatusText.Text = "Street View: klik „← Mapa”, aby wrócić.";
    }

    private void StreetViewBack_Click(object sender, RoutedEventArgs e)
    {
        CloseStreetView();
    }

    private void CloseStreetView()
    {
        if (StreetViewBorder.Visibility != Visibility.Visible) return;
        StreetViewBorder.Visibility = Visibility.Collapsed;
        if (StreetView.CoreWebView2 is not null)
            StreetView.CoreWebView2.Navigate("about:blank");
        MapView.Visibility = Visibility.Visible;
    }

    private void EnlargedOverlay_MouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e) =>
        CloseEnlarged();

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Escape) return;
        if (StreetViewBorder.Visibility == Visibility.Visible)
        {
            CloseStreetView();
            e.Handled = true;
        }
        else if (EnlargedOverlay.Visibility == Visibility.Visible)
        {
            CloseEnlarged();
            e.Handled = true;
        }
    }

    private (PhotoItem Source, double DeltaMinutes, int Zoom)? FindNearestTagged(PhotoItem item)
    {
        if (!item.HasValidDate) return null;

        var candidates = _items
            .Where(i => i.HasGps && !ReferenceEquals(i, item) && i.HasValidDate)
            .Select(i => (Source: i, Delta: Math.Abs((i.DateTaken - item.DateTaken).TotalMinutes)))
            .Where(x => x.Delta <= 720)
            .OrderBy(x => x.Delta)
            .ToList();
        if (candidates.Count == 0) return null;

        var best = candidates[0];
        var zoom = best.Delta <= 5 ? 15 : best.Delta <= 60 ? 13 : 6;
        return (best.Source, best.Delta, zoom);
    }
    private static string FormatDelta(double minutes) =>
        minutes < 60 ? $"{minutes:0} min" : $"{minutes / 60.0:0.##} h";

    private EstimateInfo? GetTimeEstimate(PhotoItem item)
    {
        if (!item.HasValidDate) return null;
        PhotoItem? before = null, after = null;
        foreach (var i in _items)
        {
            if (!i.HasGps || i.GpsError || !i.HasValidDate || ReferenceEquals(i, item)) continue;
            if (i.DateTaken < item.DateTaken)
            {
                if (before is null || i.DateTaken > before.DateTaken) before = i;
            }
            else if (i.DateTaken > item.DateTaken &&
                     (after is null || i.DateTaken < after.DateTaken)) after = i;
        }
        if (before is null || after is null) return null;
        var day = item.DateTaken.Date;
        if (before.DateTaken.Date != day || after.DateTaken.Date != day) return null;
        var span = (after.DateTaken - before.DateTaken).TotalMinutes;
        if (span <= 0 || span > 120) return null;
        var f = (item.DateTaken - before.DateTaken).TotalMinutes / span;
        return new EstimateInfo(
            new GeoPoint(before.Gps!.Lat + (after.Gps!.Lat - before.Gps.Lat) * f,
                         before.Gps.Lng + (after.Gps.Lng - before.Gps.Lng) * f),
            before, after);
    }

    private void SendMapEstimate(EstimateInfo est, int zoom = 15)
    {
        if (MapView.CoreWebView2 is null) return;
        var json = JsonSerializer.Serialize(new
        {
            type = "estimate",
            lat = est.Point.Lat, lng = est.Point.Lng, zoom,
            aLat = est.Before.Gps!.Lat, aLng = est.Before.Gps.Lng,
            bLat = est.After.Gps!.Lat, bLng = est.After.Gps.Lng
        });
        MapView.CoreWebView2.PostWebMessageAsJson(json);
    }

    private void SendMapSetView(GeoPoint point, int zoom = 13)
    {
        if (MapView.CoreWebView2 is null) return;
        var json = JsonSerializer.Serialize(new { type = "setView", lat = point.Lat, lng = point.Lng, zoom });
        MapView.CoreWebView2.PostWebMessageAsJson(json);
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pending is null)
        {
            ShowInfo("Najpierw kliknij na mapie, aby wybrać lokalizację.");
            return;
        }

        var batch = ApplyAllCheck.IsChecked == true;
        var targets = new List<PhotoItem>();
        if (batch)
            targets.AddRange(_items.Where(i => !i.HasGps && !i.GpsError));
        else if (PhotoList.SelectedItem is PhotoItem selected)
            targets.Add(selected);

        if (targets.Count == 0)
        {
            ShowInfo("Brak zdjęć do oznaczenia (wybierz zdjęcie lub zaznacz tryb „wszystkie bez GPS”).");
            return;
        }

        var point = _pending!;
        var pointText = $"{point.Lat:F6}, {point.Lng:F6}";
        var prompt = targets.Count > 1
            ? $"Dodać lokalizację {pointText} do {targets.Count} zdjęć?"
            : null;

        await ApplyEntriesAsync(targets.Select(t => (Item: t, Point: point)).ToList(), prompt);
    }

    private async void InterpolateButton_Click(object sender, RoutedEventArgs e)
    {
        var entries = _items
            .Where(i => !i.HasGps && !i.GpsError && i.HasValidDate)
            .Select(i => (Item: i, Est: GetTimeEstimate(i)))
            .Where(x => x.Est is not null)
            .Select(x => (Item: x.Item, Point: x.Est!.Point))
            .ToList();

        if (entries.Count == 0)
        {
            ShowInfo("Brak zdjęć do interpolacji. Potrzebne: zdjęcia bez GPS między zdjęciami z GPS, " +
                     "tego samego dnia, łącznie Δ ≤ 2 h, z datą EXIF.");
            return;
        }

        await ApplyEntriesAsync(entries,
            $"Interpolować pozycje na podstawie czasu między zdjęciami z GPS\ni zapisać lokalizacje dla {entries.Count} zdjęć bez GPS?");
    }

    private async Task ApplyEntriesAsync(List<(PhotoItem Item, GeoPoint Point)> entries, string? batchPrompt)
    {
        if (batchPrompt is not null &&
            MessageBox.Show(this, batchPrompt, "PhotoTag", MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes)
            return;

        var deleteOriginal = DeleteOriginalCheck.IsChecked == true;
        var done = 0;
        var errors = new List<string>();

        foreach (var entry in entries)
        {
            var target = entry.Item;
            var point = entry.Point;
            try
            {
                var targetPath = ResolveTargetPath(target.Path);
                var inPlace = string.Equals(targetPath, target.Path, StringComparison.OrdinalIgnoreCase);

                if (inPlace)
                {
                    if (MessageBox.Show(this,
                            $"Nadpisać istniejący plik {target.FileName} nowymi współrzędnymi?",
                            "PhotoTag — nadpisywanie", MessageBoxButton.YesNo, MessageBoxImage.Question)
                        != MessageBoxResult.Yes)
                        continue;
                }
                else if (File.Exists(targetPath))
                {
                    if (MessageBox.Show(this,
                            $"Plik {System.IO.Path.GetFileName(targetPath)} już istnieje.\nNadpisać?",
                            "PhotoTag — nadpisywanie", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                        != MessageBoxResult.Yes)
                        continue;
                }

                var tmp = targetPath + ".tmp";
                var moved = false;
                try
                {
                    ExifService.WriteGps(target.Path, tmp, point.Lat, point.Lng);
                    if (File.Exists(targetPath))
                        File.Delete(targetPath);
                    File.Move(tmp, targetPath);
                    moved = true;
                }
                finally
                {
                    if (!moved && File.Exists(tmp))
                        File.Delete(tmp);
                }

                if (deleteOriginal && !inPlace)
                    File.Delete(target.Path);
                done++;
            }
            catch (Exception ex)
            {
                errors.Add($"{target.FileName}: {ex.Message}");
            }
        }

        var sharedPoint = entries.Select(e => e.Point).Distinct().Count() == 1
            ? $"{entries[0].Point.Lat:F6}, {entries[0].Point.Lng:F6}"
            : "interpolowane punkty";
        StatusText.Text = done > 0
            ? $"Zapisano lokalizację {sharedPoint} w {done} plikach."
            : "Nie zapisano żadnego pliku.";
        if (errors.Count > 0)
        {
            StatusText.Text += $" Błędy: {errors.Count}.";
            MessageBox.Show(this, string.Join("\n", errors), "Błędy zapisu", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        if (_folder is not null)
            await LoadFolderAsync(_folder);

        var next = _items.FirstOrDefault(i => !i.HasGps && !i.GpsError);
        MaintainSelection(next);
    }

    private static string ResolveTargetPath(string source)
    {
        var dir = System.IO.Path.GetDirectoryName(source)!;
        var ext = System.IO.Path.GetExtension(source);
        var stem = System.IO.Path.GetFileNameWithoutExtension(source);

        if (stem.EndsWith("_LOCPT", StringComparison.OrdinalIgnoreCase))
            return source;
        return System.IO.Path.Combine(dir, stem + "_LOCPT" + ext);
    }

    private void UpdateCounts()
    {
        var noGps = _items.Count(i => !i.HasGps && !i.GpsError);
        CountText.Text = $"zdjęć: {_items.Count} | bez GPS: {noGps}";
    }

    private void UpdateStatus(PhotoItem? item)
    {
        var point = _pending is null ? "" : $" | punkt mapy: {_pending!.Lat:F6}, {_pending.Lng:F6}";
        if (item is null)
        {
            StatusText.Text = _pending is null
                ? "Kliknij na mapie, aby wybrać lokalizację."
                : $"Wybrany punkt mapy: {_pending.Lat:F6}, {_pending.Lng:F6}";
            return;
        }

        if (item.GpsError)
        {
            StatusText.Text = $"{item.FileName} ({item.DateText}) — błąd odczytu EXIF{point}";
        }
        else if (item.HasGps)
        {
            StatusText.Text = $"{item.FileName} ({item.DateText}) — ma GPS: {item.Gps!.Lat:F6}, {item.Gps.Lng:F6}{point}";
        }
        else if (_estimate is { } est && item.HasValidDate)
        {
            var span = FormatDelta((est.After.DateTaken - est.Before.DateTaken).TotalMinutes);
            StatusText.Text = $"{item.FileName} ({item.DateText}) — brak GPS. Pozycja szacowana (interpolacja czasu): " +
                              $"{est.Before.FileName} [{est.Before.DateTaken:HH:mm}] → {est.After.FileName} [{est.After.DateTaken:HH:mm}] " +
                              $"(Δ {span}) — mapa ustawiona na szacowany punkt; przeciągnij znacznik, aby skorygować, i kliknij zapis.";
        }
        else if (_suggestionSource is { HasGps: true } src && src.HasValidDate && item.HasValidDate)
        {
            var delta = FormatDelta(Math.Abs((src.DateTaken - item.DateTaken).TotalMinutes));
            var when = src.DateTaken < item.DateTaken ? "przed nim" : "po nim";
            StatusText.Text = $"{item.FileName} ({item.DateText}) — brak GPS. Sugestia: punkt ze zdjęcia {src.FileName} " +
                              $"(robione {when}, Δ {delta}) — mapa ustawiona na ten punkt; " +
                              "przeciągnij znacznik, aby skorygować, i kliknij zapis.";
        }
        else
        {
            StatusText.Text = $"{item.FileName} ({item.DateText}) — brak GPS{point}. Kliknij na mapie i kliknij „Dodaj lokalizację i zapisz”.";
        }
    }

    private void ShowInfo(string message) =>
        MessageBox.Show(this, message, "PhotoTag", MessageBoxButton.OK, MessageBoxImage.Information);
}