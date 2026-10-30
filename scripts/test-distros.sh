#!/usr/bin/env bash
# Binarka ksef w czystych kontenerach różnych dystrybucji: czy rusza od razu, a po
# scripts/install-deps.sh --runtime — czy działają validate, show, cert new i status (sieć).
# Wynik: tabela Markdown na stdout (do docs/INSTALL.md), szczegóły w dist/distro-test/<obraz>.log.
# Użycie: scripts/test-distros.sh [binarka] [obraz...]   (domyślnie dist/aot/ksef i lista niżej)
set -euo pipefail
cd "$(dirname "$0")/.."

binary=${1:-dist/aot/ksef}
shift || true
images=("$@")
if [[ ${#images[@]} == 0 ]]; then
    images=(ubuntu:22.04 ubuntu:24.04 ubuntu:26.04 debian:12 debian:13 almalinux:9 almalinux:10
        fedora:latest archlinux:latest opensuse/tumbleweed)
fi
engine=${CONTAINER_ENGINE:-$(command -v docker || command -v podman || true)}
if [[ -z $engine ]]; then
    echo "test-distros: potrzebny docker albo podman" >&2
    exit 1
fi

# Katalog w repo, nie /tmp: docker ze snapa nie widzi /tmp hosta.
work=dist/distro-test
rm -rf "$work"
mkdir -p "$work"
cp "$binary" "$work/ksef"
cp scripts/install-deps.sh "$work/"
sed -e 's/#nip#/1111111111/' -e 's/#invoice_number#/FV\/1/' \
    external/ksef-client-csharp/KSeF.Client.Tests.Core/Templates/invoice-template-fa-3.xml > "$work/faktura.xml"

cat > "$work/check.sh" <<'EOF'
set -u
result() { printf '%s=%s\n' "$1" "$2"; }
check() { local name=$1; shift; if "$@" >/tmp/out 2>&1; then result "$name" ok; else result "$name" "BŁĄD"; sed 's/^/    /' /tmp/out | head -3; fi; }

. /etc/os-release
result system "$PRETTY_NAME"
result glibc "$(ldd --version 2>&1 | head -1 | grep -oE '[0-9]+\.[0-9]+$')"
if /k/ksef --help >/tmp/out 2>&1; then result od_razu ok; else result od_razu "$(grep -oE "GLIBC_[0-9.]+' not found|ICU" /tmp/out | head -1 || echo BŁĄD)"; fi

case " $ID ${ID_LIKE:-} " in
    *" debian "* | *" ubuntu "*) apt-get update -qq >/dev/null ;;
    *" arch "*) pacman -Sy --noconfirm >/dev/null ;;
esac
if bash /k/install-deps.sh --runtime --yes >/tmp/deps 2>&1; then
    result pakiety "$(bash /k/install-deps.sh --runtime | sed -E 's/.* (install|-S) (-[^ ]+ )*//')"
else
    result pakiety "BŁĄD"; tail -3 /tmp/deps | sed 's/^/    /'
fi

check validate /k/ksef validate /k/faktura.xml
check show sh -c '/k/ksef show /k/faktura.xml | grep -q "<html"'
check cert sh -c 'cd /tmp && /k/ksef cert new --nip 1111111111 -o /tmp/c.pem'
check status /k/ksef status -e test
EOF

echo "| Obraz | System | glibc | Bez pakietów | Pakiety (install-deps --runtime) | validate | show | cert | status |"
echo "|---|---|---|---|---|---|---|---|---|"
for image in "${images[@]}"; do
    log="$work/${image//[:\/]/_}.log"
    "$engine" run --rm -v "$PWD/$work":/k:ro "$image" bash /k/check.sh >"$log" 2>&1 || true
    get() { grep -m1 "^$1=" "$log" | cut -d= -f2- || true; }
    echo "| \`$image\` | $(get system) | $(get glibc) | $(get od_razu) | $(get pakiety) | $(get validate) | $(get show) | $(get cert) | $(get status) |"
done
