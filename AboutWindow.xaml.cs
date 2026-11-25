using System.IO;
using System.Reflection;
using System.Windows;

namespace PhotoTag;

/// <summary>Okno „O programie”: wersja, autor i licencja (dane z atrybutów zestawu, ustawianych w PhotoTag.csproj).</summary>
public partial class AboutWindow : Window
{
    private const string ThirdParty =
        "PhotoTag korzysta z następujących komponentów i usług:\n\n" +
        "• ExifLibNet — odczyt i zapis EXIF (licencja MIT)\n" +
        "• Microsoft Edge WebView2 — wyświetlanie mapy (licencja Microsoft, BSD dla SDK)\n" +
        "• Leaflet 1.9.4 — biblioteka map (licencja BSD 2-Clause)\n" +
        "• OpenStreetMap — dane i kafelki mapy © współtwórcy OpenStreetMap (ODbL)\n" +
        "• Nominatim — wyszukiwanie adresów (zasady korzystania OSMF; limit 1 zapytanie/s)\n" +
        "• Overpass API — wyszukiwanie ulic (dane OSM, ODbL)\n" +
        "• Esri World Imagery — zdjęcia satelitarne © Esri i dostawcy danych (warunki Esri)\n" +
        "• Google Street View — podgląd ulic (warunki korzystania z Google Maps)\n\n" +
        "Korzystanie z usług sieciowych podlega warunkom ich dostawców.";

    public static string Product => Attr<AssemblyProductAttribute>()?.Product ?? "PhotoTag";
    public static string Author => Attr<AssemblyCompanyAttribute>()?.Company ?? "";
    public static string Copyright => Attr<AssemblyCopyrightAttribute>()?.Copyright ?? "";

    /// <summary>Wersja bez sufiksu „+hash” dopisywanego przez SDK.</summary>
    public static string Version
    {
        get
        {
            var v = Attr<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                    ?? "?";
            var plus = v.IndexOf('+');
            return plus >= 0 ? v[..plus] : v;
        }
    }

    public AboutWindow()
    {
        InitializeComponent();
        ProductText.Text = Product;
        DescriptionText.Text = Attr<AssemblyDescriptionAttribute>()?.Description ?? "";
        VersionText.Text = Version;
        AuthorText.Text = Author;
        CopyrightText.Text = Copyright;
        LicenseBox.Text = LoadLicense();
        ThirdPartyBox.Text = ThirdParty;
    }

    private static T? Attr<T>() where T : Attribute =>
        Assembly.GetExecutingAssembly().GetCustomAttribute<T>();

    private static string LoadLicense()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PhotoTag.LICENSE");
        if (stream is null) return "Licencja MIT — tekst niedostępny.";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void CopyBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText($"{Product} {Version}\n{Copyright}\nLicencja: MIT");
        }
        catch
        {
        }
    }
}
