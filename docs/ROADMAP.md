# Plan rozwoju

Wersja 0.1 to podstawa: wysyłka i odbiór faktur FA(3) w sesji interaktywnej, walidacja, podgląd z kodem QR (KOD I),
UPO. Poniżej plan kolejnych wersji, ułożony według przeglądu wymagań MF i porównania z innymi narzędziami KSeF
(stan 2026-10-31). Kolejność w grupie to kolejność realizacji; wszystko w duchu narzędzia — jedna rzecz dobrze,
potoki, sekrety nigdy w argumentach.

## Przed pierwszą fakturą produkcyjną

- [x] Logowanie Profilem Zaufanym (`--request`/`--signed`) na DEMO — sprawdzone ręcznie 2026-10-31.
- [ ] DEMO: podpis kwalifikowany i pieczęć; pełny obieg send/upo/get na DEMO z prawdziwymi uprawnieniami —
  samopodpisane certyfikaty działają tylko na TEST, a DEMO/PROD mają zaostrzoną walidację XAdES.

## Priorytet wysoki

- [ ] `logout`: usunąć sesję lokalnie także wtedy, gdy KSeF odrzuci unieważnienie (token już unieważniony).
- [ ] `list`: daty bez godziny w czasie polskim (jak w `send`), nie w strefie maszyny — cron na serwerze w UTC.
- [ ] `get` i `upo`: błąd sieci przy jednej pozycji nie przerywa całej partii (dziś tylko błędy KSeF są per pozycja).
- [ ] Ostrzeżenie, gdy `--token-file`/`--pin-file` jest czytelny dla innych; pliki faktur i UPO z prawami 0600.

- [ ] `ksef export --state PLIK` — eksport asynchroniczny (paczki ZIP) z przyrostowym pobieraniem od HWM i jawnym
  plikiem stanu; wynik do potoku (`| ksef get`). MF zaleca eksport zamiast serii pojedynczych pobrań (limit 64/h).
- [ ] Sesja wsadowa: `ksef send --batch`, automatycznie dla faktur z `Zalacznik` (wysyłanych tylko wsadowo).
- [ ] `validate`: arytmetyka `P_13_x` / `P_14_x` / `P_15` w groszach z tolerancją 1 gr (od sum i od wierszy),
  z osobną semantyką dla ZAL/ROZ/KOR i stałymi kodami błędów w `--json` — bez fałszywych alarmów na poprawnych
  fakturach (wzór reguł: walidator ksefuj, Apache-2.0).
- [ ] Błędy w `--json` na stderr z polami `ksef_code`, `trace_id` i podpowiedzią dla znanych kodów KSeF.
- [ ] Pokazywanie ostrzeżeń `X-System-Warning` (np. `sig-001`, `xml-002`) — zapowiadają przyszłe odrzucenia.

## Priorytet średni

- [ ] macOS: notaryzacja Apple (podpis Developer ID w CI) i formuła Homebrew — bez blokady Gatekeepera przy pobraniu
  przeglądarką.
- [ ] Tryby offline (offline24, niedostępność, awaria) i KOD II „CERTYFIKAT” w `show`; terminy dosłania w dniach
  roboczych; korekta techniczna (`hashOfCorrectedInvoice`).
- [ ] `ksef cert enroll|list|revoke` — wnioski o certyfikat KSeF (Authentication i Offline), także z kluczem na
  karcie/tokenie (PKCS#11); logowanie certyfikatem KSeF jest bardziej przyszłościowe niż token.
- [ ] `ksef token new|list|revoke` — tokeny KSeF; nowy token tylko na stdout (np. prosto do menedżera sekretów).
- [ ] `ksef status --check` / `doctor` — diagnostyka: pakiety systemowe, czas, sieć, sesja, Latarnia.

## Priorytet niski

- [ ] PKCS#11 na macOS (karta/token przez kryptografię Apple albo OpenSSL z Homebrew).
- [ ] Kod wyjścia 2 dla błędu użycia (zgodnie z konwencją), uzupełnianie poleceń w powłoce (completion).
- [ ] `ksef --version` z wersją biblioteki MF.
- [ ] Lista i unieważnianie aktywnych sesji uwierzytelnienia; `upo --json`.
- [ ] Uprawnienia (np. nadanie biuru rachunkowemu), limity API, dane testowe (tylko TEST).
- [ ] `cert verify` — kontrola certyfikatu i klucza (próbny podpis, w tym KOD II).

## Zrobione w 0.1.4

- [x] macOS: binarki `osx-arm64` (Apple Silicon) i `osx-x64` (Intel), bez pakietów (ICU, kryptografia i libxslt
  z systemu); CI i wydanie na runnerach macOS z atestacją i SBOM. Bez PKCS#11 i bez notaryzacji Apple — na później.
- [x] SBOM (CycloneDX) z podpisem przy każdym archiwum; instalator .NET w `install-deps.sh` przypięty skrótem.

## Zrobione w 0.1.2

- [x] `send` nie gubi numerów już wysłanych faktur: gdy po 21184 nie da się otworzyć nowej sesji, po Ctrl+C/SIGTERM
  (kod 130) i przy błędzie bramy 408/5xx bez kodu KSeF (szukanie w sesji po skrócie). Faktura, której stanu nie da
  się ustalić, ma „STATUS NIEZNANY” i `statusUnknown` w `--json` — nie wygląda jak odrzucenie.
- [x] `KSEF_API_URL` (symulator w testach) tylko na ten komputer — token nie poleci na obcy serwer.

## Zrobione w 0.1.1

- [x] Zmienna `KSEF_ENV` (odpowiednik `-e`) i pierwsze uruchomienie bez dokumentacji: `ksef` bez komendy opisuje
  środowiska, komunikaty mówią po ludzku, co znaczy test/demo/prod, podpowiedź przy tokenie z innego środowiska (450)
  i przy sesji w innym środowisku, ślad `send` na produkcji.

## Świadomie poza zakresem

Keyring i profile (sekrety z menedżera przez stdin), wbudowany PDF (`show` + przeglądarka `--print-to-pdf`),
tryb `--watch` i powiadomienia (cron/systemd + potoki), serwer MCP. Do ponownej oceny, jeśli pojawi się
realna potrzeba użytkowników.

Plan powstał na podstawie dokumentacji API KSeF 2.0 MF ([CIRFMF/ksef-api](https://github.com/CIRFMF/ksef-api))
i porównania z innymi publicznymi narzędziami KSeF (stan 2026-10-31).
