using PhotoTag.Models;

namespace PhotoTag.Services;

/// <summary>
/// Szacuje położenie zdjęcia bez GPS na osi czasu z numeru w nazwie pliku (IMG_1234…):
/// aparat numeruje zdjęcia po kolei, więc zdjęcie leży między sąsiednimi numerami tego samego aparatu.
/// Używane, gdy zdjęcie nie ma daty EXIF (tylko data pliku lub brak) albo gdy jego data EXIF
/// wyraźnie odstaje od sąsiadów w numeracji (np. zegar aparatu zresetowany po wymianie baterii).
/// Szacunek służy tylko do kolejności i podpowiedzi lokalizacji — nie jest zapisywany do pliku.
/// </summary>
public static class SequenceEstimator
{
    /// <summary>Maksymalna różnica numerów między zdjęciem a sąsiadem z datą.</summary>
    private const int MaxGap = 50;
    /// <summary>Gdy sąsiad z datą jest tylko z jednej strony — musi być bardzo blisko w numeracji.</summary>
    private const int MaxEdgeGap = 5;
    /// <summary>Sąsiedzi dalej od siebie w czasie nie wyznaczają wiarygodnego przedziału.</summary>
    private static readonly TimeSpan MaxSpan = TimeSpan.FromHours(12);
    /// <summary>O tyle data EXIF musi wyjść poza przedział sąsiadów, by uznać ją za błędną.</summary>
    private static readonly TimeSpan OutlierMargin = TimeSpan.FromHours(1);

    private sealed record Anchor(PhotoItem Item, int Number, DateTime Time);

    /// <summary>Ustawia (lub czyści) szacunki dla wszystkich zdjęć; zwraca liczbę oszacowanych.</summary>
    public static int Apply(IReadOnlyCollection<PhotoItem> items)
    {
        foreach (var i in items)
            i.SetSequenceEstimate(null, null);

        var count = 0;
        // licznik ma ten sam przedrostek i liczbę cyfr; różne aparaty mogą używać tych samych nazw
        foreach (var group in items.Where(i => i.SeqNumber is not null).GroupBy(i => (i.SeqPrefix, i.SeqDigits)))
        {
            var all = group.ToList();
            var cameras = all.Where(i => i.Camera != PhotoItem.UnknownCamera && AnchorTime(i) is not null)
                .Select(i => i.Camera).Distinct().ToList();

            foreach (var camera in all.Select(i => i.Camera).Distinct())
            {
                var members = all.Where(i => i.Camera == camera).ToList();
                // zdjęcie bez danych aparatu (EXIF usunięty): sąsiedzi z aparatu, który jako jedyny używa tych nazw
                var source = camera == PhotoItem.UnknownCamera && cameras.Count == 1
                    ? all.Where(i => i.Camera == cameras[0] || i.Camera == camera).ToList()
                    : members;

                var anchors = source
                    .Select(i => AnchorTime(i) is { } t ? new Anchor(i, i.SeqNumber!.Value, t) : null)
                    .OfType<Anchor>()
                    .OrderBy(a => a.Number).ThenBy(a => a.Time)
                    .ToList();
                if (anchors.Count == 0) continue;

                var outliers = FindOutliers(anchors);
                var good = anchors.Where(a => !outliers.Contains(a.Item)).ToList();
                if (good.Count == 0) continue;
                var numbers = good.Select(a => a.Number).ToArray();

                foreach (var item in members)
                {
                    if (item.HasGps || item.TimeOverride is not null) continue;
                    if (item.DateFromExif && !outliers.Contains(item)) continue;
                    if (Estimate(item, good, numbers) is { } est)
                    {
                        item.SetSequenceEstimate(est.Time, est.Note);
                        count++;
                    }
                }
            }
        }
        return count;
    }

    /// <summary>Czas, któremu można ufać: ręczny albo z EXIF (po korekcie zegara).</summary>
    private static DateTime? AnchorTime(PhotoItem i) =>
        i.TimeOverride ?? (i.DateFromExif ? i.DateTaken + i.ClockOffset : null);

    /// <summary>Zdjęcia bez GPS, których data EXIF nie pasuje do zgodnych ze sobą sąsiadów w numeracji.</summary>
    private static HashSet<PhotoItem> FindOutliers(List<Anchor> anchors)
    {
        var result = new HashSet<PhotoItem>();
        for (int k = 1; k < anchors.Count - 1; k++)
        {
            var (a, b, c) = (anchors[k - 1], anchors[k], anchors[k + 1]);
            if (b.Item.HasGps || b.Item.TimeOverride is not null) continue;
            if (b.Number - a.Number > MaxGap || c.Number - b.Number > MaxGap) continue;
            if (c.Time < a.Time || c.Time - a.Time > MaxSpan) continue; // sąsiedzi sami niespójni
            if (b.Time < a.Time - OutlierMargin || b.Time > c.Time + OutlierMargin)
                result.Add(b.Item);
        }
        return result;
    }

    private static (DateTime Time, string Note)? Estimate(PhotoItem item, List<Anchor> good, int[] numbers)
    {
        var n = item.SeqNumber!.Value;
        var idx = Array.BinarySearch(numbers, n);
        if (idx >= 0)
        {
            // ten sam numer (np. kopia z datą EXIF w innym folderze)
            var same = good.Skip(idx).TakeWhile(a => a.Number == n).FirstOrDefault(a => !ReferenceEquals(a.Item, item))
                       ?? good.Take(idx).Reverse().TakeWhile(a => a.Number == n).FirstOrDefault(a => !ReferenceEquals(a.Item, item));
            if (same is not null)
                return (same.Time, $"szac. z numeru: jak {same.Item.FileName}");
            idx = ~idx; // tylko ono samo (data odrzucona) — szukaj sąsiadów obok
        }
        else
        {
            idx = ~idx;
        }

        var prev = good.Take(idx).LastOrDefault(a => a.Number < n && !ReferenceEquals(a.Item, item));
        var next = good.Skip(idx).FirstOrDefault(a => a.Number > n && !ReferenceEquals(a.Item, item));
        if (prev is not null && next is not null &&
            n - prev.Number <= MaxGap && next.Number - n <= MaxGap &&
            next.Time >= prev.Time && next.Time - prev.Time <= MaxSpan)
        {
            var f = (double)(n - prev.Number) / (next.Number - prev.Number);
            var t = prev.Time + TimeSpan.FromTicks((long)((next.Time - prev.Time).Ticks * f));
            return (t, $"szac. z numeru: {prev.Item.FileName} → {next.Item.FileName}");
        }
        if (prev is not null && n - prev.Number <= MaxEdgeGap &&
            (next is null || next.Number - n > MaxGap || next.Time < prev.Time || next.Time - prev.Time > MaxSpan))
            return (prev.Time, $"szac. z numeru: po {prev.Item.FileName}");
        if (next is not null && next.Number - n <= MaxEdgeGap &&
            (prev is null || n - prev.Number > MaxGap || next.Time < prev.Time || next.Time - prev.Time > MaxSpan))
            return (next.Time, $"szac. z numeru: przed {next.Item.FileName}");
        return null;
    }
}
