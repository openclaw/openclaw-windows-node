"""Seed V2 string keys into all 5 .resw locale files.

Reads V2Strings.DefaultEnUs by parsing V2Strings.cs (since the C# dictionary
literal is stable and easy to scan). Adds any missing V2_* keys to each
locale's Resources.resw with the English value. Existing keys are left
untouched. Idempotent.
"""
from __future__ import annotations
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

REPO_ROOT = Path(__file__).resolve().parent.parent
V2STRINGS_CS = REPO_ROOT / "src" / "OpenClawTray.OnboardingV2" / "V2Strings.cs"
STRINGS_DIR = REPO_ROOT / "src" / "OpenClaw.Tray.WinUI" / "Strings"
LOCALES = ["en-us", "fr-fr", "nl-nl", "zh-cn", "zh-tw"]

# Match a dictionary entry  ["KEY"] = "VALUE",  with optional string concat
# across lines, and grab the joined value. Heuristic but works because
# V2Strings.cs uses simple "literal" + "literal" concatenation.
KEY_VALUE_RE = re.compile(
    r'\["(?P<key>V2_[A-Za-z0-9_]+)"\]\s*=\s*(?P<rhs>(?:"[^"]*"\s*(?:\+\s*"[^"]*"\s*)*)),',
    re.MULTILINE,
)


def parse_v2_strings() -> dict[str, str]:
    text = V2STRINGS_CS.read_text(encoding="utf-8")
    # Bound the search to the DefaultEnUs block.
    block_start = text.find("DefaultEnUs")
    if block_start == -1:
        raise RuntimeError("DefaultEnUs not found in V2Strings.cs")
    block = text[block_start:]
    out: dict[str, str] = {}
    for m in KEY_VALUE_RE.finditer(block):
        rhs = m.group("rhs")
        parts = re.findall(r'"([^"]*)"', rhs)
        value = "".join(parts)
        # Decode \u escapes the way C# parses them.
        value = re.sub(
            r"\\u([0-9A-Fa-f]{4})",
            lambda mm: chr(int(mm.group(1), 16)),
            value,
        )
        out[m.group("key")] = value
    return out


def read_resw_keys(path: Path) -> set[str]:
    """Return the set of <data name="..."> keys already in the file."""
    keys: set[str] = set()
    tree = ET.parse(path)
    for data in tree.getroot().findall("data"):
        name = data.get("name")
        if name:
            keys.add(name)
    return keys


def append_keys(path: Path, missing: dict[str, str]) -> None:
    """Append <data name="K"><value>V</value></data> entries before </root>.

    Uses text manipulation (not ElementTree write) to preserve the file's
    existing indentation, comments, and XML schema preamble exactly. Tests
    in this repo are picky about whitespace in resource files.
    """
    text = path.read_text(encoding="utf-8")
    # Find last </root>
    close_idx = text.rfind("</root>")
    if close_idx == -1:
        raise RuntimeError(f"{path}: no </root> tag found")

    lines = []
    for key, value in missing.items():
        # XML-escape value
        escaped = (value
                   .replace("&", "&amp;")
                   .replace("<", "&lt;")
                   .replace(">", "&gt;"))
        lines.append(f'  <data name="{key}" xml:space="preserve">')
        lines.append(f'    <value>{escaped}</value>')
        lines.append("  </data>")
    block = "\n".join(lines) + "\n"
    new_text = text[:close_idx] + block + text[close_idx:]
    path.write_text(new_text, encoding="utf-8")


def main() -> int:
    keys = parse_v2_strings()
    if not keys:
        print("No V2 keys parsed from V2Strings.cs", file=sys.stderr)
        return 2
    print(f"Parsed {len(keys)} V2_* keys from V2Strings.cs")

    total_added = 0
    for locale in LOCALES:
        resw = STRINGS_DIR / locale / "Resources.resw"
        if not resw.exists():
            print(f"  ! {locale}: Resources.resw missing", file=sys.stderr)
            return 2
        existing = read_resw_keys(resw)
        missing = {k: v for k, v in keys.items() if k not in existing}
        if missing:
            append_keys(resw, missing)
            print(f"  + {locale}: added {len(missing)} keys")
            total_added += len(missing)
        else:
            print(f"  = {locale}: already up to date")

    print(f"Done. Added {total_added} total entries.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
