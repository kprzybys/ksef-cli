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
| `ksef cert new --nip <NIP> [--seal] [-o plik.pem]` | nie (offline) | Certyfikat testowy podpisany samym sobą (osoba `TINPL-NIP` albo pieczęć `VATPL-NIP`) jako PEM z kluczem prywatnym. Tylko dla środowiska TEST. Nie pisze klucza na terminal ani nie nadpisuje pliku; plik ma prawa 0600. |
| `ksef login --nip <NIP> --cert <plik.pem\|->` | — | Logowanie certyfikatem (podpis XAdES wyzwania KSeF) w kontekście NIP-u. Przy sukcesie milczy. |
| `ksef logout [--nip <NIP>]` | tak | Unieważnia sesję w KSeF i usuwa ją lokalnie. |
| `ksef session [-a] [--json]` | nie | Lokalne sesje: środowisko, NIP, ważność (bez tokenów). Kod wyjścia 1, gdy brak sesji. |
| `ksef list [--subject buyer\|seller\|third\|authorized] [--from D] [--to D] [--date-type issue\|invoicing\|storage] [--json]` | tak | Metadane faktur z KSeF. Domyślnie faktury otrzymane (`buyer`) od 1. dnia bieżącego miesiąca. TSV bez nagłówka: numer KSeF, numer faktury, data wystawienia, NIP i nazwa sprzedawcy, identyfikator i nazwa nabywcy, brutto, waluta. Daty `RRRR-MM-DD` (czas lokalny, „do” włącznie) albo ISO 8601. Zakres dłuższy niż limit KSeF (100 dni) dzielony automatycznie; wynik obcięty przez KSeF do 10 000 pozycji pobierany dalej od ostatniej daty, bez powtórek. Pusty wynik to kod 0. |

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
```

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
  oba naraz (`Fa3Validator`), podgląd HTML (`Fa3Html`) i odczyt/zapis (`Fa3Serializer`). Katalogu `Generated/` nie edytujemy ręcznie.
- Nowa wersja schematu: `scripts/fetch-fa3-schemas.py <URL> && scripts/generate-fa3.sh`, potem testy.

XSD sprawdza strukturę, nie logikę (sumy VAT, adnotacje) — tę trzeba sprawdzać osobno.
Nie sprawdzamy jeszcze duplikatów (NIP sprzedawcy + `RodzajFaktury` + `P_2`, błąd KSeF 440) — wymaga
lokalnego rejestru wysłanych faktur.

Do KSeF wysyłamy dokładnie zwalidowane bajty: KSeF porównuje skrót SHA-256 i rozmiar pliku, więc
po walidacji pliku nie wolno przeformatowywać.

## Testy

```bash
dotnet test --project tests/Ksef.Fa3.Tests      # model FA(3), walidacja, podgląd — offline
dotnet test --project tests/KsefCli.Tests       # CLI — offline (testy na KSeF TEST pominięte)
KSEF_E2E=1 dotnet test --project tests/KsefCli.Tests   # + testy na żywym KSeF TEST
```

- `Ksef.Fa3.Tests` wypełniają przykładowe faktury MF z submodułu, walidują je i przepuszczają przez wygenerowane klasy.
- `KsefCli.Tests` uruchamiają zbudowany `ksef` jako proces (stdout/stderr, kody wyjścia, stdin, prawa plików,
  odmowa wypisania klucza na terminal przez `script`) oraz sprawdzają NIP i magazyn sesji, w tym odświeżanie tokenu.
- Testy E2E (`KSEF_E2E=1`) logują się na KSeF TEST certyfikatami podpisanymi samym sobą, każdy na losowy NIP:
  pełny cykl login/session/logout, certyfikat ze stdin, pieczęć, odrzucenie certyfikatu na inny NIP,
  odświeżenie wygasłego access tokenu i to, że po `logout` refresh token przestaje działać.

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
