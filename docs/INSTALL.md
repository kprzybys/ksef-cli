# Instalacja ksef

`ksef` to jedna natywna binarka (Native AOT) dla Linuksa **x64** i **arm64**, a od wersji 0.1.4 także dla
macOS (Apple Silicon i Intel — [niżej](#macos)). Nie potrzebuje .NET, ale korzysta z kilku bibliotek systemowych.

## Wymagania binarki

| Biblioteka | Po co | Kiedy |
|---|---|---|
| glibc ≥ 2.34 | podstawa (binarka z wydania budowana na AlmaLinux 9) | zawsze |
| ICU (`libicuuc`, `libicui18n`) | ustawienia regionalne, porównywanie tekstu po polsku | zawsze — bez ICU binarka kończy się na starcie: „Couldn't find a valid ICU package” |
| OpenSSL 3 (`libssl.so.3`; 1.1 też działa) | HTTPS, szyfrowanie, podpis XAdES | każda komenda sieciowa i `cert new` |
| certyfikaty CA (`ca-certificates`) | weryfikacja TLS serwerów KSeF | komendy sieciowe |
| libxslt 1.1 (z libxml2 2.x) | podgląd HTML (`ksef show`) | tylko `show` |

Kerberos (`libgssapi_krb5`) .NET ładuje tylko przy uwierzytelnianiu Negotiate — `ksef` go nie używa.
Alpine (musl) nie jest obsługiwany.

Pakiety dla danej dystrybucji wypisze (a z `--yes` zainstaluje) `scripts/install-deps.sh`:

```bash
scripts/install-deps.sh            # do uruchomienia binarki
scripts/install-deps.sh --build    # do budowania ze źródeł
scripts/install-deps.sh --yes      # instaluje (sudo, gdy nie root)
```

| Rodzina | Do uruchomienia | Dodatkowo do budowania |
|---|---|---|
| Debian, Ubuntu, Mint | `libicuNN libssl3 (libssl3t64) libxslt1.1 ca-certificates` | `clang zlib1g-dev make git curl`, `dotnet-sdk-10.0` (gdy jest w repozytorium) |
| Fedora, RHEL, Alma, Rocky | `libicu openssl-libs libxslt ca-certificates` | `clang zlib-devel make git dotnet-sdk-10.0` |
| Arch | `icu openssl libxslt ca-certificates` | `clang zlib make git dotnet-sdk` |
| openSUSE | `libicuNN libopenssl3 libxslt1 ca-certificates` | `clang zlib-devel make git curl` |

.NET SDK 10, którego nie ma w repozytorium dystrybucji (Ubuntu 22.04, Debian, openSUSE), skrypt instaluje
oficjalnym `dotnet-install.sh` do `~/.dotnet` — bez sudo; trzeba dopisać `~/.dotnet` do `PATH`.

## Klucz na karcie lub tokenie (PKCS#11)

Opcjonalnie, tylko dla logowania kluczem z karty, tokenu albo TPM (`ksef login --key pkcs11:…`, zob. sekcję
„Logowanie” w [docs/COMMANDS.md](COMMANDS.md#logowanie)). Moduł karty ładuje p11-kit, a OpenSSL systemu — silnik (engine) albo provider PKCS#11;
zwykle nie trzeba nic konfigurować (opcjonalnie `PKCS11_PROVIDER_MODULE=/ścieżka/modułu.so`).

```bash
scripts/install-deps.sh --pkcs11 --yes
```

| Rodzina | Pakiety (`--pkcs11`) | Uwagi |
|---|---|---|
| Debian, Ubuntu | `p11-kit p11-kit-modules opensc libengine-pkcs11-openssl` + `pkcs11-provider` | provider od Ubuntu 24.04 / Debiana 13; Ubuntu 22.04 i Debian 12 — tylko silnik |
| Fedora, RHEL, Alma | `p11-kit opensc pkcs11-provider` + `openssl-pkcs11` | RHEL/Alma 10 nie mają już silnika — tylko provider |
| Arch | `p11-kit opensc libp11 pkcs11-provider` | |
| openSUSE | `p11-kit opensc libp11-3 pkcs11-provider` | |

Wymaga OpenSSL 3 (`libcrypto.so.3`) — mają go wszystkie sprawdzone dystrybucje (od 3.0.2 w Ubuntu 22.04).
Brak silnika lub providera `ksef` zgłasza wprost, np. „OpenSSL nie ma providera pkcs11 — zainstaluj
pkcs11-provider albo użyj --key-via engine”; przy OpenSSL starszym niż 3.2 domyślnie używa silnika.

Klucz w TPM: dodatkowo `libtpm2-pkcs11-1` (Debian/Ubuntu) albo `tpm2-pkcs11`; do testów bez sprzętu: `softhsm2`
(Debian/Ubuntu) albo `softhsm`. Instalacja `--pkcs11` sprawdzona w kontenerach x64 wszystkich dystrybucji
z tabel niżej; samo logowanie — na Ubuntu 24.04 (OpenSSL 3.0). arm64 nietestowane.

## macOS

Od wersji 0.1.4: `ksef-<wersja>-osx-arm64.tar.gz` (Apple Silicon) i `osx-x64` (Intel). Wszystko, czego binarka
potrzebuje (ICU, kryptografia, libxslt), jest w systemie — bez Homebrew i bez pakietów.

```bash
v=0.1.4 a=osx-arm64   # Intel: osx-x64
curl -LO https://github.com/kprzybys/ksef-cli/releases/download/v$v/ksef-$v-$a.tar.gz{,.sha256}
shasum -a 256 -c ksef-$v-$a.tar.gz.sha256 && tar -xzf ksef-$v-$a.tar.gz
mkdir -p ~/.local/bin && install -m755 ksef-$v-$a/ksef ~/.local/bin/ksef   # ~/.local/bin dopisz do PATH
ksef --help
```

- **Gatekeeper.** Binarka nie jest jeszcze notaryzowana przez Apple. Pobrana przez `curl` (jak wyżej) uruchamia się
  od razu; pobrana przeglądarką dostaje znacznik kwarantanny i macOS jej nie otworzy — wtedy
  `xattr -d com.apple.quarantine ~/.local/bin/ksef`. Pochodzenie archiwum sprawdzisz tak samo jak na Linuksie
  (atestacja, [niżej](#instalacja-z-wydania)).
- **Sesje** trzyma w `$TMPDIR/ksef/` (prywatny katalog użytkownika, prawa 0700) — macOS nie ma `$XDG_RUNTIME_DIR`.
- **Klucz na karcie/tokenie (PKCS#11)** na razie tylko na Linuksie; na macOS logowanie certyfikatem z pliku,
  tokenem KSeF albo podpisem zewnętrznym (`--request`/`--signed`, np. Profil Zaufany).
- **Budowanie ze źródeł:** Xcode Command Line Tools (`xcode-select --install`), .NET SDK 10
  (`brew install --cask dotnet-sdk`), GNU make z Homebrew (`gmake`; systemowy make 3.81 też wystarcza), potem
  `make build test aot` jak na Linuksie. Lint (`make lint`, `make hooks`) potrzebuje `uv` (`brew install uv`).

## Instalacja z wydania

```bash
tar -xzf ksef-<wersja>-linux-x64.tar.gz          # albo linux-arm64
sha256sum -c ksef-<wersja>-linux-x64.tar.gz.sha256
install -Dm755 ksef-<wersja>-linux-x64/ksef ~/.local/bin/ksef
ksef --help
```

Suma SHA-256 chroni tylko przed uszkodzonym pobraniem — kto podmieni archiwum, podmieni i sumę. Od wersji 0.1.3
każde archiwum ma też **podpis pochodzenia** (atestacja SLSA w otwartym standardzie Sigstore, plik
`ksef-<wersja>-linux-<arch>.tar.gz.sigstore.json` w wydaniu). Potwierdza, że archiwum zbudował workflow wydań
z tego repozytorium (`.github/workflows/release.yml` przy tagu wersji), a nie ktoś inny. Sprawdzenie:

```bash
# GitHub CLI — podpis z rejestru GitHuba albo z pliku z wydania (--bundle)
gh attestation verify ksef-<wersja>-linux-x64.tar.gz --repo kprzybys/ksef-cli
gh attestation verify ksef-<wersja>-linux-x64.tar.gz --repo kprzybys/ksef-cli --bundle ksef-<wersja>-linux-x64.tar.gz.sigstore.json

# niezależnie od GitHuba — cosign 3+ (Sigstore) i plik podpisu z wydania; cosign 2.x: dodaj --new-bundle-format
cosign verify-blob-attestation --type slsaprovenance1 \
  --bundle ksef-<wersja>-linux-x64.tar.gz.sigstore.json \
  --certificate-oidc-issuer https://token.actions.githubusercontent.com \
  --certificate-identity-regexp '^https://github\.com/kprzybys/ksef-cli/\.github/workflows/release\.yml@refs/tags/v' \
  ksef-<wersja>-linux-x64.tar.gz
```

Od wersji 0.1.4 wydanie ma też **SBOM** — listę wszystkich składników binarki w formacie CycloneDX
(`ksef-<wersja>-linux-<arch>.tar.gz.cdx.json`: biblioteka MF, pakiety NuGet, środowisko .NET wkompilowane
w binarkę) — z osobnym podpisem (`….cdx.json.sigstore.json`). Pozwala sprawdzić skanerem podatności
(np. `grype sbom:ksef-<wersja>-linux-x64.tar.gz.cdx.json`), czy nowa luka w zależności dotyczy `ksef`.

```bash
gh attestation verify ksef-<wersja>-linux-x64.tar.gz --repo kprzybys/ksef-cli \
  --predicate-type https://cyclonedx.org/bom --bundle ksef-<wersja>-linux-x64.tar.gz.cdx.json.sigstore.json
```

## Budowanie ze źródeł

```bash
git clone --recurse-submodules=external/ksef-client-csharp https://github.com/kprzybys/ksef-cli.git
cd ksef-cli
scripts/install-deps.sh --build --yes
make aot && make install          # ~/.local/bin/ksef
```

Binarka z `make aot` wymaga glibc nie starszej niż ta, na której ją zbudowano — do innych maszyn
lepiej nadaje się wydanie: `make release` buduje je w kontenerze AlmaLinux 9 (potrzebny docker albo podman)
z tym samym SDK co na hoście, a `make test-distros BIN=...` sprawdza binarkę w czystych kontenerach.
Oba cele można puścić na mocniejszej maszynie: `make release REMOTE=host` (albo `export KSEF_REMOTE=host`)
kopiuje źródła rsync-iem do `host:~/ksef-build`, buduje tam i odbiera wyniki do `dist/`.

RHEL 9 i pochodne: domyślna polityka kryptograficzna odrzuca podpisy SHA-1, a kompilator podpisuje nimi
biblioteki MF (silna nazwa) — budowanie kończy się błędem w `RSAOpenSsl.SignHash`. Na maszynie do budowania:
`sudo update-crypto-policies --set DEFAULT:SHA1` (samej binarki `ksef` to nie dotyczy).

## Sprawdzone dystrybucje

<!-- Tabele z scripts/test-distros.sh; po zmianach zależności odświeżyć: make release && make test-distros BIN=... -->

Stan 2026-10-30, wydanie z kontenera AlmaLinux 9 (SDK 10.0.114). „Bez pakietów” — czy binarka rusza
w minimalnym obrazie przed `install-deps.sh` (zwykle brakuje ICU); dalsze kolumny — po instalacji pakietów.

### x64 (kontenery)

| Obraz | System | glibc | Bez pakietów | Pakiety (install-deps --runtime) | validate | show | cert | status |
|---|---|---|---|---|---|---|---|---|
| `ubuntu:22.04` | Ubuntu 22.04.5 LTS | 2.35 | ICU | libicu70 libssl3 libxslt1.1 ca-certificates | ok | ok | ok | ok |
| `ubuntu:24.04` | Ubuntu 24.04.5 LTS | 2.39 | ICU | libicu74 libssl3t64 libxslt1.1 ca-certificates | ok | ok | ok | ok |
| `ubuntu:26.04` | Ubuntu 26.04.1 LTS | 2.43 | ICU | libicu78 libssl3t64 libxslt1.1 ca-certificates | ok | ok | ok | ok |
| `debian:12` | Debian GNU/Linux 12 (bookworm) | 2.36 | ICU | libicu72 libssl3 libxslt1.1 ca-certificates | ok | ok | ok | ok |
| `debian:13` | Debian GNU/Linux 13 (trixie) | 2.41 | ICU | libicu76 libssl3t64 libxslt1.1 ca-certificates | ok | ok | ok | ok |
| `almalinux:9` | AlmaLinux 9.8 (Olive Jaguar) | 2.34 | ICU | libicu openssl-libs libxslt ca-certificates | ok | ok | ok | ok |
| `almalinux:10` | AlmaLinux 10.2 (Lavender Lion) | 2.39 | ICU | libicu openssl-libs libxslt ca-certificates | ok | ok | ok | ok |
| `fedora:latest` | Fedora Linux 44 (Container Image) | 2.43 | ICU | libicu openssl-libs libxslt ca-certificates | ok | ok | ok | ok |
| `archlinux:latest` | Arch Linux | 2.44 | ok | icu openssl libxslt ca-certificates | ok | ok | ok | ok |
| `opensuse/tumbleweed` | openSUSE Tumbleweed | 2.44 | ICU | libicu78 libopenssl3 libxslt1 ca-certificates | ok | ok | ok | ok |

### arm64 (kontenery na Raspberry Pi 5; Arch nie ma oficjalnego obrazu arm64)

| Obraz | System | glibc | Bez pakietów | Pakiety (install-deps --runtime) | validate | show | cert | status |
|---|---|---|---|---|---|---|---|---|
| `ubuntu:22.04` | Ubuntu 22.04.5 LTS | 2.35 | ICU | libicu70 libssl3 libxslt1.1 ca-certificates | ok | ok | ok | ok |
| `ubuntu:24.04` | Ubuntu 24.04.5 LTS | 2.39 | ICU | libicu74 libssl3t64 libxslt1.1 ca-certificates | ok | ok | ok | ok |
| `ubuntu:26.04` | Ubuntu 26.04.1 LTS | 2.43 | ICU | libicu78 libssl3t64 libxslt1.1 ca-certificates | ok | ok | ok | ok |
| `debian:12` | Debian GNU/Linux 12 (bookworm) | 2.36 | ICU | libicu72 libssl3 libxslt1.1 ca-certificates | ok | ok | ok | ok |
| `debian:13` | Debian GNU/Linux 13 (trixie) | 2.41 | ICU | libicu76 libssl3t64 libxslt1.1 ca-certificates | ok | ok | ok | ok |
| `almalinux:9` | AlmaLinux 9.8 (Olive Jaguar) | 2.34 | ICU | libicu openssl-libs libxslt ca-certificates | ok | ok | ok | ok |
| `almalinux:10` | AlmaLinux 10.2 (Lavender Lion) | 2.39 | ICU | libicu openssl-libs libxslt ca-certificates | ok | ok | ok | ok |
| `fedora:latest` | Fedora Linux 44 (Container Image) | 2.43 | ICU | libicu openssl-libs libxslt ca-certificates | ok | ok | ok | ok |
| `opensuse/tumbleweed` | openSUSE Tumbleweed | 2.44 | ICU | libicu78 libopenssl3 libxslt1 ca-certificates | ok | ok | ok | ok |

### Maszyny fizyczne i VM (bez instalowania pakietów)

| Maszyna | System | glibc | Wynik |
|---|---|---|---|
| maszyny fizyczne | Linux Mint 22.1 / 22.3 | 2.39 | wszystko działa |
| stacja robocza | Ubuntu 26.10 (libxml2.so.16, OpenSSL 4 obok 3) | 2.44 | wszystko działa |
| VPS w kontenerze LXC | Ubuntu 22.04 | 2.35 | wszystko działa |
| hosting współdzielony, bez sudo | RHEL 9 (el9_7) | 2.34 | wszystko działa |
| VM w chmurze, kontenery LXC | Ubuntu 26.04 / 24.04 | 2.43 / 2.39 | bez libxslt — działa wszystko poza `show` |
| VPS z minimalnym obrazem | Debian 13 (obraz cloud) | 2.41 | brak ICU — binarka nie rusza |
| Raspberry Pi 5 | Debian 13, arm64 | 2.41 | wszystko działa; `make test-aot` z E2E: 178/178 |

### Budowanie ze źródeł (`install-deps.sh --build --yes`, potem `make build test aot`)

| System | .NET SDK | Wynik |
|---|---|---|
| Ubuntu 22.04 (kontener) | 10.0.403 z `dotnet-install.sh` | build, testy, AOT — ok |
| AlmaLinux 9 (kontener) | 10.0.114 z repozytorium | ok (z `update-crypto-policies --set DEFAULT:SHA1`) |
| Debian 13 arm64 (Raspberry Pi 5) | 10.0.403 z `dotnet-install.sh` | ok; testy 179 + 28 E2E pominiętych |
| Linux Mint 22.1 | 10.0.114 z repozytorium | ok |
