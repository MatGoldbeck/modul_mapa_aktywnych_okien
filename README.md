# modul_mapa_aktywnych_okien

Globalna mapa wszystkich aktywnych okien Windows w postaci siatki (Grid) nodów z miniaturami.
Kliknięcie noda przełącza do okna; mapę otwiera gest **Ctrl+Shift + ruch myszą**.
Rozmiar siatki zależy od rozdzielczości monitora głównego (5×5 / 6×6 / 8×8).

Osobna aplikacja, spójna technologicznie z DocMonitorem (`App_Info_o_aktywnym_pliku`):
backend .NET 9 + frontend Electron + wspólne rozszerzenie Edge/Chrome.

## Stan

- [x] Specyfikacja – [`docs/specyfikacja.md`](docs/specyfikacja.md)
- [x] Test techniczny – [`spike/`](spike/README.md), wyniki: [`docs/wyniki-testu-technicznego.md`](docs/wyniki-testu-technicznego.md)
- [x] Prototyp – okno siatki, ikona w zasobniku, zapis pozycji nodów (ta instrukcja)

## Wymagania

- Windows 10 2004+ / Windows 11,
- .NET 9 SDK (`winget install Microsoft.DotNet.SDK.9`),
- Node.js 20+ (`winget install OpenJS.NodeJS.LTS`) – tylko do instalacji Electrona.

Repozytorium najlepiej trzymać poza OneDrive (np. `C:\src\modul_mapa_aktywnych_okien`).

## Uruchomienie

Dwuklik na **`start.cmd`** w głównym folderze (albo `.\start.cmd` w terminalu). Skrypt:

1. buduje backend (`dotnet build backend -c Release`),
2. przy pierwszym uruchomieniu instaluje frontend (`npm ci` w `frontend\`),
3. uruchamia aplikację w tle – pojawia się **ikona w zasobniku** (kwadrat z siatką) i dymek
   „Mapa aktywnych okien działa w tle”.

Okno konsoli `start.cmd` można zamknąć – aplikacja działa dalej. Okno mapy jest ukryte do
pierwszego gestu.

## Obsługa

| Co | Jak |
|----|-----|
| Otwórz mapę | trzymaj **Ctrl+Shift** i przesuń mysz o ≥ 50 px (bez klikania); albo klik w ikonę w zasobniku |
| Przełącz do okna / karty | klik w nod – mapa się chowa, okno wychodzi na pierwszy plan (zminimalizowane zostaje przywrócone) |
| Przesuń nod | przeciągnij go na inne pole; na zajętym polu nody zamieniają się miejscami |
| Odśwież miniatury | przycisk **⟳ Odśwież miniatury** albo zasobnik → *Odśwież miniatury* |
| Schowaj mapę | **Esc**, klik w nod albo krzyżyk okna (program działa dalej w tle) |
| Przesuń okno mapy | przeciągnij za pasek tytułu – pozycja jest zapamiętywana |

Nowe okna pojawiają się przy krawędziach: **góra** – karty Edge/Chrome, **lewa** – Office,
**dół** – Eksplorator, **prawa** – pozostałe. Gdy nodów jest więcej niż pól, nad siatką
pojawia się komunikat, a nadmiarowe nody wchodzą, gdy zwolni się pole.

Pozycje nodów są zapamiętywane także po zamknięciu okna: gdy otworzysz je ponownie (ten sam
proces i tytuł, dla kart – ten sam adres), nod wraca na swoje pole.

## Rozszerzenie przeglądarki (Edge, Chrome)

Bez rozszerzenia okna przeglądarek są zwykłymi nodami. Z rozszerzeniem **każda karta** to
osobny nod (karty o tym samym adresie – jeden nod).

1. Edge: `edge://extensions`, Chrome: `chrome://extensions`.
2. Włącz **Tryb dewelopera**.
3. **Załaduj rozpakowane** → wskaż folder `extension\` z repozytorium.
   Jeśli było załadowane w czasie testu technicznego – kliknij **Załaduj ponownie** (wersja 0.2.0).

Na pasku mapy kropki przy „Edge” i „Chrome” są zielone, gdy rozszerzenie jest połączone.
Miniatura karty powstaje, gdy karta jest aktywna (ograniczenie przeglądarki); do tego czasu
nod pokazuje favikonę i tytuł.

## Wyłączanie programu

- **Zalecane:** prawy przycisk na ikonie w zasobniku → **Zakończ**. Zapisuje pozycje nodów
  i zamyka frontend oraz backend.
- Esc, klik w nod i krzyżyk okna mapy **tylko chowają** okno – program działa dalej.
- Awaryjnie (gdy ikona nie reaguje): Menedżer zadań → zakończ `electron.exe` z tego folderu;
  backend `MapaOkien.Backend.exe` zakończy się sam w ciągu sekundy.

Po wyłączeniu nic nie zostaje uruchomione w tle (globalny hook myszy znika razem z procesem).
Program nie startuje sam z systemem.

## Dane i konfiguracja

Wszystko w `%APPDATA%\MapaAktywnychOkien\`:

| Plik | Zawartość |
|------|-----------|
| `map.json` | pola nodów (wiersz, kolumna) i pozycja okna mapy |
| `thumbs\` | miniatury WebP (320 px); miniatury zamkniętych okien są usuwane |
| `config.json` | `excludedProcesses` – procesy pomijane na mapie (domyślnie 1Password, KeePass, KeePassXC, Bitwarden); `port` – port WebSocket (domyślnie 5892) |
| `backend.log` | log backendu – pierwsze miejsce do sprawdzenia przy problemach |

Po zmianie `config.json` uruchom program ponownie (Zakończ → `start.cmd`).

## Rozwiązywanie problemów

| Objaw | Co zrobić |
|-------|-----------|
| Okno mapy pokazuje „Łączenie z backendem…” | zajrzyj do `backend.log`; najczęściej port 5892 zajęty – zmień `port` w `config.json` (i `ENDPOINT` w `extension\background.js`) |
| Gest nie otwiera mapy | czy ikona jest w zasobniku? w `backend.log` powinno być „Hook myszy aktywny” |
| `start.cmd`: błąd budowania „plik używany przez inny proces” | aplikacja już działa – zakończ ją z zasobnika i uruchom ponownie |
| Kropki Edge/Chrome szare | rozszerzenie niezaładowane albo wyłączone – patrz sekcja wyżej |

## Struktura

```
backend/     .NET 9: okna, gest, miniatury, siatka, zapis, WebSocket (127.0.0.1:5892)
frontend/    Electron: okno siatki + ikona w zasobniku; uruchamia backend
extension/   rozszerzenie Edge/Chrome (Manifest V3)
tests/       testy logiki siatki (dotnet test – działają na każdym systemie)
spike/       test techniczny (konsola)
docs/        specyfikacja, wyniki testu technicznego
start.cmd    budowanie + uruchomienie
```

Testy: `dotnet test tests/MapaOkien.Tests`.
