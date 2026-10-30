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
| `ksef status [-e test\|demo\|prod]` | nie | Komunikaty Latarni MF o dostępności KSeF + czy API środowiska odpowiada. Kod wyjścia: 0 dostępny, 2 KSeF niedostępny/prace, 1 błąd połączenia. |
| `ksef validate <pliki>...` | nie (offline) | Walidacja XML faktury względem schematu FA(3). Kod wyjścia: 0 wszystkie poprawne, 1 jest błąd. |

Domyślne środowisko: `test`.

## Faktury FA(3)

- `schemas/fa3/` — oficjalny schemat FA(3) z crd.gov.pl (`wzor/2025/06/25/13775`) z importami,
  `schemaLocation` przepisane na lokalne pliki. Pobiera `scripts/fetch-fa3-schemas.py`.
- `src/Ksef.Fa3/` — klasy C# wygenerowane ze schematu (`scripts/generate-fa3.sh`, narzędzie
  `dotnet-xscgen` z `dotnet-tools.json`), walidator XSD (`Fa3Schema`, schematy wbudowane w DLL)
  i odczyt/zapis (`Fa3Serializer`). Katalogu `Generated/` nie edytujemy ręcznie.
- Nowa wersja schematu: `scripts/fetch-fa3-schemas.py <URL> && scripts/generate-fa3.sh`, potem testy.

XSD sprawdza strukturę, nie logikę (sumy VAT, adnotacje) — tę trzeba sprawdzać osobno.

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
