# NovaBrowser — NovaBlock 2.0, RAM Saver 2.0, Nova Private

Ta gałąź bazuje na `feature/library-panels-refresh-v3` i **nie zmienia** `main`.

## NovaBlock 2.0
- Wbudowany zestaw domen reklamowych i śledzących działa od uruchomienia.
- Przycisk **Aktualizuj listy filtrów** pobiera EasyList i EasyPrivacy HTTPS z limitem 8 MiB na plik.
- Parser obsługuje **wyłącznie prosty, pozbawiony opcji** wzorzec ABP `||hostname^`.
- **Nie** obsługuje: wyjątków typu `@@`, filtrów kosmetycznych, ścieżek, modyfikatorów `$...`, skryptletów ani pełnej semantyki uBlock Origin. Te reguły są pomijane.
- Wyjątki domenowe dotyczą **witryny głównej**, nie zewnętrznego zasobu.
- Licznik dotyczy odrzuconych żądań, a nie liczby usuniętych elementów wizualnych.
- `novablock-rules.txt` trafia do LocalAppData i nie powinien być commitowany.

## RAM Saver 2.0
- Aktywna karta ma cel pamięci `Normal`.
- Zbalansowany: po 3 min w tle — `Low`; Oszczędny: po 1 min; Wydajność: `Normal`.
- Karty odtwarzające dźwięk oraz wszystkie karty podczas aktywnego pobierania zostają w `Normal`.
- Silnik karty jest tworzony leniwie, a inicjalizacja tej samej karty jest serializowana.
- Pasek stanu pokazuje **liczbę celów Low**, a nie zmierzoną liczbę MB.
- **Nie ma automatycznego odrzucania/ponownego ładowania kart:** unika się ryzyka utraty formularzy, bez wiarygodnego mechanizmu ochrony niezapisanych danych.

## Nova Private
- Otwórz przez **Ctrl+Shift+N** albo ustawienia.
- Oddzielne okno z własnymi kartami i nawigacją, bez zależności od usług SQLite historii/sesji/zakładek.
- WebView2 używa `CoreWebView2ControllerOptions.IsInPrivateModeEnabled = true` i nazwanej odizolowanej sesji `NovaPrivate`.
- Nie ma automatycznego przejścia na zwykły profil, jeśli InPrivate nie jest dostępny.
- Tryb prywatny nie ukrywa ruchu przed witryną/dostawcą internetu; zapisane przez użytkownika pliki pobrane pozostają na dysku.

## Testy ręczne przed scaleniem
1. `dotnet build -c Release -p:Platform=x64`, następnie `dotnet run`.
2. Otwórz kilka kart; sprawdź `+`, przełączanie, zamykanie i przywracanie sesji.
3. W ustawieniach włącz/wyłącz NovaBlock, zaktualizuj filtry, przetestuj licznik i wyjątek dla jednej witryny oraz restart przeglądarki.
4. Przetestuj stronę z reklamami i stronę z formularzem, na której blokowanie może powodować problemy.
5. Odtwarzaj dźwięk w karcie w tle przez ponad 3 min; sprawdź, czy karta zachowuje cel `Normal`.
6. Pobierz plik podczas wielu nieaktywnych kart; sprawdź ochronę pobierania.
7. Otwórz `Ctrl+Shift+N`, zaloguj się w prywatnym oknie i sprawdź, że zwykłe okno nie widzi prywatnej sesji; odwiedzone adresy nie powinny pojawić się w zwykłej historii.
8. Otwórz drugie prywatne okno/kartę, zamknij wszystkie prywatne okna i uruchom nowe, sprawdzając czyszczenie prywatnej sesji.
9. Testuj zarówno motyw jasny, jak i ciemny, oraz minimalizowanie/maksymalizowanie okien.
10. Pamięć sprawdzaj w Menedżerze zadań **łącznie z procesami msedgewebview2.exe** na tych samych stronach i liczbie kart. Bez porównywalnych pomiarów nie deklaruj konkretnych oszczędności.

CI GitHub Actions weryfikuje kompilację WPF na Windows, ale **nie** klika GUI ani nie wykonuje testów WebView2 w przeglądarce.
