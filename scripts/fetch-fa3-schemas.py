#!/usr/bin/env python3
"""Pobiera z crd.gov.pl schemat FA(3) i oficjalny arkusz wizualizacji (XSLT) razem ze wszystkimi
plikami, do których się odwołują, i przepisuje odwołania na lokalne nazwy plików (praca offline).

Śledzone odwołania: xsd:import/include (schemaLocation), xsl:import/include (href)
oraz adresy schematów-słowników w parametrach XSLT czytanych przez document().

Użycie: scripts/fetch-fa3-schemas.py [URL katalogu wzoru]
"""

import re
import sys
import urllib.parse
import urllib.request
from pathlib import Path

WZOR_URL = "http://crd.gov.pl/wzor/2025/06/25/13775/"
ROOT = Path(__file__).resolve().parent.parent

SCHEMA_LOCATION = re.compile(r'(schemaLocation=")([^"]+)(")')
XSL_HREF = re.compile(r'(<xsl:(?:import|include)\s+href=")([^"]+)(")')
DOCUMENT_PARAM = re.compile(r"""(select="')(https?://crd\.gov\.pl/[^']+\.xsd)(')""")


def fetch_tree(root_url: str, out_dir: Path) -> None:
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
            target = urllib.parse.urljoin(url, match.group(2))
            queue.append(target)
            return (
                match.group(1)
                + Path(urllib.parse.urlparse(target).path).name
                + match.group(3)
            )

        for pattern in (SCHEMA_LOCATION, XSL_HREF, DOCUMENT_PARAM):
            text = pattern.sub(localize, text)
        (out_dir / name).write_text(text, encoding="utf-8")
        print(f"{out_dir.relative_to(ROOT)}/{name}  <-  {url}")


def main() -> None:
    wzor = sys.argv[1] if len(sys.argv) > 1 else WZOR_URL
    fetch_tree(urllib.parse.urljoin(wzor, "schemat.xsd"), ROOT / "schemas" / "fa3")
    fetch_tree(urllib.parse.urljoin(wzor, "styl.xsl"), ROOT / "stylesheets" / "fa3")


if __name__ == "__main__":
    main()
