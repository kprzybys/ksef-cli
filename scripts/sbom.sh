#!/usr/bin/env bash
# SBOM (CycloneDX JSON) binarki ksef: pakiety NuGet z grafu, który naprawdę trafia do binarki (bez narzędzi
# budowania), plus to, czego CycloneDX nie widzi — wersja biblioteki MF z taga submodułu i środowisko .NET
# wkompilowane w binarkę Native AOT. Użycie: scripts/sbom.sh <wersja> <rid> <plik.cdx.json>
set -euo pipefail
cd "$(dirname "$0")/.."

version=$1 rid=$2 out=$3
mkdir -p "$(dirname "$out")"
# Bez --recursive: osobna analiza projektu MF dałaby jego własne (starsze) wersje pakietów zamiast tych,
# które rozwiązuje KsefCli (np. przypięte System.Security.Cryptography.* 10.0.14).
dotnet tool run dotnet-CycloneDX -- src/KsefCli/KsefCli.csproj -tfm net10.0 -rt "$rid" -c Release \
    --exclude-dev --include-project-references --set-name ksef --set-version "$version" \
    --output-format Json -o "$(dirname "$out")" -fn "$(basename "$out")" >&2

mf=$(git -C external/ksef-client-csharp describe --tags --exact-match 2>/dev/null \
    || git -C external/ksef-client-csharp rev-parse --short HEAD)
runtime=$(dotnet msbuild src/KsefCli -getProperty:BundledNETCoreAppPackageVersion)

python3 - "$out" "$version" "$rid" "$mf" "$runtime" <<'PY'
import json, sys
path, version, rid, mf, runtime = sys.argv[1:]
bom = json.load(open(path, encoding="utf-8"))
mf_url = "https://github.com/CIRFMF/ksef-client-csharp"
for c in bom.get("components", []):
    if c["name"] in ("KSeF.Client", "KSeF.Client.Core"):
        # Projekt z submodułu (bez numeru w csproj): wersja = tag biblioteki MF.
        c["version"] = mf
        c["purl"] = f"pkg:github/CIRFMF/ksef-client-csharp@{mf}"
        c["licenses"] = [{"license": {"id": "MIT"}}]
        c["externalReferences"] = [{"type": "vcs", "url": mf_url}]
    elif c["name"] == "Ksef.Fa3":
        c["version"] = version
# Native AOT: runtime .NET jest częścią binarki — bez tego skaner nie dopasuje luk w samym .NET.
root = bom["metadata"]["component"]
added = []
for name in (f"Microsoft.NETCore.App.Runtime.{rid}", f"runtime.{rid}.Microsoft.DotNet.ILCompiler"):
    ref = f"pkg:nuget/{name}@{runtime}"
    bom.setdefault("components", []).append({
        "type": "framework", "bom-ref": ref, "name": name, "version": runtime, "purl": ref,
        "licenses": [{"license": {"id": "MIT"}}],
    })
    added.append(ref)
for d in bom.get("dependencies", []):
    if d.get("ref") == root.get("bom-ref"):
        d.setdefault("dependsOn", []).extend(added)
json.dump(bom, open(path, "w", encoding="utf-8"), indent=2, ensure_ascii=False)
PY
echo "$out"
