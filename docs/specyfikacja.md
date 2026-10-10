# Specyfikacja — modul_mapa_aktywnych_okien (prototyp 1)

Status: zatwierdzona (2026-10-10).

## 1. Cel

Globalna mapa (Canvas) wszystkich aktywnych okien jako nodów z miniaturami. Kliknięcie
noda przełącza do jego okna. Moduł służy **wyłącznie do przełączania się między otwartymi
oknami** — nie przechowuje ścieżek plików ani referencji do ponownego otwierania.

## 2. Architektura

- **Osobna aplikacja**, niezależna od DocMonitora (`App_Info_o_aktywnym_pliku`), ale ze
  wspólnym stosem i konwencjami — docelowo oba moduły trafią do jednej aplikacji nadrzędnej.
- **Backend:** .NET 9 (C#, Windows):
  - wykrywanie okien zdarzeniowo (`SetWinEventHook`, bez pollingu),
  - miniatury okien,
  - globalny gest mysz + klawiatura,
  - przełączanie okien,
  - zapis stanu,
  - serwer WebSocket na `127.0.0.1` (port inny niż DocMonitor `5891`).
- **Frontend:** Electron — okno z Canvasem + ikona w zasobniku.
- **Rozszerzenie przeglądarki:** Manifest V3, Edge + Chrome, wspólne dla obu modułów (pkt 6).
- **Repozytorium:** `MatGoldbeck/modul_mapa_aktywnych_okien` — `backend/`, `frontend/`,
  `extension/`, `docs/`.
- **Uruchamianie:** ręczne (bez autostartu); po starcie aplikacja działa w tle z ikoną
  w zasobniku.

## 3. Co jest nodem

- Każde aktywne okno widoczne w Alt+Tab (widoczne okno najwyższego poziomu z paskiem
  zadań), także okna bez rozpoznanego zasobu (Kalkulator, Teams itp.).
- **Edge i Chrome:** każda karta to osobny nod; dane kart dostarcza rozszerzenie.
- **Pomijane:**
  - okno samego modułu i elementy systemowe,
  - procesy z listy wykluczeń (pkt 9),
  - karty InPrivate/incognito (rozszerzenie domyślnie w nich nie działa).
- Okna ze **wszystkich monitorów i wszystkich pulpitów wirtualnych** trafiają na jedną mapę.
- **Klucz noda:**
  - okno: nazwa procesu + tytuł okna,
  - karta: URL.
- **Nody są unikalne** (klucz eliminuje duplikaty):
  - dwie karty z tym samym URL → jeden nod; klik przełącza do ostatnio aktywnej z nich,
  - ponowne otwarcie czegoś, co już ma nod, nie zmienia mapy,
  - okna jednej aplikacji o różnych tytułach (np. dwa Notatniki z różnymi plikami) to
    osobne nody.

## 4. Cykl życia nodów

- Mapa aktualizuje się automatycznie przy każdym otwarciu i zamknięciu okna/karty.
- **Nowe okno/karta:** nod pojawia się przy krawędzi właściwej dla typu (pkt 5),
  miniatura jest robiona od razu.
- **Zamknięcie:** nod znika z mapy.
- **Ponowne pojawienie się znanego klucza:** nod wraca w zapamiętaną pozycję zamiast
  na krawędź.
- Na mapie są wyłącznie nody aktywnych okien.

## 5. Canvas

- Stały rozmiar, ustalany przy otwarciu okna i dopasowany do monitora, na którym okno
  się otwiera.
- Wyłącznie nody — bez linii, relacji i notatek.
- Nod: miniatura + ikona/favikona + tytuł.
- **Nowe nody przy krawędzi według typu:**
  - górna: karty Edge/Chrome,
  - lewa: Office (Word, Excel, PowerPoint, Outlook),
  - dolna: Eksplorator,
  - prawa: pozostałe aplikacje.
- Nody można dowolnie przesuwać w obszarze Canvasu; pozycje są zapamiętywane.
- Przycisk **„Odśwież miniatury”** robi nowe miniatury wszystkich aktualnych okien.

## 6. Rozszerzenie przeglądarki (Edge, Chrome)

- **Jedno rozszerzenie** dla obu modułów (w DocMonitorze dotąd nie istniało):
  - dla tego modułu: lista kart (id, URL, tytuł, favikona, okno), zdarzenia otwarcia /
    zamknięcia / aktywacji kart, przełączenie do wskazanej karty,
  - dla DocMonitora: metadane aktywnej karty (URL, tytuł, favikona).
- Komunikacja: WebSocket na `127.0.0.1` do backendu każdego modułu, bez wpisów w rejestrze.
- Instalacja w prototypie: „Załaduj rozpakowane” w trybie dewelopera.
- Miniatury kart: obraz da się złapać tylko dla aktywnej karty. Karta nieaktywna pokazuje
  ostatni obraz z chwili, gdy była aktywna; do tego czasu favikonę i tytuł.
- **Inne przeglądarki** (Firefox, Opera, Brave itp.): przy pierwszym wykryciu ich okna
  w danej sesji jednorazowy komunikat: „W modul_mapa_aktywnych_okien obsługiwane są tylko
  przeglądarki Edge, Chrome”.

## 7. Miniatury

- Bitmapy konkretnych okien, zapisywane jako WebP ok. 320 px szerokości.
- Powstają automatycznie przy pojawieniu się nowego okna oraz na żądanie
  („Odśwież miniatury”).
- Okno zminimalizowane nie daje świeżego obrazu — zostaje ostatnia zapisana miniatura.
- Okna z innych pulpitów wirtualnych — do rozstrzygnięcia w teście technicznym;
  w najgorszym razie zostaje ostatnia zapisana miniatura.
- Miniatury zamkniętych okien są usuwane.

## 8. Wywołanie okna i przełączanie

- **Gest:** trzymane Ctrl+Shift + ruch myszą o ≥ 50 px, bez wciśniętego przycisku myszy
  (globalny hook myszy/klawiatury w backendzie).
- Okno pojawia się w ostatnio używanym miejscu; za pierwszym razem na środku monitora
  głównego.
- Okno znika po Esc albo po kliknięciu noda.
- **Kliknięcie noda** przełącza do jego okna:
  - okno zminimalizowane zostaje przywrócone,
  - okno z innego pulpitu wirtualnego → przełączenie pulpitu (weryfikacja w teście),
  - nod karty → aktywacja karty przez rozszerzenie + okna przeglądarki.

## 9. Zapis i konfiguracja

- `%APPDATA%\MapaAktywnychOkien\map.json` — klucze nodów, pozycje, ostatnia pozycja okna
  aplikacji.
- `%APPDATA%\MapaAktywnychOkien\thumbs\` — miniatury.
- Lista wykluczeń: edytowalna lista nazw procesów w pliku konfiguracyjnym (bez okna
  ustawień). Na start: 1Password, KeePass, Bitwarden.

## 10. Poza zakresem prototypu

- otwieranie zamkniętych zasobów, referencje do plików/linków,
- wykrywanie nieaktywnych linków,
- relacje, linie, notatki,
- przeglądarki inne niż Edge/Chrome,
- okno ustawień, autostart, instalator,
- integracja z DocMonitorem poza wspólnym rozszerzeniem.

## 11. Test techniczny (przed prototypem)

Na Windowsie użytkownika:

1. Lista okien zgodna z Alt+Tab, ze wszystkich monitorów i pulpitów wirtualnych.
2. Miniatury przez Windows.Graphics.Capture i `PrintWindow` dla okien zwykłych,
   zminimalizowanych i z innych pulpitów — jakość i czas.
3. Gest Ctrl+Shift + ruch ≥ 50 px — niezawodność, brak fałszywych wywołań.
4. Przełączanie do okna, w tym przywracanie zminimalizowanego i zmiana pulpitu.
5. Połączenie rozszerzenie → WebSocket → backend.
