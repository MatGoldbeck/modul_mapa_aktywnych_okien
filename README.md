# modul_mapa_aktywnych_okien

Globalna mapa wszystkich aktywnych okien Windows w postaci siatki (Grid) nodów z miniaturami.
Rozmiar siatki zależy od rozdzielczości ekranu (5×5 / 6×6 / 8×8).
Kliknięcie noda przełącza do okna; okno mapy wywołuje gest Ctrl+Shift + ruch myszy.

Osobna aplikacja, spójna technologicznie z DocMonitorem (`App_Info_o_aktywnym_pliku`):
backend .NET 9 + frontend Electron + wspólne rozszerzenie Edge/Chrome.

## Stan

- [x] Specyfikacja – [`docs/specyfikacja.md`](docs/specyfikacja.md)
- [ ] Test techniczny – [`spike/`](spike/README.md) (do uruchomienia na Windows)
- [ ] Prototyp właściwy

## Struktura

```
docs/        specyfikacja
spike/       test techniczny (konsola .NET 9)
extension/   rozszerzenie Edge/Chrome (Manifest V3)
```

## Wyłączanie programu

- **Test techniczny (`spike/`):** w konsoli wpisz `q` + Enter (albo Ctrl+C, albo zamknij okno
  konsoli). Szczegóły: [`spike/README.md`](spike/README.md#wyłączanie-programu).
- **Prototyp (docelowo):** prawy przycisk na ikonie w zasobniku → **Zakończ**. Esc i klik
  w nod tylko chowają okno siatki – program dalej działa w tle.
