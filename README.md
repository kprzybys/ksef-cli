# ksef-app

CLI do obsługi KSeF 2.0 na oficjalnej bibliotece MF
[`CIRFMF/ksef-client-csharp`](https://github.com/CIRFMF/ksef-client-csharp) (MIT),
budowanej ze źródeł — bez paczek z GitHub Packages.

## Wymagania

- .NET SDK 10 (`sudo apt install dotnet-sdk-10.0`). SDK 8 nie zbuduje biblioteki: jej projekty
  deklarują net8.0, net9.0 i net10.0 jednocześnie.
- Native AOT (`make aot`): `clang` i `zlib1g-dev`.
- `ksef show`: libxslt (`libxslt1.1`, w praktyce zawsze zainstalowany).

## Budowanie

```bash
git clone --recurse-submodules=external/ksef-client-csharp <repo>   # albo: git submodule update --init external/ksef-client-csharp
dotnet build src/KsefCli -c Release
src/KsefCli/bin/Release/net10.0/ksef --help
```

## Native AOT

```bash
make aot          # dist/aot/ksef — jedna natywna binarka, bez .NET na maszynie docelowej
make install      # ~/.local/bin/ksef
make test-aot     # testy CLI (z E2E na KSeF TEST) na binarce AOT
```

Start ok. 15 ms zamiast ok. 250 ms w JIT. Biblioteka MF nie wspiera AOT
([CIRFMF/ksef-client-csharp#6](https://github.com/CIRFMF/ksef-client-csharp/issues/6)), więc dostosowujemy ją
z zewnątrz, bez forka:

- JSON: konteksty generowane przy kompilacji w `src/KsefCli/KsefJson.cs` — także dla biblioteki MF, której
  prywatne opcje (`JsonUtil._settings`) podmieniamy po każdej rejestracji klienta (`KsefServices.AddKsefClient`).
  Ten sam kod działa w JIT, więc brak typu w kontekście wychodzi już w zwykłych testach.
  **Każdy nowy typ JSON** (żądanie/odpowiedź MF w nowej komendzie, rekord wyniku `--json`) dopisujemy do
  kontekstu. Brakujące typy wypisze tryb odkrywania:
  `KSEF_JSON_DISCOVER=/tmp/braki.txt KSEF_E2E=1 dotnet test --project tests/KsefCli.Tests`.
- Podpis XAdES: algorytmy, które `SignedXml` tworzy po nazwie, rejestrujemy typem (`XmlDsigAlgorithms.cs`);
  model żądania logowania dla `XmlSerializer` chroni przed trimmerem `ILLink.Descriptors.xml`. Oba sprawdza
  bez sieci `LoginOfflineTests` (logowanie przeciw fałszywemu KSeF, podpis weryfikowany po stronie serwera).
- Podgląd HTML: libxslt przez P/Invoke (`src/Ksef.Fa3/LibXslt.cs`) zamiast `XslCompiledTransform`, który
  generuje kod w czasie działania (przy okazji `show` trwa 0,1 s zamiast 1,7 s).
- Analizatory AOT działają przy każdym buildzie; refleksja w naszym kodzie to błąd kompilacji.
- Ostrzeżenia kompilatora AOT o bibliotece MF są spisane w `src/KsefCli/aot-warnings.txt` (z opisem, co je
  obsługuje). Nowe ostrzeżenie — np. po aktualizacji MF — przerywa `make aot`: trzeba je obsłużyć, przejść
  `make test-aot` i dopiero wtedy dopisać do listy.
- W `dist/aot/` jest tylko `ksef`: bez symboli natywnych (ślady stosu wyjątków działają bez nich) i bez
  `libSkiaSharp` (kody QR biblioteki MF, nieużywane).

Zagnieżdżonego submodułu biblioteki (`ksef-pdf-generator`, używany tylko przez jej aplikację testową)
nie trzeba pobierać.

## Komendy

| Komenda | Logowanie | Opis |
|---|---|---|
| `ksef status [-e test\|demo\|prod] [--json]` | nie | Komunikaty Latarni MF o dostępności KSeF + czy API środowiska odpowiada. Kod wyjścia: 0 dostępny, 2 KSeF niedostępny/prace, 1 błąd połączenia. |
| `ksef validate [pliki\|-]... [-v] [--json]` | nie (offline) | Kontrola przed wysyłką: wymagania KSeF (UTF-8 bez BOM, kodowanie w prologu, brak instrukcji przetwarzania, niezalecane znaki Unicode, limit 1 MB / 3 MB z załącznikiem) + schemat FA(3). Przy sukcesie milczy; błędy na stderr jako `plik:wiersz:kolumna: ...`. Kod wyjścia: 0 wszystkie poprawne, 1 jest błąd. |
| `ksef show [plik\|-] [-o plik.html]` | nie (offline) | Podgląd faktury w HTML oficjalnym arkuszem MF (`styl.xsl`), domyślnie na stdout. |
| `ksef cert new --nip <NIP> [--seal] [-o plik.pem]` | nie (offline) | Certyfikat testowy podpisany samym sobą (osoba `TINPL-NIP` albo pieczęć `VATPL-NIP`) jako PEM z kluczem prywatnym. Tylko dla środowiska TEST. Nie pisze klucza na terminal ani nie nadpisuje pliku; plik ma prawa 0600. |
| `ksef login --nip <NIP> --cert <plik.pem\|->` | — | Logowanie certyfikatem (podpis XAdES wyzwania KSeF) w kontekście NIP-u. Przy sukcesie milczy. |
| `ksef logout [--nip <NIP>]` | tak | Unieważnia sesję w KSeF i usuwa ją lokalnie. |
| `ksef session [-a] [--json]` | nie | Lokalne sesje: środowisko, NIP, ważność (bez tokenów). Kod wyjścia 1, gdy brak sesji. |
| `ksef list [--subject buyer\|seller\|third\|authorized] [--from D] [--to D] [--date-type issue\|invoicing\|storage] [--json]` | tak | Metadane faktur z KSeF. Domyślnie faktury otrzymane (`buyer`) od 1. dnia bieżącego miesiąca. TSV bez nagłówka: numer KSeF, numer faktury, data wystawienia, NIP i nazwa sprzedawcy, identyfikator i nazwa nabywcy, brutto, waluta. Daty `RRRR-MM-DD` (czas lokalny, „do” włącznie) albo ISO 8601. Zakres dłuższy niż limit KSeF (100 dni) dzielony automatycznie; wynik obcięty przez KSeF do 10 000 pozycji pobierany dalej od ostatniej daty, bez powtórek. Pusty wynik to kod 0. |
| `ksef send [pliki\|-]... [--json]` | tak | Wysyła faktury FA(3) w jednej sesji interaktywnej i czeka na numery KSeF. Najpierw ta sama kontrola co `validate` — jeśli któryś plik jej nie przejdzie, nie wysyła nic. Wynik TSV: numer KSeF, plik, numer sesji (do UPO); odrzucone (np. 440 duplikat z `originalKsefNumber`) na stderr. Kod wyjścia: 0 wszystkie przyjęte, 1 jest błąd. |
| `ksef get [numery\|-]... [-d katalog]` | tak | XML faktur po numerze KSeF — bajt w bajt to, co wysłał wystawca. Numery z argumentów albo ze stdin (pierwsza kolumna TSV, więc `ksef list \| ksef get -d faktury/` działa bez `cut`). Jeden numer bez `-d`: XML na stdout. Z `-d`: pliki `<numer KSeF>.xml` (zapis atomowy), ścieżki na stdout; już pobrane pomija bez sieci. Faktura przyjęta przed chwilą, jeszcze niedostępna (KSeF 21165), jest pobierana ponownie do 1 min. Kod wyjścia: 0 wszystkie pobrane, 1 jest błąd. |
| `ksef upo [numery\|-]... [--session S] [--collective] [-d katalog]` | tak | Urzędowe Poświadczenie Odbioru (XML z podpisem MF). Dla faktur: numer KSeF + numer sesji wysyłki — wejście w formacie `ksef send` (numer KSeF, plik, sesja), więc `ksef send *.xml \| ksef upo -d upo/` działa wprost; numery bez kolumny sesji biorą `--session`. `--collective`: zbiorcze UPO sesji. UPO powstaje po zamknięciu sesji — komenda czeka, aż będzie gotowe (do 3 min). Jedno UPO na stdout, kilka do `-d` (`<numer KSeF>.upo.xml`, `<sesja>.upo.xml`). Kod wyjścia: 0 wszystkie pobrane, 1 jest błąd. |

## Zasady

`ksef` robi jedną rzecz: przenosi faktury między plikami a KSeF (plus offline kontrola i podgląd pliku FA(3)).
Nie wystawia faktur, nie księguje i nie trzyma własnej bazy — to zadania innych narzędzi.

- wynik na stdout, komunikaty i błędy na stderr (z prefiksem `ksef:`), przy sukcesie bez zbędnych komunikatów;
- `-` albo brak pliku oznacza stdin/stdout, więc komendy łączą się w potoki;
- `--json` daje wynik dla skryptów (`jq`), domyślnie zwięzły tekst dla człowieka;
- stałe kody wyjścia, żadnych pytań interaktywnych;
- sekrety nigdy w argumentach wywołania.

```bash
ksef validate faktury/*.xml && echo wszystkie poprawne
ksef show faktura.xml > faktura.html && xdg-open faktura.html
ksef status --json | jq -r .lighthouse.status
ksef list --from 2026-01-01 | cut -f1                  # numery KSeF faktur otrzymanych
ksef list --subject seller --json | jq 'map(.grossAmount) | add'
ksef send faktury/*.xml > wyslane.tsv                   # numer KSeF, plik, sesja
ksef list | ksef get -d otrzymane/                      # pobiera tylko nowe faktury otrzymane
ksef send faktury/*.xml | tee wyslane.tsv | ksef upo -d upo/   # wysyłka + UPO każdej faktury
```

Limity KSeF: 429 z krótkim czasem (np. „8 żądań na sekundę”) komendy przeczekują same (do 3 ponowień,
czas z `Retry-After`); długi limit (np. „20 zapytań o metadane na godzinę”) kończy komendę od razu komunikatem
KSeF z czasem do ponowienia. Każde 90 dni zakresu `ksef list` to co najmniej jedno zapytanie — długich zakresów
nie warto odpytywać często. Przekroczenie czasu odpowiedzi HTTP (5 min) kończy komendę komunikatem i kodem 1.

Wyszukiwanie metadanych w KSeF jest ostatecznie spójne: faktura przyjęta przed chwilą bywa widoczna
w `ksef list` dopiero po kilku sekundach.

Domyślne środowisko: `test`.

## Logowanie

```bash
ksef cert new --nip 5908996669 -o ~/.config/ksef/test-cert.pem   # raz; tylko TEST
ksef login --nip 5908996669 --cert ~/.config/ksef/test-cert.pem
ksef session
ksef logout
```

Certyfikat można też podać na stdin (`--cert -`), np. prosto z sejfu, więc `ksef` nie musi wiedzieć,
gdzie leży klucz. Na DEMO i PROD potrzebny jest certyfikat kwalifikowany albo certyfikat KSeF.

Sesje (access token ważny ok. 15 min, refresh token 7 dni) to stan narzędzia, nie trwały sekret:
`$XDG_RUNTIME_DIR/ksef/<środowisko>-<NIP>.json` z prawami 0600 — katalog w RAM, znika po wylogowaniu
z systemu. Komendy wymagające logowania same odświeżają access token. Przy kilku sesjach w jednym
środowisku wskazuje się `--nip`.

## Faktury FA(3)

- `schemas/fa3/` — oficjalny schemat FA(3) z crd.gov.pl (`wzor/2025/06/25/13775`) z importami.
- `stylesheets/fa3/` — oficjalny arkusz wizualizacji `styl.xsl` (XSLT 1.0) ze wspólnymi szablonami
  i słownikami (kraje, waluty, urzędy) czytanymi przez `document()`.
- Oba katalogi pobiera `scripts/fetch-fa3-schemas.py`; odwołania są przepisane na lokalne nazwy plików,
  a pliki wbudowane w `Ksef.Fa3.dll` — walidacja i podgląd nie sięgają do sieci.
- `src/Ksef.Fa3/` — klasy C# wygenerowane ze schematu (`scripts/generate-fa3.sh`, narzędzie
  `dotnet-xscgen` z `dotnet-tools.json`), walidator XSD (`Fa3Schema`),
  wymagania KSeF ponad XSD (`KsefDocumentRules`, wg `ksef-api/faktury/weryfikacja-faktury.md`),
  oba naraz (`Fa3Validator`), podgląd HTML (`Fa3Html`, libxslt) i odczyt/zapis (`Fa3Serializer`). Katalogu `Generated/` nie edytujemy ręcznie.
- Nowa wersja schematu: `scripts/fetch-fa3-schemas.py <URL> && scripts/generate-fa3.sh`, potem testy.

XSD sprawdza strukturę, nie logikę (sumy VAT, adnotacje) — tę trzeba sprawdzać osobno.
Duplikatów (NIP sprzedawcy + `RodzajFaktury` + `P_2`) nie sprawdzamy lokalnie — `ksef send` zgłasza
odrzucenie przez KSeF (błąd 440) z numerem KSeF i sesją oryginału, bez własnego rejestru wysłanych faktur.

Do KSeF wysyłamy dokładnie zwalidowane bajty: KSeF porównuje skrót SHA-256 i rozmiar pliku, więc
po walidacji pliku nie wolno przeformatowywać.

## Testy

```bash
dotnet test --project tests/Ksef.Fa3.Tests      # model FA(3), walidacja, podgląd — offline
dotnet test --project tests/KsefCli.Tests       # CLI — offline (testy na KSeF TEST pominięte)
KSEF_E2E=1 dotnet test --project tests/KsefCli.Tests   # + testy na żywym KSeF TEST
make test-aot                                    # testy CLI z E2E na binarce Native AOT (KSEF_BIN=dist/aot/ksef)
```

- `Ksef.Fa3.Tests` wypełniają przykładowe faktury MF z submodułu, walidują je i przepuszczają przez wygenerowane klasy.
- `KsefCli.Tests` uruchamiają zbudowany `ksef` jako proces (stdout/stderr, kody wyjścia, stdin, prawa plików,
  odmowa wypisania klucza na terminal przez `script`) oraz sprawdzają NIP i magazyn sesji, w tym odświeżanie tokenu.
- Przebiegi `send`/`get`/`upo` bez sieci: `ksef` rozmawia przez bibliotekę MF z lokalnym fałszywym KSeF
  (`tests/KsefCli.Tests/FakeKsef.cs`, adres podany w `KSEF_API_URL`). Fałszywy serwer odszyfrowuje wysłane faktury
  i porównuje je z plikami, a do tego zrywa połączenie w połowie wysyłki, odrzuca faktury (440 z numerem oryginału),
  zwraca limity 429, „faktura jeszcze niedostępna” (21165), sesje bez UPO i niebezpieczne numery stron UPO.
  `make test-aot` uruchamia je także na binarce AOT.
- Testy E2E (`KSEF_E2E=1`) logują się na KSeF TEST certyfikatami podpisanymi samym sobą, każdy na losowy NIP:
  pełny cykl login/session/logout, certyfikat ze stdin, pieczęć, odrzucenie certyfikatu na inny NIP,
  odświeżenie wygasłego access tokenu i to, że po `logout` refresh token przestaje działać;
  wysyłka faktur (kolejność, stdin, JSON, duplikat 440 obok nowej faktury), widoczność wysłanych faktur
  w `ksef list` po stronie sprzedawcy i — po zalogowaniu na NIP nabywcy — po stronie nabywcy;
  pobieranie faktur (bajty i SHA-256 zgodne z wysłanymi) i UPO (pojedyncze, z potoku `send`, zbiorcze),
  z weryfikacją podpisu MF pod każdym UPO.

## Aktualizacja biblioteki MF

```bash
git -C external/ksef-client-csharp fetch --tags
git -C external/ksef-client-csharp checkout <tag>     # np. 2.10.0
git add external/ksef-client-csharp
```

Przed zmianą tagu przejrzyj `RELEASE_NOTES.md` biblioteki, a po zmianie uruchom `make test-aot`
(wychodzą nowe typy JSON i zmiany wewnętrzne, na których opiera się dostosowanie do AOT). W `src/KsefCli/KsefCli.csproj` są przypięte
`System.Security.Cryptography.Pkcs/Xml` 10.0.14 (2.9.0 ciągnie 10.0.10 z lukami DoS) — usuń, gdy biblioteka
podbije zależności. Ostrzeżenia NU1902/NU1903 przy budowaniu dotyczą projektu biblioteki; binarka `ksef`
dostaje wersje poprawione.

## Sekrety

Token / certyfikat KSeF trzymamy w menedżerze sekretów, nigdy w repo ani w plikach konfiguracyjnych.
