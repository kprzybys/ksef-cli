# Jak pomóc

Dziękuję za zainteresowanie `ksef`. Zgłoszenia błędów i propozycje: [Issues](https://github.com/kprzybys/ksef-cli/issues/new/choose).
Luki bezpieczeństwa — prywatnie, zob. [SECURITY.md](SECURITY.md).

## Zasady narzędzia

`ksef` robi jedną rzecz: przenosi faktury między plikami a KSeF (plus kontrola i podgląd pliku FA(3) bez sieci).
Każda zmiana trzyma się tych reguł: wynik na stdout, komunikaty na stderr z prefiksem `ksef:`, stałe kody wyjścia,
`-` jako stdin/stdout, `--json` dla skryptów, żadnych pytań interaktywnych, sekrety nigdy w argumentach.
Wystawianie faktur, księgowość i własna baza danych są poza zakresem. Większą zmianę najlepiej najpierw omówić
w zgłoszeniu.

## Przygotowanie

Budowanie ze źródeł, Native AOT i testy: [docs/DEVELOPMENT.md](docs/DEVELOPMENT.md). W skrócie:

```bash
git clone --recurse-submodules=external/ksef-client-csharp https://github.com/kprzybys/ksef-cli.git
cd ksef-cli
make hooks     # pre-commit: formatowanie, gitleaks, blokada danych prywatnych
make check     # lint + build + testy (bez sieci)
```

## Zmiany

- Testy bez sieci: przebiegi z KSeF przez fałszywy serwer (`tests/KsefCli.Tests/FakeKsef.cs`), który odtwarza też
  awarie (zerwane połączenie, 21184, limity). Testy na żywym KSeF tylko na środowisku TEST (`make test-e2e`).
- **Nigdy na produkcji** i nigdy z prawdziwymi danymi: tylko fikcyjne NIP-y (np. `5908996669`, `1234563218`),
  bez prawdziwych faktur, numerów KSeF, tokenów ani nazw prywatnych serwerów — w kodzie, testach i opisach commitów.
- Analizatory działają jako błędy kompilacji; `make check` musi przejść.
- Komunikaty i dokumentacja po polsku, krótko i konkretnie.
- Kod biblioteki MF (`external/`), schematy (`schemas/`) i arkusze MF (`stylesheets/`) zostają bajt w bajt —
  poprawki biblioteki zgłaszaj w [CIRFMF/ksef-client-csharp](https://github.com/CIRFMF/ksef-client-csharp).

Wkład trafia do projektu na licencji [MIT](LICENSE).
