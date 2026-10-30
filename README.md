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

Domyślne środowisko: `test`.

## Aktualizacja biblioteki MF

```bash
git -C external/ksef-client-csharp fetch --tags
git -C external/ksef-client-csharp checkout <tag>     # np. 2.10.0
git add external/ksef-client-csharp
```

Przed zmianą tagu przejrzyj `RELEASE_NOTES.md` biblioteki.

## Sekrety

Token / certyfikat KSeF trzymamy w menedżerze sekretów, nigdy w repo ani w plikach konfiguracyjnych.
