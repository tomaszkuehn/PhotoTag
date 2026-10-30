using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace PhotoTag.Services;

public sealed record TrackPoint(DateTime Utc, double Lat, double Lng);

public sealed record TrackMatch(GeoPoint Point, double DeltaMinutes, TrackPoint? Before, TrackPoint? After);

/// <summary>
/// Ślady GPS: GPX, KML (gx:Track / Placemark z TimeStamp) i JSON z Google (Records.json,
/// Timeline.json z telefonu, Semantic Location History). Czas zawsze w UTC.
/// </summary>
public sealed class TrackStore
{
    public const double MaxInterpolationMinutes = 120;
    public const double MaxNearestMinutes = 10;

    private readonly List<TrackPoint> _points = new();

    public IReadOnlyList<TrackPoint> Points => _points;
    public int Count => _points.Count;

    public int Load(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        var loaded = ext switch
        {
            ".gpx" => ParseGpx(path),
            ".kml" => ParseKml(path),
            ".json" => ParseJson(path),
            _ => throw new NotSupportedException($"Nieobsługiwany format: {ext}"),
        };
        _points.AddRange(loaded);
        _points.Sort((a, b) => a.Utc.CompareTo(b.Utc));
        return loaded.Count;
    }

    public void Clear() => _points.Clear();

    public TrackMatch? Find(DateTime utc)
    {
        if (_points.Count == 0) return null;
        int lo = 0, hi = _points.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) / 2;
            if (_points[mid].Utc < utc) lo = mid + 1;
            else hi = mid - 1;
        }
        var after = lo < _points.Count ? _points[lo] : null;
        var before = lo > 0 ? _points[lo - 1] : null;

        if (before is not null && after is not null)
        {
            var span = (after.Utc - before.Utc).TotalMinutes;
            if (span <= MaxInterpolationMinutes)
            {
                var f = span <= 0 ? 0 : (utc - before.Utc).TotalMinutes / span;
                var p = new GeoPoint(before.Lat + (after.Lat - before.Lat) * f,
                                     before.Lng + (after.Lng - before.Lng) * f);
                var delta = Math.Min((utc - before.Utc).TotalMinutes, (after.Utc - utc).TotalMinutes);
                return new TrackMatch(p, delta, before, after);
            }
        }

        var nearest = new[] { before, after }
            .Where(x => x is not null)
            .OrderBy(x => Math.Abs((x!.Utc - utc).TotalMinutes))
            .FirstOrDefault();
        if (nearest is null) return null;
        var d = Math.Abs((nearest.Utc - utc).TotalMinutes);
        return d <= MaxNearestMinutes
            ? new TrackMatch(new GeoPoint(nearest.Lat, nearest.Lng), d, null, null)
            : null;
    }

    /// <summary>Punkt śladu najbliższy przestrzennie, opcjonalnie w oknie czasowym.</summary>
    public TrackPoint? NearestInSpace(GeoPoint p, DateTime? fromUtc = null, DateTime? toUtc = null)
    {
        TrackPoint? best = null;
        var bestD = double.MaxValue;
        var cosLat = Math.Cos(p.Lat * Math.PI / 180);
        foreach (var t in _points)
        {
            if (fromUtc is not null && t.Utc < fromUtc) continue;
            if (toUtc is not null && t.Utc > toUtc) break;
            var dx = (t.Lng - p.Lng) * cosLat;
            var dy = t.Lat - p.Lat;
            var d = dx * dx + dy * dy;
            if (d < bestD) { bestD = d; best = t; }
        }
        return best;
    }

    public IEnumerable<TrackPoint> Range(DateTime fromUtc, DateTime toUtc) =>
        _points.Where(t => t.Utc >= fromUtc && t.Utc <= toUtc);

    private static List<TrackPoint> ParseGpx(string path)
    {
        var doc = XDocument.Load(path);
        var list = new List<TrackPoint>();
        foreach (var el in doc.Descendants().Where(e => e.Name.LocalName is "trkpt" or "rtept" or "wpt"))
        {
            var time = el.Elements().FirstOrDefault(e => e.Name.LocalName == "time")?.Value;
            if (time is null || !TryParseTime(time, out var utc)) continue;
            if (double.TryParse((string?)el.Attribute("lat"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lat) &&
                double.TryParse((string?)el.Attribute("lon"), NumberStyles.Float, CultureInfo.InvariantCulture, out var lon))
                list.Add(new TrackPoint(utc, lat, lon));
        }
        return list;
    }

    private static List<TrackPoint> ParseKml(string path)
    {
        var doc = XDocument.Load(path);
        var list = new List<TrackPoint>();

        // gx:Track: naprzemiennie <when> i <gx:coord>lon lat alt</gx:coord>
        foreach (var track in doc.Descendants().Where(e => e.Name.LocalName == "Track"))
        {
            var whens = track.Elements().Where(e => e.Name.LocalName == "when").Select(e => e.Value).ToList();
            var coords = track.Elements().Where(e => e.Name.LocalName == "coord").Select(e => e.Value).ToList();
            for (int i = 0; i < Math.Min(whens.Count, coords.Count); i++)
            {
                var parts = coords[i].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2 && TryParseTime(whens[i], out var utc) &&
                    double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) &&
                    double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat))
                    list.Add(new TrackPoint(utc, lat, lon));
            }
        }

        // Placemark z TimeStamp/when lub TimeSpan/begin+end i Point/coordinates
        foreach (var pm in doc.Descendants().Where(e => e.Name.LocalName == "Placemark"))
        {
            var coord = pm.Descendants().FirstOrDefault(e => e.Name.LocalName == "Point")?
                .Descendants().FirstOrDefault(e => e.Name.LocalName == "coordinates")?.Value.Trim();
            if (coord is null) continue;
            var parts = coord.Split(',');
            if (parts.Length < 2 ||
                !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var lon) ||
                !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var lat))
                continue;
            foreach (var t in pm.Descendants().Where(e => e.Name.LocalName is "when" or "begin" or "end"))
                if (TryParseTime(t.Value, out var utc))
                    list.Add(new TrackPoint(utc, lat, lon));
        }
        return list;
    }

    private static List<TrackPoint> ParseJson(string path)
    {
        using var stream = File.OpenRead(path);
        using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { AllowTrailingCommas = true });
        var list = new List<TrackPoint>();
        Walk(doc.RootElement, list, 0);
        return list;
    }

    private static void Walk(JsonElement el, List<TrackPoint> list, int depth)
    {
        if (depth > 12) return;
        if (el.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in el.EnumerateArray())
                Walk(child, list, depth + 1);
            return;
        }
        if (el.ValueKind != JsonValueKind.Object) return;

        // Pobyt (wizyta): ta sama pozycja na początku i na końcu przedziału czasu.
        if (TryGetSpan(el, out var start, out var end) &&
            FindPosition(el, 4) is { } visitPos &&
            (el.TryGetProperty("visit", out _) || el.TryGetProperty("placeVisit", out _) ||
             el.TryGetProperty("location", out _) || el.TryGetProperty("duration", out _)))
        {
            list.Add(new TrackPoint(start, visitPos.Lat, visitPos.Lng));
            list.Add(new TrackPoint(end, visitPos.Lat, visitPos.Lng));
        }

        if (TryGetTime(el, out var utc) && DirectPosition(el) is { } pos)
            list.Add(new TrackPoint(utc, pos.Lat, pos.Lng));

        foreach (var prop in el.EnumerateObject())
            if (prop.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array)
                Walk(prop.Value, list, depth + 1);
    }

    private static bool TryGetTime(JsonElement el, out DateTime utc)
    {
        utc = default;
        foreach (var name in new[] { "time", "timestamp", "timestampMs", "when" })
        {
            if (!el.TryGetProperty(name, out var v)) continue;
            if (v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString()!;
                if (name == "timestampMs" && long.TryParse(s, out var ms))
                {
                    utc = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
                    return true;
                }
                if (TryParseTime(s, out utc)) return true;
            }
            else if (v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n))
            {
                utc = n > 100_000_000_000
                    ? DateTimeOffset.FromUnixTimeMilliseconds(n).UtcDateTime
                    : DateTimeOffset.FromUnixTimeSeconds(n).UtcDateTime;
                return true;
            }
        }
        return false;
    }

    private static bool TryGetSpan(JsonElement el, out DateTime start, out DateTime end)
    {
        start = end = default;
        var src = el.TryGetProperty("duration", out var dur) && dur.ValueKind == JsonValueKind.Object ? dur : el;
        string? s = null, e = null;
        foreach (var n in new[] { "startTime", "startTimestamp" })
            if (src.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String) s = v.GetString();
        foreach (var n in new[] { "endTime", "endTimestamp" })
            if (src.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String) e = v.GetString();
        return s is not null && e is not null && TryParseTime(s, out start) && TryParseTime(e, out end);
    }

    private static GeoPoint? FindPosition(JsonElement el, int depth)
    {
        if (DirectPosition(el) is { } p) return p;
        if (depth == 0 || el.ValueKind != JsonValueKind.Object) return null;
        foreach (var name in new[] { "visit", "placeVisit", "topCandidate", "placeLocation", "location" })
            if (el.TryGetProperty(name, out var child) && child.ValueKind == JsonValueKind.Object &&
                FindPosition(child, depth - 1) is { } found)
                return found;
        return null;
    }

    private static readonly Regex LatLngText = new(
        @"(?:geo:)?\s*(-?\d+(?:\.\d+)?)\s*°?\s*,\s*(-?\d+(?:\.\d+)?)\s*°?", RegexOptions.Compiled);

    private static GeoPoint? DirectPosition(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object) return null;
        if (el.TryGetProperty("latitudeE7", out var la7) && el.TryGetProperty("longitudeE7", out var lo7) &&
            la7.TryGetInt64(out var a) && lo7.TryGetInt64(out var b))
            return Valid(a / 1e7, b / 1e7);
        foreach (var (latName, lngName) in new[] { ("lat", "lng"), ("lat", "lon"), ("latitude", "longitude") })
            if (el.TryGetProperty(latName, out var lv) && el.TryGetProperty(lngName, out var gv) &&
                lv.ValueKind == JsonValueKind.Number && gv.ValueKind == JsonValueKind.Number)
                return Valid(lv.GetDouble(), gv.GetDouble());
        foreach (var name in new[] { "point", "latLng", "LatLng", "latlng" })
            if (el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String &&
                LatLngText.Match(v.GetString()!) is { Success: true } m)
                return Valid(double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture),
                             double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture));
        return null;
    }

    private static GeoPoint? Valid(double lat, double lng) =>
        lat is >= -90 and <= 90 && lng is >= -180 and <= 180 && !(lat == 0 && lng == 0)
            ? new GeoPoint(lat, lng)
            : null;

    private static bool TryParseTime(string s, out DateTime utc)
    {
        if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dto))
        {
            utc = dto.UtcDateTime;
            return true;
        }
        utc = default;
        return false;
    }
}
