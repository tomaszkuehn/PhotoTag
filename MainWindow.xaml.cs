using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using PhotoTag.Models;
using PhotoTag.Services;

namespace PhotoTag;

public partial class MainWindow : Window
{
    private static readonly string UndoDir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PhotoTag", "undo");

    private readonly ObservableCollection<PhotoItem> _items = new();
    private readonly SemaphoreSlim _thumbLock = new(4, 4);
    private readonly AppSettings _settings = SettingsService.Load();
    private readonly TrackStore _tracks = new();
    private readonly Stack<List<UndoEntry>> _undo = new();
    private readonly Dictionary<string, PhotoItem> _mapIds = new();

    private List<PhotoItem> _refItems = new();
    private List<PhotoItem> _pool = new();
    private GeoPoint? _pending;
    private double? _pendingDirection;
    private string _address = "";
    private PhotoItem? _suggestionSource;
    private PhotoItem? _current;
    /// <summary>Wybór zdjęcia kliknięciem jego punktu na mapie — nie zmieniaj widoku mapy.</summary>
    private bool _keepMapView;
    private bool _bulkSelecting;
    private bool _suppressPlaces;

    private sealed record Estimate(GeoPoint Point, string Description, GeoPoint? A, GeoPoint? B, bool FromTrack);
    private sealed record UndoEntry(string TargetPath, string? TargetBackup, string? OriginalPath, string? OriginalBackup);
    private sealed record PlaceEntry(SavedPlace Place, bool Favorite)
    {
        public string Display => (Favorite ? "★ " : "🕘 ") + Place.Name;
    }

    private Estimate? _estimate;
    private string? _folder;

    public MainWindow()
    {
        InitializeComponent();
        PhotoList.ItemsSource = _items;
        CollectionViewSource.GetDefaultView(_items).GroupDescriptions
            .Add(new PropertyGroupDescription(nameof(PhotoItem.SessionLabel)));
        PhotoList.SelectionChanged += PhotoList_SelectionChanged;
        Loaded += OnLoaded;
        PreviewKeyDown += Window_PreviewKeyDown;
        SessionGapBox.Text = _settings.SessionGapMinutes.ToString(CultureInfo.InvariantCulture);
        RefreshPlaces();
        try
        {
            if (Directory.Exists(UndoDir)) Directory.Delete(UndoDir, true);
        }
        catch
        {
        }
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

        await LoadSavedTracksAsync();
        await LoadRefFoldersAsync();
        if (_settings.LastFolder is { } last && Directory.Exists(last))
        {
            _folder = last;
            FolderBox.Text = last;
            await LoadFolderAsync(last);
        }
    }

    // ---------------------------------------------------------------- komunikacja z mapą

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            switch (root.GetProperty("type").GetString())
            {
                case "ready":
                    SendPhotosToMap();
                    SendTrackToMap();
                    break;
                case "pick":
                    _pending = new GeoPoint(root.GetProperty("lat").GetDouble(), root.GetProperty("lng").GetDouble());
                    UpdateStatus(_current);
                    break;
                case "dir":
                    _pendingDirection = root.TryGetProperty("deg", out var deg) && deg.ValueKind == JsonValueKind.Number
                        ? deg.GetDouble()
                        : null;
                    UpdateStatus(_current);
                    break;
                case "address":
                    _address = root.GetProperty("text").GetString() ?? "";
                    AddressText.Text = _address;
                    AddressText.ToolTip = _address;
                    break;
                case "streetview":
                    var p = new GeoPoint(root.GetProperty("lat").GetDouble(), root.GetProperty("lng").GetDouble());
                    double? heading = root.TryGetProperty("heading", out var h) && h.ValueKind == JsonValueKind.Number
                        ? h.GetDouble()
                        : null;
                    _ = ShowStreetViewAsync(p, heading);
                    break;
                case "thumbReq":
                    _ = SendThumbAsync(root.GetProperty("id").GetString()!);
                    break;
                case "photoClick":
                    OnMapPhotoClick(root.GetProperty("id").GetString()!);
                    break;
                case "key":
                    var key = root.GetProperty("key").GetString()!;
                    if (key is "ArrowUp" or "ArrowDown" &&
                        root.TryGetProperty("alt", out var alt) && alt.ValueKind == JsonValueKind.True)
                    {
                        MoveInTimeline(key == "ArrowDown");
                        break;
                    }
                    HandleKey(key.Length == 1 ? key.ToLowerInvariant() : key,
                        root.GetProperty("shift").GetBoolean(), root.GetProperty("ctrl").GetBoolean());
                    break;
            }
        }
        catch
        {
        }
    }

    private void PostToMap(object message)
    {
        if (MapView.CoreWebView2 is null) return;
        MapView.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private void SendPhotosToMap()
    {
        _mapIds.Clear();
        var mainPaths = new HashSet<string>(_items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
        var tagged = _items.Where(i => i.HasGps && !i.GpsError).Select(i => (Item: i, Ref: false)).ToList();

        var dated = _items.Where(i => i.HasValidDate).ToList();
        if (dated.Count > 0)
        {
            var from = dated.Min(i => i.TimeUtc).AddDays(-1);
            var to = dated.Max(i => i.TimeUtc).AddDays(1);
            tagged.AddRange(_refItems
                .Where(r => r.TimeUtc >= from && r.TimeUtc <= to && !mainPaths.Contains(r.Path))
                .Take(5000)
                .Select(r => (Item: r, Ref: true)));
        }

        var items = new List<object>();
        for (int n = 0; n < tagged.Count; n++)
        {
            var (item, isRef) = tagged[n];
            var id = (isRef ? "r" : "m") + n.ToString(CultureInfo.InvariantCulture);
            _mapIds[id] = item;
            items.Add(new
            {
                id,
                lat = Math.Round(item.Gps!.Lat, 6),
                lng = Math.Round(item.Gps.Lng, 6),
                name = item.FileName,
                time = item.HasValidDate ? item.Time.ToString("yyyy-MM-dd HH:mm") : "brak daty",
                dir = item.Direction,
                @ref = isRef,
                device = item.GpsFromDevice,
            });
        }

        // Trasa: zdjęcia z GPS w kolejności czasu, nowy odcinek po przerwie > 3 h.
        var route = new List<List<double[]>>();
        DateTime? prev = null;
        foreach (var (item, _) in tagged.Where(t => t.Item.HasValidDate).OrderBy(t => t.Item.TimeUtc))
        {
            if (prev is null || (item.TimeUtc - prev.Value).TotalHours > 3)
                route.Add(new List<double[]>());
            route[^1].Add(new[] { Math.Round(item.Gps!.Lat, 6), Math.Round(item.Gps.Lng, 6) });
            prev = item.TimeUtc;
        }

        PostToMap(new { type = "photos", items, route });
    }

    private void SendTrackToMap()
    {
        var segments = new List<List<double[]>>();
        var dated = _items.Where(i => i.HasValidDate).ToList();
        if (_tracks.Count > 0 && dated.Count > 0)
        {
            var pts = _tracks.Range(dated.Min(i => i.TimeUtc).AddHours(-12), dated.Max(i => i.TimeUtc).AddHours(12)).ToList();
            var step = Math.Max(1, pts.Count / 20000);
            TrackPoint? prev = null;
            for (int i = 0; i < pts.Count; i += step)
            {
                var t = pts[i];
                if (prev is null || (t.Utc - prev.Utc).TotalMinutes > TrackStore.MaxInterpolationMinutes)
                    segments.Add(new List<double[]>());
                segments[^1].Add(new[] { Math.Round(t.Lat, 6), Math.Round(t.Lng, 6) });
                prev = t;
            }
        }
        PostToMap(new { type = "track", segments });
    }

    private async Task SendThumbAsync(string id)
    {
        if (!_mapIds.TryGetValue(id, out var item)) return;
        try
        {
            var data = await Task.Run(() =>
            {
                var bmp = Imaging.LoadBitmap(item.Path, 180, item.Orientation);
                var enc = new JpegBitmapEncoder { QualityLevel = 80 };
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using var ms = new MemoryStream();
                enc.Save(ms);
                return "data:image/jpeg;base64," + Convert.ToBase64String(ms.ToArray());
            });
            PostToMap(new { type = "thumb", id, data });
        }
        catch
        {
        }
    }

    private void OnMapPhotoClick(string id)
    {
        if (!_mapIds.TryGetValue(id, out var item)) return;
        if (_items.Contains(item))
        {
            _keepMapView = true;
            try
            {
                MaintainSelection(item);
            }
            finally
            {
                _keepMapView = false;
            }
        }
        else
        {
            StatusText.Text = $"Zdjęcie referencyjne: {item.Path} ({item.DateText}) — {item.Gps!.Lat:F6}, {item.Gps.Lng:F6}";
        }
    }

    private void SendMapEstimate(Estimate est)
    {
        PostToMap(new
        {
            type = "estimate",
            lat = est.Point.Lat, lng = est.Point.Lng,
            aLat = est.A?.Lat, aLng = est.A?.Lng,
            bLat = est.B?.Lat, bLng = est.B?.Lng,
            label = est.FromTrack ? "Pozycja ze śladu GPS" : "Pozycja szacowana (interpolacja czasu)",
            dir = (double?)null,
        });
    }

    /// <summary>Ustawia znacznik; bez <paramref name="zoom"/> mapa tylko przesuwa się, zachowując powiększenie.</summary>
    private void SendMapSetView(GeoPoint point, int? zoom = null, double? dir = null, bool keepView = false) =>
        PostToMap(new { type = "setView", lat = point.Lat, lng = point.Lng, zoom, dir, keep = keepView });

    // ---------------------------------------------------------------- folder i lista

    private void PickFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Wybierz folder ze zdjęciami" };
        if (dlg.ShowDialog(this) != true) return;
        FolderBox.Text = dlg.FolderName;
        _folder = dlg.FolderName;
        _settings.LastFolder = _folder;
        SettingsService.Save(_settings);
        _ = LoadFolderAsync(_folder);
    }

    private void RefreshBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_folder is not null)
            _ = LoadFolderAsync(_folder);
    }

    private async Task LoadFolderAsync(string folder, string? selectPath = null)
    {
        if (!Directory.Exists(folder)) return;

        StatusText.Text = "Skanowanie folderu…";
        var previousName = selectPath is not null ? System.IO.Path.GetFileName(selectPath) : _current?.FileName;

        var found = await Task.Run(() =>
            Directory.EnumerateFiles(folder)
                .Where(f => ExifService.Supports(System.IO.Path.GetExtension(f)))
                .Select(f => new PhotoItem(f, ExifService.ReadInfo(f)))
                .ToList());

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

        foreach (var item in found)
        {
            item.ClockOffset = OffsetFor(item.Camera);
            if (_settings.TimeOverrides.TryGetValue(item.Path, out var manual))
                item.TimeOverride = manual;
        }

        _current = null;
        PreviewImage.Source = null;
        var restored = previousName is null
            ? null
            : found.FirstOrDefault(i => string.Equals(i.FileName, previousName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(ResolveTargetPath(i.Path), ResolveTargetPath(System.IO.Path.Combine(folder, previousName)),
                    StringComparison.OrdinalIgnoreCase));
        RebuildList(found, restored);
        foreach (var item in found)
            _ = LoadThumbAsync(item);

        RefreshCameras();
        SendPhotosToMap();
        SendTrackToMap();
        StatusText.Text = $"Załadowano {found.Count} zdjęć; {found.Count(i => !i.HasGps && !i.GpsError)} bez GPS, " +
                          $"{found.Count(i => i.GpsFromDevice)} z GPS z aparatu. " +
                          "Wybierz zdjęcie, kliknij lokalizację na mapie, a następnie użyj przycisku zapisu.";
    }

    /// <summary>Sortuje wg czasu (po korekcie zegara), dzieli na sesje i odtwarza zaznaczenie.</summary>
    private void RebuildList(IEnumerable<PhotoItem> source, PhotoItem? select)
    {
        var sorted = source
            .OrderBy(i => i.HasValidDate ? i.TimeUtc : DateTime.MaxValue)
            .ThenBy(i => i.FileName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var gap = TimeSpan.FromMinutes(Math.Max(1, _settings.SessionGapMinutes));
        var sessions = new List<List<PhotoItem>>();
        var undated = new List<PhotoItem>();
        foreach (var item in sorted)
        {
            if (!item.HasValidDate)
            {
                undated.Add(item);
                continue;
            }
            if (sessions.Count == 0 || item.TimeUtc - sessions[^1][^1].TimeUtc > gap)
                sessions.Add(new List<PhotoItem>());
            sessions[^1].Add(item);
        }
        for (int n = 0; n < sessions.Count; n++)
        {
            var s = sessions[n];
            var first = s[0].Time;
            var last = s[^1].Time;
            var range = first.Date == last.Date
                ? $"{first:yyyy-MM-dd HH:mm}–{last:HH:mm}"
                : $"{first:yyyy-MM-dd HH:mm} – {last:yyyy-MM-dd HH:mm}";
            var noGps = s.Count(i => !i.HasGps && !i.GpsError);
            var label = $"Sesja {n + 1} · {range} · {s.Count} zdj." + (noGps > 0 ? $" ({noGps} bez GPS)" : "");
            foreach (var i in s) i.SessionLabel = label;
        }
        foreach (var i in undated) i.SessionLabel = $"Bez daty · {undated.Count} zdj.";

        _bulkSelecting = true;
        try
        {
            _items.Clear();
            foreach (var item in sorted)
                _items.Add(item);
        }
        finally
        {
            _bulkSelecting = false;
        }

        RefreshPool();
        UpdateCounts();
        _current = null;
        MaintainSelection(select);
    }

    private void RefreshPool()
    {
        var mainPaths = new HashSet<string>(_items.Select(i => i.Path), StringComparer.OrdinalIgnoreCase);
        _pool = _items.Where(i => i.HasGps && !i.GpsError && i.HasValidDate)
            .Concat(_refItems.Where(r => !mainPaths.Contains(r.Path)))
            .OrderBy(i => i.TimeUtc)
            .ToList();
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

    private List<PhotoItem> SelectedPhotos() =>
        PhotoList.SelectedItems.Cast<PhotoItem>().OrderBy(i => _items.IndexOf(i)).ToList();

    private async void PhotoList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_bulkSelecting) return;
        var item = e.AddedItems.Count > 0
            ? e.AddedItems[^1] as PhotoItem
            : _current is not null && PhotoList.SelectedItems.Contains(_current) ? _current : PhotoList.SelectedItem as PhotoItem;
        if (item is null || ReferenceEquals(item, _current))
        {
            UpdateStatus(_current);
            return;
        }
        await ShowItemAsync(item);
    }

    private async Task ShowItemAsync(PhotoItem item)
    {
        _current = item;
        if (CameraCombo.Items.Contains(item.Camera))
            CameraCombo.SelectedItem = item.Camera;

        if (item.HasGps)
        {
            _estimate = null;
            _suggestionSource = null;
            _pendingDirection = item.Direction;
            SendMapSetView(item.Gps!, dir: item.Direction, keepView: _keepMapView);
        }
        else if (GetEstimate(item) is { } est)
        {
            _estimate = est;
            _suggestionSource = null;
            _pendingDirection = null;
            SendMapEstimate(est);
        }
        else if (FindNearestTagged(item) is { } sugg)
        {
            _estimate = null;
            _suggestionSource = sugg.Source;
            _pendingDirection = null;
            SendMapSetView(sugg.Source.Gps!);
        }
        else
        {
            _estimate = null;
            _suggestionSource = null;
            _pendingDirection = null;
            PostToMap(new { type = "direction", deg = (double?)null });
        }
        UpdateStatus(item);

        PreviewImage.Source = null;
        try
        {
            var bmp = await Task.Run(() => Imaging.LoadBitmap(item.Path, 1400, item.Orientation));
            if (ReferenceEquals(_current, item))
                PreviewImage.Source = bmp;
        }
        catch
        {
        }
    }

    private void SelectSessionBtn_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is CollectionViewGroup group)
            SelectItems(group.Items.Cast<PhotoItem>().ToList());
    }

    private void SelectCurrentSession()
    {
        if (_current is null) return;
        SelectItems(_items.Where(i => i.SessionLabel == _current.SessionLabel).ToList());
    }

    private void SelectItems(List<PhotoItem> items)
    {
        if (items.Count == 0) return;
        var keep = items.Contains(_current!) ? _current : items.FirstOrDefault(i => !i.HasGps) ?? items[0];
        _bulkSelecting = true;
        try
        {
            PhotoList.SelectedItems.Clear();
            foreach (var i in items)
                PhotoList.SelectedItems.Add(i);
        }
        finally
        {
            _bulkSelecting = false;
        }
        PhotoList.ScrollIntoView(items[0]);
        if (!ReferenceEquals(keep, _current))
            _ = ShowItemAsync(keep!);
        else
            UpdateStatus(_current);
        StatusText.Text = $"Zaznaczono {items.Count} zdjęć ({items.Count(i => !i.HasGps && !i.GpsError)} bez GPS). " +
                          "Wskaż punkt na mapie i naciśnij Enter, aby zapisać lokalizację we wszystkich.";
    }

    private void SelectNextWithoutGps(bool backwards)
    {
        if (_items.Count == 0) return;
        var start = _current is null ? -1 : _items.IndexOf(_current);
        for (int k = 1; k <= _items.Count; k++)
        {
            var idx = ((start + (backwards ? -k : k)) % _items.Count + _items.Count) % _items.Count;
            var i = _items[idx];
            if (!i.HasGps && !i.GpsError)
            {
                MaintainSelection(i);
                return;
            }
        }
        StatusText.Text = "Wszystkie zdjęcia mają już lokalizację.";
    }

    private void MoveSelection(int delta)
    {
        if (_items.Count == 0) return;
        var idx = _current is null ? 0 : Math.Clamp(_items.IndexOf(_current) + delta, 0, _items.Count - 1);
        MaintainSelection(_items[idx]);
    }

    // ---------------------------------------------------------------- podgląd i Street View

    private void PreviewBorder_MouseDown(object sender, MouseButtonEventArgs e)
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

    private void PhotoList_MouseDoubleCick(object sender, MouseButtonEventArgs e)
    {
        if (PreviewImage.Source is not null)
            ShowEnlarged();
    }

    private async void ShowEnlarged()
    {
        CloseStreetView();
        var item = _current;
        MapView.Visibility = Visibility.Collapsed;
        PreviewBorder.Visibility = Visibility.Collapsed;
        EnlargedImage.Source = PreviewImage.Source;
        EnlargedOverlay.Visibility = Visibility.Visible;
        Keyboard.Focus(EnlargedOverlay);
        if (item is null) return;
        await LoadEnlargedAsync(item);
    }

    /// <summary>Strzałki w dużym podglądzie: poprzednie/następne zdjęcie z listy (zaznaczenie idzie razem).</summary>
    private async void StepEnlarged(int delta)
    {
        if (_items.Count == 0) return;
        var idx = _current is null ? 0 : Math.Clamp(_items.IndexOf(_current) + delta, 0, _items.Count - 1);
        var item = _items[idx];
        if (ReferenceEquals(item, _current)) return;
        MaintainSelection(item);
        // miniatura od razu, pełna rozdzielczość po wczytaniu
        EnlargedImage.Source = item.Thumb;
        await LoadEnlargedAsync(item);
    }

    private async Task LoadEnlargedAsync(PhotoItem item)
    {
        var n = _items.IndexOf(item) + 1;
        EnlargedHint.Text = $"{item.FileName} · {n}/{_items.Count} · ← → poprzednie/następne · Kliknij lub Esc — zamknij podgląd";
        try
        {
            var bmp = await Task.Run(() => Imaging.LoadBitmap(item.Path, 2400, item.Orientation));
            if (ReferenceEquals(_current, item) && EnlargedOverlay.Visibility == Visibility.Visible)
                EnlargedImage.Source = bmp;
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

    private async Task ShowStreetViewAsync(GeoPoint point, double? heading)
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

        var inv = CultureInfo.InvariantCulture;
        var url = $"https://www.google.com/maps/@?api=1&map_action=pano&viewpoint={point.Lat.ToString("R", inv)},{point.Lng.ToString("R", inv)}";
        if (heading is { } hd)
            url += "&heading=" + Math.Round(hd).ToString(inv);
        StreetView.CoreWebView2.Navigate(url);

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

    private void EnlargedOverlay_MouseDown(object sender, MouseButtonEventArgs e) =>
        CloseEnlarged();

    // ---------------------------------------------------------------- skróty klawiszowe

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = HandleKey("Escape", false, false);
            return;
        }
        if (EnlargedOverlay.Visibility == Visibility.Visible && Keyboard.Modifiers == ModifierKeys.None)
        {
            var step = e.Key switch
            {
                Key.Left or Key.Up or Key.PageUp => -1,
                Key.Right or Key.Down or Key.PageDown or Key.Space => 1,
                Key.Home => -_items.Count,
                Key.End => _items.Count,
                _ => 0,
            };
            if (step != 0)
            {
                StepEnlarged(step);
                e.Handled = true;
                return;
            }
        }
        if (Keyboard.FocusedElement is TextBox or ComboBox or ComboBoxItem)
            return;
        if (e.Key == Key.System && e.SystemKey is Key.Up or Key.Down && Keyboard.Modifiers == ModifierKeys.Alt)
        {
            MoveInTimeline(e.SystemKey == Key.Down);
            e.Handled = true;
            return;
        }
        if (e.Key == Key.Return && Keyboard.FocusedElement is System.Windows.Controls.Primitives.ButtonBase)
            return;
        var key = e.Key switch
        {
            Key.Return => "Enter",
            Key.N => "n",
            Key.P => "p",
            Key.G => "g",
            Key.S => "s",
            Key.Z => "z",
            Key.C => "c",
            Key.V => "v",
            Key.Delete => "Delete",
            _ => null,
        };
        if (key is null) return;
        var mods = Keyboard.Modifiers;
        if (mods.HasFlag(ModifierKeys.Alt)) return;
        e.Handled = HandleKey(key, mods.HasFlag(ModifierKeys.Shift), mods.HasFlag(ModifierKeys.Control));
    }

    private bool HandleKey(string key, bool shift, bool ctrl)
    {
        switch (key)
        {
            case "Escape" when StreetViewBorder.Visibility == Visibility.Visible:
                CloseStreetView();
                return true;
            case "Escape" when EnlargedOverlay.Visibility == Visibility.Visible:
                CloseEnlarged();
                return true;
            case "Enter" when !ctrl:
                ApplyButton_Click(this, new RoutedEventArgs());
                return true;
            case "n" when !ctrl:
                SelectNextWithoutGps(shift);
                return true;
            case "p" when !ctrl:
                SelectNextWithoutGps(true);
                return true;
            case "g" when !ctrl:
                SelectCurrentSession();
                return true;
            case "s" when !ctrl && _pending is not null:
                _ = ShowStreetViewAsync(_pending, _pendingDirection);
                return true;
            case "z" when ctrl:
                UndoButton_Click(this, new RoutedEventArgs());
                return true;
            case "c" when ctrl && shift:
                CopyLocation();
                return true;
            case "v" when ctrl && shift:
                _ = PasteLocationAsync();
                return true;
            case "c" when ctrl:
                CopyBtn_Click(this, new RoutedEventArgs());
                return true;
            case "v" when ctrl:
                PasteBtn_Click(this, new RoutedEventArgs());
                return true;
            case "Delete" when !ctrl && !shift:
                DeleteSelected();
                return true;
            case "ArrowUp":
                MoveSelection(-1);
                return true;
            case "ArrowDown":
                MoveSelection(1);
                return true;
        }
        return false;
    }

    // ---------------------------------------------------------------- szacowanie pozycji

    private (PhotoItem Source, double DeltaMinutes)? FindNearestTagged(PhotoItem item)
    {
        if (!item.HasValidDate) return null;

        var best = _pool
            .Where(i => !ReferenceEquals(i, item))
            .Select(i => (Source: i, Delta: Math.Abs((i.TimeUtc - item.TimeUtc).TotalMinutes)))
            .Where(x => x.Delta <= 720)
            .OrderBy(x => x.Delta)
            .FirstOrDefault();
        if (best.Source is null) return null;

        return (best.Source, best.Delta);
    }

    private static string FormatDelta(double minutes) =>
        minutes < 1 ? $"{minutes * 60:0} s" : minutes < 60 ? $"{minutes:0} min" : $"{minutes / 60.0:0.##} h";

    /// <summary>Ślad GPS, a gdy go brak — interpolacja między zdjęciami z GPS (Δ ≤ 2 h).</summary>
    private Estimate? GetEstimate(PhotoItem item)
    {
        if (!item.HasValidDate) return null;

        if (_tracks.Find(item.TimeUtc) is { } m)
        {
            var desc = m.Before is not null
                ? $"ślad GPS, interpolacja {m.Before.Utc.ToLocalTime():HH:mm:ss} → {m.After!.Utc.ToLocalTime():HH:mm:ss}"
                : $"ślad GPS, najbliższy punkt (Δ {FormatDelta(m.DeltaMinutes)})";
            return new Estimate(m.Point, desc,
                m.Before is null ? null : new GeoPoint(m.Before.Lat, m.Before.Lng),
                m.After is null ? null : new GeoPoint(m.After.Lat, m.After.Lng),
                true);
        }

        PhotoItem? before = null, after = null;
        foreach (var i in _pool)
        {
            if (ReferenceEquals(i, item)) continue;
            if (i.TimeUtc < item.TimeUtc)
            {
                if (before is null || i.TimeUtc > before.TimeUtc) before = i;
            }
            else if (i.TimeUtc > item.TimeUtc &&
                     (after is null || i.TimeUtc < after.TimeUtc)) after = i;
        }
        if (before is null || after is null) return null;
        var span = (after.TimeUtc - before.TimeUtc).TotalMinutes;
        if (span <= 0 || span > 120) return null;
        var f = (item.TimeUtc - before.TimeUtc).TotalMinutes / span;
        return new Estimate(
            new GeoPoint(before.Gps!.Lat + (after.Gps!.Lat - before.Gps.Lat) * f,
                         before.Gps.Lng + (after.Gps.Lng - before.Gps.Lng) * f),
            $"interpolacja czasu: {before.FileName} [{before.Time:HH:mm}] → {after.FileName} [{after.Time:HH:mm}] (Δ {FormatDelta(span)})",
            before.Gps, after.Gps, false);
    }

    // ---------------------------------------------------------------- zapis

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_pending is null)
        {
            ShowInfo("Najpierw kliknij na mapie, aby wybrać lokalizację.");
            return;
        }

        var batch = ApplyAllCheck.IsChecked == true;
        var targets = batch
            ? _items.Where(i => !i.HasGps && !i.GpsError).ToList()
            : SelectedPhotos();

        if (targets.Count == 0)
        {
            ShowInfo("Brak zdjęć do oznaczenia (wybierz zdjęcie lub zaznacz tryb „wszystkie bez GPS”).");
            return;
        }

        var point = _pending!;
        var direction = batch ? null : _pendingDirection;
        var pointText = $"{point.Lat:F6}, {point.Lng:F6}" + (direction is { } d ? $", kierunek {d:0}°" : "");
        var prompt = targets.Count > 1
            ? $"Dodać lokalizację {pointText} do {targets.Count} zdjęć?"
            : null;

        await ApplyEntriesAsync(targets.Select(t => (Item: t, Point: point, Direction: direction)).ToList(), prompt);
    }

    private async void InterpolateButton_Click(object sender, RoutedEventArgs e)
    {
        var estimates = _items
            .Where(i => !i.HasGps && !i.GpsError && i.HasValidDate)
            .Select(i => (Item: i, Est: GetEstimate(i)))
            .Where(x => x.Est is not null)
            .ToList();

        if (estimates.Count == 0)
        {
            ShowInfo("Brak zdjęć do automatycznego uzupełnienia. Potrzebny ślad GPS obejmujący czas zdjęć " +
                     "albo zdjęcia z GPS (także z folderów ref.) zrobione Δ ≤ 2 h przed i po zdjęciu bez GPS.");
            return;
        }

        var fromTrack = estimates.Count(x => x.Est!.FromTrack);
        await ApplyEntriesAsync(
            estimates.Select(x => (Item: x.Item, Point: x.Est!.Point, Direction: (double?)null)).ToList(),
            $"Zapisać automatycznie wyznaczone lokalizacje dla {estimates.Count} zdjęć bez GPS?\n\n" +
            $"• ze śladu GPS: {fromTrack}\n• z interpolacji między zdjęciami: {estimates.Count - fromTrack}");
    }

    private async Task ApplyEntriesAsync(List<(PhotoItem Item, GeoPoint Point, double? Direction)> entries, string? batchPrompt,
        bool confirmOverwrite = true, bool keepSelection = false)
    {
        if (batchPrompt is not null &&
            MessageBox.Show(this, batchPrompt, "PhotoTag", MessageBoxButton.YesNo, MessageBoxImage.Question)
                != MessageBoxResult.Yes)
            return;

        var deleteOriginal = DeleteOriginalCheck.IsChecked == true;
        var done = 0;
        var errors = new List<string>();
        var undo = new List<UndoEntry>();
        Directory.CreateDirectory(UndoDir);

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
                    if (confirmOverwrite && MessageBox.Show(this,
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

                string? targetBackup = null;
                if (File.Exists(targetPath))
                {
                    targetBackup = System.IO.Path.Combine(UndoDir, Guid.NewGuid().ToString("N") + System.IO.Path.GetExtension(targetPath));
                    File.Copy(targetPath, targetBackup);
                }

                // czas tylko z metadanych lub ustawiony ręcznie — data pliku to zwykle data kopiowania/edycji
                var writeTime = target.DateFromExif || target.TimeOverride is not null;
                var tmp = targetPath + ".tmp";
                var moved = false;
                try
                {
                    if (target.IsVideo)
                        VideoService.WriteMetadata(target.Path, tmp, point.Lat, point.Lng,
                            writeTime ? target.TimeUtc : null,
                            writeTime ? target.Time : null,
                            target.UtcOffset);
                    else
                        ExifService.WriteGps(target.Path, tmp, point.Lat, point.Lng, entry.Direction,
                            writeTime ? target.Time : null);
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

                string? originalBackup = null;
                if (deleteOriginal && !inPlace)
                {
                    originalBackup = System.IO.Path.Combine(UndoDir, Guid.NewGuid().ToString("N") + System.IO.Path.GetExtension(target.Path));
                    File.Move(target.Path, originalBackup);
                }
                // czas (ręczny / po korekcie) jest już w pliku — ręczne ustawienie dla niego niepotrzebne
                if (writeTime && _settings.TimeOverrides.Remove(targetPath))
                    SettingsService.Save(_settings);
                undo.Add(new UndoEntry(targetPath, targetBackup, originalBackup is null ? null : target.Path, originalBackup));
                done++;
            }
            catch (Exception ex)
            {
                errors.Add($"{target.FileName}: {ex.Message}");
            }
        }

        if (undo.Count > 0)
        {
            _undo.Push(undo);
            UndoButton.IsEnabled = true;
        }

        var shared = entries.Select(e => e.Point).Distinct().Count() == 1;
        if (done > 0 && shared)
            AddRecent(entries[0].Point);
        var sharedPoint = shared
            ? $"{entries[0].Point.Lat:F6}, {entries[0].Point.Lng:F6}"
            : "wyznaczone punkty";
        var status = done > 0
            ? $"Zapisano lokalizację {sharedPoint} w {done} plikach (Ctrl+Z cofa)."
            : "Nie zapisano żadnego pliku.";
        if (errors.Count > 0)
        {
            status += $" Błędy: {errors.Count}.";
            MessageBox.Show(this, string.Join("\n", errors), "Błędy zapisu", MessageBoxButton.OK, MessageBoxImage.Warning);
        }

        // następne zdjęcie bez GPS po ostatnim zapisanym
        var lastIdx = entries.Max(e => _items.IndexOf(e.Item));
        var processed = entries.Select(e => e.Item).ToHashSet();
        var next = _items.Skip(lastIdx + 1).Concat(_items.Take(Math.Max(0, lastIdx)))
            .FirstOrDefault(i => !i.HasGps && !i.GpsError && !processed.Contains(i));

        if (_folder is not null)
            await LoadFolderAsync(_folder, keepSelection ? ResolveTargetPath(entries[0].Item.Path) : next?.Path);
        StatusText.Text = status;
    }

    private async void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (_undo.Count == 0) return;
        var batch = _undo.Peek();
        if (MessageBox.Show(this, $"Cofnąć ostatni zapis ({batch.Count} plików)?", "PhotoTag — cofnij",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        _undo.Pop();
        UndoButton.IsEnabled = _undo.Count > 0;

        var errors = new List<string>();
        foreach (var u in Enumerable.Reverse(batch))
        {
            try
            {
                if (File.Exists(u.TargetPath))
                    File.Delete(u.TargetPath);
                if (u.TargetBackup is not null)
                    File.Move(u.TargetBackup, u.TargetPath);
                if (u.OriginalBackup is not null && u.OriginalPath is not null)
                    File.Move(u.OriginalBackup, u.OriginalPath);
            }
            catch (Exception ex)
            {
                errors.Add($"{System.IO.Path.GetFileName(u.TargetPath)}: {ex.Message}");
            }
        }
        if (errors.Count > 0)
            MessageBox.Show(this, string.Join("\n", errors), "Błędy cofania", MessageBoxButton.OK, MessageBoxImage.Warning);

        var restore = batch[0].OriginalPath ?? batch[0].TargetPath;
        if (_folder is not null)
            await LoadFolderAsync(_folder, restore);
        StatusText.Text = $"Cofnięto zapis {batch.Count - errors.Count} plików.";
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

    // ---------------------------------------------------------------- usuwanie

    /// <summary>
    /// „Usuwa” zaznaczone zdjęcia: zmienia nazwę na …jpg.bak (razem z ukrytym oryginałem pliku _LOCPT,
    /// który inaczej wróciłby na listę). Przywrócenie = usunięcie „.bak” z nazwy.
    /// </summary>
    private void DeleteSelected()
    {
        var sel = SelectedPhotos();
        if (sel.Count == 0) return;
        var what = sel.Count == 1 ? sel[0].FileName : $"{sel.Count} zdjęć";
        if (MessageBox.Show(this,
                $"Usunąć {what}?\n\nPliki dostaną dodatkowe rozszerzenie .bak (np. {sel[0].FileName}.bak) — " +
                "można je przywrócić, usuwając „.bak” z nazwy.",
                "Usuń zdjęcia", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        var lastIdx = _items.IndexOf(sel[^1]);
        var next = _items.Skip(lastIdx + 1).Concat(_items.Take(lastIdx).Reverse()).FirstOrDefault(i => !sel.Contains(i));
        var deleted = new List<PhotoItem>();
        var errors = new List<string>();
        foreach (var item in sel)
        {
            try
            {
                File.Move(item.Path, BakPath(item.Path));
                deleted.Add(item);
            }
            catch (Exception ex)
            {
                errors.Add($"{item.FileName}: {ex.Message}");
                continue;
            }
            _settings.TimeOverrides.Remove(item.Path);

            var stem = System.IO.Path.GetFileNameWithoutExtension(item.Path);
            if (!stem.EndsWith("_LOCPT", StringComparison.OrdinalIgnoreCase)) continue;
            var original = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(item.Path)!,
                stem[..^"_LOCPT".Length] + System.IO.Path.GetExtension(item.Path));
            _settings.TimeOverrides.Remove(original);
            if (!File.Exists(original)) continue;
            try
            {
                File.Move(original, BakPath(original));
            }
            catch (Exception ex)
            {
                errors.Add($"{System.IO.Path.GetFileName(original)} (oryginał): {ex.Message}");
            }
        }

        if (deleted.Count > 0)
        {
            SettingsService.Save(_settings);
            PreviewImage.Source = null;
            RebuildList(_items.Except(deleted).ToList(), next);
            SendPhotosToMap();
            StatusText.Text = deleted.Count == 1
                ? $"Usunięto {deleted[0].FileName} (→ .bak)."
                : $"Usunięto {deleted.Count} zdjęć (→ .bak).";
        }
        if (errors.Count > 0)
            MessageBox.Show(this, string.Join("\n", errors), "Nie udało się usunąć", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>plik.jpg → plik.jpg.bak (gdy zajęte: plik.jpg.2.bak, …).</summary>
    private static string BakPath(string path)
    {
        var bak = path + ".bak";
        for (int n = 2; File.Exists(bak); n++)
            bak = $"{path}.{n}.bak";
        return bak;
    }

    private void DeleteMenu_Click(object sender, RoutedEventArgs e) => DeleteSelected();

    // ---------------------------------------------------------------- kopiowanie lokalizacji między zdjęciami

    /// <summary>Lokalizacja skopiowana z innego zdjęcia (punkt + kierunek), do wklejenia na zaznaczone.</summary>
    private (GeoPoint Point, double? Direction, string Source)? _copiedLocation;

    private void CopyLocation()
    {
        if (_current is not { HasGps: true, Gps: { } gps })
        {
            ShowInfo("Wybrane zdjęcie nie ma lokalizacji do skopiowania.");
            return;
        }
        _copiedLocation = (gps, _current.Direction, _current.FileName);
        StatusText.Text = $"Skopiowano lokalizację z {_current.FileName}: {gps.Lat:F6}, {gps.Lng:F6}" +
                          (_current.Direction is { } d ? $", kierunek {d:0}°" : "") +
                          ". Zaznacz inne zdjęcia i użyj „Wklej lokalizację” (Ctrl+Shift+V).";
    }

    private async Task PasteLocationAsync()
    {
        if (_copiedLocation is not { } copied)
        {
            ShowInfo("Najpierw skopiuj lokalizację ze zdjęcia (prawy klik → „Kopiuj lokalizację”, Ctrl+Shift+C).");
            return;
        }
        var targets = SelectedPhotos();
        if (targets.Count == 0)
        {
            ShowInfo("Zaznacz zdjęcia, do których wkleić lokalizację.");
            return;
        }
        var pointText = $"{copied.Point.Lat:F6}, {copied.Point.Lng:F6}" +
                        (copied.Direction is { } d ? $", kierunek {d:0}°" : "");
        var what = targets.Count == 1 ? targets[0].FileName : $"{targets.Count} zdjęć";
        await ApplyEntriesAsync(
            targets.Select(t => (Item: t, Point: copied.Point, Direction: copied.Direction)).ToList(),
            $"Skopiować lokalizację z {copied.Source} ({pointText}) do: {what}?");
    }

    private void PhotoListMenu_Opened(object sender, RoutedEventArgs e)
    {
        CopyLocationMenu.IsEnabled = _current is { HasGps: true };
        CopyLocationMenu.Header = _current is { HasGps: true }
            ? $"Kopiuj lokalizację z {_current.FileName} (Ctrl+Shift+C)"
            : "Kopiuj lokalizację tego zdjęcia (Ctrl+Shift+C)";
        PasteLocationMenu.IsEnabled = _copiedLocation is not null;
        PasteLocationMenu.Header = _copiedLocation is { } c
            ? $"Wklej lokalizację z {c.Source} do zaznaczonych (Ctrl+Shift+V)"
            : "Wklej lokalizację do zaznaczonych (Ctrl+Shift+V)";
    }

    private void CopyLocationMenu_Click(object sender, RoutedEventArgs e) => CopyLocation();

    private async void PasteLocationMenu_Click(object sender, RoutedEventArgs e) => await PasteLocationAsync();

    // ---------------------------------------------------------------- ślady GPS

    private async void LoadTrackBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Wczytaj ślad GPS",
            Multiselect = true,
            Filter = "Ślady GPS (*.gpx;*.kml;*.json)|*.gpx;*.kml;*.json|Wszystkie pliki|*.*",
        };
        if (dlg.ShowDialog(this) != true) return;

        var total = 0;
        var errors = new List<string>();
        foreach (var file in dlg.FileNames)
        {
            StatusText.Text = $"Wczytywanie śladu {System.IO.Path.GetFileName(file)}…";
            try
            {
                var n = await Task.Run(() => _tracks.Load(file));
                total += n;
                if (n == 0)
                    errors.Add($"{System.IO.Path.GetFileName(file)}: brak punktów z czasem");
                else if (!_settings.TrackFiles.Contains(file, StringComparer.OrdinalIgnoreCase))
                    _settings.TrackFiles.Add(file);
            }
            catch (Exception ex)
            {
                errors.Add($"{System.IO.Path.GetFileName(file)}: {ex.Message}");
            }
        }
        SettingsService.Save(_settings);
        if (errors.Count > 0)
            MessageBox.Show(this, string.Join("\n", errors), "Ślad GPS", MessageBoxButton.OK, MessageBoxImage.Warning);
        AfterTracksChanged();
        StatusText.Text = $"Wczytano {total} punktów śladu. Zdjęcia bez GPS w zakresie czasu śladu dostaną pozycję automatycznie " +
                          "(sprawdź też korektę zegara aparatu).";
    }

    private async Task LoadSavedTracksAsync()
    {
        foreach (var file in _settings.TrackFiles.Where(File.Exists).ToList())
        {
            try
            {
                await Task.Run(() => _tracks.Load(file));
            }
            catch
            {
            }
        }
        AfterTracksChanged();
    }

    private void ClearTrackBtn_Click(object sender, RoutedEventArgs e)
    {
        _tracks.Clear();
        _settings.TrackFiles.Clear();
        SettingsService.Save(_settings);
        AfterTracksChanged();
    }

    private void AfterTracksChanged()
    {
        TrackInfo.Text = _tracks.Count == 0
            ? "brak"
            : $"{_tracks.Count} pkt ({_tracks.Points[0].Utc.ToLocalTime():yyyy-MM-dd} – {_tracks.Points[^1].Utc.ToLocalTime():yyyy-MM-dd})";
        TrackInfo.ToolTip = string.Join("\n", _settings.TrackFiles);
        SendTrackToMap();
        RefreshCurrent();
    }

    private void RefreshCurrent()
    {
        if (_current is null) return;
        var item = _current;
        _current = null;
        _ = ShowItemAsync(item);
    }

    // ---------------------------------------------------------------- foldery referencyjne

    private async void AddRefFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFolderDialog { Title = "Folder referencyjny (np. zdjęcia z telefonu z GPS)" };
        if (dlg.ShowDialog(this) != true) return;
        if (!_settings.ReferenceFolders.Contains(dlg.FolderName, StringComparer.OrdinalIgnoreCase))
            _settings.ReferenceFolders.Add(dlg.FolderName);
        SettingsService.Save(_settings);
        await LoadRefFoldersAsync();
    }

    private async void ClearRefFoldersBtn_Click(object sender, RoutedEventArgs e)
    {
        _settings.ReferenceFolders.Clear();
        SettingsService.Save(_settings);
        await LoadRefFoldersAsync();
    }

    private async Task LoadRefFoldersAsync()
    {
        var folders = _settings.ReferenceFolders.Where(Directory.Exists).ToList();
        if (folders.Count > 0)
            StatusText.Text = "Skanowanie folderów referencyjnych…";
        _refItems = await Task.Run(() =>
            folders
                .SelectMany(f =>
                {
                    try
                    {
                        return Directory.EnumerateFiles(f, "*", SearchOption.AllDirectories).ToList();
                    }
                    catch
                    {
                        return new List<string>();
                    }
                })
                .Where(f => ExifService.Supports(System.IO.Path.GetExtension(f)))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Select(f => new PhotoItem(f, ExifService.ReadInfo(f)))
                .Where(i => i.HasGps && !i.GpsError && i.HasValidDate)
                .ToList());
        foreach (var r in _refItems)
            r.ClockOffset = OffsetFor(r.Camera);

        RefInfo.Text = folders.Count == 0 ? "brak" : $"{folders.Count} folder(y), {_refItems.Count} zdjęć z GPS";
        RefInfo.ToolTip = string.Join("\n", _settings.ReferenceFolders);
        if (folders.Count > 0)
            StatusText.Text = $"Foldery referencyjne: {_refItems.Count} zdjęć z GPS.";
        RefreshPool();
        RefreshCameras();
        SendPhotosToMap();
        RefreshCurrent();
    }

    // ---------------------------------------------------------------- zegar aparatu

    private TimeSpan OffsetFor(string camera) =>
        _settings.ClockOffsets.TryGetValue(camera, out var s) ? TimeSpan.FromSeconds(s) : TimeSpan.Zero;

    private void RefreshCameras()
    {
        var selected = CameraCombo.SelectedItem as string;
        var cameras = _items.Select(i => i.Camera)
            .Concat(_refItems.Select(i => i.Camera))
            .Concat(_settings.ClockOffsets.Keys)
            .Distinct()
            .OrderBy(c => c)
            .ToList();
        CameraCombo.ItemsSource = cameras;
        CameraCombo.SelectedItem = selected is not null && cameras.Contains(selected)
            ? selected
            : _items.Select(i => i.Camera).FirstOrDefault();
    }

    private void CameraCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        OffsetBox.Text = CameraCombo.SelectedItem is string cam ? PhotoItem.FormatOffset(OffsetFor(cam)) : "";
    }

    private void SetOffsetBtn_Click(object sender, RoutedEventArgs e)
    {
        if (CameraCombo.SelectedItem is not string cam)
        {
            ShowInfo("Wybierz aparat.");
            return;
        }
        if (!TryParseOffset(OffsetBox.Text, out var offset))
        {
            ShowInfo("Nieprawidłowe przesunięcie. Przykłady: +01:00:00, -00:03:20, +1.00:00:00 (1 dzień).");
            return;
        }
        SetCameraOffset(cam, offset);
    }

    private static bool TryParseOffset(string text, out TimeSpan offset)
    {
        text = text.Trim();
        var sign = text.StartsWith('-') ? -1 : 1;
        var ok = TimeSpan.TryParse(text.TrimStart('+', '-'), CultureInfo.InvariantCulture, out offset);
        offset = sign * offset;
        return ok;
    }

    private void SetCameraOffset(string camera, TimeSpan offset)
    {
        if (offset == TimeSpan.Zero)
            _settings.ClockOffsets.Remove(camera);
        else
            _settings.ClockOffsets[camera] = offset.TotalSeconds;
        SettingsService.Save(_settings);

        foreach (var i in _items.Concat(_refItems).Where(i => i.Camera == camera))
            i.ClockOffset = offset;

        var keep = _current;
        var selected = SelectedPhotos();
        RebuildList(_items.ToList(), keep);
        if (selected.Count > 1)
            SelectItems(selected);
        RefreshCameras();
        CameraCombo.SelectedItem = camera;
        SendPhotosToMap();
        SendTrackToMap();
        RefreshCurrent();
        StatusText.Text = $"Zegar aparatu „{camera}”: przesunięcie {PhotoItem.FormatOffset(offset)}.";
    }

    private void CalibrateBtn_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedPhotos();
        if (sel.Count == 2)
        {
            CalibrateFromPair(sel[0], sel[1]);
            return;
        }
        if (sel.Count == 1 && _pending is not null && _tracks.Count > 0)
        {
            CalibrateFromTrack(sel[0], _pending);
            return;
        }
        ShowInfo("Kalibracja zegara:\n\n" +
                 "• zaznacz (Ctrl+klik) 2 zdjęcia tej samej sceny zrobione w tej samej chwili różnymi aparatami — " +
                 "zegar aparatu zdjęcia bez GPS zostanie zrównany z drugim, albo\n" +
                 "• zaznacz 1 zdjęcie, wskaż na mapie miejsce, w którym je zrobiono, i kliknij „Kalibruj” — " +
                 "czas zostanie dopasowany do najbliższego punktu wczytanego śladu GPS.");
    }

    private void CalibrateFromPair(PhotoItem a, PhotoItem b)
    {
        if (!a.HasValidDate || !b.HasValidDate)
        {
            ShowInfo("Oba zdjęcia muszą mieć datę.");
            return;
        }
        if (a.Camera == b.Camera)
        {
            ShowInfo("Oba zdjęcia pochodzą z tego samego aparatu — kalibracja wymaga dwóch różnych aparatów.");
            return;
        }

        PhotoItem reference, adjust;
        if (a.HasGps && !b.HasGps) (reference, adjust) = (a, b);
        else if (b.HasGps && !a.HasGps) (reference, adjust) = (b, a);
        else
        {
            var r = MessageBox.Show(this,
                $"Który zegar skorygować?\n\nTak — „{a.Camera}” ({a.FileName})\nNie — „{b.Camera}” ({b.FileName})",
                "PhotoTag — kalibracja", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return;
            (reference, adjust) = r == MessageBoxResult.Yes ? (b, a) : (a, b);
        }

        var newOffset = adjust.ClockOffset + (reference.TimeUtc - adjust.TimeUtc);
        ConfirmOffset(adjust.Camera, newOffset, $"zrównanie z {reference.FileName} ({reference.Camera})");
    }

    private void CalibrateFromTrack(PhotoItem item, GeoPoint place)
    {
        if (!item.HasValidDate)
        {
            ShowInfo("Zdjęcie nie ma daty.");
            return;
        }
        var t = _tracks.NearestInSpace(place, item.TimeUtc.AddHours(-36), item.TimeUtc.AddHours(36))
                ?? _tracks.NearestInSpace(place);
        if (t is null) return;

        var meters = DistanceMeters(place, new GeoPoint(t.Lat, t.Lng));
        var newOffset = item.ClockOffset + (t.Utc - item.TimeUtc);
        ConfirmOffset(item.Camera, newOffset,
            $"najbliższy punkt śladu {t.Utc.ToLocalTime():yyyy-MM-dd HH:mm:ss}, {meters:0} m od wskazanego miejsca" +
            (meters > 500 ? " — UWAGA: daleko, wynik może być niedokładny" : ""));
    }

    private void ConfirmOffset(string camera, TimeSpan offset, string basis)
    {
        if (MessageBox.Show(this,
                $"Ustawić przesunięcie zegara aparatu „{camera}” na {PhotoItem.FormatOffset(offset)}?\n\nPodstawa: {basis}.",
                "PhotoTag — kalibracja zegara", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
            SetCameraOffset(camera, TimeSpan.FromSeconds(Math.Round(offset.TotalSeconds)));
    }

    private static double DistanceMeters(GeoPoint a, GeoPoint b)
    {
        const double r = 6371000;
        var dLat = (b.Lat - a.Lat) * Math.PI / 180;
        var dLng = (b.Lng - a.Lng) * Math.PI / 180;
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) +
                Math.Cos(a.Lat * Math.PI / 180) * Math.Cos(b.Lat * Math.PI / 180) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return 2 * r * Math.Asin(Math.Sqrt(h));
    }

    private void SessionGapBox_LostFocus(object sender, RoutedEventArgs e) => ApplySessionGap();

    private void SessionGapBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Return) ApplySessionGap();
    }

    private void ApplySessionGap()
    {
        if (!int.TryParse(SessionGapBox.Text, out var minutes) || minutes < 1 || minutes > 1440)
        {
            SessionGapBox.Text = _settings.SessionGapMinutes.ToString(CultureInfo.InvariantCulture);
            return;
        }
        if (minutes == _settings.SessionGapMinutes) return;
        _settings.SessionGapMinutes = minutes;
        SettingsService.Save(_settings);
        RebuildList(_items.ToList(), _current);
    }

    // ---------------------------------------------------------------- miejsca, schowek

    private void RefreshPlaces()
    {
        _suppressPlaces = true;
        PlacesCombo.ItemsSource = _settings.Favorites.Select(f => new PlaceEntry(f, true))
            .Concat(_settings.Recents.Select(r => new PlaceEntry(r, false)))
            .ToList();
        PlacesCombo.SelectedIndex = -1;
        _suppressPlaces = false;
    }

    private void PlacesCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressPlaces || PlacesCombo.SelectedItem is not PlaceEntry p) return;
        CloseStreetView();
        CloseEnlarged();
        SendMapSetView(new GeoPoint(p.Place.Lat, p.Place.Lng), 17, _pendingDirection);
    }

    private void AddRecent(GeoPoint point)
    {
        var name = string.IsNullOrWhiteSpace(_address) ? $"{point.Lat:F5}, {point.Lng:F5}" : ShortAddress(_address);
        _settings.Recents.RemoveAll(r => DistanceMeters(new GeoPoint(r.Lat, r.Lng), point) < 25);
        _settings.Recents.Insert(0, new SavedPlace(name, point.Lat, point.Lng));
        if (_settings.Recents.Count > 15)
            _settings.Recents.RemoveRange(15, _settings.Recents.Count - 15);
        SettingsService.Save(_settings);
        RefreshPlaces();
    }

    private static string ShortAddress(string address)
    {
        var parts = address.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return string.Join(", ", parts.Take(3));
    }

    private void AddFavoriteBtn_Click(object sender, RoutedEventArgs e)
    {
        if (_pending is null)
        {
            ShowInfo("Najpierw wskaż punkt na mapie.");
            return;
        }
        var name = PromptText("Nazwa ulubionego miejsca:",
            string.IsNullOrWhiteSpace(_address) ? "" : ShortAddress(_address));
        if (string.IsNullOrWhiteSpace(name)) return;
        _settings.Favorites.Add(new SavedPlace(name.Trim(), _pending.Lat, _pending.Lng));
        _settings.Favorites.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase));
        SettingsService.Save(_settings);
        RefreshPlaces();
    }

    private void RemovePlaceBtn_Click(object sender, RoutedEventArgs e)
    {
        if (PlacesCombo.SelectedItem is not PlaceEntry p)
        {
            ShowInfo("Wybierz miejsce z listy.");
            return;
        }
        (p.Favorite ? _settings.Favorites : _settings.Recents).Remove(p.Place);
        SettingsService.Save(_settings);
        RefreshPlaces();
    }

    private void CopyBtn_Click(object sender, RoutedEventArgs e)
    {
        var p = _pending ?? _current?.Gps;
        if (p is null)
        {
            StatusText.Text = "Brak punktu do skopiowania.";
            return;
        }
        var text = p.Lat.ToString("F6", CultureInfo.InvariantCulture) + ", " + p.Lng.ToString("F6", CultureInfo.InvariantCulture);
        try
        {
            Clipboard.SetText(text);
            StatusText.Text = $"Skopiowano: {text}";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Nie udało się skopiować: " + ex.Message;
        }
    }

    private void PasteBtn_Click(object sender, RoutedEventArgs e)
    {
        string text;
        try
        {
            text = Clipboard.GetText().Trim();
        }
        catch
        {
            return;
        }
        if (text.Length == 0)
        {
            StatusText.Text = "Schowek jest pusty.";
            return;
        }
        CloseStreetView();
        CloseEnlarged();
        PostToMap(new { type = "search", q = text.Length > 2000 ? text[..2000] : text });
    }

    private string? PromptText(string label, string initial)
    {
        var box = new TextBox { Text = initial, Margin = new Thickness(0, 6, 0, 10), MinWidth = 320 };
        var ok = new Button { Content = "OK", IsDefault = true, Padding = new Thickness(16, 4, 16, 4) };
        var cancel = new Button { Content = "Anuluj", IsCancel = true, Padding = new Thickness(12, 4, 12, 4), Margin = new Thickness(8, 0, 0, 0) };
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(new TextBlock { Text = label });
        panel.Children.Add(box);
        panel.Children.Add(buttons);
        var dlg = new Window
        {
            Title = "PhotoTag",
            Content = panel,
            Owner = this,
            SizeToContent = SizeToContent.WidthAndHeight,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
        };
        ok.Click += (_, _) => dlg.DialogResult = true;
        dlg.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        return dlg.ShowDialog() == true ? box.Text : null;
    }

    // ---------------------------------------------------------------- status

    private void UpdateCounts()
    {
        var noGps = _items.Count(i => !i.HasGps && !i.GpsError);
        CountText.Text = $"zdjęć: {_items.Count} | bez GPS: {noGps}";
    }

    private void UpdateStatus(PhotoItem? item)
    {
        var dir = _pendingDirection is { } d ? $", kierunek {d:0}°" : "";
        var point = _pending is null ? "" : $" | punkt mapy: {_pending!.Lat:F6}, {_pending.Lng:F6}{dir}";
        var selCount = PhotoList.SelectedItems.Count;
        var sel = selCount > 1 ? $" [zaznaczono {selCount}]" : "";
        if (item is null)
        {
            StatusText.Text = _pending is null
                ? "Kliknij na mapie, aby wybrać lokalizację."
                : $"Wybrany punkt mapy: {_pending.Lat:F6}, {_pending.Lng:F6}{dir}";
            return;
        }

        if (item.GpsError)
        {
            StatusText.Text = $"{item.FileName} ({item.DateText}) — błąd odczytu EXIF{point}{sel}";
        }
        else if (item.HasGps)
        {
            StatusText.Text = $"{item.FileName} ({item.DateText}) — ma GPS ({item.GpsSourceText}): {item.Gps!.Lat:F6}, {item.Gps.Lng:F6}{point}{sel}";
        }
        else if (_estimate is { } est && item.HasValidDate)
        {
            StatusText.Text = $"{item.FileName} ({item.DateText}) — brak GPS. Pozycja szacowana ({est.Description}) — " +
                              $"przeciągnij znacznik, aby skorygować, i naciśnij Enter.{sel}";
        }
        else if (_suggestionSource is { HasGps: true } src && src.HasValidDate && item.HasValidDate)
        {
            var delta = FormatDelta(Math.Abs((src.TimeUtc - item.TimeUtc).TotalMinutes));
            var when = src.TimeUtc < item.TimeUtc ? "przed nim" : "po nim";
            StatusText.Text = $"{item.FileName} ({item.DateText}) — brak GPS. Sugestia: punkt ze zdjęcia {src.FileName} " +
                              $"(robione {when}, Δ {delta}) — przeciągnij znacznik, aby skorygować, i naciśnij Enter.{sel}";
        }
        else
        {
            StatusText.Text = $"{item.FileName} ({item.DateText}) — brak GPS{point}. Kliknij na mapie i naciśnij Enter.{sel}";
        }
    }

    private void ShowInfo(string message) =>
        MessageBox.Show(this, message, "PhotoTag", MessageBoxButton.OK, MessageBoxImage.Information);
}
