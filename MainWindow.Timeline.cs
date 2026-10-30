using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using PhotoTag.Models;
using PhotoTag.Services;

namespace PhotoTag;

/// <summary>Ręczne ustawianie czasu zdjęć: przeciąganie na liście, Alt+↑/↓, menu kontekstowe.</summary>
public partial class MainWindow
{
    private const string DragFormat = "PhotoTag.Items";
    private static readonly TimeSpan PlacementStep = TimeSpan.FromSeconds(1);

    private Point _dragStart;
    private PhotoItem? _dragItem;
    private bool _dragKeepsSelection;
    private InsertionAdorner? _insertion;

    // ---------------------------------------------------------------- umieszczanie na osi czasu

    /// <summary>
    /// Ustawia ręczny czas zdjęć <paramref name="moving"/> tuż przed/po <paramref name="anchor"/>
    /// (krok 1 s; gdy brak miejsca do sąsiedniego zdjęcia — równomiernie pomiędzy).
    /// </summary>
    private void PlaceInTimeline(List<PhotoItem> moving, PhotoItem anchor, bool after)
    {
        if (!anchor.HasValidDate)
        {
            StatusText.Text = "Nie można wstawić obok zdjęcia bez daty.";
            return;
        }

        var others = _items.Where(i => !moving.Contains(i) && i.HasValidDate).ToList();
        var idx = others.IndexOf(anchor);
        if (idx < 0) return;
        var k = moving.Count;

        DateTime lo, hi;
        DateTime? bound;
        if (after)
        {
            lo = anchor.TimeUtc;
            bound = idx + 1 < others.Count ? others[idx + 1].TimeUtc : null;
            hi = lo + PlacementStep * (k + 1);
            if (bound is { } b && hi > b) hi = b;
        }
        else
        {
            hi = anchor.TimeUtc;
            bound = idx > 0 ? others[idx - 1].TimeUtc : null;
            lo = hi - PlacementStep * (k + 1);
            if (bound is { } b && lo < b) lo = b;
        }
        var step = (hi - lo) / (k + 1);

        for (int j = 0; j < k; j++)
        {
            var item = moving[j];
            SetTimeOverride(item, item.LocalFor(lo + step * (j + 1)));
        }
        SettingsService.Save(_settings);

        AfterTimeChange(moving);
        StatusText.Text = (k == 1 ? $"{moving[0].FileName}: " : $"Przesunięto {k} zdjęć: ") +
                          $"czas ustawiony na {moving[0].Time:yyyy-MM-dd HH:mm:ss} " +
                          $"({(after ? "po" : "przed")} {anchor.FileName}). Przywrócenie: prawy klik → „Przywróć datę z EXIF”.";
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

    // ---------------------------------------------------------------- menu kontekstowe

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

    private void ResetTimeMenu_Click(object sender, RoutedEventArgs e)
    {
        var sel = SelectedPhotos().Where(i => i.TimeOverride is not null).ToList();
        if (sel.Count == 0)
        {
            StatusText.Text = "Zaznaczone zdjęcia nie mają ręcznie ustawionego czasu.";
            return;
        }
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

    // ---------------------------------------------------------------- przeciąganie na liście

    private void PhotoList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        _dragItem = null;
        _dragKeepsSelection = false;
        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is not PhotoItem item) return;

        _dragStart = e.GetPosition(PhotoList);
        _dragItem = item;
        // klik bez modyfikatorów w zaznaczoną grupę skasowałby zaznaczenie — przeciąga się całą grupę
        if (container.IsSelected && PhotoList.SelectedItems.Count > 1 && Keyboard.Modifiers == ModifierKeys.None)
        {
            _dragKeepsSelection = true;
            e.Handled = true;
        }
    }

    private void PhotoList_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragKeepsSelection && _dragItem is not null)
            MaintainSelection(_dragItem);
        _dragItem = null;
        _dragKeepsSelection = false;
    }

    private void PhotoList_PreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null || e.LeftButton != MouseButtonState.Pressed) return;
        var d = e.GetPosition(PhotoList) - _dragStart;
        if (Math.Abs(d.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(d.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        var moving = PhotoList.SelectedItems.Contains(_dragItem) ? SelectedPhotos() : new List<PhotoItem> { _dragItem };
        _dragItem = null;
        _dragKeepsSelection = false;
        try
        {
            DragDrop.DoDragDrop(PhotoList, new DataObject(DragFormat, moving), DragDropEffects.Move);
        }
        finally
        {
            RemoveInsertion();
        }
    }

    private void PhotoList_DragOver(object sender, DragEventArgs e)
    {
        AutoScroll(e.GetPosition(PhotoList).Y);
        var target = DropTarget(e);
        if (target is null)
        {
            e.Effects = DragDropEffects.None;
            RemoveInsertion();
        }
        else
        {
            e.Effects = DragDropEffects.Move;
            ShowInsertion(target.Value.Container, target.Value.After);
        }
        e.Handled = true;
    }

    private void PhotoList_DragLeave(object sender, DragEventArgs e) => RemoveInsertion();

    private void PhotoList_Drop(object sender, DragEventArgs e)
    {
        RemoveInsertion();
        if (DropTarget(e) is not { } target) return;
        var moving = (List<PhotoItem>)e.Data.GetData(DragFormat)!;
        PlaceInTimeline(moving, (PhotoItem)target.Container.DataContext, target.After);
        e.Handled = true;
    }

    /// <summary>Element, przed/po którym wstawić; null, gdy upuszczenie niemożliwe.</summary>
    private (ListBoxItem Container, bool After)? DropTarget(DragEventArgs e)
    {
        if (e.Data.GetData(DragFormat) is not List<PhotoItem> moving) return null;
        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (container?.DataContext is not PhotoItem anchor || moving.Contains(anchor) || !anchor.HasValidDate)
            return null;
        var after = e.GetPosition(container).Y > container.ActualHeight / 2;
        return (container, after);
    }

    private void AutoScroll(double y)
    {
        if (FindDescendant<ScrollViewer>(PhotoList) is not { } sv) return;
        const double edge = 30;
        if (y < edge) sv.LineUp();
        else if (y > PhotoList.ActualHeight - edge) sv.LineDown();
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

    /// <summary>Linia wskazująca miejsce wstawienia (nad lub pod elementem listy).</summary>
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
            var y = _after ? size.Height - 1 : 1;
            dc.DrawLine(LinePen, new Point(4, y), new Point(size.Width - 4, y));
        }

        private static Pen CreatePen()
        {
            var pen = new Pen(new SolidColorBrush(Color.FromRgb(0xD6, 0x33, 0x6C)), 3);
            pen.Freeze();
            return pen;
        }
    }
}
