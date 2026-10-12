# Specyfikacja — modul_mapa_aktywnych_okien (prototyp 1)

Status: zatwierdzona (2026-10-10), zmiana Canvas → Grid (2026-10-12), prototyp zbudowany (2026-10-12).

## 1. Cel

Globalna mapa w postaci siatki (Grid) wszystkich aktywnych okien jako nodów z miniaturami. Kliknięcie
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
- **Frontend:** Electron — okno z siatką (Grid) + ikona w zasobniku.
- **Rozszerzenie przeglądarki:** Manifest V3, Edge + Chrome, wspólne dla obu modułów (pkt 6).
- **Repozytorium:** `MatGoldbeck/modul_mapa_aktywnych_okien` — `backend/`, `frontend/`,
  `extension/`, `docs/`.
- **Uruchamianie:** ręczne (bez autostartu) przez `start.cmd`; frontend uruchamia backend
  i działa w tle z ikoną w zasobniku. Backend kończy się razem z frontendem.
- **Wyłączanie:** menu ikony w zasobniku (prawy przycisk) → **„Zakończ”**. Zamyka
  frontend i backend. Esc i klik w nod tylko chowają okno – program działa dalej w tle.

## 3. Co jest nodem

- Każde aktywne okno widoczne w Alt+Tab (widoczne okno najwyższego poziomu z paskiem
  zadań), także okna bez rozpoznanego zasobu (Kalkulator, Teams itp.).
- **Edge i Chrome:** każda karta to osobny nod; dane kart dostarcza rozszerzenie. Gdy
  rozszerzenie danej przeglądarki nie jest połączone, jej okna są zwykłymi nodami okien.
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
- **Nowe okno/karta:** nod trafia do wolnego pola przy krawędzi właściwej dla typu (pkt 5),
  miniatura jest robiona od razu.
- **Zamknięcie:** nod znika z mapy.
- **Ponowne pojawienie się znanego klucza:** nod wraca do zapamiętanego pola (jeśli jest
  wolne i mieści się w bieżącej siatce) zamiast na krawędź.
- Na mapie są wyłącznie nody aktywnych okien.

## 5. Grid (siatka)

- Mapa to siatka pól (Grid); każdy nod zajmuje dokładnie jedno pole.
- **Liczba pól zależy od rozdzielczości ekranu**, odczytywanej raz – przy uruchomieniu
  aplikacji (monitor główny, rozdzielczość fizyczna w pikselach, niezależnie od skalowania):

  | Rozdzielczość do (włącznie) | Siatka | Pól |
  |-----------------------------|--------|-----|
  | 1920×1080                   | 5×5    | 25  |
  | 2560×1440                   | 6×6    | 36  |
  | 3840×2160                   | 8×8    | 64  |
  | powyżej 3840×2160           | 8×8    | 64  |

  Rozdzielczość mieści się w progu, gdy szerokość i wysokość są nie większe niż próg
  (np. 1920×1200 → 6×6).
- Okno siatki ma stały rozmiar dopasowany do monitora głównego.
- Wyłącznie nody — bez linii, relacji i notatek.
- Nod: miniatura + ikona/favikona + tytuł.
- **Nowe nody trafiają do wolnych pól przy krawędzi według typu:**
  - górny wiersz: karty Edge/Chrome,
  - lewa kolumna: Office (Word, Excel, PowerPoint, Outlook),
  - dolny wiersz: Eksplorator,
  - prawa kolumna: pozostałe aplikacje.

  Gdy pola przy właściwej krawędzi są zajęte, nod trafia do najbliższego wolnego pola.
- **Przesuwanie:** nod przeciąga się do innego pola siatki. Upuszczenie na zajęte pole
  zamienia nody miejscami. Pozycja (wiersz, kolumna) jest zapamiętywana.
- **Przepełnienie:** gdy nodów jest więcej niż pól, wyświetlany jest komunikat:
  „Liczba nodów (N) przekracza liczbę pól siatki (M). Nie wyświetlono X nodów.”
  Nody, które się nie zmieściły, pojawiają się automatycznie, gdy zwolni się pole.
- Zapamiętane pole spoza bieżącej siatki (np. po zmianie rozdzielczości) jest ignorowane –
  nod trafia na krawędź.
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

- Bitmapy konkretnych okien robione przez `PrintWindow` (wynik testu technicznego: szybsze
  niż Windows.Graphics.Capture i bez żółtej ramki na Windows 10), zapisywane jako WebP
  ok. 320 px szerokości.
- Powstają automatycznie przy pojawieniu się nowego okna oraz na żądanie
  („Odśwież miniatury”).
- Okno zminimalizowane nie daje świeżego obrazu — zostaje ostatnia zapisana miniatura.
- Okna z innych pulpitów wirtualnych — nie sprawdzone w teście (jeden pulpit), do weryfikacji
  w prototypie;
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
  - nod karty → aktywacja karty przez rozszerzenie; jeśli okno przeglądarki nie wyjdzie na
    pierwszy plan, backend wysuwa je sam,
  - kolejność metod przełączania: `SetForegroundWindow`, a przy blokadzie pierwszego planu
    „trik z Alt” (wynik testu).

## 9. Zapis i konfiguracja

- `%APPDATA%\MapaAktywnychOkien\map.json` — klucze nodów, pola siatki (wiersz, kolumna),
  ostatnia pozycja okna aplikacji.
- `%APPDATA%\MapaAktywnychOkien\thumbs\` — miniatury.
- Lista wykluczeń: edytowalna lista nazw procesów w pliku konfiguracyjnym (bez okna
  ustawień). Na start: 1Password, KeePass, Bitwarden.

## 10. Poza zakresem prototypu

- otwieranie zamkniętych zasobów, referencje do plików/linków,
- wykrywanie nieaktywnych linków,
- relacje, linie, notatki, swobodne (poza siatką) rozmieszczanie nodów,
- przeglądarki inne niż Edge/Chrome,
- okno ustawień, autostart, instalator,
- integracja z DocMonitorem poza wspólnym rozszerzeniem.

## 11. Test techniczny (przed prototypem)

Wykonany 2026-10-12 – wyniki: [`wyniki-testu-technicznego.md`](wyniki-testu-technicznego.md).

Na Windowsie użytkownika:

1. Lista okien zgodna z Alt+Tab, ze wszystkich monitorów i pulpitów wirtualnych.
2. Miniatury przez Windows.Graphics.Capture i `PrintWindow` dla okien zwykłych,
   zminimalizowanych i z innych pulpitów — jakość i czas.
3. Gest Ctrl+Shift + ruch ≥ 50 px — niezawodność, brak fałszywych wywołań.
4. Przełączanie do okna, w tym przywracanie zminimalizowanego i zmiana pulpitu.
5. Połączenie rozszerzenie → WebSocket → backend.
