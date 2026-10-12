# Wyniki testu technicznego — 2026-10-12

Środowisko: Windows 10 22H2 (19045), jeden monitor 3840×2160, jeden pulpit wirtualny,
Edge + Chrome z rozszerzeniem 0.1.0. Trzy sesje testu (`spike/`).

## Podsumowanie

| # | Obszar | Wynik | Decyzja |
|---|--------|-------|---------|
| 1 | Lista okien (Alt+Tab) | 11 okien, klucz proces+tytuł unikalny. W 1. sesji fałszywe okna (WebView2 CCleaner, „Chrome Legacy Window”, „Zegar systemowy”, „Adres: …”); po filtrach (≥ 0,8 s życia, ≥ 80 px, lista ignorowanych tytułów) w 3. sesji nie wystąpiły | filtry zostają w prototypie |
| 2 | Miniatury | Zwykłe okna: PrintWindow 10/10 (śr. ~60 ms), WGC 10/10 (śr. ~150–190 ms, pierwsze ~520 ms). Obraz identyczny. Zminimalizowane: obie metody zawodzą (PrintWindow daje pasek 160×28, WGC brak klatki) | **PrintWindow** (szybsze, bez żółtej ramki WGC na Windows 10); zminimalizowane → ostatnia zapisana miniatura |
| 3 | Gest Ctrl+Shift + ruch ≥ 50 px | Wykryty za każdym razem (dystans 50–76 px) | bez zmian |
| 4 | Przełączanie okien | Zwykła metoda zadziałała w 8/9 prób, w 1 (blokada pierwszego planu) dopiero „trik z Alt”. Przywracanie zminimalizowanego działa | kolejność: zwykła → Alt |
| 5 | Rozszerzenie | Edge i Chrome połączone, lista kart, zdarzenia, miniatury aktywnej karty działają. Strony sklepu z dodatkami nie dają miniatury (ograniczenie przeglądarki) | favikona + tytuł jako zastępstwo |
| 6 | Aktywacja karty | Karta aktywowana zawsze, ale okno przeglądarki raz zostało za VS Code | backend dodatkowo wysuwa okno przeglądarki (poprawione w spike) |
| 7 | Siatka | 3840×2160 odczytane poprawnie (świadomość DPI) → 8×8; 27 nodów / 64 pola | bez zmian |

## Nie sprawdzone (brak sprzętu/konfiguracji w teście)

- okna na **innym pulpicie wirtualnym** (miniatury, przełączanie z wymianą pulpitu),
- **drugi monitor**,
- fałszywe wywołania gestu podczas dłuższej normalnej pracy.

Te punkty zostają do weryfikacji w prototypie.

## Poprawki w spike po teście

- licznik „unikalnych adresów” liczył karty ze wszystkich przeglądarek naraz – opis
  komunikatu poprawiony,
- `k <nr>`: jeśli rozszerzenie nie wysunie okna przeglądarki, robi to backend.
