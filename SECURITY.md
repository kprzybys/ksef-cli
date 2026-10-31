# Bezpieczeństwo

## Zgłaszanie podatności

Podatności zgłaszaj prywatnie przez
[GitHub Security Advisories](https://github.com/kprzybys/ksef-cli/security/advisories/new),
nie w publicznych zgłoszeniach. Odpowiadam w ciągu 14 dni (zwykle kilku dni roboczych); zgłoszenia po angielsku
też są w porządku (reports in English are welcome).

Nie dołączaj prawdziwych tokenów, certyfikatów, kluczy ani faktur — wystarczy opis i kroki na środowisku TEST.

## Wspierane wersje

Poprawki bezpieczeństwa trafiają do najnowszego wydania.

## Założenia

- Sekrety (token KSeF, PIN, klucz prywatny) `ksef` przyjmuje tylko z pliku albo stdin, nigdy jako argument.
- Sesje (access i refresh token) leżą w `$XDG_RUNTIME_DIR/ksef/` (RAM) z prawami 0600.
- Kryptografię KSeF (AES-256 faktur, RSA-OAEP klucza, XAdES) robi oficjalna biblioteka MF i .NET.
- Podatności w samej bibliotece MF zgłaszaj w [CIRFMF/ksef-client-csharp](https://github.com/CIRFMF/ksef-client-csharp).
