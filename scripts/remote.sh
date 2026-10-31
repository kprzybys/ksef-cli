#!/usr/bin/env bash
# Cel make na innej maszynie (ciężkie budowanie, macierz kontenerów): źródła rsync-iem do ~/ksef-build
# na hoście, tam `make <cel>`, wyniki (dist/release, dist/distro-test) z powrotem do dist/.
# Wersja i SDK z tej maszyny, bo na hoście nie ma .git (git describe) i może nie być dotnet.
# Użycie: scripts/remote.sh <host> <cel> [ZMIENNA=wartość...]
set -euo pipefail
cd "$(dirname "$0")/.."

host=$1
shift
version=$(git describe --tags --always --dirty)
version=${version#v}
sdk=$(dotnet --version 2>/dev/null || true)

rsync -a --delete --exclude=bin/ --exclude=obj/ --exclude=dist/ --exclude=.git ./ "$host:ksef-build/"
# Wyniki poprzednich przebiegów na hoście nie wracają razem z nowymi.
ssh "$host" "rm -rf ksef-build/dist/release ksef-build/dist/distro-test"
# shellcheck disable=SC2029 # argumenty make rozwijane lokalnie celowo
# Bez ponownego użycia procesów MSBuild: wiszące węzły z poprzednich przebiegów potrafią wywrócić `dotnet test`
# na starcie („Internal CLR error”), a host służy też innym.
ssh "$host" "cd ksef-build && PATH=\$HOME/.dotnet:\$PATH MSBUILDDISABLENODEREUSE=1 DOTNET_CLI_USE_MSBUILD_SERVER=0 DOTNET_SDK_VERSION=$sdk make $* KSEF_VERSION=$version"
mkdir -p dist
for dir in release distro-test; do
    if ssh "$host" "test -d ksef-build/dist/$dir"; then
        rsync -a "$host:ksef-build/dist/$dir/" "dist/$dir/"
    fi
done
