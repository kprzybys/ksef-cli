# Budowanie i testy ksef. Native AOT wymaga clang i zlib (Linux: pakiety clang, zlib1g-dev; macOS: Xcode
# Command Line Tools); `ksef show` w czasie działania potrzebuje libxslt (Linux: libxslt1.1; macOS: w systemie).
# System i architektura tej maszyny (Native AOT: linux/osx, x64/arm64); inne — make aot RID=...
RID ?= $(if $(filter Darwin,$(shell uname -s)),osx,linux)-$(if $(filter aarch64 arm64,$(shell uname -m)),arm64,x64)
AOT_DIR := dist/aot
PREFIX ?= $(HOME)/.local

.PHONY: build test test-e2e aot test-aot release test-distros install clean

build:
	dotnet build ksef-cli.slnx -c Release

# Testy bez sieci; test-e2e dokłada testy na żywym KSeF TEST.
test:
	dotnet test --solution ksef-cli.slnx -c Release

test-e2e:
	KSEF_E2E=1 dotnet test --solution ksef-cli.slnx -c Release

# Jedna natywna binarka $(AOT_DIR)/ksef (bez .NET na maszynie docelowej). Nowe ostrzeżenie kompilatora
# AOT (spoza src/KsefCli/aot-warnings.txt) przerywa budowanie. KSEF_VERSION=… trafia do `ksef --version`
# (nie VERSION: make eksportuje zmienne z wiersza poleceń, a MSBuild wziąłby ją za wersję pakietu).
aot:
	rm -rf $(AOT_DIR) && mkdir -p $(AOT_DIR)
	dotnet publish src/KsefCli -c Release -r $(RID) -p:KsefAot=true -o $(AOT_DIR) "-flp:logfile=$(AOT_DIR).log;verbosity=normal" \
		$(if $(KSEF_VERSION),-p:InformationalVersion=$(KSEF_VERSION))
	scripts/check-aot-warnings.sh $(AOT_DIR).log src/KsefCli/aot-warnings.txt

# Te same testy CLI (z E2E) na binarce AOT — po każdej zmianie typów JSON i po podbiciu biblioteki MF.
test-aot: aot
	KSEF_E2E=1 KSEF_BIN=$(CURDIR)/$(AOT_DIR)/ksef dotnet test --project tests/KsefCli.Tests -c Release

# Wydanie dla architektury tej maszyny, budowane w kontenerze AlmaLinux 9 (glibc 2.34 — działa też na starszych
# dystrybucjach): dist/release/ksef-<wersja>-linux-<arch>.tar.gz. Wymaga podmana albo dockera.
# REMOTE=host (domyślnie $$KSEF_REMOTE, np. export KSEF_REMOTE=serwer-budowania): oba cele na tamtej maszynie
# przez scripts/remote.sh, wyniki wracają do dist/.
REMOTE ?= $(KSEF_REMOTE)

release:
ifneq ($(REMOTE),)
	scripts/remote.sh $(REMOTE) release
else
	scripts/release-build.sh $(KSEF_VERSION)
endif

# Binarka w czystych kontenerach kilku dystrybucji (tabela do docs/INSTALL.md); domyślnie $(AOT_DIR)/ksef.
test-distros:
ifneq ($(REMOTE),)
	scripts/remote.sh $(REMOTE) test-distros BIN=$(BIN)
else
	scripts/test-distros.sh $(or $(BIN),$(AOT_DIR)/ksef)
endif

install: aot
	mkdir -p $(PREFIX)/bin && install -m755 $(AOT_DIR)/ksef $(PREFIX)/bin/ksef

clean:
	rm -rf dist
	dotnet clean ksef-cli.slnx -c Release

# Jakość kodu. Analizatory (Roslyn latest-recommended + Meziantou) i NuGetAudit działają w każdym
# `dotnet build` — ostrzeżenie przerywa budowanie (Directory.Build.props, wyłączenia w .editorconfig).
.PHONY: fmt lint audit hooks check

FORMAT := dotnet format whitespace --folder --exclude src/Ksef.Fa3/Generated external

fmt:
	$(FORMAT)

# Białe znaki w C# i higiena pozostałych plików (pre-commit na całym repo).
lint:
	$(FORMAT) --verify-no-changes
	uvx pre-commit run --all-files

# Znane luki w pakietach (także przechodnich) naszych projektów; NuGetAudit robi to samo przy restore.
audit:
	for p in src/*/*.csproj tests/*/*.csproj; do \
		dotnet list $$p package --vulnerable --include-transitive || exit 1; \
	done

hooks:
	uvx pre-commit install

# Przed commitem: lint + build z analizatorami + testy.
check: lint build test
