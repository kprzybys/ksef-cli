# Budowanie i testy ksef. Native AOT wymaga clang i zlib (pakiety clang, zlib1g-dev);
# `ksef show` w czasie działania potrzebuje libxslt (pakiet libxslt1.1).
RID ?= linux-x64
AOT_DIR := dist/aot
PREFIX ?= $(HOME)/.local

.PHONY: build test test-e2e aot test-aot install clean

build:
	dotnet build ksef-app.slnx -c Release

# Testy bez sieci; test-e2e dokłada testy na żywym KSeF TEST.
test:
	dotnet test --solution ksef-app.slnx -c Release

test-e2e:
	KSEF_E2E=1 dotnet test --solution ksef-app.slnx -c Release

# Jedna natywna binarka $(AOT_DIR)/ksef (bez .NET na maszynie docelowej). Nowe ostrzeżenie kompilatora
# AOT (spoza src/KsefCli/aot-warnings.txt) przerywa budowanie.
aot:
	rm -rf $(AOT_DIR) && mkdir -p $(AOT_DIR)
	dotnet publish src/KsefCli -c Release -r $(RID) -p:KsefAot=true -o $(AOT_DIR) "-flp:logfile=$(AOT_DIR).log;verbosity=normal"
	scripts/check-aot-warnings.sh $(AOT_DIR).log src/KsefCli/aot-warnings.txt

# Te same testy CLI (z E2E) na binarce AOT — po każdej zmianie typów JSON i po podbiciu biblioteki MF.
test-aot: aot
	KSEF_E2E=1 KSEF_BIN=$(CURDIR)/$(AOT_DIR)/ksef dotnet test --project tests/KsefCli.Tests -c Release

install: aot
	install -Dm755 $(AOT_DIR)/ksef $(PREFIX)/bin/ksef

clean:
	rm -rf dist
	dotnet clean ksef-app.slnx -c Release

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
