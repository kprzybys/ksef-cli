#!/usr/bin/env python3
"""Pobiera schemat FA(3) z crd.gov.pl razem z importowanymi schematami
i przepisuje schemaLocation na lokalne nazwy plików (walidacja offline).

Użycie: scripts/fetch-fa3-schemas.py [URL schematu] [katalog docelowy]
"""
import re
import sys
import urllib.parse
import urllib.request
from pathlib import Path

ROOT_URL = "http://crd.gov.pl/wzor/2025/06/25/13775/schemat.xsd"
LOCATION = re.compile(r'schemaLocation="([^"]+)"')


def main() -> None:
    root_url = sys.argv[1] if len(sys.argv) > 1 else ROOT_URL
    out_dir = Path(sys.argv[2] if len(sys.argv) > 2 else Path(__file__).parent.parent / "schemas" / "fa3")
    out_dir.mkdir(parents=True, exist_ok=True)

    queue = [root_url]
    names: dict[str, str] = {}
    while queue:
        url = queue.pop()
        if url in names:
            continue
        name = Path(urllib.parse.urlparse(url).path).name
        if name in names.values():
            sys.exit(f"Kolizja nazw plików: {name} ({url})")
        names[url] = name
        with urllib.request.urlopen(url, timeout=30) as response:
            text = response.read().decode("utf-8")

        def localize(match: re.Match[str]) -> str:
            target = urllib.parse.urljoin(url, match.group(1))
            queue.append(target)
            return f'schemaLocation="{Path(urllib.parse.urlparse(target).path).name}"'

        text = LOCATION.sub(localize, text)
        (out_dir / name).write_text(text, encoding="utf-8")
        print(f"{name}  <-  {url}")


if __name__ == "__main__":
    main()
