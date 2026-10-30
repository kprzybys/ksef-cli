#!/usr/bin/env bash
# Pakiety systemowe dla ksef — rozpoznaje dystrybucję z /etc/os-release.
#   --runtime  (domyślnie) do uruchomienia binarki: ICU, OpenSSL, libxslt, certyfikaty CA
#   --build    do budowania ze źródeł (make build/test/aot): powyższe + clang, zlib, make, git, .NET SDK 10
#   --yes      instaluje (przez sudo, gdy nie root); bez tego tylko wypisuje polecenie
# .NET SDK 10 spoza repozytoriów dystrybucji: oficjalny dotnet-install.sh do ~/.dotnet (bez sudo).
set -euo pipefail

mode=runtime
apply=false
for arg in "$@"; do
    case $arg in
        --runtime) mode=runtime ;;
        --build) mode=build ;;
        --yes) apply=true ;;
        -h | --help) sed -n '2,6p' "$0" | sed 's/^# \{0,1\}//'; exit 0 ;;
        *) echo "install-deps: nieznana opcja $arg" >&2; exit 2 ;;
    esac
done

if [[ ! -r /etc/os-release ]]; then
    echo "install-deps: brak /etc/os-release — nie rozpoznaję dystrybucji" >&2
    exit 1
fi
. /etc/os-release
family=" ${ID:-} ${ID_LIKE:-} "

sudo=()
if [[ $(id -u) != 0 ]]; then
    sudo=(sudo)
fi

# Pierwszy pakiet z repozytorium pasujący do wzorca (nazwy z numerem wersji: libicu74, libssl3t64...).
apt_pick() {
    apt-cache pkgnames 2>/dev/null | grep -xE "$1" | sort -V | tail -1
}
zypper_pick() {
    zypper --non-interactive --quiet search --type package 2>/dev/null \
        | cut -d'|' -f2 | tr -d ' ' | grep -xE "$1" | sort -V | tail -1
}

dotnet_from_distro=true
case $family in
    *" debian "* | *" ubuntu "*)
        manager=apt
        if [[ -z $(apt-cache pkgnames 2>/dev/null | head -1) ]]; then
            echo "install-deps: puste indeksy apt — najpierw ${sudo[*]:+sudo }apt-get update" >&2
            exit 1
        fi
        packages=("$(apt_pick 'libicu[0-9]+')" "$(apt_pick 'libssl3(t64)?')" libxslt1.1 ca-certificates)
        if [[ $mode == build ]]; then
            packages+=(clang zlib1g-dev make git curl)
            if [[ -n $(apt_pick 'dotnet-sdk-10\.0') ]]; then
                packages+=(dotnet-sdk-10.0)
            else
                dotnet_from_distro=false
            fi
        fi
        install=(apt-get install -y --no-install-recommends)
        ;;
    *" fedora "* | *" rhel "* | *" centos "*)
        manager=dnf
        packages=(libicu openssl-libs libxslt ca-certificates)
        if [[ $mode == build ]]; then
            packages+=(clang zlib-devel make git dotnet-sdk-10.0)
        fi
        install=(dnf install -y)
        ;;
    *" arch "*)
        manager=pacman
        packages=(icu openssl libxslt ca-certificates)
        if [[ $mode == build ]]; then
            packages+=(clang zlib make git dotnet-sdk)
        fi
        install=(pacman -S --needed --noconfirm)
        ;;
    *" suse "* | *" opensuse "*)
        manager=zypper
        packages=("$(zypper_pick 'libicu[0-9_]+')" "$(zypper_pick 'libopenssl3')" "$(zypper_pick 'libxslt1')" ca-certificates)
        if [[ $mode == build ]]; then
            packages+=(clang zlib-devel make git curl)
            dotnet_from_distro=false
        fi
        install=(zypper --non-interactive install)
        ;;
    *)
        echo "install-deps: nieobsługiwana dystrybucja '${PRETTY_NAME:-$ID}' — zob. docs/INSTALL.md" >&2
        exit 1
        ;;
esac

for package in "${packages[@]}"; do
    if [[ -z $package ]]; then
        echo "install-deps: nie znalazłem w repozytorium $manager któregoś z pakietów (ICU/OpenSSL/libxslt)" >&2
        exit 1
    fi
done

command="${sudo[*]:+sudo }${install[*]} ${packages[*]}"
dotnet_command='curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0 && export PATH="$HOME/.dotnet:$PATH"'

if [[ $apply != true ]]; then
    echo "$command"
    if [[ $dotnet_from_distro != true ]]; then
        echo "$dotnet_command"
    fi
    exit 0
fi

"${sudo[@]}" "${install[@]}" "${packages[@]}"
if [[ $dotnet_from_distro != true ]] && ! command -v dotnet >/dev/null; then
    curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --channel 10.0
    echo "install-deps: .NET SDK w ~/.dotnet — dopisz do PATH: export PATH=\"\$HOME/.dotnet:\$PATH\"" >&2
fi
