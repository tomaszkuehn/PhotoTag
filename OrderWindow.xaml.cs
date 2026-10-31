using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using PhotoTag.Models;
using PhotoTag.Services;

namespace PhotoTag;

/// <summary>
/// Osobny widok do układania kolejności: duże miniatury bez dat i lokalizacji,
/// przeciągnięcie zdjęcia ustawia mu czas obok sąsiada (MainWindow.PlaceInTimeline).
/// </summary>
public partial class OrderWindow : Window
{
    private const string DragFormat = "PhotoTag.Tile";
    private const int DecodeWidth = 320;

    private readonly MainWindow _main;
    private readonly ObservableCollection<Tile> _tiles = new();
    private readonly SemaphoreSlim _loadLock = new(4, 4);
    private Point _dragStart;
    private Tile? _dragTile;
    private InsertionAdorner? _insertion;
    private bool _closed;

    public OrderWindow(MainWindow main, PhotoItem? focus)
    {
        InitializeComponent();
        _main = main;
        Tiles.ItemsSource = _tiles;
        foreach (var item in main.Photos)
            _tiles.Add(new Tile(item));
        foreach (var tile in _tiles)
            _ = LoadImageAsync(tile);
        UpdateUndo();

        PreviewKeyDown += OnPreviewKeyDown;
        Loaded += (_, _) =>
        {
            if (_tiles.FirstOrDefault(t => ReferenceEquals(t.Item, focus)) is { } f)
            {
                Tiles.SelectedItem = f;
                Tiles.ScrollIntoView(f);
            }
        };
        Closed += (_, _) => _closed = true;
    }

    private async Task LoadImageAsync(Tile tile)
    {
        await _loadLock.WaitAsync();
        try
        {
            if (_closed) return;
            tile.Image = await Task.Run(() => Imaging.LoadBitmap(tile.Item.Path, DecodeWidth, tile.Item.Orientation));
        }
        catch
        {
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>
    /// Ustawia kafelki w kolejności listy głównego okna (Move — bez ponownego ładowania obrazów).
    /// Po zapisie lokalizacji lista jest wczytywana od nowa (nowe obiekty, plik …_LOCPT),
    /// więc kafelki dopasowuje ścieżka bez „_LOCPT”.
    /// </summary>
    private void SyncOrder()
    {
        var order = _main.Photos;
        var byKey = new Dictionary<string, Tile>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in _tiles)
            byKey.TryAdd(TileKey(t.Item), t);
        var used = new HashSet<Tile>();
        for (int i = 0; i < order.Count; i++)
        {
            if (!byKey.TryGetValue(TileKey(order[i]), out var tile) || !used.Add(tile))
            {
                tile = new Tile(order[i]);
                used.Add(tile);
                _tiles.Insert(Math.Min(i, _tiles.Count), tile);
                _ = LoadImageAsync(tile);
                continue;
            }
            tile.Item = order[i];
            var current = _tiles.IndexOf(tile);
            if (current != i)
                _tiles.Move(current, i);
        }
        for (int i = _tiles.Count - 1; i >= 0; i--)
            if (!used.Contains(_tiles[i]))
                _tiles.RemoveAt(i);
        UpdateUndo();
    }

    private static string TileKey(PhotoItem item)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(item.Path);
        if (stem.EndsWith("_LOCPT", StringComparison.OrdinalIgnoreCase))
            stem = stem[..^"_LOCPT".Length];
        return System.IO.Path.Combine(System.IO.Path.GetDirectoryName(item.Path)!, stem + System.IO.Path.GetExtension(item.Path));
    }

    private void UpdateUndo()
    {
        var n = _main.TimeUndoCount;
        UndoButton.IsEnabled = n > 0;
        UndoButton.Content = n > 0 ? $"Cofnij (Ctrl+Z) · {n}" : "Cofnij (Ctrl+Z)";
    }

    private void Undo()
    {
        if (!_main.UndoTimeMove()) return;
        var selected = (Tiles.SelectedItem as Tile)?.Item;
        SyncOrder();
        if (selected is not null && _tiles.FirstOrDefault(t => ReferenceEquals(t.Item, selected)) is { } s)
            Tiles.ScrollIntoView(s);
    }

    private void UndoButton_Click(object sender, RoutedEventArgs e) => Undo();

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Undo();
            e.Handled = true;
        }
    }

    // ---------------------------------------------------------------- przeciąganie

    private void Tiles_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragTile = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject)?.DataContext as Tile;
        _dragStart = e.GetPosition(Tiles);
    }

    private void Tiles_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragTile is null || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(Tiles) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var tile = _dragTile;
        _dragTile = null;
        Tiles.SelectedItem = tile;
        try
        {
            DragDrop.DoDragDrop(Tiles, new DataObject(DragFormat, tile), DragDropEffects.Move);
        }
        finally
        {
            RemoveInsertion();
        }
    }

    private void Tiles_DragOver(object sender, DragEventArgs e)
    {
        AutoScroll(e.GetPosition(Tiles).Y);
        if (DropTarget(e) is { } target)
        {
            e.Effects = DragDropEffects.Move;
            if (target.Container is not null)
                ShowInsertion(target.Container, target.After);
            else
                RemoveInsertion();
        }
        else
        {
            e.Effects = DragDropEffects.None;
            RemoveInsertion();
        }
        e.Handled = true;
    }

    private void Tiles_DragLeave(object sender, DragEventArgs e) => RemoveInsertion();

    private void Tiles_Drop(object sender, DragEventArgs e)
    {
        RemoveInsertion();
        e.Handled = true;
        if (DropTarget(e) is not { } target || e.Data.GetData(DragFormat) is not Tile moving) return;

        // obok zdjęcia bez daty nie da się wyznaczyć czasu — wtedy na koniec osi czasu
        var anchor = target.Anchor;
        var after = target.After;
        if (!anchor.HasValidDate)
        {
            var lastDated = _main.Photos.LastOrDefault(i => i.HasValidDate && !ReferenceEquals(i, moving.Item));
            if (lastDated is null) return;
            anchor = lastDated;
            after = true;
        }

        var moved = new List<PhotoItem> { moving.Item };
        if (!_main.PlaceInTimeline(moved, anchor, after)) return;
        SyncOrder();
        Tiles.SelectedItem = moving;
        Tiles.ScrollIntoView(moving);

        // pytanie o pozycję sąsiada dopiero po zakończeniu przeciągania (okno modalne w trakcie DoDragDrop)
        Dispatcher.InvokeAsync(async () =>
        {
            await _main.OfferNeighborLocationAsync(moved, this);
            if (_closed) return;
            SyncOrder();
            Tiles.SelectedItem = moving;
            Tiles.ScrollIntoView(moving);
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Kafelek, przed/po którym wstawić (Container null = puste miejsce za ostatnim).</summary>
    private (PhotoItem Anchor, ListBoxItem? Container, bool After)? DropTarget(DragEventArgs e)
    {
        if (e.Data.GetData(DragFormat) is not Tile moving) return null;
        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is Tile tile)
        {
            if (ReferenceEquals(tile, moving)) return null;
            var after = e.GetPosition(container).X > container.ActualWidth / 2;
            return (tile.Item, container, after);
        }

        // puste miejsce: za ostatnim kafelkiem
        var last = _tiles.LastOrDefault(t => !ReferenceEquals(t, moving));
        return last is null ? null : (last.Item, null, true);
    }

    private void AutoScroll(double y)
    {
        if (FindDescendant<ScrollViewer>(Tiles) is not { } sv) return;
        const double edge = 40;
        if (y < edge) sv.ScrollToVerticalOffset(sv.VerticalOffset - 20);
        else if (y > Tiles.ActualHeight - edge) sv.ScrollToVerticalOffset(sv.VerticalOffset + 20);
    }

    private void ShowInsertion(UIElement container, bool after)
    {
        if (_insertion is not null && ReferenceEquals(_insertion.AdornedElement, container))
        {
            _insertion.After = after;
            return;
        }
        RemoveInsertion();
        if (AdornerLayer.GetAdornerLayer(container) is not { } layer) return;
        _insertion = new InsertionAdorner(container) { After = after };
        layer.Add(_insertion);
    }

    private void RemoveInsertion()
    {
        if (_insertion is null) return;
        AdornerLayer.GetAdornerLayer(_insertion.AdornedElement)?.Remove(_insertion);
        _insertion = null;
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null and not T)
            d = d is Visual ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        return d as T;
    }

    private static T? FindDescendant<T>(DependencyObject d) where T : DependencyObject
    {
        for (int n = 0; n < VisualTreeHelper.GetChildrenCount(d); n++)
        {
            var child = VisualTreeHelper.GetChild(d, n);
            if (child is T t) return t;
            if (FindDescendant<T>(child) is { } found) return found;
        }
        return null;
    }

    private sealed class Tile : INotifyPropertyChanged
    {
        private ImageSource? _image;
        private PhotoItem _item;

        public Tile(PhotoItem item) => _item = item;

        public PhotoItem Item
        {
            get => _item;
            set
            {
                if (ReferenceEquals(_item, value)) return;
                _item = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Item)));
            }
        }

        public ImageSource? Image
        {
            get => _image;
            set
            {
                _image = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;
    }

    /// <summary>Pionowa linia wskazująca miejsce wstawienia (z lewej lub prawej strony kafelka).</summary>
    private sealed class InsertionAdorner : Adorner
    {
        private static readonly Pen LinePen = CreatePen();
        private bool _after;

        public InsertionAdorner(UIElement adorned) : base(adorned) => IsHitTestVisible = false;

        public bool After
        {
            get => _after;
            set
            {
                if (_after == value) return;
                _after = value;
                InvalidateVisual();
            }
        }

        protected override void OnRender(DrawingContext dc)
        {
            var size = AdornedElement.RenderSize;
            var x = _after ? size.Width - 1 : 1;
            dc.DrawLine(LinePen, new Point(x, 2), new Point(x, size.Height - 2));
        }

        private static Pen CreatePen()
        {
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xD6, 0x33, 0x6C)), 5);
            pen.Freeze();
            return pen;
        }
    }
}
