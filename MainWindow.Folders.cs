using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;
using PhotoTag.Models;
using PhotoTag.Services;

namespace PhotoTag;

/// <summary>Foldery ze zdjęciami (kilka naraz, opcjonalnie z podfolderami), foldery referencyjne, postęp skanowania.</summary>
public partial class MainWindow
{
    /// <summary>Formaty, które często trafiają się w folderach ze zdjęciami, a których PhotoTag nie odczytuje.</summary>
    private static readonly HashSet<string> KnownUnsupported = new(StringComparer.OrdinalIgnoreCase)
    {
        ".heic", ".heif", ".png", ".webp", ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2", ".raf", ".avi", ".mkv",
    };

    private readonly List<string> _folders = new();
    private readonly Dictionary<string, DescriptionStore> _descStores = new(StringComparer.OrdinalIgnoreCase);
    private int _busyDepth;
    private CancellationTokenSource? _scanCts;
    /// <summary>Foldery, dla których w tej sesji odmówiono wczytania podfolderów (nie pytamy ponownie).</summary>
    private readonly HashSet<string> _declinedSubfolders = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Foldery, których zdjęcia są aktualnie na liście (po przerwaniu wczytywania wracamy do nich).</summary>
    private List<string> _loadedFolders = new();

    // ---------------------------------------------------------------- wskaźnik pracy

    private CancellationToken BeginBusy(string text)
    {
        if (_busyDepth++ == 0)
        {
            _scanCts = new CancellationTokenSource();
            Mouse.OverrideCursor = Cursors.Wait;
            MainMenu.IsEnabled = false;
            ToolbarPanel.IsEnabled = false;
            ScanProgress.IsIndeterminate = true;
            ScanProgress.Visibility = Visibility.Visible;
            CancelScanBtn.Visibility = Visibility.Visible;
            CancelScanBtn.IsEnabled = true;
        }
        StatusText.Text = text;
        return _scanCts!.Token;
    }

    private void EndBusy()
    {
        if (--_busyDepth > 0) return;
        Mouse.OverrideCursor = null;
        MainMenu.IsEnabled = true;
        ToolbarPanel.IsEnabled = true;
        ScanProgress.Visibility = Visibility.Collapsed;
        CancelScanBtn.Visibility = Visibility.Collapsed;
        _scanCts?.Dispose();
        _scanCts = null;
    }

    private void CancelScanBtn_Click(object sender, RoutedEventArgs e)
    {
        _scanCts?.Cancel();
        CancelScanBtn.IsEnabled = false;
        StatusText.Text = "Przerywanie…";
    }

    private void ReportProgress(string label, int done, int total)
    {
        ScanProgress.IsIndeterminate = false;
        ScanProgress.Maximum = Math.Max(1, total);
        ScanProgress.Value = done;
        StatusText.Text = $"{label}: {done} z {total} plików…";
    }

    // ---------------------------------------------------------------- skanowanie

    /// <summary>Pliki obsługiwane w folderze; niedostępne podfoldery są pomijane (nie przerywają skanowania).</summary>
    private static List<string> EnumerateMedia(string folder, bool recursive, CancellationToken ct,
        Dictionary<string, int>? unsupported = null)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = recursive, IgnoreInaccessible = true };
        var list = new List<string>();
        foreach (var file in Directory.EnumerateFiles(folder, "*", options))
        {
            ct.ThrowIfCancellationRequested();
            var ext = Path.GetExtension(file);
            if (ExifService.Supports(ext))
                list.Add(file);
            else if (unsupported is not null && KnownUnsupported.Contains(ext))
                unsupported[ext.ToUpperInvariant().TrimStart('.')] = unsupported.GetValueOrDefault(ext.ToUpperInvariant().TrimStart('.')) + 1;
        }
        return list;
    }

    /// <summary>Czyta metadane (kilka plików naraz), raportuje postęp; uszkodzony plik nie przerywa całości.</summary>
    private async Task<List<PhotoItem>> ReadMediaAsync(IReadOnlyList<string> files, string label, CancellationToken ct)
    {
        ReportProgress(label, 0, files.Count);
        var progress = new Progress<int>(n => ReportProgress(label, n, files.Count));
        return await Task.Run(() =>
        {
            var result = new PhotoItem?[files.Count];
            var done = 0;
            long lastReport = 0;
            Parallel.For(0, files.Count, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, i =>
            {
                try
                {
                    result[i] = new PhotoItem(files[i], ExifService.ReadInfo(files[i]));
                }
                catch
                {
                    // plik zniknął / brak dostępu — pomijamy
                }
                var n = Interlocked.Increment(ref done);
                var now = Environment.TickCount64;
                var prev = Interlocked.Read(ref lastReport);
                if (n == files.Count || (now - prev >= 100 && Interlocked.CompareExchange(ref lastReport, now, prev) == prev))
                    ((IProgress<int>)progress).Report(n);
            });
            return result.OfType<PhotoItem>().ToList();
        }, ct);
    }

    private static string FormatUnsupported(Dictionary<string, int> unsupported) =>
        string.Join(", ", unsupported.OrderByDescending(p => p.Value).Select(p => $"{p.Key}: {p.Value}"));

    // ---------------------------------------------------------------- wybór folderu

    /// <summary>
    /// Okno plików zamiast okna folderów — widać zdjęcia w środku. Można kliknąć zdjęcie albo,
    /// będąc w folderze, od razu „Otwórz” (pole nazwy zawiera „Wybierz ten folder”).
    /// Dla folderów ze zdjęciami okno ma pole „Wczytaj także zdjęcia z podfolderów”.
    /// </summary>
    private FolderPicker.Result? PickFolder(string title, string? initial, bool subfoldersOption)
    {
        title += " — wejdź do folderu i kliknij „Otwórz” (albo kliknij dowolne zdjęcie)";
        var owner = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        try
        {
            return FolderPicker.Show(owner, title, initial, subfoldersOption ? _settings.IncludeSubfolders : null);
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            // awaryjnie zwykłe okno WPF (bez pola podfolderów)
            var dlg = new OpenFileDialog
            {
                Title = title,
                Filter = "Zdjęcia i filmy (JPG, TIFF, MP4, MOV)|*.jpg;*.jpeg;*.tif;*.tiff;*.mp4;*.mov;*.m4v;*.3gp|Wszystkie pliki|*.*",
                CheckFileExists = false,
                ValidateNames = false,
                FileName = FolderPicker.PickHint,
            };
            if (initial is not null && Directory.Exists(initial))
                dlg.InitialDirectory = initial;
            if (dlg.ShowDialog(this) != true) return null;
            var path = dlg.FileName;
            if (File.Exists(path)) return new FolderPicker.Result(Path.GetDirectoryName(path)!, path, _settings.IncludeSubfolders);
            var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
            return dir is not null && Directory.Exists(dir) ? new FolderPicker.Result(dir, null, _settings.IncludeSubfolders) : null;
        }
    }

    private async void OpenFolderBtn_Click(object sender, RoutedEventArgs e) => await OpenFolderAsync(add: false);

    private async void AddFolderBtn_Click(object sender, RoutedEventArgs e) => await OpenFolderAsync(add: true);

    /// <summary>Otwiera folder (zastępuje listę) albo dodaje go do już wczytanych. Zwraca podsumowanie lub null.</summary>
    private async Task<string?> OpenFolderAsync(bool add, string? folder = null, string? file = null)
    {
        if (folder is null)
        {
            var title = add ? "Dodaj kolejny folder ze zdjęciami" : "Wybierz folder ze zdjęciami";
            var initial = _current is not null ? Path.GetDirectoryName(_current.Path) : _folders.FirstOrDefault();
            if (PickFolder(title, initial, subfoldersOption: true) is not { } picked) return null;
            (folder, file) = (picked.Folder, picked.File);
            if (picked.IncludeSubfolders != _settings.IncludeSubfolders)
            {
                _settings.IncludeSubfolders = picked.IncludeSubfolders;
                SubfoldersCheck.IsChecked = picked.IncludeSubfolders;
                SettingsService.Save(_settings);
            }
        }

        if (!add) _folders.Clear();
        if (!_folders.Contains(folder, StringComparer.OrdinalIgnoreCase))
            _folders.Add(folder);
        SaveFolders();
        return await LoadFoldersAsync(file);
    }

    private async void RemoveFoldersMenu_Click(object sender, RoutedEventArgs e)
    {
        _folders.Clear();
        SaveFolders();
        await LoadFoldersAsync();
    }

    private async void RefreshBtn_Click(object sender, RoutedEventArgs e) => await LoadFoldersAsync();

    private async void SubfoldersCheck_Click(object sender, RoutedEventArgs e)
    {
        _settings.IncludeSubfolders = SubfoldersCheck.IsChecked == true;
        SettingsService.Save(_settings);
        if (_folders.Count > 0)
            await LoadFoldersAsync();
    }

    private void SaveFolders()
    {
        _settings.Folders = _folders.ToList();
        _settings.LastFolder = _folders.FirstOrDefault();
        SettingsService.Save(_settings);
        UpdateFolderBox();
    }

    private void UpdateFolderBox()
    {
        FolderBox.Text = _folders.Count switch
        {
            0 => "",
            1 => _folders[0],
            _ => $"{_folders.Count} foldery: " + string.Join(", ", _folders.Select(Path.GetFileName)),
        };
        FolderBox.ToolTip = _folders.Count == 0 ? "Brak wczytanych folderów" : string.Join("\n", _folders);
    }

    // ---------------------------------------------------------------- przeciągnij i upuść

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && _busyDepth == 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void Window_Drop(object sender, DragEventArgs e)
    {
        if (_busyDepth > 0 || e.Data.GetData(DataFormats.FileDrop) is not string[] { Length: > 0 } paths) return;
        var folders = paths
            .Select(p => Directory.Exists(p) ? p : Path.GetDirectoryName(p))
            .OfType<string>()
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (folders.Count == 0) return;
        // upuszczenie dodaje do wczytanych; z wciśniętym Ctrl — zastępuje
        if ((e.KeyStates & DragDropKeyStates.ControlKey) != 0) _folders.Clear();
        foreach (var f in folders)
            if (!_folders.Contains(f, StringComparer.OrdinalIgnoreCase))
                _folders.Add(f);
        SaveFolders();
        await LoadFoldersAsync(paths.FirstOrDefault(File.Exists));
    }

    // ---------------------------------------------------------------- wczytywanie listy

    /// <summary>Opisy zdjęć — osobny plik opisy.json w każdym folderze.</summary>
    private DescriptionStore DescriptionsFor(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? "";
        if (!_descStores.TryGetValue(dir, out var store))
            _descStores[dir] = store = DescriptionStore.Load(dir);
        return store;
    }

    /// <summary>Pyta, czy wczytać podfoldery folderów, w których zdjęcia są tylko w podfolderach. „Tak” zaznacza „z podfolderami”.</summary>
    private bool AskSubfolders(List<(string Folder, int Count)> onlyNested)
    {
        var list = string.Join("\n", onlyNested.Select(x => $"•  {Path.GetFileName(x.Folder)} — {x.Count} zdjęć w podfolderach"));
        var cursor = Mouse.OverrideCursor;
        Mouse.OverrideCursor = null;
        var answer = MessageBox.Show(this,
            (onlyNested.Count == 1 ? "W wybranym folderze nie ma zdjęć — są tylko w jego podfolderach:" : "W tych folderach nie ma zdjęć — są tylko w podfolderach:") +
            $"\n\n{list}\n\nWczytać zdjęcia z podfolderów?\n\n(Zaznaczy to pole „z podfolderami” — dotyczy wszystkich wczytanych folderów.)",
            "PhotoTag — podfoldery", MessageBoxButton.YesNo, MessageBoxImage.Question);
        Mouse.OverrideCursor = cursor;
        if (answer != MessageBoxResult.Yes)
        {
            foreach (var x in onlyNested)
                _declinedSubfolders.Add(x.Folder);
            return false;
        }
        _settings.IncludeSubfolders = true;
        SubfoldersCheck.IsChecked = true;
        SettingsService.Save(_settings);
        return true;
    }

    /// <summary>Skanuje wszystkie wczytane foldery i odbudowuje listę. Zwraca podsumowanie (null = przerwano / błąd).</summary>
    private async Task<string?> LoadFoldersAsync(string? selectPath = null)
    {
        var folders = _folders.Where(Directory.Exists).ToList();
        UpdateFolderBox();
        var previousPath = selectPath ?? _current?.Path;

        var ct = BeginBusy("Wyszukiwanie zdjęć…");
        StopThumbs();
        try
        {
            // Podfoldery tylko przy zaznaczonym „z podfolderami”. Folder bez zdjęć w środku, ale ze zdjęciami
            // w podfolderach (np. „Wyjazd\dzień 1”) — pytamy; „Tak” zaznacza pole i skanujemy ponownie.
            var recursive = _settings.IncludeSubfolders;
            var declined = _declinedSubfolders.ToHashSet(StringComparer.OrdinalIgnoreCase);
            Dictionary<string, int> unsupported;
            List<string> files;
            while (true)
            {
                var skipped = new Dictionary<string, int>();
                var onlyNested = new List<(string Folder, int Count)>();
                var rec = recursive;
                var list = await Task.Run(() => folders
                    .SelectMany(f =>
                    {
                        var media = EnumerateMedia(f, rec, ct, skipped);
                        if (media.Count == 0 && !rec && !declined.Contains(f) &&
                            EnumerateMedia(f, true, ct).Count is var nested and > 0)
                            onlyNested.Add((f, nested));
                        return media;
                    })
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(), ct);
                if (onlyNested.Count == 0 || !AskSubfolders(onlyNested))
                {
                    (files, unsupported) = (list, skipped);
                    break;
                }
                recursive = true;
            }

            var found = await ReadMediaAsync(files, "Wczytywanie zdjęć", ct);

            var locptPaths = new HashSet<string>(
                found
                    .Where(i => Path.GetFileNameWithoutExtension(i.Path).EndsWith("_LOCPT", StringComparison.OrdinalIgnoreCase))
                    .Select(i => i.Path),
                StringComparer.OrdinalIgnoreCase);
            found.RemoveAll(i =>
                !Path.GetFileNameWithoutExtension(i.Path).EndsWith("_LOCPT", StringComparison.OrdinalIgnoreCase) &&
                locptPaths.Contains(ResolveTargetPath(i.Path)));

            CommitDescription();
            _descStores.Clear();
            foreach (var item in found)
            {
                item.ClockOffset = OffsetFor(item.Camera);
                if (_settings.TimeOverrides.TryGetValue(item.Path, out var manual))
                    item.TimeOverride = manual;
                item.Description = DescriptionsFor(item.Path).Get(item.FileName);
            }

            _current = null;
            ShowDescription(null);
            PreviewImage.Source = null;
            var restored = previousPath is null
                ? null
                : found.FirstOrDefault(i => string.Equals(i.Path, previousPath, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ResolveTargetPath(i.Path), ResolveTargetPath(previousPath), StringComparison.OrdinalIgnoreCase));
            RebuildList(found, restored);
            _loadedFolders = folders;
            LoadThumbs(found);

            RefreshCameras();
            SendPhotosToMap();
            SendTrackToMap();

            string summary;
            if (folders.Count == 0)
                summary = "Brak wczytanych folderów. Zacznij od „Otwórz folder…” albo Pomoc → Jak zacząć (kreator).";
            else if (found.Count == 0)
                summary = "W wybranym folderze nie ma zdjęć JPG/TIFF ani filmów MP4/MOV" +
                          (recursive ? " (także w podfolderach)."
                              : folders.Any(_declinedSubfolders.Contains) ? " — są tylko w podfolderach; zaznacz „z podfolderami”."
                              : ".") +
                          (unsupported.Count > 0 ? $" Pominięte, nieobsługiwane formaty: {FormatUnsupported(unsupported)}." : "");
            else
                summary = $"Wczytano {found.Count} zdjęć" + (folders.Count > 1 ? $" z {folders.Count} folderów" : "") +
                          $"; {found.Count(i => !i.HasGps && !i.GpsError)} bez GPS, {found.Count(i => i.GpsFromDevice)} z GPS z aparatu." +
                          (unsupported.Count > 0 ? $" Pominięte (nieobsługiwane): {FormatUnsupported(unsupported)}." : "");
            StatusText.Text = summary +
                              (found.Count > 0 ? " Wybierz zdjęcie, kliknij miejsce na mapie i naciśnij Enter." : "");
            return summary;
        }
        catch (OperationCanceledException)
        {
            // przerwany folder nie zostaje na liście — inaczej wczytywałby się znowu przy następnym uruchomieniu
            var dropped = _folders.Except(_loadedFolders, StringComparer.OrdinalIgnoreCase).ToList();
            _folders.Clear();
            _folders.AddRange(_loadedFolders);
            SaveFolders();
            LoadThumbs(_items);
            StatusText.Text = "Przerwano wczytywanie" +
                              (dropped.Count > 0 ? $" — nie wczytano: {string.Join(", ", dropped.Select(Path.GetFileName))}" : "") +
                              (_items.Count > 0 ? "; lista zdjęć bez zmian." : ".");
            return null;
        }
        catch (Exception ex)
        {
            LoadThumbs(_items);
            StatusText.Text = "Błąd wczytywania folderu: " + ex.Message;
            MessageBox.Show(this, "Nie udało się wczytać folderu:\n\n" + ex.Message, "PhotoTag",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        finally
        {
            EndBusy();
        }
    }

    // ---------------------------------------------------------------- foldery referencyjne

    private async void AddRefFolderBtn_Click(object sender, RoutedEventArgs e) => await AddRefFolderAsync();

    private async Task<string?> AddRefFolderAsync()
    {
        if (PickFolder("Folder referencyjny — zdjęcia z GPS (np. z telefonu)", _settings.ReferenceFolders.LastOrDefault(),
                subfoldersOption: false)
            is not { } picked) return null;
        if (!_settings.ReferenceFolders.Contains(picked.Folder, StringComparer.OrdinalIgnoreCase))
            _settings.ReferenceFolders.Add(picked.Folder);
        SettingsService.Save(_settings);
        return await LoadRefFoldersAsync(picked.Folder);
    }

    private async void ClearRefFoldersBtn_Click(object sender, RoutedEventArgs e)
    {
        _settings.ReferenceFolders.Clear();
        SettingsService.Save(_settings);
        await LoadRefFoldersAsync();
    }

    /// <summary>
    /// Wczytuje zdjęcia z GPS z folderów referencyjnych (z podfolderami). Dla <paramref name="explain"/>
    /// (folder właśnie dodany) pokazuje, co znaleziono — i dlaczego mogą nie pojawić się podpowiedzi.
    /// </summary>
    private async Task<string?> LoadRefFoldersAsync(string? explain = null)
    {
        var folders = _settings.ReferenceFolders.Where(Directory.Exists).ToList();
        var ct = BeginBusy("Wyszukiwanie zdjęć w folderach referencyjnych…");
        try
        {
            var all = new List<PhotoItem>();
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            string? report = null;
            foreach (var folder in folders)
            {
                var unsupported = new Dictionary<string, int>();
                var files = await Task.Run(() => EnumerateMedia(folder, true, ct, unsupported), ct);
                files.RemoveAll(f => !paths.Add(f));
                var items = await ReadMediaAsync(files, $"Folder referencyjny {Path.GetFileName(folder)}", ct);
                var usable = items.Where(i => i.HasGps && !i.GpsError && i.HasValidDate).ToList();
                all.AddRange(usable);
                if (string.Equals(folder, explain, StringComparison.OrdinalIgnoreCase))
                    report = ExplainRefFolder(folder, items, usable, unsupported);
            }

            _refItems = all;
            foreach (var r in _refItems)
                r.ClockOffset = OffsetFor(r.Camera);

            RefInfo.Text = folders.Count == 0 ? "brak" : $"{folders.Count} folder(y), {_refItems.Count} zdjęć z GPS";
            RefInfo.ToolTip = folders.Count == 0 ? null : string.Join("\n", _settings.ReferenceFolders);
            var summary = folders.Count == 0
                ? "Brak folderów referencyjnych."
                : $"Foldery referencyjne: {_refItems.Count} zdjęć z GPS.";
            StatusText.Text = summary;
            RefreshPool();
            RefreshCameras();
            SendPhotosToMap();
            RefreshCurrent();

            if (report is not null)
                MessageBox.Show(this, report, "PhotoTag — folder referencyjny", MessageBoxButton.OK,
                    _refItems.Count == 0 ? MessageBoxImage.Warning : MessageBoxImage.Information);
            return explain is not null ? report?.Split('\n')[0] : summary;
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "Przerwano wczytywanie folderów referencyjnych.";
            return null;
        }
        catch (Exception ex)
        {
            StatusText.Text = "Błąd wczytywania folderów referencyjnych: " + ex.Message;
            return null;
        }
        finally
        {
            EndBusy();
        }
    }

    private string ExplainRefFolder(string folder, List<PhotoItem> items, List<PhotoItem> usable,
        Dictionary<string, int> unsupported)
    {
        var name = Path.GetFileName(folder);
        var skipped = unsupported.Count > 0 ? $"\n\nPominięte, nieobsługiwane pliki: {FormatUnsupported(unsupported)}." : "";
        var heic = unsupported.ContainsKey("HEIC") || unsupported.ContainsKey("HEIF")
            ? "\nZdjęcia HEIC z iPhone'a: zapisz je jako JPG (iPhone: Ustawienia → Aparat → Formaty → „Najbardziej zgodne”)."
            : "";

        if (items.Count == 0)
            return $"W folderze „{name}” (razem z podfolderami) nie ma zdjęć JPG/TIFF ani filmów MP4/MOV." + skipped + heic;

        var withGps = items.Count(i => i.HasGps && !i.GpsError);
        if (withGps == 0)
            return $"W folderze „{name}” jest {items.Count} zdjęć/filmów, ale żadne nie ma zapisanej lokalizacji GPS.\n\n" +
                   "Najczęstsze przyczyny:\n" +
                   "• lokalizacja wyłączona w aparacie telefonu,\n" +
                   "• zdjęcia przesłane komunikatorem (WhatsApp, Messenger) lub pobrane z Facebooka — usuwają GPS,\n" +
                   "• zrzuty ekranu i zdjęcia edytowane w programach, które nie zachowują EXIF." + skipped + heic;

        var text = $"Folder „{name}”: {usable.Count} zdjęć z GPS (z {items.Count} plików).";
        if (withGps > usable.Count)
            text += $" {withGps - usable.Count} pominięto — brak daty wykonania.";

        var dated = _items.Where(i => i.HasValidDate).ToList();
        if (dated.Count > 0)
        {
            var from = dated.Min(i => i.TimeUtc);
            var to = dated.Max(i => i.TimeUtc);
            var near = usable.Count(r => r.TimeUtc >= from.AddHours(-12) && r.TimeUtc <= to.AddHours(12));
            text += near > 0
                ? $"\n\n{near} z nich pochodzi z okresu wczytanych zdjęć — przy zdjęciach bez GPS pojawi się podpowiedź " +
                  "z najbliższego w czasie zdjęcia z telefonu (do 12 h różnicy), a punkty zobaczysz na mapie."
                : $"\n\nŻadne nie pochodzi z okresu wczytanych zdjęć ({from.ToLocalTime():dd.MM.yyyy} – {to.ToLocalTime():dd.MM.yyyy}), " +
                  "więc podpowiedzi się nie pojawią. Folder referencyjny pomaga tylko dla zdjęć zrobionych w tym samym czasie " +
                  "(do 12 h różnicy) — sprawdź też ustawienie zegara aparatu.";
        }
        else
        {
            text += "\n\nPodpowiedzi pojawią się przy zdjęciach bez GPS zrobionych w tym samym czasie (do 12 h różnicy).";
        }
        return text + skipped + heic;
    }
}
