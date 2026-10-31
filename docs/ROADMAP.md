# Plan rozwoju

Wersja 0.1 to podstawa: wysyłka i odbiór faktur FA(3) w sesji interaktywnej, walidacja, podgląd z kodem QR (KOD I),
UPO. Poniżej plan kolejnych wersji, ułożony według przeglądu wymagań MF i porównania z innymi narzędziami KSeF
(stan 2026-10-31). Kolejność w grupie to kolejność realizacji; wszystko w duchu narzędzia — jedna rzecz dobrze,
potoki, sekrety nigdy w argumentach.

## Przed pierwszą fakturą produkcyjną

- [ ] Próba na DEMO prawdziwym środkiem uwierzytelnienia (podpis kwalifikowany, pieczęć, Profil Zaufany) —
  samopodpisane certyfikaty działają tylko na TEST, a DEMO/PROD mają zaostrzoną walidację XAdES.

## Priorytet wysoki

- [ ] `ksef export --state PLIK` — eksport asynchroniczny (paczki ZIP) z przyrostowym pobieraniem od HWM i jawnym
  plikiem stanu; wynik do potoku (`| ksef get`). MF zaleca eksport zamiast serii pojedynczych pobrań (limit 64/h).
- [ ] Sesja wsadowa: `ksef send --batch`, automatycznie dla faktur z `Zalacznik` (wysyłanych tylko wsadowo).
- [ ] `validate`: arytmetyka `P_13_x` / `P_14_x` / `P_15` w groszach z tolerancją 1 gr (od sum i od wierszy),
  z osobną semantyką dla ZAL/ROZ/KOR i stałymi kodami błędów w `--json` — bez fałszywych alarmów na poprawnych
  fakturach (wzór reguł: walidator ksefuj, Apache-2.0).
- [ ] Błędy w `--json` na stderr z polami `ksef_code`, `trace_id` i podpowiedzią dla znanych kodów KSeF.
- [ ] Pokazywanie ostrzeżeń `X-System-Warning` (np. `sig-001`, `xml-002`) — zapowiadają przyszłe odrzucenia.

## Priorytet średni

- [ ] Tryby offline (offline24, niedostępność, awaria) i KOD II „CERTYFIKAT” w `show`; terminy dosłania w dniach
  roboczych; korekta techniczna (`hashOfCorrectedInvoice`).
- [ ] `ksef cert enroll|list|revoke` — wnioski o certyfikat KSeF (Authentication i Offline), także z kluczem na
  karcie/tokenie (PKCS#11); logowanie certyfikatem KSeF jest bardziej przyszłościowe niż token.
- [ ] `ksef token new|list|revoke` — tokeny KSeF; nowy token tylko na stdout (np. prosto do menedżera sekretów).
- [ ] `ksef status --check` / `doctor` — diagnostyka: pakiety systemowe, czas, sieć, sesja, Latarnia.

## Priorytet niski

- [ ] Kod wyjścia 2 dla błędu użycia (zgodnie z konwencją), uzupełnianie poleceń w powłoce (completion).
- [ ] `ksef --version` z wersją biblioteki MF.
- [ ] Lista i unieważnianie aktywnych sesji uwierzytelnienia; `upo --json`.
- [ ] Uprawnienia (np. nadanie biuru rachunkowemu), limity API, dane testowe (tylko TEST).
- [ ] `cert verify` — kontrola certyfikatu i klucza (próbny podpis, w tym KOD II).

## Zrobione po 0.1

- [x] Zmienna `KSEF_ENV` (odpowiednik `-e`) i pierwsze uruchomienie bez dokumentacji: `ksef` bez komendy opisuje
  środowiska, komunikaty mówią po ludzku, co znaczy test/demo/prod, podpowiedź przy tokenie z innego środowiska (450)
  i przy sesji w innym środowisku, ślad `send` na produkcji.

## Świadomie poza zakresem

Keyring i profile (sekrety z menedżera przez stdin), wbudowany PDF (`show` + przeglądarka `--print-to-pdf`),
tryb `--watch` i powiadomienia (cron/systemd + potoki), serwer MCP. Do ponownej oceny, jeśli pojawi się
realna potrzeba użytkowników.

Szczegóły i źródła: przegląd wymagań MF oraz porównanie z go-ksef-cli, ksef2-cli, ksef-client-python,
ksef-client-ts, ksefcli, ksefuj i ksefctl (raporty projektu, 2026-10-31).
