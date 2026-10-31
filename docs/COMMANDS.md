# ksef-cli — komendy

| Komenda | Logowanie | Opis |
|---|---|---|
| `ksef status [-e test\|demo\|prod] [--json]` | nie | Komunikaty Latarni MF o dostępności KSeF + czy API środowiska odpowiada. Kod wyjścia: 0 dostępny, 2 KSeF niedostępny/prace, 1 błąd połączenia. |
| `ksef validate [pliki\|-]... [-v] [--json]` | nie (offline) | Kontrola przed wysyłką: wymagania KSeF (UTF-8 bez BOM, kodowanie w prologu, brak instrukcji przetwarzania, niezalecane znaki Unicode, limit 1 MB / 3 MB z załącznikiem, sumy kontrolne NIP — KSeF sprawdza je tylko na produkcji) + schemat FA(3). Przy sukcesie milczy; błędy na stderr jako `plik:wiersz:kolumna: ...`. Kod wyjścia: 0 wszystkie poprawne, 1 jest błąd. |
| `ksef show [plik\|-] [-o plik.html] [--ksef-number NR] [--no-qr]` | nie (offline) | Podgląd faktury w HTML oficjalnym arkuszem MF (`styl.xsl`), domyślnie na stdout, z kodem QR weryfikującym fakturę (KOD I) — wymaganym na fakturze przekazanej poza KSeF (konsument, nabywca zagraniczny, wydruk). Link `https://qr.ksef.mf.gov.pl/invoice/{NIP sprzedawcy}/{data wystawienia}/{SHA-256 pliku}`; pod kodem numer KSeF (`--ksef-number`) albo „OFFLINE”. Host wg `-e`: dla faktur z produkcyjnego KSeF `-e prod` (poza prod na podglądzie widoczna adnotacja). |
| `ksef cert new --nip <NIP> [--seal] [-o plik.pem]` | nie (offline) | Certyfikat testowy podpisany samym sobą (osoba `TINPL-NIP` albo pieczęć `VATPL-NIP`) jako PEM z kluczem prywatnym. Tylko dla środowiska TEST. Nie pisze klucza na terminal ani nie nadpisuje pliku; plik ma prawa 0600. |
| `ksef login --nip <NIP> --cert <plik.pem\|->` | — | Logowanie certyfikatem (podpis XAdES wyzwania KSeF) w kontekście NIP-u. Przy sukcesie milczy. |
| `ksef logout [--nip <NIP>]` | tak | Unieważnia sesję w KSeF i usuwa ją lokalnie. |
| `ksef session [-a] [--json]` | nie | Lokalne sesje: środowisko, NIP, ważność (bez tokenów). Kod wyjścia 1, gdy brak sesji. |
| `ksef list [--subject buyer\|seller\|third\|authorized] [--from D] [--to D] [--date-type issue\|invoicing\|storage] [--json]` | tak | Metadane faktur z KSeF. Domyślnie faktury otrzymane (`buyer`) od 1. dnia bieżącego miesiąca. TSV bez nagłówka: numer KSeF, numer faktury, data wystawienia, NIP i nazwa sprzedawcy, identyfikator i nazwa nabywcy, brutto, waluta. Daty `RRRR-MM-DD` (czas lokalny, „do” włącznie) albo ISO 8601. Zakres dłuższy niż limit KSeF (100 dni) dzielony automatycznie; wynik obcięty przez KSeF do 10 000 pozycji pobierany dalej od ostatniej daty, bez powtórek. Pusty wynik to kod 0. |
| `ksef send [pliki\|-]... [--json]` | tak | Wysyła faktury FA(3) w jednej sesji interaktywnej i czeka na numery KSeF. Najpierw ta sama kontrola co `validate` oraz data wystawienia nie z przyszłości — jeśli któryś plik jej nie przejdzie, nie wysyła nic. Gdy KSeF zgłosi sesję tymczasowo niedostępną (21184), wysyła dalej w nowej sesji. Gdy odpowiedź na wysyłkę nie dotrze (sieć, limit czasu), szuka faktury w sesji po skrócie SHA-256 — przyjętą podaje z numerem KSeF zamiast zgłaszać jako niewysłaną. Wynik TSV: numer KSeF, plik, numer sesji (do UPO); odrzucone (np. 440 duplikat z `originalKsefNumber`) na stderr. Kod wyjścia: 0 wszystkie przyjęte, 1 jest błąd. |
| `ksef get [numery\|-]... [-d katalog]` | tak | XML faktur po numerze KSeF — bajt w bajt to, co wysłał wystawca. Numery z argumentów albo ze stdin (pierwsza kolumna TSV, więc `ksef list \| ksef get -d faktury/` działa bez `cut`). Jeden numer bez `-d`: XML na stdout. Z `-d`: pliki `<numer KSeF>.xml` (zapis atomowy), ścieżki na stdout; już pobrane pomija bez sieci. Faktura przyjęta przed chwilą, jeszcze niedostępna (KSeF 21165), jest pobierana ponownie do 1 min. Kod wyjścia: 0 wszystkie pobrane, 1 jest błąd. |
| `ksef upo [numery\|-]... [--session S] [--collective] [-d katalog]` | tak | Urzędowe Poświadczenie Odbioru (XML z podpisem MF). Dla faktur: numer KSeF + numer sesji wysyłki — wejście w formacie `ksef send` (numer KSeF, plik, sesja), więc `ksef send *.xml \| ksef upo -d upo/` działa wprost; numery bez kolumny sesji biorą `--session`. `--collective`: zbiorcze UPO sesji. UPO faktury KSeF wydaje razem z numerem KSeF, także gdy sesja jest jeszcze otwarta (np. przez inny program); zbiorcze UPO powstaje dopiero po zamknięciu sesji — przy otwartej `--collective` od razu to zgłasza, przy zamkniętej czeka (do 3 min). Jedno UPO na stdout, kilka do `-d` (`<numer KSeF>.upo.xml`, `<sesja>.upo.xml`). Kod wyjścia: 0 wszystkie pobrane, 1 jest błąd. |

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
ksef send faktury/*.xml > wyslane.tsv                   # numer KSeF, plik, sesja
ksef list | ksef get -d otrzymane/                      # pobiera tylko nowe faktury otrzymane
ksef send faktury/*.xml | tee wyslane.tsv | ksef upo -d upo/   # wysyłka + UPO każdej faktury
```

Limity KSeF: 429 z krótkim czasem (np. „8 żądań na sekundę”) komendy przeczekują same (do 3 ponowień,
czas z `Retry-After`); długi limit (np. „20 zapytań o metadane na godzinę”) kończy komendę od razu komunikatem
KSeF z czasem do ponowienia. Każde 90 dni zakresu `ksef list` to co najmniej jedno zapytanie — długich zakresów
nie warto odpytywać często. Przekroczenie czasu odpowiedzi HTTP (5 min) kończy komendę komunikatem i kodem 1.

Wyszukiwanie metadanych w KSeF jest ostatecznie spójne: faktura przyjęta przed chwilą bywa widoczna
w `ksef list` dopiero po kilku sekundach.

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

Druga metoda to token KSeF, wygenerowany w aplikacji KSeF albo przez API po zalogowaniu certyfikatem:

```bash
pass show ksef/token | ksef login --nip 5908996669 --token-file -   # z menedżera haseł przez stdin
ksef login --nip 5908996669 --token-file ~/.config/ksef/token   # albo z pliku (0600)
```

Token jest sekretem, więc `ksef` przyjmuje go tylko z pliku albo stdin, nigdy jako argument (byłby widoczny
w `ps` i historii powłoki). Liczy się pierwszy niepusty wiersz. KSeF dostaje token zaszyfrowany kluczem
publicznym MF razem z czasem wyzwania.

Trzecia — podpis innym programem (Profil Zaufany, aplikacja do podpisu kwalifikowanego na karcie):
`ksef` wypisuje niepodpisane żądanie z wyzwaniem KSeF, a po podpisaniu je wysyła.

```bash
ksef login --nip 5908996669 --request > logowanie.xml
# podpisz logowanie.xml podpisem XAdES otaczanym (enveloped), np. na gov.pl (Profil Zaufany)
ksef login --nip 5908996669 --signed logowanie-podpisany.xml
```

Wyzwanie jest ważne kilka minut, więc podpisać trzeba od razu. Przed wysyłką `ksef` sprawdza tylko, czy plik
to podpisane żądanie logowania dla tego NIP-u; sam podpis weryfikuje KSeF.

Czwarta — klucz, który nie opuszcza karty, tokenu ani TPM: `--cert` z samym certyfikatem i `--key` z URI PKCS#11
(RFC 7512). Podpis robi OpenSSL systemu przez moduł PKCS#11, a p11-kit udostępnia wszystkie zarejestrowane karty
(OpenSC, TPM, SoftHSM).

```bash
p11tool --list-all-privkeys                    # znajdź URI klucza (gnutls-bin), np. pkcs11:token=…;object=…
ksef login --nip 5908996669 --cert cert.pem --key 'pkcs11:token=Karta;object=Podpis;type=private' --pin-file -
```

- PIN tylko z pliku albo stdin (`--pin-file`, pierwszy niepusty wiersz, spacje się liczą), nigdy jako argument
  ani w `--key` (`pin-value=` jest odrzucane). Komunikaty modułów PKCS#11 przechwytujemy przy każdej operacji
  na kluczu, więc udane logowanie jest ciche, a przy błędzie PIN jest zamaskowany. Zły PIN wychodzi przed
  połączeniem z KSeF (próbny podpis).
- `--key-via engine` (libp11, pakiet `libengine-pkcs11-openssl`) albo `provider` (`pkcs11-provider`).
  Domyślnie engine na OpenSSL < 3.2, provider na nowszym. pkcs11-provider 0.3 na OpenSSL 3.0 (Ubuntu 24.04)
  zawiesza się z SoftHSM, dlatego tam engine.
  Gdy w systemie brakuje wybranego modułu, `ksef` podaje pakiet do zainstalowania i drugą możliwość `--key-via`.
- Testy (`LoginPkcs11Tests`, E2E `Login_KeyOnPkcs11Token`) zakładają tymczasowy token SoftHSM (`softhsm2`);
  bez niego się pomijają.

Sesje (access token ważny ok. 15 min, refresh token 7 dni) to stan narzędzia, nie trwały sekret:
`$XDG_RUNTIME_DIR/ksef/<środowisko>-<NIP>.json` z prawami 0600 — katalog w RAM, znika po wylogowaniu
z systemu. Komendy wymagające logowania same odświeżają access token. Przy kilku sesjach w jednym
środowisku wskazuje się `--nip`.
