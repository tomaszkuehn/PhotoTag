# PhotoTag

Aplikacja desktopowa (WPF, .NET 8) do szybkiego uzupełniania współrzędnych GPS zdjęć bez danych lokalizacji. Zdjęcia zapisane z lokalizacją dostają sufiks `_LOCPT` (podwójne kliknięcie w systemie otwiera je w aplikacji obsługującej ten sufiks).

## Funkcje

- Przeglądanie folderu z filtrami `.jpg/.jpeg/.tif/.tiff` — miniaturki, status GPS, data zdjęcia (EXIF `DateTimeOriginal` → `DateTimeDigitized` → data pliku, oznaczona „(plik)").
- Sortowanie wg daty; panel pojedynczego zdjęcia z dużym podglądem (miniaturka/podgląd, kliknięcie otwiera powiększenie na cały panel prawy, zamknięcie kliknięciem lub `Esc`). W powiększeniu strzałki `←`/`→` (także `↑`/`↓`, `PgUp`/`PgDn`, spacja) przechodzą do poprzedniego/następnego zdjęcia z listy, `Home`/`End` — pierwsze/ostatnie; u dołu nazwa i numer zdjęcia.
- Mapa OpenStreetMap (Leaflet 1.9.4 w WebView2, lokalny host wirtualny `maphost`):
  - klik / przeciąganie markera = wybór współrzędnych,
  - wyszukiwanie miejsc i adresów (Nominatim, ważone aktualnym obszarem widoku),
  - **wyszukiwanie ulic** (Overpass API) — jeśli geokoder nie rozpozna frazy, rysowane są niebieskie linie odcinków ulic o pasującej nazwie w widocznym obszarze (do 50),
  - przełącznik warstw: mapa OSM / **satelita** (Esri World Imagery),
  - **Street View** (przycisk „Street View” na pasku mapy) — osadzony panel zastępujący mapę; powrót przyciskiem „← Mapa" lub `Esc`.
  - pole wyszukiwania rozpoznaje też współrzędne: `50.06, 19.94`, `50°03'41"N 19°56'12"E`, linki Google Maps (`@lat,lng`, `!3d…!4d…`, `?q=`), OSM (`#map=`), `geo:`,
  - **adres punktu** (reverse geocoding Nominatim) pod mapą i pod przyciskami,
  - **kierunek zdjęcia**: prawy klik = kierunek, w którym patrzył aparat (stożek); zapisywany jako `GPSImgDirection` (północ geograficzna), Street View otwiera się w tym kierunku; „Kierunek ✕” usuwa.
- **Ślad GPS** („Ślad GPS: Wczytaj…”): GPX, KML (`gx:Track`, Placemark z czasem), JSON z historii lokalizacji Google (`Records.json`, `Timeline.json` z telefonu, Semantic Location History). Pozycja = interpolacja między punktami śladu (luka ≤ 2 h) lub najbliższy punkt (≤ 10 min). Ślad rysowany na mapie (czerwona linia); wczytane pliki są pamiętane.
- **Korekta zegara aparatu** („Zegar aparatu”): przesunięcie per aparat (EXIF `Make`/`Model`), np. `+01:00:00`. Uwzględniana strefa `OffsetTimeOriginal`, a bez niej strefa systemowa. „Kalibruj”:
  - 2 zaznaczone zdjęcia tej samej sceny z różnych aparatów → zrównanie czasu (korygowany aparat zdjęcia bez GPS),
  - 1 zdjęcie + punkt na mapie, gdzie je zrobiono → czas najbliższego punktu śladu GPS.
- **Źródło GPS**: zdjęcia z pozycją zapisaną przez aparat/telefon w chwili zrobienia (są znaczniki `GPSTimeStamp`/`GPSDateStamp`/`GPSAltitude`/`GPSProcessingMethod` itp.) oznaczone „📷 GPS z aparatu” i złotym kolorem na liście i mapie; pozycje dopisane przez PhotoTag (`GPSProcessingMethod = MANUAL` lub plik `_LOCPT`) — „GPS dodany”; same współrzędne bez innych znaczników — „GPS” (źródło nieznane).
- **Zdjęcia z GPS na mapie**: zielone (złote = GPS z aparatu) punkty (miniatura po najechaniu, klik = wybór zdjęcia) i fioletowa trasa w kolejności czasu; warstwy w przełączniku mapy.
- **Foldery referencyjne** (np. zdjęcia z telefonu, z podfolderami): ich zdjęcia z GPS służą tylko jako źródło podpowiedzi i interpolacji (niebieskie punkty), nie trafiają na listę.
- Sugestia lokalizacji przy wyborze zdjęcia bez GPS: ślad GPS → interpolacja między zdjęciami z GPS (Δ ≤ 2 h, także z folderów ref.) → najbliższe zdjęcie z GPS (Δ ≤ 12 h). Wybór zdjęcia (z listy lub klik punktu na mapie) tylko przesuwa mapę — powiększenie pozostaje takie, jak ustawił użytkownik.
- **Przesuwanie na osi czasu** (zdjęcia ze złą datą): przycisk „Ułóż kolejność…” (lub prawy klik na liście) otwiera osobny widok z dużymi miniaturami (3× większymi, bez dat i lokalizacji), ułożonymi wierszami w kolejności czasu. Przeciągnij dowolne zdjęcie na inne — lewa/prawa połowa kafelka decyduje, czy trafi przed, czy po nim; dostaje czas o 1 s przed/po tym zdjęciu (gdy brak miejsca — równomiernie między sąsiadami). „Cofnij” (Ctrl+Z) cofa 4 ostatnie przesunięcia. Na liście głównej: `Alt+↑`/`Alt+↓` przesuwa o jedną pozycję; prawy klik → „Ustaw datę i godzinę…” (grupa zachowuje odstępy) / „Przywróć datę z EXIF”. Czas ręczny (oznaczony „ręcznie”) zastępuje datę EXIF i korektę zegara przy sortowaniu, sesjach, śladzie GPS i interpolacji; jest pamiętany w ustawieniach do zapisu — przy zapisie lokalizacji trafia do pliku. Po przesunięciu (przeciągnięcie w „Ułóż kolejność”, `Alt+↑/↓`, menu) program pyta, czy skopiować pozycję od najbliższego sąsiada z GPS z tego samego dnia — „Tak” = poprzednie, „Nie” = następne zdjęcie (w pytaniu nazwa, godzina, współrzędne i odległość między nimi); zgoda od razu zapisuje pozycję i poprawiony czas do pliku `_LOCPT` (cofanie: `Ctrl+Z`). Pytania nie ma przy zdjęciach z GPS z aparatu, gdy w tym dniu sąsiad nie ma GPS albo gdy zdjęcie już ma jego pozycję.
- **Filmy** (MP4, MOV, M4V, 3GP) są na liście razem ze zdjęciami (znacznik „▶ film”): miniatura i podgląd to klatka z systemowego podglądu Windows (jak w Eksploratorze; bez kodeka, np. HEVC, pokazywana jest ikona). Czas i lokalizacja są czytane z metadanych QuickTime (`mvhd`, `udta/©xyz`, klucze Apple `creationdate` / `location.ISO6709`). Zapis tworzy `film_LOCPT.mp4` z lokalizacją (`©xyz` + klucz Apple, jeśli plik go używa) i czasem z listy (z korektą zegara / czasem ręcznym) w `mvhd`/`tkhd`/`mdhd` i `creationdate`; obraz i dźwięk są kopiowane bez przekodowania. Pofragmentowane MP4 nie są obsługiwane.
- **Sesje**: lista grupowana po przerwie w czasie (domyślnie 30 min); „zaznacz” w nagłówku lub `G` zaznacza całą sesję. Wiele zdjęć: Ctrl/Shift + klik.
- **Miejsca**: ulubione (★ Zapisz) i ostatnio użyte (automatycznie po zapisie); kopiuj/wklej punkt (Ctrl+C / Ctrl+V — współrzędne, link Google Maps/OSM lub adres).
- Zapis GPS:
  - zapis tylko współrzędnych (+ kierunku i znacznika `GPSProcessingMethod = MANUAL`; piksele bez zmian), orientacja EXIF zachowana,
  - zapisywany jest też czas z listy (z korektą zegara / czasem ręcznym) w `DateTimeOriginal`, `CreateDate` i `ModifyDate`, z `Software = PhotoTag` — po ponownym wczytaniu korekta zegara aparatu nie jest doliczana drugi raz (w filmach znacznik `©too`); czas tylko z daty pliku nie jest zapisywany,
  - EXIF poprawiany po zapisie dla telefonów (Android/iOS odrzucają niezgodne pliki): wpisy IFD posortowane wg tagu, `GPSVersionID = 2.3.0.0`, współrzędne jako `st/1 min/1 sek·10000/10000`,
  - nazwa: `foto.jpg` → `foto_LOCPT.jpg`; edycja już oznaczonego pliku `_LOCPT` nadpisuje go po potwierdzeniu,
  - przy istniejącym `foto_LOCPT.jpg` — pytanie o nadpisaniu,
  - zapis przez plik tymczasowy `.tmp` + przeniesienie (atomowościowo bezpieczne),
  - zapis do wszystkich zaznaczonych zdjęć albo (checkbox) wszystkich bez GPS,
  - „Uzupełnij automatycznie” — wszystkie zdjęcia bez GPS ze śladu GPS lub interpolacji,
  - **Cofnij** (Ctrl+Z) — przywraca stan sprzed ostatnich zapisów (kopie w `%TEMP%\PhotoTag\undo`, czyszczone przy starcie),
  - po zapisie automatycznie zaznaczane jest kolejne zdjęcie bez GPS.
- **Kopiowanie lokalizacji między zdjęciami**: prawy klik na zdjęciu z GPS → „Kopiuj lokalizację z …” (`Ctrl+Shift+C`), potem zaznacz inne zdjęcia → „Wklej lokalizację … do zaznaczonych” (`Ctrl+Shift+V`). Kopiowany jest punkt i kierunek; po potwierdzeniu zapis od razu (jak `Enter`, z cofaniem). `Ctrl+C`/`Ctrl+V` dalej kopiuje punkt mapy jako tekst przez schowek.
- **Usuwanie**: `Del` lub prawy klik → „Usuń” zmienia nazwę zaznaczonych plików na `…jpg.bak` (np. `IMG_1.jpg.bak`; gdy zajęte — `IMG_1.jpg.2.bak`) i zdejmuje je z listy. Przy pliku `_LOCPT` tak samo zmieniany jest jego oryginał, żeby nie wrócił na listę. Przywrócenie: usuń `.bak` z nazwy i odśwież folder.
- Skróty (działają też przy fokusie na mapie): `Enter` zapis, `Del` usuń (→ `.bak`), `N` / `Shift+N` następne/poprzednie bez GPS, `G` sesja, `S` Street View, `Ctrl+C`/`Ctrl+V`, `Ctrl+Shift+C`/`Ctrl+Shift+V` lokalizacja zdjęcia → zaznaczone, `Ctrl+Z`, `Alt+↑`/`Alt+↓` przesunięcie na osi czasu, `Esc`.
- Ustawienia (ostatni folder, ślady, foldery ref., zegary, miejsca, ręczne czasy zdjęć): `%APPDATA%\PhotoTag\settings.json`.

## Uruchomienie

Gotowy plik exe (self-contained, bez instalacji .NET; wymaga WebView2 Runtime):

```powershell
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o bin/publish
```

Folder `Assets` musi leżeć obok `PhotoTag.exe`. Uruchomienie ze źródeł:

```powershell
dotnet run
```

Wymaga .NET SDK 8 oraz połączenia z internetem (kafelki mapy, Nominatim, Overpass).

## Architektura

- `Models/PhotoItem.cs` — model zdjęcia (INPC): status i źródło GPS, czas po korekcie zegara (lokalny/UTC), sesja, miniaturka.
- `Services/ExifService.cs` — odczyt/zapis EXIF (`ExifLibNet`): `GeoPoint`, `ReadInfo` (GPS i jego źródło, kierunek, data, strefa, aparat), `WriteGps` (z markerem `GPSProcessingMethod = MANUAL`) i poprawką `TiffFixer`, konwersja stopnie–DMS (clamp ujemnych sekund).
- `Services/TiffFixer.cs` — poprawka zapisanego EXIF w miejscu: sortowanie wpisów IFD, `GPSVersionID`, czytelne wymierne współrzędne.
- `Services/TrackStore.cs` — ślady GPS (GPX/KML/JSON Google), wyszukiwanie pozycji po czasie UTC.
- `Services/SettingsService.cs` — trwałe ustawienia (JSON w `%APPDATA%`).
- `Services/Imaging.cs` — dekodowanie miniatur/podglądów z uwzględnieniem orientacji EXIF; klatki filmów przez `IShellItemImageFactory`.
- `Assets/map.html` — mapa: warstwy, wyszukiwanie, komunikacja z WPF przez `window.chrome.webview.postMessage` / `setView(lat,lng,zoom)`.
- `Services/VideoService.cs` — odczyt i zapis metadanych MP4/MOV (drzewo atomów moov, korekta `stco`/`co64`).
- `MainWindow.Timeline.cs` — ręczny czas zdjęć: wstawianie na osi czasu, cofanie (4 kroki), `Alt+↑/↓`, menu kontekstowe.
- `OrderWindow.xaml(.cs)` — widok „Ułóż kolejność”: siatka dużych miniatur, przeciąganie z linią wstawienia, cofanie.
- `MainWindow.xaml(.cs)` — UI, sugerowanie punktów, zapis zbiorczy, zarządzanie trybami ( mapa / Street View / powiększenie). WebView2 jest natywnym HWND i wypada NAD elementami WPF — nakładki wymagają zwijania panelu mapy (`MapView.Visibility`).

## Ograniczenia

- Street View osadzony przez mapę Google bez klucza API (nawigacja top-level WebView2); przy pierwszym użyciu może pojawić się zgoda cookies Google.
- Rozszerzenia inne niż JPEG/TIFF (np. RAW) nie są obsługiwane.
- HEIC nieobsługiwany.