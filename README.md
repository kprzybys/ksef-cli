# ksef-app

CLI do obsługi KSeF 2.0 na oficjalnej bibliotece MF
[`CIRFMF/ksef-client-csharp`](https://github.com/CIRFMF/ksef-client-csharp) (MIT),
budowanej ze źródeł — bez paczek z GitHub Packages.

## Wymagania

- .NET SDK 10 (`sudo apt install dotnet-sdk-10.0`). SDK 8 nie zbuduje biblioteki: jej projekty
  deklarują net8.0, net9.0 i net10.0 jednocześnie.

## Budowanie

```bash
git clone --recurse-submodules=external/ksef-client-csharp <repo>   # albo: git submodule update --init external/ksef-client-csharp
dotnet build src/KsefCli -c Release
src/KsefCli/bin/Release/net10.0/ksef --help
```

Zagnieżdżonego submodułu biblioteki (`ksef-pdf-generator`, używany tylko przez jej aplikację testową)
nie trzeba pobierać.

## Komendy

| Komenda | Logowanie | Opis |
|---|---|---|
| `ksef status [-e test\|demo\|prod] [--json]` | nie | Komunikaty Latarni MF o dostępności KSeF + czy API środowiska odpowiada. Kod wyjścia: 0 dostępny, 2 KSeF niedostępny/prace, 1 błąd połączenia. |
| `ksef validate [pliki\|-]... [-v] [--json]` | nie (offline) | Kontrola przed wysyłką: wymagania KSeF (UTF-8 bez BOM, kodowanie w prologu, brak instrukcji przetwarzania, niezalecane znaki Unicode, limit 1 MB / 3 MB z załącznikiem) + schemat FA(3). Przy sukcesie milczy; błędy na stderr jako `plik:wiersz:kolumna: ...`. Kod wyjścia: 0 wszystkie poprawne, 1 jest błąd. |
| `ksef show [plik\|-] [-o plik.html]` | nie (offline) | Podgląd faktury w HTML oficjalnym arkuszem MF (`styl.xsl`), domyślnie na stdout. |

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
```

Domyślne środowisko: `test`.

## Faktury FA(3)

- `schemas/fa3/` — oficjalny schemat FA(3) z crd.gov.pl (`wzor/2025/06/25/13775`) z importami.
- `stylesheets/fa3/` — oficjalny arkusz wizualizacji `styl.xsl` (XSLT 1.0) ze wspólnymi szablonami
  i słownikami (kraje, waluty, urzędy) czytanymi przez `document()`.
- Oba katalogi pobiera `scripts/fetch-fa3-schemas.py`; odwołania są przepisane na lokalne nazwy plików,
  a pliki wbudowane w `Ksef.Fa3.dll` — walidacja i podgląd nie sięgają do sieci.
- `src/Ksef.Fa3/` — klasy C# wygenerowane ze schematu (`scripts/generate-fa3.sh`, narzędzie
  `dotnet-xscgen` z `dotnet-tools.json`), walidator XSD (`Fa3Schema`),
  wymagania KSeF ponad XSD (`KsefDocumentRules`, wg `ksef-api/faktury/weryfikacja-faktury.md`),
  oba naraz (`Fa3Validator`), podgląd HTML (`Fa3Html`) i odczyt/zapis (`Fa3Serializer`). Katalogu `Generated/` nie edytujemy ręcznie.
- Nowa wersja schematu: `scripts/fetch-fa3-schemas.py <URL> && scripts/generate-fa3.sh`, potem testy.

XSD sprawdza strukturę, nie logikę (sumy VAT, adnotacje) — tę trzeba sprawdzać osobno.
Nie sprawdzamy jeszcze duplikatów (NIP sprzedawcy + `RodzajFaktury` + `P_2`, błąd KSeF 440) — wymaga
lokalnego rejestru wysłanych faktur.

Do KSeF wysyłamy dokładnie zwalidowane bajty: KSeF porównuje skrót SHA-256 i rozmiar pliku, więc
po walidacji pliku nie wolno przeformatowywać.

## Testy

```bash
dotnet test --project tests/Ksef.Fa3.Tests
```

Testy wypełniają przykładowe faktury MF z submodułu, walidują je i przepuszczają przez wygenerowane klasy.

## Aktualizacja biblioteki MF

```bash
git -C external/ksef-client-csharp fetch --tags
git -C external/ksef-client-csharp checkout <tag>     # np. 2.10.0
git add external/ksef-client-csharp
```

Przed zmianą tagu przejrzyj `RELEASE_NOTES.md` biblioteki. W `src/KsefCli/KsefCli.csproj` są przypięte
`System.Security.Cryptography.Pkcs/Xml` 10.0.14 (2.9.0 ciągnie 10.0.10 z lukami DoS) — usuń, gdy biblioteka
podbije zależności. Ostrzeżenia NU1902/NU1903 przy budowaniu dotyczą projektu biblioteki; binarka `ksef`
dostaje wersje poprawione.

## Sekrety

Token / certyfikat KSeF trzymamy w menedżerze sekretów, nigdy w repo ani w plikach konfiguracyjnych.
