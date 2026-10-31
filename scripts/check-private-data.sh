#!/usr/bin/env bash
# Blokuje commit, gdy dodawane wiersze albo opis commita pasują do prywatnych wzorców (NIP-y firm, nazwy
# wewnętrznych serwerów, ścieżki domowe). Lista leży POZA repo — repo jest publiczne, więc lista też jest
# prywatna: KSEF_PRIVATE_PATTERNS albo ~/.config/ksef-cli/private-patterns (wiersz = wyrażenie ERE, # = komentarz).
# Bez listy nic nie sprawdza — współtwórcy bez niej nie są blokowani.
# Użycie: check-private-data.sh            — zmiany w indeksie (pre-commit)
#         check-private-data.sh <plik-msg> — opis commita (commit-msg)
set -euo pipefail

list=${KSEF_PRIVATE_PATTERNS:-$HOME/.config/ksef-cli/private-patterns}
[[ -r $list ]] || exit 0
patterns=$(grep -vE '^[[:space:]]*(#|$)' "$list" || true)
[[ -n $patterns ]] || exit 0

if [[ $# -gt 0 ]]; then
    what="opis commita"
    content=$(grep -v '^#' "$1")
else
    what="dodawane wiersze"
    # Tylko dodawane wiersze, bez submodułu biblioteki MF; każdy poprzedzony nazwą pliku i tabulatorem.
    content=$(git diff --cached --no-color --unified=0 -- . ':!external' \
        | awk '/^\+\+\+ /{file=substr($0, 7); next} /^\+/{print file "\t" substr($0, 2)}')
fi

if hits=$(grep -iE -f <(printf '%s\n' "$patterns") <<<"$content"); then
    echo "Dane prywatne ($what) — repo jest publiczne. Usuń je albo zamień na fikcyjne (np. NIP 1234563218):" >&2
    # Bez treści dopasowania: komunikat sam nie powinien powtarzać danych.
    if [[ $# -gt 0 ]]; then
        echo "  opis commita" >&2
    else
        cut -f1 <<<"$hits" | sort | uniq -c | sed 's/^ */  /' >&2
    fi
    exit 1
fi
