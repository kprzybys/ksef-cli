# ksef-cli — rozwój

Budowanie ze źródeł, Native AOT, model FA(3), testy i bramka jakości. Instalacja dla użytkowników:
[INSTALL.md](INSTALL.md); komendy: [COMMANDS.md](COMMANDS.md).

## Wymagania

- .NET SDK 10 (`sudo apt install dotnet-sdk-10.0`). SDK 8 nie zbuduje biblioteki: jej projekty
  deklarują net8.0, net9.0 i net10.0 jednocześnie.
- Native AOT (`make aot`): `clang` i `zlib1g-dev`.
- `ksef show`: libxslt (`libxslt1.1`; w minimalnych obrazach, kontenerach i obrazach chmurowych bywa nieobecny).

## Budowanie

```bash
git clone --recurse-submodules=external/ksef-client-csharp https://github.com/kprzybys/ksef-cli.git   # albo: git submodule update --init external/ksef-client-csharp
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

## Faktury FA(3)

- `schemas/fa3/` — oficjalny schemat FA(3) z crd.gov.pl (`wzor/2025/06/25/13775`) z importami.
- `stylesheets/fa3/` — oficjalny arkusz wizualizacji `styl.xsl` (XSLT 1.0) ze wspólnymi szablonami
  i słownikami (kraje, waluty, urzędy) czytanymi przez `document()`.
- Oba katalogi pobiera `scripts/fetch-fa3-schemas.py`; odwołania są przepisane na lokalne nazwy plików,
  a pliki wbudowane w `Ksef.Fa3.dll` — walidacja i podgląd nie sięgają do sieci.
- `src/Ksef.Fa3/` — klasy C# wygenerowane ze schematu (`scripts/generate-fa3.sh`, narzędzie
  `dotnet-xscgen` z `dotnet-tools.json`), walidator XSD (`Fa3Schema`),
  wymagania KSeF ponad XSD (`KsefDocumentRules`, wg [dokumentacji MF: weryfikacja faktury](https://github.com/CIRFMF/ksef-api/blob/main/faktury/weryfikacja-faktury.md)),
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
  (`tests/KsefCli.Tests/FakeKsef.cs`, adres podany w `KSEF_API_URL` — `ksef` przyjmuje tylko adres na tym komputerze, np. `http://127.0.0.1:…`). Fałszywy serwer odszyfrowuje wysłane faktury
  i porównuje je z plikami, a do tego zrywa połączenie w połowie wysyłki, odrzuca faktury (440 z numerem oryginału),
  zwraca limity 429, „faktura jeszcze niedostępna” (21165), „nie znaleziono UPO” (21178), sesje otwarte i bez UPO
  oraz niebezpieczne numery stron UPO.
  `make test-aot` uruchamia je także na binarce AOT.
- Testy E2E (`KSEF_E2E=1`) logują się na KSeF TEST certyfikatami podpisanymi samym sobą, każdy na losowy NIP:
  pełny cykl login/session/logout, certyfikat ze stdin, pieczęć, odrzucenie certyfikatu na inny NIP,
  odświeżenie wygasłego access tokenu i to, że po `logout` refresh token przestaje działać;
  wysyłka faktur (kolejność, stdin, JSON, duplikat 440 obok nowej faktury), widoczność wysłanych faktur
  w `ksef list` po stronie sprzedawcy i — po zalogowaniu na NIP nabywcy — po stronie nabywcy;
  pobieranie faktur (bajty i SHA-256 zgodne z wysłanymi) i UPO (pojedyncze, z potoku `send`, zbiorcze),
  z weryfikacją podpisu MF pod każdym UPO.

## Jakość kodu

```bash
make hooks   # raz po sklonowaniu: bramka pre-commit
make fmt     # białe znaki w C# (dotnet format)
make lint    # dotnet format --verify-no-changes + pre-commit na całym repo
make audit   # znane luki w pakietach naszych projektów (także przechodnich)
make check   # lint + build + test — przed commitem
```

- Każdy `dotnet build` uruchamia analizatory Roslyn (`latest-recommended`, reguły bezpieczeństwa `All`),
  Meziantou.Analyzer i NuGetAudit; ostrzeżenie przerywa budowanie (`Directory.Build.props`).
  Wyłączone reguły z uzasadnieniem są w `.editorconfig`. Nie dotyczy to submodułu biblioteki MF
  ani publikacji AOT (tam ostrzeżenia ILC pilnuje `aot-warnings.txt`).
- Napisy w kodzie produkcyjnym: porównania i sortowanie z `StringComparer.Ordinal` / `StringComparison.Ordinal`,
  liczby i daty z `CultureInfo.InvariantCulture` — kwoty i daty faktur nie mogą zależeć od ustawień systemu.
- pre-commit: higiena plików, `detect-private-key`, gitleaks, blokada `*.pfx/*.p12/*.pem/*.key`,
  `dotnet format`, shellcheck, ruff i markdownlint. Wymaga `uv`, `gitleaks` i `dotnet` w `PATH`.
  `schemas/`, `stylesheets/` i `src/Ksef.Fa3/Generated/` zostają bajt w bajt jak w źródle.

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
