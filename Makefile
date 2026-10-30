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

# Jedna natywna binarka $(AOT_DIR)/ksef (bez .NET na maszynie docelowej).
aot:
	dotnet publish src/KsefCli -c Release -r $(RID) -p:KsefAot=true -o $(AOT_DIR)

# Te same testy CLI (z E2E) na binarce AOT — po każdej zmianie typów JSON i po podbiciu biblioteki MF.
test-aot: aot
	KSEF_E2E=1 KSEF_BIN=$(CURDIR)/$(AOT_DIR)/ksef dotnet test --project tests/KsefCli.Tests -c Release

install: aot
	install -Dm755 $(AOT_DIR)/ksef $(PREFIX)/bin/ksef

clean:
	rm -rf dist
	dotnet clean ksef-app.slnx -c Release
