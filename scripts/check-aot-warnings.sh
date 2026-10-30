#!/usr/bin/env bash
# Porównuje ostrzeżenia kompilatora Native AOT z logu `dotnet publish` (-flp, verbosity=normal)
# ze wzorcem znanych ostrzeżeń. Nowe → błąd (kod 1); zniknięte → tylko informacja.
# Użycie: check-aot-warnings.sh <log> <wzorzec>
set -euo pipefail

log=$1
baseline=$2

if ! grep -q "Generating native code" "$log"; then
    echo "check-aot-warnings: kompilator AOT nie uruchomił się w tej publikacji — brak ostrzeżeń do sprawdzenia" >&2
    exit 0
fi

# Bez ścieżki, numeru linii i nazwy projektu: zmieniają się przy każdej aktualizacji biblioteki MF.
current=$(grep -E "warning IL[0-9]+:" "$log" | sed -E 's/^.* warning (IL[0-9]+): (.*) \[[^]]*\]$/\1 \2/' | LC_ALL=C sort -u || true)
known=$(grep -v '^#' "$baseline" | LC_ALL=C sort -u)

new=$(LC_ALL=C comm -23 <(printf '%s\n' "$current") <(printf '%s\n' "$known") | sed '/^$/d')
gone=$(LC_ALL=C comm -13 <(printf '%s\n' "$current") <(printf '%s\n' "$known") | sed '/^$/d')

if [[ -n $gone ]]; then
    echo "check-aot-warnings: tych ostrzeżeń już nie ma — można je usunąć z $baseline:" >&2
    sed 's/^/  /' <<<"$gone" >&2
fi
if [[ -n $new ]]; then
    echo "check-aot-warnings: NOWE ostrzeżenia Native AOT (opis postępowania w $baseline):" >&2
    sed 's/^/  /' <<<"$new" >&2
    exit 1
fi
