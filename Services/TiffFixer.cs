using System.IO;

namespace PhotoTag.Services;

/// <summary>
/// Poprawki po zapisie przez ExifLibNet, bez których część czytników (galerie w telefonach) nie widzi GPS:
/// sortowanie wpisów IFD wg numeru tagu (wymóg TIFF), poprawny GPSVersionID (BYTE[4] = 2.3.0.0)
/// i współrzędne jako d/1, m/1, s·10000/10000 zamiast przybliżeń z ogromnymi mianownikami.
/// Zmienia bajty w miejscu — rozmiar pliku i przesunięcia danych bez zmian.
/// </summary>
internal static class TiffFixer
{
    private const ushort ExifIfdTag = 0x8769;
    private const ushort GpsIfdTag = 0x8825;
    private const ushort InteropIfdTag = 0xA005;
    private const ushort TypeByte = 1;
    private const ushort TypeRational = 5;

    public static void Fix(string path, double lat, double lng)
    {
        var data = File.ReadAllBytes(path);
        var (start, end) = FindTiff(data);
        if (start < 0) return;
        if (new Fixer(data, start, end, lat, lng).Run())
            File.WriteAllBytes(path, data);
    }

    /// <summary>Początek i koniec bloku TIFF: cały plik .tif albo segment APP1 „Exif” w JPEG.</summary>
    private static (int Start, int End) FindTiff(byte[] d)
    {
        if (d.Length > 8 && ((d[0] == 'I' && d[1] == 'I') || (d[0] == 'M' && d[1] == 'M')))
            return (0, d.Length);
        if (d.Length < 4 || d[0] != 0xFF || d[1] != 0xD8) return (-1, -1);
        var pos = 2;
        while (pos + 4 <= d.Length && d[pos] == 0xFF)
        {
            var marker = d[pos + 1];
            if (marker is 0xDA or 0xD9) break;
            var len = (d[pos + 2] << 8) | d[pos + 3];
            if (marker == 0xE1 && pos + 10 <= d.Length &&
                d[pos + 4] == 'E' && d[pos + 5] == 'x' && d[pos + 6] == 'i' && d[pos + 7] == 'f' && d[pos + 8] == 0 && d[pos + 9] == 0)
                return (pos + 10, Math.Min(d.Length, pos + 2 + len));
            pos += 2 + len;
        }
        return (-1, -1);
    }

    private sealed class Fixer
    {
        private readonly byte[] _d;
        private readonly int _t;
        private readonly int _end;
        private readonly bool _little;
        private readonly double _lat;
        private readonly double _lng;
        private readonly HashSet<int> _visited = new();

        public Fixer(byte[] d, int t, int end, double lat, double lng)
        {
            _d = d;
            _t = t;
            _end = end;
            _little = d[t] == 'I';
            _lat = lat;
            _lng = lng;
        }

        public bool Run()
        {
            if (_t + 8 > _end) return false;
            var ifd0 = (int)U32(_t + 4);
            var next = FixIfd(ifd0, gps: false);
            if (next > 0) FixIfd(next, gps: false); // IFD1 (miniatura)
            return _visited.Count > 0;
        }

        /// <summary>Sortuje wpisy IFD, poprawia GPS; zwraca przesunięcie następnego IFD (0 = brak).</summary>
        private int FixIfd(int offset, bool gps)
        {
            var abs = _t + offset;
            if (offset <= 0 || abs + 2 > _end || !_visited.Add(offset)) return 0;
            int n = U16(abs);
            if (n == 0 || abs + 2 + n * 12 + 4 > _end) return 0;

            var entries = new List<byte[]>(n);
            for (int i = 0; i < n; i++)
                entries.Add(_d[(abs + 2 + i * 12)..(abs + 2 + i * 12 + 12)]);
            entries = entries.OrderBy(e => Tag(e)).ToList();
            for (int i = 0; i < n; i++)
                entries[i].CopyTo(_d, abs + 2 + i * 12);

            for (int i = 0; i < n; i++)
            {
                var e = abs + 2 + i * 12;
                var tag = U16(e);
                if (tag is ExifIfdTag or InteropIfdTag)
                    FixIfd((int)U32(e + 8), gps: false);
                else if (tag == GpsIfdTag)
                    FixIfd((int)U32(e + 8), gps: true);
                else if (gps)
                    FixGpsEntry(e, tag);
            }
            return (int)U32(abs + 2 + n * 12);
        }

        private void FixGpsEntry(int e, ushort tag)
        {
            if (tag == 0)
            {
                // GPSVersionID: BYTE[4] = 2.3.0.0 (mieści się w polu wartości)
                W16(e + 2, TypeByte);
                W32(e + 4, 4);
                _d[e + 8] = 2;
                _d[e + 9] = 3;
                _d[e + 10] = 0;
                _d[e + 11] = 0;
            }
            else if (tag is 2 or 4 && U16(e + 2) == TypeRational && U32(e + 4) == 3)
            {
                var data = _t + (int)U32(e + 8);
                if (data < _t || data + 24 > _end) return;
                var (deg, min, sec) = Dms(tag == 2 ? _lat : _lng);
                W32(data, deg);
                W32(data + 4, 1);
                W32(data + 8, min);
                W32(data + 12, 1);
                W32(data + 16, sec);
                W32(data + 20, 10000);
            }
        }

        private static (uint Deg, uint Min, uint SecX10000) Dms(double value)
        {
            value = Math.Abs(value);
            var deg = (uint)Math.Floor(value);
            var minutes = (value - deg) * 60;
            var min = (uint)Math.Floor(minutes);
            var sec = (uint)Math.Round((minutes - min) * 60 * 10000);
            if (sec >= 600000) { sec -= 600000; min++; }
            if (min >= 60) { min -= 60; deg++; }
            return (deg, min, sec);
        }

        private ushort Tag(byte[] e) => _little ? (ushort)(e[0] | (e[1] << 8)) : (ushort)((e[0] << 8) | e[1]);

        private ushort U16(int p) => _little ? (ushort)(_d[p] | (_d[p + 1] << 8)) : (ushort)((_d[p] << 8) | _d[p + 1]);

        private uint U32(int p) => _little
            ? (uint)(_d[p] | (_d[p + 1] << 8) | (_d[p + 2] << 16) | (_d[p + 3] << 24))
            : (uint)((_d[p] << 24) | (_d[p + 1] << 16) | (_d[p + 2] << 8) | _d[p + 3]);

        private void W16(int p, ushort v)
        {
            if (_little) { _d[p] = (byte)v; _d[p + 1] = (byte)(v >> 8); }
            else { _d[p] = (byte)(v >> 8); _d[p + 1] = (byte)v; }
        }

        private void W32(int p, uint v)
        {
            if (_little)
            {
                _d[p] = (byte)v; _d[p + 1] = (byte)(v >> 8); _d[p + 2] = (byte)(v >> 16); _d[p + 3] = (byte)(v >> 24);
            }
            else
            {
                _d[p] = (byte)(v >> 24); _d[p + 1] = (byte)(v >> 16); _d[p + 2] = (byte)(v >> 8); _d[p + 3] = (byte)v;
            }
        }
    }
}
