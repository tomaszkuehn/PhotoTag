using System.Buffers.Binary;
using System.IO;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace PhotoTag.Services;

/// <summary>
/// Metadane filmów MP4/MOV (ISO BMFF / QuickTime): czas z <c>mvhd</c> (lub klucza Apple creationdate),
/// lokalizacja z <c>udta/©xyz</c> lub klucza Apple <c>location.ISO6709</c>; zapis obu bez przekodowania.
/// </summary>
public static class VideoService
{
    private const string AppleLocationKey = "com.apple.quicktime.location.ISO6709";
    private const string AppleCreationKey = "com.apple.quicktime.creationdate";
    private const string AppleMakeKey = "com.apple.quicktime.make";
    private const string AppleModelKey = "com.apple.quicktime.model";

    private static readonly DateTime Epoch1904 = new(1904, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly string[] Containers = { "moov", "trak", "mdia", "minf", "stbl", "udta", "meta", "ilst" };
    private static readonly Regex Iso6709 = new(@"^([+-]\d+(?:\.\d+)?)([+-]\d+(?:\.\d+)?)", RegexOptions.Compiled);

    // typy „©xyz”, „©mak”, „©mod” (pierwszy bajt 0xA9; nazwy typów czytane jako Latin-1)
    private const string XyzType = "©xyz";
    private const string MakeType = "©mak";
    private const string ToolType = "©too";
    private const string ModelType = "©mod";

    public static bool Supports(string extension) =>
        extension.ToLowerInvariant() is ".mp4" or ".mov" or ".m4v" or ".3gp";

    public static bool IsVideo(string path) => Supports(System.IO.Path.GetExtension(path));

    public static PhotoExif ReadInfo(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var moov = ReadMoov(fs) ?? throw new InvalidDataException("Brak atomu moov.");
            var keys = AppleKeys(moov);

            DateTime? date = null;
            TimeSpan? offset = null;
            if (keys.TryGetValue(AppleCreationKey, out var created) && ParseAppleDate(created) is { } dto)
            {
                date = DateTime.SpecifyKind(dto.DateTime, DateTimeKind.Unspecified);
                offset = dto.Offset;
            }
            else if (moov.Find("mvhd") is { } mvhd && ReadCreationUtc(mvhd.Data!) is { } utc)
            {
                // mvhd jest w UTC; bez strefy w pliku — strefa systemowa (jak w zdjęciach bez OffsetTime)
                date = DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeFromUtc(utc, TimeZoneInfo.Local), DateTimeKind.Unspecified);
            }

            var iso = keys.GetValueOrDefault(AppleLocationKey)
                      ?? (moov.Find("udta")?.Find(XyzType) is { } xyz ? ReadQtString(xyz.Data!) : null);
            var gps = ParseIso6709(iso);

            var make = keys.GetValueOrDefault(AppleMakeKey) ?? (moov.Find("udta")?.Find(MakeType) is { } mk ? ReadQtString(mk.Data!) : null);
            var model = keys.GetValueOrDefault(AppleModelKey) ?? (moov.Find("udta")?.Find(ModelType) is { } md ? ReadQtString(md.Data!) : null);
            var camera = string.IsNullOrWhiteSpace(model) ? (string.IsNullOrWhiteSpace(make) ? null : make.Trim())
                : string.IsNullOrWhiteSpace(make) || model.StartsWith(make, StringComparison.OrdinalIgnoreCase) ? model.Trim()
                : $"{make.Trim()} {model.Trim()}";

            var source = gps is null ? GpsSource.None
                : System.IO.Path.GetFileNameWithoutExtension(path).EndsWith("_LOCPT", StringComparison.OrdinalIgnoreCase)
                    ? GpsSource.Added
                    : GpsSource.Device;
            var timeWritten = moov.Find("udta")?.Find(ToolType) is { } too && ReadQtString(too.Data!) == ExifService.TimeWrittenMarker;
            return new PhotoExif(gps, 1, date, offset, camera, null, source, false, timeWritten);
        }
        catch
        {
            return new PhotoExif(null, 1, null, null, null, null, GpsSource.None, true);
        }
    }

    /// <summary>
    /// Kopiuje film do <paramref name="target"/> z nową lokalizacją i (opcjonalnie) czasem.
    /// Zmienia tylko atom moov; dane obrazu i dźwięku są kopiowane bez zmian (przesunięcia stco/co64 korygowane).
    /// </summary>
    public static void WriteMetadata(string source, string target, double lat, double lng, DateTime? utc, DateTime? local, TimeSpan? offset)
    {
        using var input = File.OpenRead(source);
        var top = ReadTopLevel(input);
        var moovHeader = top.FirstOrDefault(b => b.Type == "moov") ?? throw new InvalidDataException("Brak atomu moov.");
        if (moovHeader.Size > 256L * 1024 * 1024) throw new InvalidDataException("Atom moov jest zbyt duży.");

        input.Position = moovHeader.Offset + moovHeader.HeaderSize;
        var moovData = new byte[moovHeader.Size - moovHeader.HeaderSize];
        input.ReadExactly(moovData);
        var moov = new Box("moov") { Children = ParseChildren(moovData, 0, moovData.Length, "moov", out var trailer) };
        moov.Trailer = trailer;
        if (moov.Find("mvex") is not null)
            throw new NotSupportedException("Pofragmentowane pliki MP4 nie są obsługiwane.");

        var iso = FormatIso6709(lat, lng);
        SetQtString(moov, XyzType, iso);
        var metas = KeysMetas(moov);
        var withLocation = metas.Where(m => ReadKeyNames(m.Find("keys")!.Data!).Contains(AppleLocationKey)).ToList();
        foreach (var m in withLocation.Count > 0 ? withLocation : metas.Take(1))
            SetAppleKey(m, AppleLocationKey, iso, addIfMissing: true);

        if (utc is { } u)
        {
            foreach (var box in moov.All().Where(b => b.Type is "mvhd" or "tkhd" or "mdhd"))
                SetCreationUtc(box.Data!, u);
            if (local is { } l)
            {
                var off = offset ?? TimeZoneInfo.Local.GetUtcOffset(u);
                foreach (var m in metas)
                    SetAppleKey(m, AppleCreationKey, FormatAppleDate(l, off), addIfMissing: false);
            }
            SetQtString(moov, ToolType, ExifService.TimeWrittenMarker);
        }

        // chunk offsets wskazujące za stary moov przesuwają się o różnicę rozmiaru
        var newSize = moov.TotalSize();
        var delta = newSize - moovHeader.Size;
        var moovEnd = moovHeader.Offset + moovHeader.Size;
        if (delta != 0)
            ShiftChunkOffsets(moov, moovEnd, delta);

        using var output = File.Create(target);
        foreach (var b in top)
        {
            if (ReferenceEquals(b, moovHeader))
            {
                var bytes = new byte[newSize];
                moov.WriteTo(bytes, 0);
                output.Write(bytes);
            }
            else
            {
                input.Position = b.Offset;
                CopyBytes(input, output, b.Size);
            }
        }
    }

    // ---------------------------------------------------------------- odczyt struktury

    private sealed record TopBox(string Type, long Offset, long Size, int HeaderSize);

    private static List<TopBox> ReadTopLevel(Stream s)
    {
        var list = new List<TopBox>();
        var header = new byte[16];
        long pos = 0, length = s.Length;
        while (pos + 8 <= length)
        {
            s.Position = pos;
            s.ReadExactly(header, 0, 8);
            long size = BinaryPrimitives.ReadUInt32BigEndian(header);
            var type = Latin1(header, 4);
            var headerSize = 8;
            if (size == 1)
            {
                s.ReadExactly(header, 8, 8);
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(header.AsSpan(8));
                headerSize = 16;
            }
            else if (size == 0)
                size = length - pos;
            if (size < headerSize || pos + size > length)
            {
                // uszkodzona końcówka — kopiujemy resztę jako całość
                list.Add(new TopBox("????", pos, length - pos, 0));
                break;
            }
            list.Add(new TopBox(type, pos, size, headerSize));
            pos += size;
        }
        if (pos < length && (list.Count == 0 || list[^1].Offset + list[^1].Size < length))
            list.Add(new TopBox("????", pos, length - pos, 0));
        return list;
    }

    private static Box? ReadMoov(Stream s)
    {
        var moov = ReadTopLevel(s).FirstOrDefault(b => b.Type == "moov");
        if (moov is null || moov.Size > 256L * 1024 * 1024) return null;
        s.Position = moov.Offset + moov.HeaderSize;
        var data = new byte[moov.Size - moov.HeaderSize];
        s.ReadExactly(data);
        return new Box("moov") { Children = ParseChildren(data, 0, data.Length, "moov", out _) };
    }

    private static List<Box> ParseChildren(byte[] data, int start, int end, string parent, out byte[]? trailer)
    {
        var list = new List<Box>();
        var pos = start;
        trailer = null;
        while (pos + 8 <= end)
        {
            var size = (long)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos));
            var type = Latin1(data, pos + 4);
            var headerSize = 8;
            if (size == 1 && pos + 16 <= end)
            {
                size = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(pos + 8));
                headerSize = 16;
            }
            else if (size == 0)
                size = end - pos;
            if (size < headerSize || pos + size > end) break;

            var box = new Box(type);
            var bodyStart = pos + headerSize;
            var bodyEnd = (int)(pos + size);
            // elementy ilst (np. klucz nr 1) zawierają atom „data”
            if (Array.IndexOf(Containers, type) >= 0 || parent == "ilst")
            {
                // meta w MP4 to „full box” (4 bajty wersji/flag), w QuickTime nie
                if (type == "meta" && bodyEnd - bodyStart >= 12 && Latin1(data, bodyStart + 8) == "hdlr"
                    && Latin1(data, bodyStart + 4) != "hdlr")
                {
                    box.Prefix = data[bodyStart..(bodyStart + 4)];
                    bodyStart += 4;
                }
                box.Children = ParseChildren(data, bodyStart, bodyEnd, type, out var t);
                box.Trailer = t;
            }
            else
                box.Data = data[bodyStart..bodyEnd];
            list.Add(box);
            pos = bodyEnd;
        }
        if (pos < end)
            trailer = data[pos..end];
        return list;
    }

    private sealed class Box
    {
        public Box(string type) => Type = type;

        public string Type { get; }
        public byte[]? Prefix { get; set; }
        public byte[]? Data { get; set; }
        public List<Box>? Children { get; set; }
        public byte[]? Trailer { get; set; }

        public Box? Find(string type) => Children?.FirstOrDefault(c => c.Type == type);

        public IEnumerable<Box> All()
        {
            yield return this;
            if (Children is null) yield break;
            foreach (var c in Children)
            foreach (var d in c.All())
                yield return d;
        }

        public long TotalSize() =>
            8 + (Prefix?.Length ?? 0) + (Data?.Length ?? 0) + (Children?.Sum(c => c.TotalSize()) ?? 0) + (Trailer?.Length ?? 0);

        public int WriteTo(byte[] buffer, int pos)
        {
            var size = TotalSize();
            if (size > uint.MaxValue) throw new InvalidDataException("Atom zbyt duży.");
            BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(pos), (uint)size);
            for (int i = 0; i < 4; i++) buffer[pos + 4 + i] = (byte)Type[i];
            var p = pos + 8;
            if (Prefix is not null) { Prefix.CopyTo(buffer, p); p += Prefix.Length; }
            if (Data is not null) { Data.CopyTo(buffer, p); p += Data.Length; }
            if (Children is not null)
                foreach (var c in Children)
                    p = c.WriteTo(buffer, p);
            if (Trailer is not null) { Trailer.CopyTo(buffer, p); p += Trailer.Length; }
            return p;
        }
    }

    // ---------------------------------------------------------------- czas

    private static DateTime? ReadCreationUtc(byte[] full)
    {
        if (full.Length < 12) return null;
        ulong seconds = full[0] == 1 && full.Length >= 20
            ? BinaryPrimitives.ReadUInt64BigEndian(full.AsSpan(4))
            : BinaryPrimitives.ReadUInt32BigEndian(full.AsSpan(4));
        if (seconds == 0) return null;
        var dt = Epoch1904.AddSeconds(seconds);
        return dt.Year is >= 1990 and <= 2100 ? dt : null;
    }

    private static void SetCreationUtc(byte[] full, DateTime utc)
    {
        var seconds = (ulong)Math.Max(0, (DateTime.SpecifyKind(utc, DateTimeKind.Utc) - Epoch1904).TotalSeconds);
        if (full[0] == 1 && full.Length >= 20)
            BinaryPrimitives.WriteUInt64BigEndian(full.AsSpan(4), seconds);
        else if (full.Length >= 12)
            BinaryPrimitives.WriteUInt32BigEndian(full.AsSpan(4), (uint)Math.Min(seconds, uint.MaxValue));
    }

    private static DateTimeOffset? ParseAppleDate(string text)
    {
        // „2023-07-15T14:30:00+0200” → dwukropek w strefie dla parsera
        text = Regex.Replace(text.Trim(), @"([+-]\d{2})(\d{2})$", "$1:$2");
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto) && dto.Year >= 1990
            ? dto
            : null;
    }

    private static string FormatAppleDate(DateTime local, TimeSpan offset) =>
        local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) +
        (offset < TimeSpan.Zero ? "-" : "+") + offset.Duration().ToString("hhmm", CultureInfo.InvariantCulture);

    // ---------------------------------------------------------------- lokalizacja

    private static GeoPoint? ParseIso6709(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var m = Iso6709.Match(text.Trim());
        if (!m.Success) return null;
        var lat = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        var lng = double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture);
        if (Math.Abs(lat) > 90 || Math.Abs(lng) > 180 || (lat == 0 && lng == 0)) return null;
        return new GeoPoint(lat, lng);
    }

    private static string FormatIso6709(double lat, double lng) =>
        lat.ToString("+00.000000;-00.000000", CultureInfo.InvariantCulture) +
        lng.ToString("+000.000000;-000.000000", CultureInfo.InvariantCulture) + "/";

    /// <summary>Tekst QuickTime w udta (©xyz, ©too…): u16 długość, u16 język, UTF-8.</summary>
    private static void SetQtString(Box moov, string type, string value)
    {
        var udta = moov.Find("udta");
        if (udta is null)
        {
            udta = new Box("udta") { Children = new List<Box>() };
            moov.Children!.Add(udta);
        }
        udta.Children ??= new List<Box>();
        var text = Encoding.UTF8.GetBytes(value);
        var data = new byte[4 + text.Length];
        BinaryPrimitives.WriteUInt16BigEndian(data, (ushort)text.Length);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(2), 0x15C7); // język „und”
        text.CopyTo(data, 4);
        var existing = udta.Find(type);
        if (existing is not null)
            existing.Data = data;
        else
            udta.Children.Insert(0, new Box(type) { Data = data });
    }

    private static string? ReadQtString(byte[] data)
    {
        if (data.Length < 4) return null;
        int len = BinaryPrimitives.ReadUInt16BigEndian(data);
        len = Math.Min(len, data.Length - 4);
        var s = Encoding.UTF8.GetString(data, 4, len).Trim('\0', ' ');
        return s.Length == 0 ? null : s;
    }

    // ---------------------------------------------------------------- klucze Apple (moov/meta/keys + ilst)

    private static Dictionary<string, string> AppleKeys(Box moov)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        // od ostatniej: wartości z moov/meta (iPhone) mają pierwszeństwo
        foreach (var meta in Enumerable.Reverse(KeysMetas(moov)))
            ReadKeys(meta, result);
        return result;
    }

    private static void ReadKeys(Box meta, Dictionary<string, string> result)
    {
        var keys = meta.Find("keys")?.Data;
        var ilst = meta.Find("ilst");
        if (keys is null || ilst?.Children is null) return;
        var names = ReadKeyNames(keys);
        foreach (var item in ilst.Children)
        {
            var index = (int)BinaryPrimitives.ReadUInt32BigEndian(Encoding.Latin1.GetBytes(item.Type)) - 1;
            if (index < 0 || index >= names.Count) continue;
            var data = item.Find("data")?.Data;
            if (data is null || data.Length < 8) continue;
            var type = BinaryPrimitives.ReadUInt32BigEndian(data) & 0xFFFFFF;
            if (type == 1) result[names[index]] = Encoding.UTF8.GetString(data, 8, data.Length - 8).Trim('\0');
        }
    }

    /// <summary>meta z kluczami (może być kilka): iPhone zapisuje ją w moov, ffmpeg i inne programy — w moov/udta.</summary>
    private static List<Box> KeysMetas(Box moov) =>
        new[] { moov.Find("meta"), moov.Find("udta")?.Find("meta") }
            .Where(m => m?.Find("keys")?.Data is not null)
            .Select(m => m!)
            .ToList();

    private static List<string> ReadKeyNames(byte[] keys)
    {
        var names = new List<string>();
        if (keys.Length < 8) return names;
        var count = BinaryPrimitives.ReadUInt32BigEndian(keys.AsSpan(4));
        var pos = 8;
        for (int i = 0; i < count && pos + 8 <= keys.Length; i++)
        {
            var size = (int)BinaryPrimitives.ReadUInt32BigEndian(keys.AsSpan(pos));
            if (size < 8 || pos + size > keys.Length) break;
            names.Add(Encoding.UTF8.GetString(keys, pos + 8, size - 8));
            pos += size;
        }
        return names;
    }

    private static void SetAppleKey(Box meta, string name, string value, bool addIfMissing)
    {
        var keysBox = meta.Find("keys");
        if (keysBox?.Data is null) return;
        var ilst = meta.Find("ilst");
        if (ilst is null)
        {
            if (!addIfMissing) return;
            ilst = new Box("ilst") { Children = new List<Box>() };
            meta.Children!.Add(ilst);
        }
        ilst.Children ??= new List<Box>();

        var names = ReadKeyNames(keysBox.Data);
        var index = names.IndexOf(name);
        if (index < 0)
        {
            if (!addIfMissing) return;
            var nameBytes = Encoding.UTF8.GetBytes(name);
            var entry = new byte[8 + nameBytes.Length];
            BinaryPrimitives.WriteUInt32BigEndian(entry, (uint)entry.Length);
            Encoding.ASCII.GetBytes("mdta").CopyTo(entry, 4);
            nameBytes.CopyTo(entry, 8);
            var updated = new byte[keysBox.Data.Length + entry.Length];
            keysBox.Data.CopyTo(updated, 0);
            entry.CopyTo(updated, keysBox.Data.Length);
            BinaryPrimitives.WriteUInt32BigEndian(updated.AsSpan(4), (uint)(names.Count + 1));
            keysBox.Data = updated;
            index = names.Count;
        }

        var valueBytes = Encoding.UTF8.GetBytes(value);
        var data = new byte[8 + valueBytes.Length];
        BinaryPrimitives.WriteUInt32BigEndian(data, 1); // UTF-8
        valueBytes.CopyTo(data, 8);

        var indexBytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(indexBytes, (uint)(index + 1));
        var itemType = Latin1(indexBytes, 0);
        var item = ilst.Children.FirstOrDefault(c => c.Type == itemType);
        if (item is null)
        {
            item = new Box(itemType) { Children = new List<Box>() };
            ilst.Children.Add(item);
        }
        item.Children ??= new List<Box>();
        item.Data = null;
        item.Children.RemoveAll(c => c.Type == "data");
        item.Children.Insert(0, new Box("data") { Data = data });
    }

    // ---------------------------------------------------------------- przesunięcia danych

    private static void ShiftChunkOffsets(Box moov, long moovEnd, long delta)
    {
        foreach (var box in moov.All())
        {
            if (box.Type == "stco" && box.Data is { Length: >= 8 } d32)
            {
                var n = BinaryPrimitives.ReadUInt32BigEndian(d32.AsSpan(4));
                for (int i = 0; i < n && 8 + i * 4 + 4 <= d32.Length; i++)
                {
                    var span = d32.AsSpan(8 + i * 4);
                    long v = BinaryPrimitives.ReadUInt32BigEndian(span);
                    if (v < moovEnd) continue;
                    v += delta;
                    if (v is < 0 or > uint.MaxValue) throw new InvalidDataException("Przesunięcie danych poza zakresem stco.");
                    BinaryPrimitives.WriteUInt32BigEndian(span, (uint)v);
                }
            }
            else if (box.Type == "co64" && box.Data is { Length: >= 8 } d64)
            {
                var n = BinaryPrimitives.ReadUInt32BigEndian(d64.AsSpan(4));
                for (int i = 0; i < n && 8 + i * 8 + 8 <= d64.Length; i++)
                {
                    var span = d64.AsSpan(8 + i * 8);
                    var v = (long)BinaryPrimitives.ReadUInt64BigEndian(span);
                    if (v < moovEnd) continue;
                    BinaryPrimitives.WriteUInt64BigEndian(span, (ulong)(v + delta));
                }
            }
        }
    }

    private static void CopyBytes(Stream input, Stream output, long count)
    {
        var buffer = new byte[1 << 20];
        while (count > 0)
        {
            var read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read <= 0) throw new EndOfStreamException();
            output.Write(buffer, 0, read);
            count -= read;
        }
    }

    private static string Latin1(byte[] data, int offset) => Encoding.Latin1.GetString(data, offset, 4);
}
