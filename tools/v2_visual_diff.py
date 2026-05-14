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


def ensure_preview_exe() -> Path:
    exe = preview_exe()
    if exe and exe.exists():
        return exe
    print("[vv] OpenClaw.SetupPreview.exe not found; building...", flush=True)
    proj = repo_root() / "src" / "OpenClaw.SetupPreview" / "OpenClaw.SetupPreview.csproj"
    subprocess.run(
        ["dotnet", "build", str(proj), "-r", "win-x64", "--nologo"],
        check=True,
        cwd=repo_root(),
    )
    exe = preview_exe()
    if not exe or not exe.exists():
        raise RuntimeError("dotnet build succeeded but no exe was produced")
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
        reference="Dialog-4.png",  # Designer didn't ship a no-node variant; we
        # diff against Dialog-4 so we visibly see the "warning collapsed" diff
        # and judge whether the layout still looks balanced. Treat ignore_warning_band=True.
        env={"OPENCLAW_PREVIEW_NODE_MODE": "0"},
        description="All set! without Node Mode — confirm warning collapses cleanly",
    ),
}


# ---------------------------------------------------------------------------
# Paths
# ---------------------------------------------------------------------------


def repo_root() -> Path:
    here = Path(__file__).resolve()
    # tools/ is a top-level dir; repo root is one level up.
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
    # Newest wins (handles parallel x64/arm64 outputs).
    return max(candidates, key=lambda p: p.stat().st_mtime)


def reference_path(page: PageSpec) -> Path:
    return repo_root() / "tools" / "v2-design-refs" / page.reference


def output_dir(page: PageSpec) -> Path:
    return repo_root() / "out" / "v2-visual" / page.name


# ---------------------------------------------------------------------------
# Capture
# ---------------------------------------------------------------------------


def ensure_preview_exe() -> Path:
    exe = preview_exe()
    if exe and exe.exists():
        return exe
    print("[vv] OpenClaw.SetupPreview.exe not found; building...", flush=True)
    proj = repo_root() / "src" / "OpenClaw.SetupPreview" / "OpenClaw.SetupPreview.csproj"
    subprocess.run(
        ["dotnet", "build", str(proj), "-r", "win-x64", "--nologo"],
        check=True,
        cwd=repo_root(),
    )
    exe = preview_exe()
    if not exe or not exe.exists():
        raise RuntimeError("dotnet build succeeded but no exe was produced")
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
# Diff
# ---------------------------------------------------------------------------


def to_canvas(img: Image.Image, target: tuple[int, int]) -> Image.Image:
    """Letterbox-fit *img* into *target* preserving aspect ratio. The
    output is RGB (no alpha) — any transparency in the source is composited
    over the target background colour ``(28, 28, 28)`` (near-black, matches
    the designer canvas) so a captured PNG with transparent regions does
    not read as "pure black" in the side-by-side. Both designer refs and
    captures live at the same aspect (~0.745) so this is normally a pure
    resize."""
    img = img.convert("RGBA")
    src_w, src_h = img.size
    tgt_w, tgt_h = target
    scale = min(tgt_w / src_w, tgt_h / src_h)
    new_w, new_h = int(round(src_w * scale)), int(round(src_h * scale))
    resized = img.resize((new_w, new_h), Image.LANCZOS)
    canvas = Image.new("RGBA", target, (28, 28, 28, 255))
    canvas.paste(resized, ((tgt_w - new_w) // 2, (tgt_h - new_h) // 2), resized)
    return canvas.convert("RGBA")


def per_pixel_overlay(expected: Image.Image, actual: Image.Image) -> Image.Image:
    """Build a perceptually-weighted red overlay highlighting per-pixel
    differences. Returns an image the same size as the inputs."""
    e = np.asarray(expected.convert("RGB"), dtype=np.int16)
    a = np.asarray(actual.convert("RGB"), dtype=np.int16)
    delta = np.abs(e - a)
    # Luminance-weighted magnitude (Rec. 709) ranged 0..255.
    weight = np.array([0.2126, 0.7152, 0.0722], dtype=np.float32)
    mag = (delta.astype(np.float32) @ weight).clip(0, 255).astype(np.uint8)
    base = expected.convert("RGB").convert("L").convert("RGB")
    base_arr = np.asarray(base, dtype=np.uint8).copy()
    # Boost red channel where diff exists; keep base luminance under it.
    base_arr[..., 0] = np.maximum(base_arr[..., 0], mag)
    base_arr[..., 1] = (base_arr[..., 1].astype(np.uint16) * (255 - mag) // 255).astype(np.uint8)
    base_arr[..., 2] = (base_arr[..., 2].astype(np.uint16) * (255 - mag) // 255).astype(np.uint8)
    return Image.fromarray(base_arr, mode="RGB")


def mismatched_bboxes(
    expected: Image.Image,
    actual: Image.Image,
    threshold: int = 24,
    min_area: int = 32,
    max_boxes: int = 16,
    max_diff_pct: float = 25.0,
) -> list[dict]:
    """Find the largest bounding boxes of mismatched regions.

    Two pixels are considered different if any RGB channel differs by
    more than *threshold*. Connected mismatched pixels are grouped via a
    binary erosion + label pass and the largest *max_boxes* are returned.

    When the diff covers more than *max_diff_pct* of the canvas (early
    iterations where the page hasn't been built yet) the connected-
    component pass would explode into thousands of tiny clusters. In
    that case we short-circuit with a single "almost everything"
    pseudo-bbox so the report still completes in seconds.
    """
    e = np.asarray(expected.convert("RGB"), dtype=np.int16)
    a = np.asarray(actual.convert("RGB"), dtype=np.int16)
    diff = np.any(np.abs(e - a) > threshold, axis=-1)

    diff_pct = float(diff.mean() * 100.0)
    if diff_pct > max_diff_pct:
        ys, xs = np.where(diff)
        return [
            {
                "x": int(xs.min()),
                "y": int(ys.min()),
                "w": int(xs.max() - xs.min() + 1),
                "h": int(ys.max() - ys.min() + 1),
                "area": int(ys.size),
                "note": (
                    f"diff covers {diff_pct:.1f}% of canvas (> {max_diff_pct:.0f}%); "
                    "connected-component analysis skipped — page likely not yet "
                    "implemented or shell still placeholder"
                ),
            }
        ]

    try:
        from scipy import ndimage  # type: ignore
        labeled, count = ndimage.label(diff)
        if count == 0:
            return []
        boxes = []
        for i in range(1, count + 1):
            ys, xs = np.where(labeled == i)
            if ys.size < min_area:
                continue
            boxes.append(
                {
                    "x": int(xs.min()),
                    "y": int(ys.min()),
                    "w": int(xs.max() - xs.min() + 1),
                    "h": int(ys.max() - ys.min() + 1),
                    "area": int(ys.size),
                }
            )
        boxes.sort(key=lambda b: b["area"], reverse=True)
        return boxes[:max_boxes]
    except ImportError:
        # Fallback: a single bounding box around all diff pixels.
        ys, xs = np.where(diff)
        if ys.size < min_area:
            return []
        return [
            {
                "x": int(xs.min()),
                "y": int(ys.min()),
                "w": int(xs.max() - xs.min() + 1),
                "h": int(ys.max() - ys.min() + 1),
                "area": int(ys.size),
            }
        ]


def build_three_up(
    expected: Image.Image, actual: Image.Image, overlay: Image.Image
) -> Image.Image:
    """Side-by-side: expected | actual | overlay with caption strip."""
    w, h = expected.size
    gap = 24
    cap = 56
    out = Image.new("RGB", (w * 3 + gap * 2, h + cap), (24, 24, 24))
    out.paste(expected.convert("RGB"), (0, cap))
    out.paste(actual.convert("RGB"), (w + gap, cap))
    out.paste(overlay.convert("RGB"), (w * 2 + gap * 2, cap))
    draw = ImageDraw.Draw(out)
    try:
        font = ImageFont.truetype("segoeui.ttf", 22)
    except OSError:
        font = ImageFont.load_default()
    for i, label in enumerate(("expected (designer)", "actual (preview)", "overlay (red = diff)")):
        x = i * (w + gap) + 16
        draw.text((x, 16), label, fill=(230, 230, 230), font=font)
    return out


def diff_page(page: PageSpec) -> dict:
    """Capture and diff one page; write artifacts; return the report dict."""
    print(f"[vv] {page.name}: capturing...", flush=True)
    actual_path = capture(page)
    out_dir = output_dir(page)

    ref = reference_path(page)
    if not ref.exists():
        raise FileNotFoundError(f"reference image missing: {ref}")
    shutil.copy(ref, out_dir / "expected.png")

    expected_img = Image.open(ref)
    actual_img = Image.open(actual_path)
    canvas_size = expected_img.size
    expected_n = to_canvas(expected_img, canvas_size)
    actual_n = to_canvas(actual_img, canvas_size)

    # SSIM on grayscale.
    e_gray = np.asarray(expected_n.convert("L"), dtype=np.float32) / 255.0
    a_gray = np.asarray(actual_n.convert("L"), dtype=np.float32) / 255.0
    ssim_score = float(ssim(e_gray, a_gray, data_range=1.0))

    # Mean delta.
    e_rgb = np.asarray(expected_n.convert("RGB"), dtype=np.float32)
    a_rgb = np.asarray(actual_n.convert("RGB"), dtype=np.float32)
    diff = np.abs(e_rgb - a_rgb)
    mean_delta = float(diff.mean())
    pct_over_24 = float((np.any(diff > 24, axis=-1)).mean() * 100.0)

    overlay = per_pixel_overlay(expected_n, actual_n)
    bboxes = mismatched_bboxes(expected_n, actual_n)
    three_up = build_three_up(expected_n, actual_n, overlay)
    three_up.save(out_dir / "diff.png", optimize=True)

    report = {
        "page": page.name,
        "route": page.route,
        "reference": str(ref.relative_to(repo_root())),
        "ssim": round(ssim_score, 4),
        "mean_delta": round(mean_delta, 3),
        "pct_pixels_diff_over_24": round(pct_over_24, 3),
        "mismatched_bboxes": bboxes,
        "passed": ssim_score >= 0.95
        and not any(b["w"] > 24 and b["h"] > 24 for b in bboxes),
        "env": page.env,
        "description": page.description,
    }
    (out_dir / "report.json").write_text(json.dumps(report, indent=2))
    status = "PASS" if report["passed"] else "FAIL"
    print(
        f"[vv] {page.name}: {status}  ssim={ssim_score:.3f}  "
        f"mean_dE={mean_delta:.1f}  bboxes={len(bboxes)}",
        flush=True,
    )
    return report


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
            report = diff_page(page)
            if not report["passed"]:
                failed += 1
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
