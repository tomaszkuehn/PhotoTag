# PhotoTag

Aplikacja desktopowa (WPF, .NET 8) do szybkiego uzupełniania współrzędnych GPS zdjęć bez danych lokalizacji. Zdjęcia zapisane z lokalizacją dostają sufiks `_LOCPT` (podwójne kliknięcie w systemie otwiera je w aplikacji obsługującej ten sufiks).

## Funkcje

- Przeglądanie folderu z filtrami `.jpg/.jpeg/.tif/.tiff` — miniaturki, status GPS, data zdjęcia (EXIF `DateTimeOriginal` → `DateTimeDigitized` → data pliku, oznaczona „(plik)").
- Sortowanie wg daty; panel pojedynczego zdjęcia z dużym podglądem (miniaturka/podgląd, kliknięcie otwiera powiększenie na cały panel prawy, zamknięcie kliknięciem lub `Esc`).
- Mapa OpenStreetMap (Leaflet 1.9.4 w WebView2, lokalny host wirtualny `maphost`):
  - klik / przeciąganie markera = wybór współrzędnych,
  - wyszukiwanie miejsc i adresów (Nominatim, ważone aktualnym obszarem widoku),
  - **wyszukiwanie ulic** (Overpass API) — jeśli geokoder nie rozpozna frazy, rysowane są niebieskie linie odcinków ulic o pasującej nazwie w widocznym obszarze (do 50),
  - przełącznik warstw: mapa OSM / **satelita** (Esri World Imagery),
  - **Street View** (przycisk „Street View” na pasku mapy) — osadzony panel zastępujący mapę; powrót przyciskiem „← Mapa" lub `Esc`.
- Sugestia lokalizacji: przy wyborze zdjęcia bez GPS mapa podąża za najbliższym zdjęciem z GPS (Δ≤5 min → zoom 15, ≤60 min → zoom 13, ≤12 h → zoom 6, dalej brak podglądu).
- Zapis GPS:
  - zapis tylko współrzędnych (piksele bez zmian), orientacja EXIF zachowana,
  - nazwa: `foto.jpg` → `foto_LOCPT.jpg`; edycja już oznaczonego pliku `_LOCPT` nadpisuje go po potwierdzeniu,
  - przy istniejącym `foto_LOCPT.jpg` — pytanie o nadpisaniu,
  - zapis przez plik tymczasowy `.tmp` + przeniesienie (atomowościowo bezpieczne),
  - tryb zbiorczy (checkbox) dla wszystkich zdjęć bez GPS w folderze,
  - po zapisie automatycznie zaznaczane jest kolejne zdjęcie bez GPS.

## Uruchomienie

```powershell
dotnet run
```

Wymaga .NET SDK 8 oraz połączenia z internetem (kafelki mapy, Nominatim, Overpass).

## Architektura

- `Models/PhotoItem.cs` — model zdjęcia (INPC): status GPS, daty, miniaturka.
- `Services/ExifService.cs` — odczyt/zapis EXIF (`ExifLibNet`): `GeoPoint`, `ReadInfo`, `WriteGps`, konwersja stopnie–DMS (clamp ujemnych sekund).
- `Services/Imaging.cs` — dekodowanie miniatur/podglądów z uwzględnieniem orientacji EXIF.
- `Assets/map.html` — mapa: warstwy, wyszukiwanie, komunikacja z WPF przez `window.chrome.webview.postMessage` / `setView(lat,lng,zoom)`.
- `MainWindow.xaml(.cs)` — UI, sugerowanie punktów, zapis zbiorczy, zarządzanie trybami ( mapa / Street View / powiększenie). WebView2 jest natywnym HWND i wypada NAD elementami WPF — nakładki wymagają zwijania panelu mapy (`MapView.Visibility`).

## Ograniczenia

- Street View osadzony przez mapę Google bez klucza API (nawigacja top-level WebView2); przy pierwszym użyciu może pojawić się zgoda cookies Google.
- Rozszerzenia inne niż JPEG/TIFF (np. RAW) nie są obsługiwane.
- HEIC nieobsługiwany.