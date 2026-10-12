# Test techniczny (spike)

Sprawdza na prawdziwym Windowsie pięć rzeczy ze specyfikacji (`docs/specyfikacja.md`,
pkt 11), zanim powstanie prototyp właściwy:

1. lista okien jak w Alt+Tab, ze wszystkich monitorów i pulpitów wirtualnych,
2. miniatury okien dwiema metodami (PrintWindow, Windows.Graphics.Capture),
3. gest Ctrl+Shift + ruch myszy ≥ 50 px,
4. przełączanie do okna (zminimalizowanego, z innego pulpitu, przy blokadzie pierwszego planu),
5. połączenie rozszerzenie Edge/Chrome → WebSocket → backend.

## Wymagania

- Windows 10 2004+ albo Windows 11,
- .NET 9 SDK (`dotnet --list-sdks` pokazuje `9.x`).

## Uruchomienie

Projekt najlepiej sklonować poza OneDrive (np. `C:\src\modul_mapa_aktywnych_okien`).

```
cd spike\SpikeTest
dotnet run -c Release
```

Program to interaktywna konsola – **okno siatki (Grid) jeszcze się nie pojawia** (powstanie
w prototypie właściwym). Po starcie program wypisuje rozdzielczość monitora głównego
i wynikający z niej rozmiar siatki; polecenie `l` pokazuje liczbę nodów względem liczby
pól i komunikat o przepełnieniu. Gest jest tylko wykrywany i zapisywany jako `[GEST #n]`;
polecenia z tabeli niżej wpisuje się w tej konsoli i zatwierdza Enterem. Wszystko, co wypisuje, trafia też do
`spike\SpikeTest\wyniki\raport.txt`; miniatury do `wyniki\miniatury\` i `wyniki\karty\`.

## Wyłączanie programu

Program działa, dopóki go nie zamkniesz – także gdy nic nie wpisujesz (w tle wykrywa
gest i zmiany okien). Wyłączenie, dowolnym sposobem:

- wpisz `q` i naciśnij Enter (zalecane – zapisuje koniec sesji w raporcie),
- naciśnij **Ctrl+C** w oknie konsoli,
- zamknij okno konsoli / terminala.

Po wyłączeniu nic nie zostaje uruchomione w tle (hooki myszy i zdarzeń okien znikają
razem z procesem).

## Rozszerzenie przeglądarki

1. Edge: `edge://extensions`, Chrome: `chrome://extensions`.
2. Włącz **Tryb dewelopera**.
3. **Załaduj rozpakowane** → wskaż folder `extension\` z repozytorium.
4. Przy uruchomionym teście w konsoli pojawi się `[ROZSZERZENIE] Połączono: edge` / `chrome`.

## Scenariusz (ok. 15 minut)

Przygotowanie: otwórz kilka różnych okien (Word, Excel, Eksplorator, Notatnik, Kalkulator),
**jedno zminimalizuj**, **jedno przenieś na drugi pulpit wirtualny** (Win+Tab), jeśli masz
drugi monitor – połóż tam jakieś okno. W przeglądarce otwórz kilka kart, w tym **dwie
z tym samym adresem**.

| Krok | Polecenie | Co sprawdzić |
|------|-----------|--------------|
| 1 | `l` | Czy lista zgadza się z Alt+Tab? Czy widać okna z drugiego pulpitu (kolumna `pulpit` = `INNY`) i drugiego monitora? Czy czegoś brakuje albo jest coś zbędnego? |
| 2 | `m` | Miniatury wszystkich okien obiema metodami. Obejrzyj `wyniki\miniatury\` – które są poprawne, które czarne? Na Windows 10 podczas WGC wokół okien na chwilę pojawia się żółta ramka – to oczekiwane; oceń, czy przeszkadza. |
| 3 | gest | Przytrzymaj Ctrl+Shift i rusz myszą – w konsoli powinno pojawić się `[GEST #n]`. Potem popracuj normalnie kilka minut (pisanie, zaznaczanie z Ctrl+Shift+strzałki, przeciąganie z Ctrl+Shift) i sprawdź, czy nie ma fałszywych wywołań. |
| 4 | `p <nr>` | Przełączenie do zwykłego okna, do **zminimalizowanego** i do okna z **innego pulpitu**. Czy pulpit się przełączył? |
| 5 | `pp <nr>` | Jak wyżej, ale w ciągu 3 s kliknij inne okno – to symuluje warunki docelowej aplikacji. Która metoda zadziałała? |
| 6 | `k`, `k <nr>` | Lista kart, liczba nodów (duplikaty adresu liczone raz), aktywacja karty. Zmieniaj karty – w `wyniki\karty\` powinny pojawiać się miniatury. |
| 7 | — | Otwieraj i zamykaj okna oraz karty – czy w konsoli pojawiają się `[+ OKNO]`, `[- OKNO]`, `[+ KARTA]`, `[- KARTA]`? Otwórz Firefoksa, jeśli jest – powinien pojawić się jednorazowy komunikat. |
| 8 | `q` | Koniec. |

## Co odesłać

- plik `wyniki\raport.txt`,
- kilka miniatur, zwłaszcza tych oznaczonych jako `CZARNY` / `JEDNOLITY` albo wyglądających źle,
- krótko: czy gest dawał fałszywe wywołania i czy była żółta ramka.

Raport i miniatury zawierają tytuły i zawartość Twoich okien – przejrzyj je przed wysłaniem
i usuń to, czego nie chcesz udostępniać.
