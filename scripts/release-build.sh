#!/usr/bin/env bash
# Wydanie ksef (Native AOT) dla architektury tej maszyny, zbudowane w kontenerze AlmaLinux 9
# (scripts/release.Containerfile), żeby binarka działała też na starszych dystrybucjach.
# Wynik: dist/release/ksef-<wersja>-linux-<x64|arm64>.tar.gz + .sha256.
# Użycie: scripts/release-build.sh [wersja]   (domyślnie git describe)
set -euo pipefail
cd "$(dirname "$0")/.."

# Najwyższa wersja glibc, której binarka może wymagać — glibc AlmaLinux 9.
max_glibc=2.34

version=${1:-$(git describe --tags --always --dirty)}
case $(uname -m) in
    x86_64) arch=x64 ;;
    aarch64) arch=arm64 ;;
    *) echo "release-build: nieobsługiwana architektura $(uname -m) (Native AOT: x64, arm64)" >&2; exit 1 ;;
esac
engine=${CONTAINER_ENGINE:-$(command -v podman || command -v docker || true)}
if [[ -z $engine ]]; then
    echo "release-build: potrzebny podman albo docker" >&2
    exit 1
fi

image=ksef-release-alma9
out=dist/release
mkdir -p "$out"
"$engine" build -q -t "$image" -f scripts/release.Containerfile scripts >/dev/null

# Źródła tylko do odczytu, budowanie na kopii w kontenerze: obj/ i bin/ z builda na hoście się nie mieszają.
# Pakiety NuGet w nazwanym wolumenie — kolejne wydania nie pobierają ich od nowa.
"$engine" run --rm \
    -v "$PWD":/src:ro \
    -v "$PWD/$out":/out \
    -v ksef-release-nuget:/root/.nuget/packages \
    -e KSEF_VERSION="$version" -e ARCH="$arch" -e MAX_GLIBC="$max_glibc" -e HOST_UID="$(id -u)" -e HOST_GID="$(id -g)" \
    "$image" bash -euo pipefail -c '
        rsync -a --exclude=bin/ --exclude=obj/ --exclude=dist/ --exclude=.git/ /src/ /work/
        cd /work
        make aot RID=linux-$ARCH
        needed=$(objdump -T dist/aot/ksef | grep -oE "GLIBC_[0-9.]+" | sed "s/GLIBC_//" | sort -V | tail -1)
        if [[ $(printf "%s\n%s\n" "$needed" "$MAX_GLIBC" | sort -V | tail -1) != "$MAX_GLIBC" ]]; then
            echo "release-build: binarka wymaga glibc $needed > $MAX_GLIBC" >&2
            exit 1
        fi
        name=ksef-$KSEF_VERSION-linux-$ARCH
        mkdir -p /tmp/$name
        cp dist/aot/ksef README.md /tmp/$name/
        tar -C /tmp -czf /out/$name.tar.gz $name
        (cd /out && sha256sum $name.tar.gz > $name.tar.gz.sha256)
        chown "$HOST_UID:$HOST_GID" /out/$name.tar.gz /out/$name.tar.gz.sha256
        echo "glibc >= $needed: /out/$name.tar.gz"
    '
