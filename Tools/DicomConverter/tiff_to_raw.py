#!/usr/bin/env python3
"""Convert an XRaySystem Gray16 Snap TIFF into DiskImageSensorService RAW assets.

Usage:
  python3 tiff_to_raw.py <snap.tiff> --out ../../XRaySystem.Infrastructure/SampleData

The full-resolution RAW preserves the TIFF's Gray16 samples when it is already
2304x2930. A separate 512x512 live-preview RAW is generated for Disk mode.
"""

import argparse
import json
from pathlib import Path

import numpy as np

try:
    from PIL import Image
    from skimage.transform import resize as sk_resize
except ImportError as error:
    raise SystemExit(
        f"Missing dependency: {error}\n"
        "Install with: pip3 install pillow scikit-image numpy"
    )


SNAP_WIDTH = 2304
SNAP_HEIGHT = 2930
LIVE_WIDTH = 512
LIVE_HEIGHT = 512
NORMALIZATION_POLICY = "preserved-gray16-tiff"


def resize_gray16(pixels: np.ndarray, width: int, height: int) -> np.ndarray:
    if pixels.shape == (height, width):
        return pixels

    resized = np.asarray(sk_resize(
        pixels.astype(np.float64) / 65535.0,
        (height, width),
        anti_aliasing=True,
        order=3,
        mode="reflect",
    ), dtype=np.float64)
    return (np.clip(resized, 0.0, 1.0) * 65535.0).round().astype(np.uint16)


def load_gray16(path: Path) -> np.ndarray:
    with Image.open(path) as image:
        pixels = np.asarray(image)

    if pixels.ndim != 2:
        raise ValueError("The source TIFF must be a single-channel grayscale image.")
    if pixels.dtype == np.uint16:
        return pixels
    if np.issubdtype(pixels.dtype, np.integer):
        maximum = np.iinfo(pixels.dtype).max
        return (pixels.astype(np.float64) * 65535.0 / maximum).round().astype(np.uint16)
    raise ValueError(f"Unsupported TIFF pixel type: {pixels.dtype}")


def save_raw(pixels: np.ndarray, path: Path) -> None:
    pixels.astype("<u2", copy=False).tofile(path)


def update_manifest(out_dir: Path, entry: dict) -> None:
    manifest_path = out_dir / "manifest.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8")) if manifest_path.exists() else {}
    manifest.setdefault("version", 2)
    manifest.setdefault("raw_representation", "uint16-little-endian")
    manifest.setdefault("snap_width", SNAP_WIDTH)
    manifest.setdefault("snap_height", SNAP_HEIGHT)
    manifest.setdefault("live_width", LIVE_WIDTH)
    manifest.setdefault("live_height", LIVE_HEIGHT)
    images = [item for item in manifest.get("images", []) if item.get("entry_id") != entry["entry_id"]]
    images.insert(0, entry)
    manifest["images"] = images
    manifest_path.write_text(json.dumps(manifest, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")


def main() -> None:
    parser = argparse.ArgumentParser(description="XRaySystem Snap TIFF to Disk RAW converter")
    parser.add_argument("input", type=Path, help="Gray16 TIFF produced by XRaySystem Snap")
    parser.add_argument("--out", "-o", type=Path, default=Path("../../XRaySystem.Infrastructure/SampleData"))
    parser.add_argument("--id", default=None, help="Manifest entry ID (defaults to TIFF file stem)")
    args = parser.parse_args()

    source = args.input.resolve()
    if not source.is_file():
        raise SystemExit(f"Input TIFF not found: {source}")

    out_dir = args.out.resolve()
    out_dir.mkdir(parents=True, exist_ok=True)
    entry_id = args.id or source.stem
    pixels = load_gray16(source)
    snap = resize_gray16(pixels, SNAP_WIDTH, SNAP_HEIGHT)
    live = resize_gray16(pixels, LIVE_WIDTH, LIVE_HEIGHT)

    snap_name = f"{entry_id}_snap_{SNAP_WIDTH}x{SNAP_HEIGHT}.raw"
    live_name = f"{entry_id}_live_{LIVE_WIDTH}x{LIVE_HEIGHT}.raw"
    save_raw(snap, out_dir / snap_name)
    save_raw(live, out_dir / live_name)

    update_manifest(out_dir, {
        "stem": entry_id,
        "entry_id": entry_id,
        "snap_file": snap_name,
        "live_file": live_name,
        "snap_width": SNAP_WIDTH,
        "snap_height": SNAP_HEIGHT,
        "live_width": LIVE_WIDTH,
        "live_height": LIVE_HEIGHT,
        "modality": "DX",
        "source_file": source.name,
        "normalization_policy": NORMALIZATION_POLICY,
        "normalization_policy_version": 1,
    })

    print(f"Added Disk sample '{entry_id}'")
    print(f"  Snap: {out_dir / snap_name}")
    print(f"  Live: {out_dir / live_name}")
    print(f"  Manifest: {out_dir / 'manifest.json'}")


if __name__ == "__main__":
    main()
