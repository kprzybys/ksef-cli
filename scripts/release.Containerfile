# Środowisko budowania wydań: AlmaLinux 9 ma glibc 2.34, więc binarka Native AOT zbudowana tutaj działa
# na RHEL/Alma/Rocky 9+, Ubuntu 22.04+, Debianie 12+ (binarka AOT wymaga glibc nie nowszej niż ta z builda).
FROM almalinux:9
# DEFAULT:SHA1 — kompilator podpisuje biblioteki MF silną nazwą (RSA/SHA-1), a domyślna polityka
# kryptograficzna RHEL 9 odrzuca podpisy SHA-1. Dotyczy tylko tego kontenera, nie binarki ksef.
RUN dnf -y install clang zlib-devel make binutils rsync libicu crypto-policies-scripts \
    && update-crypto-policies --set DEFAULT:SHA1 \
    && dnf clean all
# Oficjalne SDK w wersji podanej przez release-build.sh (ta sama co na hoście / w CI), nie paczka dystrybucji.
ARG DOTNET_SDK_VERSION
RUN curl -sSL https://dot.net/v1/dotnet-install.sh | bash -s -- --version "$DOTNET_SDK_VERSION" --install-dir /usr/share/dotnet \
    && ln -s /usr/share/dotnet/dotnet /usr/bin/dotnet
ENV DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
