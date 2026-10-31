# ksef-cli

**Krajowy System e-Faktur z wiersza poleceń.** Wysyłanie, odbieranie, walidacja i podgląd faktur FA(3)
w KSeF 2.0 — na oficjalnej bibliotece Ministerstwa Finansów, w jednej natywnej binarce dla Linuksa.

[![CI](https://github.com/kprzybys/ksef-cli/actions/workflows/ci.yml/badge.svg)](https://github.com/kprzybys/ksef-cli/actions/workflows/ci.yml)
[![Wydanie](https://img.shields.io/github/v/release/kprzybys/ksef-cli?label=wydanie)](https://github.com/kprzybys/ksef-cli/releases)
[![Licencja: MIT](https://img.shields.io/badge/licencja-MIT-blue)](LICENSE)
![Linux x64 | arm64](https://img.shields.io/badge/Linux-x64%20%7C%20arm64-informational)

*A command-line client for Poland's national e-invoicing system (KSeF 2.0), built on the official Ministry
of Finance library and shipped as a single native binary for Linux.*

```console
$ ksef validate faktury/*.xml && ksef send faktury/*.xml | tee wyslane.tsv | ksef upo -d upo/
$ ksef list --from 2026-10-01 | ksef get -d otrzymane/
$ ksef show faktura.xml -e prod --ksef-number 1234563218-20261031-0A1B2C3D4E5F-6A > faktura.html
```

## Dlaczego ksef-cli

- **Oficjalna biblioteka MF.** Protokół, szyfrowanie i podpisy robi
  [`CIRFMF/ksef-client-csharp`](https://github.com/CIRFMF/ksef-client-csharp), budowana ze źródeł —
  nie własna, nieoficjalna implementacja API.
- **Jedna binarka, bez .NET.** Native AOT, ok. 15 MB, start w ok. 15 ms. Działa na Ubuntu 22.04+,
  Debianie 12+, RHEL/Alma 9+, Fedorze, Archu i openSUSE — x64 i arm64 (także Raspberry Pi).
- **Po uniksowemu.** Wynik na stdout, błędy na stderr, `-` to stdin/stdout, `--json` dla skryptów, stałe kody
  wyjścia, zero pytań interaktywnych — komendy łączą się w potoki i działają w cronie.
- **Bezpiecznie.** Sekrety (token, PIN, klucz) nigdy w argumentach wywołania; logowanie certyfikatem,
  tokenem KSeF, kluczem na karcie/tokenie/TPM (PKCS#11) albo podpisem zewnętrznym (np. Profil Zaufany);
  sesje w RAM (`$XDG_RUNTIME_DIR`), prawa 0600.
- **Offline, gdzie się da.** Walidacja (schemat FA(3) + wymagania techniczne KSeF + sumy kontrolne NIP)
  i podgląd HTML oficjalnym arkuszem MF z kodem QR weryfikującym fakturę — bez sieci.

## Szybki start

```bash
# 1. Instalacja (x64; dla arm64: linux-arm64)
v=0.1.0 a=linux-x64
curl -LO https://github.com/kprzybys/ksef-cli/releases/download/v$v/ksef-$v-$a.tar.gz{,.sha256}
sha256sum -c ksef-$v-$a.tar.gz.sha256 && tar -xzf ksef-$v-$a.tar.gz
install -Dm755 ksef-$v-$a/ksef ~/.local/bin/ksef
ksef status                                  # czy KSeF działa (komunikaty Latarni MF)

# 2. Środowisko testowe MF: certyfikat testowy i logowanie (fikcyjny NIP)
ksef cert new --nip 5908996669 -o ~/.config/ksef/test-cert.pem
ksef login --nip 5908996669 --cert ~/.config/ksef/test-cert.pem

# 3. Faktury
ksef validate faktura.xml                    # cisza = poprawna
ksef send faktura.xml                        # numer KSeF, plik, numer sesji
ksef list --subject seller                   # faktury wystawione (domyślnie: otrzymane)
```

Domyślne środowisko to `test` (piaskownica MF, fikcyjne dane); prawdziwa firma tylko jawnie: `ksef -e prod …`
albo `export KSEF_ENV=prod` — zob. [środowiska](docs/COMMANDS.md#środowiska). Samo `ksef` pokazuje pierwsze kroki.
Pakiety systemowe (ICU, OpenSSL, libxslt) dla każdej dystrybucji: `scripts/install-deps.sh` i
[docs/INSTALL.md](docs/INSTALL.md).

## Komendy

| Komenda | Co robi |
|---|---|
| `ksef status` | Dostępność KSeF (Latarnia MF) i API środowiska |
| `ksef validate` | Kontrola faktur przed wysyłką — offline |
| `ksef show` | Podgląd HTML oficjalnym arkuszem MF z kodem QR KSeF — offline |
| `ksef cert new` | Certyfikat testowy (tylko środowisko TEST) |
| `ksef login` / `logout` / `session` | Logowanie: certyfikat, token KSeF, PKCS#11, podpis zewnętrzny |
| `ksef list` | Metadane faktur otrzymanych i wystawionych; długie zakresy dzielone automatycznie |
| `ksef send` | Wysyłka faktur FA(3) i czekanie na numery KSeF |
| `ksef get` | XML faktur po numerze KSeF — bajt w bajt |
| `ksef upo` | Urzędowe Poświadczenie Odbioru — faktury albo zbiorcze sesji |

Opcje, formaty wyjścia, kody wyjścia i wszystkie metody logowania: [docs/COMMANDS.md](docs/COMMANDS.md).

## Przykłady

```bash
ksef list | ksef get -d otrzymane/                           # tylko nowe faktury otrzymane
ksef send faktury/*.xml | tee wyslane.tsv | ksef upo -d upo/ # wysyłka + UPO każdej faktury
ksef list --subject seller --json | jq 'map(.grossAmount) | add'
pass show ksef/token | ksef -e prod login --nip 1234563218 --token-file -
ksef show faktura.xml -e prod -k "$(cut -f1 wyslane.tsv)" -o faktura.html   # dla nabywcy poza KSeF
```

## Zakres i ograniczenia

`ksef` przenosi faktury między plikami a KSeF — nie wystawia ich, nie księguje i nie ma własnej bazy.

- Sesje interaktywne (do 10 000 faktur na sesję). **Nie ma** sesji wsadowych, więc faktur z załącznikiem
  (wysyłanych tylko wsadowo) `ksef` nie wyśle.
- **Nie ma** trybów offline (offline24, awaria) ani kodu QR „CERTYFIKAT” (KOD II), którego wymagają faktury
  offline przekazywane poza KSeF; kod weryfikujący fakturę (KOD I) jest.
- `validate` sprawdza schemat i wymagania techniczne KSeF, **nie** rachunki (sumy VAT) — tych nie sprawdza też KSeF.
- Testy automatyczne (ponad 200, w tym na żywym środowisku TEST MF i na binarce AOT) nie obejmują DEMO ani produkcji.
- Limity KSeF (np. 20 zapytań o metadane na godzinę) obowiązują — krótkie 429 `ksef` przeczekuje sam.

## Plany

Najbliżej: eksport z przyrostowym pobieraniem faktur, sesje wsadowe i faktury z załącznikiem, kontrola
arytmetyki VAT w `validate`; dalej tryby offline z kodem „CERTYFIKAT” (KOD II), certyfikaty i tokeny KSeF.
Pełna lista: [docs/ROADMAP.md](docs/ROADMAP.md).

## Dokumentacja

- [docs/COMMANDS.md](docs/COMMANDS.md) — komendy, zasady, logowanie
- [docs/INSTALL.md](docs/INSTALL.md) — wymagania, pakiety, sprawdzone dystrybucje, budowanie wydań
- [docs/ROADMAP.md](docs/ROADMAP.md) — plan kolejnych wersji
- [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md) — budowanie ze źródeł, Native AOT, FA(3), testy, jakość kodu

## Bezpieczeństwo

Token i certyfikat KSeF trzymaj w menedżerze sekretów (np. `pass`, KeePassXC, Vault/OpenBao) i podawaj
`ksef` przez stdin — nigdy w repo ani w plikach konfiguracyjnych. Problemy z bezpieczeństwem zgłaszaj
prywatnie przez [GitHub Security Advisories](https://github.com/kprzybys/ksef-cli/security/advisories/new).

## Licencja

MIT — zob. [LICENSE](LICENSE). Składniki innych autorów (biblioteka MF, .NET, wzory FA(3)):
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md). Projekt nie jest powiązany z Ministerstwem Finansów.
