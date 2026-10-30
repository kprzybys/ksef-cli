#!/usr/bin/env bash
# Generuje klasy C# z oficjalnego schematu FA(3) (schemas/fa3) do src/Ksef.Fa3/Generated.
# Po zmianie schematu: scripts/fetch-fa3-schemas.py && scripts/generate-fa3.sh
set -euo pipefail
cd "$(dirname "$0")/.."

out=src/Ksef.Fa3/Generated
rm -rf "$out"
dotnet tool restore >/dev/null
dotnet xscgen \
  -n "http://crd.gov.pl/wzor/2025/06/25/13775/=Ksef.Fa3" \
  -n "http://crd.gov.pl/xml/schematy/dziedzinowe/mf/2022/01/05/eD/DefinicjeTypy/=Ksef.Fa3.Typy" \
  --netCore --nullable --commandArgs \
  -o "$out" \
  schemas/fa3/schemat.xsd
