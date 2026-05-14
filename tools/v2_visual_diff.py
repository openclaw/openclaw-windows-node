"""
v2_visual_diff.py — visual validation loop for the OnboardingV2 redesign.

For each design screen the designer sent us, this tool:

  1. Spawns OpenClaw.SetupPreview.exe in headless capture mode with the
     env vars that select the matching V2 route + scenario flags.
  2. Resizes both the designer reference PNG and the rendered capture
     to a common height and renders a clean side-by-side
     (designer | actual) image at ``out/v2-visual/<page>/diff.png``.

There is no automated PASS/FAIL gate. The agent (and humans) ``view``
``diff.png`` after every implementation change, articulate every visible
discrepancy in semantic terms (font weight, spacing, alignment, colour,
icon size, etc.), fix them, re-capture, and loop until a fresh look at
the side-by-side reveals no remaining differences. Pixel-level metrics
were tried and discarded — designer mock-canvas shadows + sub-pixel
font AA + the dialog-vs-canvas offset all created systematic noise that
drowned out the real signal coming from the agent's own eyes.

A separate snapshot-regression tool (added at cutover) handles
"detect unintentional changes vs the approved render"; that's a
different problem from "is the new render right?" and shouldn't be
conflated.

CLI:
    python tools/v2_visual_diff.py --page welcome
    python tools/v2_visual_diff.py --all
    python tools/v2_visual_diff.py --page allset --open
"""

from __future__ import annotations

import argparse
import os
import shutil
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path
from typing import Optional

from PIL import Image, ImageDraw, ImageFont

# ---------------------------------------------------------------------------
# Page registry
# ---------------------------------------------------------------------------


@dataclass(frozen=True)
class PageSpec:
    """Maps a logical page name to its V2 route + scenario flags."""

    name: str
    route: str  # OPENCLAW_PREVIEW_PAGE
    reference: str  # filename under tools/v2-design-refs/
    env: dict[str, str] = field(default_factory=dict)
    description: str = ""


PAGES: dict[str, PageSpec] = {
    "welcome": PageSpec(
        name="welcome",
        route="Welcome",
        reference="Dialog.png",
        description="Get started — lobster + Set up locally + Advanced setup",
    ),
    "progress-running": PageSpec(
        name="progress-running",
        route="LocalSetupProgress",
        reference="Dialog-1.png",
        env={
            "OPENCLAW_PREVIEW_FAIL_STAGE": "",
            "OPENCLAW_PREVIEW_PROGRESS_FROZEN_STAGE": "PreparingGateway",
        },
        description="Setting up locally — rows 1-4 done, row 5 spinning",
    ),
    "progress-failed": PageSpec(
        name="progress-failed",
        route="LocalSetupProgress",
        reference="Dialog-6.png",
        env={
            "OPENCLAW_PREVIEW_FAIL_STAGE": "StartingGateway",
            "OPENCLAW_PREVIEW_PROGRESS_FROZEN_STAGE": "StartingGateway",
        },
        description="Setting up locally — Starting gateway failed, inline error card",
    ),
    "gateway": PageSpec(
        name="gateway",
        route="GatewayWelcome",
        reference="Dialog-2.png",
        description="Configuring gateway — welcome card + Open in browser",
    ),
    "permissions": PageSpec(
        name="permissions",
        route="Permissions",
        reference="Dialog-5.png",
        env={"OPENCLAW_PREVIEW_PERMS_SCENARIO": "all-granted"},
        description="Grant permissions — five rows + Open Settings + Refresh status",
    ),
    "allset": PageSpec(
        name="allset",
        route="AllSet",
        reference="Dialog-4.png",
        env={"OPENCLAW_PREVIEW_NODE_MODE": "1"},
        description="All set! — party popper + Node Mode warning + Launch toggle",
    ),
    "allset-no-node": PageSpec(
        name="allset-no-node",
        route="AllSet",
        reference="Dialog-4.png",  # No designer no-node variant; we diff
        # against Dialog-4 to visually confirm the warning collapses cleanly.
        env={"OPENCLAW_PREVIEW_NODE_MODE": "0"},
        description="All set! without Node Mode — confirm warning collapses cleanly",
    ),
}


# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------


def repo_root() -> Path:
    here = Path(__file__).resolve()
    return here.parent.parent


def preview_exe() -> Path:
    """Locate the OpenClaw.SetupPreview.exe; rebuild if missing."""
    root = repo_root()
    candidates = list(
        root.glob(
            "src/OpenClaw.SetupPreview/bin/Debug/net*-windows*/win-*/OpenClaw.SetupPreview.exe"
        )
    )
    if not candidates:
        return Path()
    return max(candidates, key=lambda p: p.stat().st_mtime)


def reference_path(page: PageSpec) -> Path:
    return repo_root() / "tools" / "v2-design-refs" / page.reference


def output_dir(page: PageSpec) -> Path:
    return repo_root() / "out" / "v2-visual" / page.name


# ---------------------------------------------------------------------------
# Capture
# ---------------------------------------------------------------------------


_PREVIEW_EXE_CACHE: Optional[Path] = None


def ensure_preview_exe() -> Path:
    """Always run an incremental dotnet build (once per invocation) to pick up code changes, then locate the exe."""
    global _PREVIEW_EXE_CACHE
    if _PREVIEW_EXE_CACHE is not None:
        return _PREVIEW_EXE_CACHE
    proj = repo_root() / "src" / "OpenClaw.SetupPreview" / "OpenClaw.SetupPreview.csproj"
    print("[vv] building OpenClaw.SetupPreview (incremental)...", flush=True)
    result = subprocess.run(
        ["dotnet", "build", str(proj), "-r", "win-x64", "--nologo", "-v", "minimal"],
        cwd=repo_root(),
        capture_output=True,
        text=True,
    )
    if result.returncode != 0:
        sys.stderr.write(result.stdout)
        sys.stderr.write(result.stderr)
        raise RuntimeError("dotnet build failed; see output above")
    exe = preview_exe()
    if not exe or not exe.exists():
        raise RuntimeError("dotnet build succeeded but no exe was produced")
    _PREVIEW_EXE_CACHE = exe
    return exe


def capture(page: PageSpec) -> Path:
    """Run the preview exe in headless capture mode and return the PNG path."""
    exe = ensure_preview_exe()
    out_dir = output_dir(page)
    out_dir.mkdir(parents=True, exist_ok=True)
    actual_path = out_dir / "actual.png"
    if actual_path.exists():
        actual_path.unlink()

    env = os.environ.copy()
    env["OPENCLAW_PREVIEW_CAPTURE"] = "1"
    env["OPENCLAW_PREVIEW_PAGE"] = page.route
    env["OPENCLAW_PREVIEW_CAPTURE_PATH"] = str(actual_path)
    for k, v in page.env.items():
        env[k] = v

    proc = subprocess.run(
        [str(exe)],
        env=env,
        capture_output=True,
        text=True,
        timeout=60,
    )
    log_path = out_dir / "preview.log"
    log_path.write_text(
        f"=== exit {proc.returncode} ===\n"
        f"=== stdout ===\n{proc.stdout}\n=== stderr ===\n{proc.stderr}\n"
    )
    if proc.returncode != 0:
        raise RuntimeError(
            f"preview capture failed (exit={proc.returncode}); see {log_path}"
        )
    if not actual_path.exists():
        raise RuntimeError(
            f"preview reported success but no PNG at {actual_path}; see {log_path}"
        )
    return actual_path


# ---------------------------------------------------------------------------
# Side-by-side render
# ---------------------------------------------------------------------------


def fit_to_height(img: Image.Image, target_h: int) -> Image.Image:
    """Resize *img* preserving aspect ratio so its height equals *target_h*.
    Designer refs and captures share the same aspect (~0.813) so this is a
    pure resize."""
    w, h = img.size
    new_w = max(1, int(round(w * target_h / h)))
    return img.convert("RGB").resize((new_w, target_h), Image.LANCZOS)


def build_side_by_side(expected: Image.Image, actual: Image.Image) -> Image.Image:
    """Designer reference on the left, actual capture on the right, with a
    caption strip and a 24px gap between the two panes."""
    target_h = 1400  # readable on a typical screen; preserves enough detail
    e = fit_to_height(expected, target_h)
    a = fit_to_height(actual, target_h)

    gap = 24
    cap = 64
    total_w = e.width + a.width + gap
    out = Image.new("RGB", (total_w, target_h + cap), (24, 24, 24))
    out.paste(e, (0, cap))
    out.paste(a, (e.width + gap, cap))

    draw = ImageDraw.Draw(out)
    try:
        font = ImageFont.truetype("segoeui.ttf", 24)
    except OSError:
        font = ImageFont.load_default()
    draw.text((16, 20), "expected (designer)", fill=(230, 230, 230), font=font)
    draw.text((e.width + gap + 16, 20), "actual (preview)", fill=(230, 230, 230), font=font)
    return out


def diff_page(page: PageSpec) -> None:
    """Capture and render side-by-side; write artifacts."""
    print(f"[vv] {page.name}: capturing...", flush=True)
    actual_path = capture(page)
    out_dir = output_dir(page)

    ref = reference_path(page)
    if not ref.exists():
        raise FileNotFoundError(f"reference image missing: {ref}")
    shutil.copy(ref, out_dir / "expected.png")

    expected_img = Image.open(ref)
    actual_img = Image.open(actual_path)
    side_by_side = build_side_by_side(expected_img, actual_img)
    side_by_side.save(out_dir / "diff.png", optimize=True)
    print(
        f"[vv] {page.name}: rendered {out_dir / 'diff.png'} — "
        f"view it and judge visually",
        flush=True,
    )


# ---------------------------------------------------------------------------
# CLI
# ---------------------------------------------------------------------------


def main(argv: Optional[list[str]] = None) -> int:
    p = argparse.ArgumentParser(description=__doc__)
    p.add_argument("--page", choices=sorted(PAGES.keys()))
    p.add_argument("--all", action="store_true")
    p.add_argument("--open", action="store_true", help="Open diff.png after generating")
    args = p.parse_args(argv)

    if not args.all and not args.page:
        p.error("provide --page <name> or --all")

    pages = list(PAGES.values()) if args.all else [PAGES[args.page]]
    failed = 0
    for page in pages:
        try:
            diff_page(page)
            if args.open:
                diff_path = output_dir(page) / "diff.png"
                if sys.platform.startswith("win"):
                    os.startfile(str(diff_path))  # noqa: S606
                else:
                    subprocess.run(["xdg-open", str(diff_path)], check=False)
        except Exception as ex:  # noqa: BLE001
            print(f"[vv] {page.name}: ERROR {ex}", file=sys.stderr)
            failed += 1
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    sys.exit(main())
