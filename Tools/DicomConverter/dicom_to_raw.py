#!/usr/bin/env python3
"""
XRaySystem - DICOM to RAW Converter
=====================================
Gerçek DICOM X-ray dosyalarını XRaySystem simülatörünün anlayacağı
 .raw (ushort little-endian) formatına çevirir. RAW, görüntüleme için
 normalize edilmiş uint16'tır; fiziksel kalibrasyon değerleri RAW'a gömülmez.

Kullanım:
  python3 dicom_to_raw.py <dicom_dosyası_veya_klasörü> [--out <çıktı_klasörü>]

Örnekler:
  python3 dicom_to_raw.py chest.dcm
  python3 dicom_to_raw.py /downloads/dataset/ --out ../../XRaySystem.Infrastructure/SampleData/
  python3 dicom_to_raw.py chest.dcm --width 2304 --height 2930 --live-width 512 --live-height 512

Bağımlılıklar:
  pip3 install pydicom pylibjpeg pylibjpeg-openjpeg scikit-image numpy
"""

import sys
import os
import json
import argparse
import glob
from pathlib import Path

try:
    import pydicom
    import numpy as np
    from skimage.transform import resize as sk_resize
except ImportError as e:
    print(f"[HATA] Eksik kütüphane: {e}")
    print("  pip3 install pydicom scikit-image numpy pylibjpeg pylibjpeg-openjpeg")
    sys.exit(1)


# ── Sabitler ──────────────────────────────────────────────────────────────────
DEFAULT_SNAP_WIDTH   = 2304
DEFAULT_SNAP_HEIGHT  = 2930
DEFAULT_LIVE_WIDTH   = 512
DEFAULT_LIVE_HEIGHT  = 512

# ── Yardımcı fonksiyonlar ─────────────────────────────────────────────────────

RAW_POLICY = "display-normalized-uint16"
RAW_POLICY_VERSION = 1


def _safe_float(value, default: float) -> float:
    try:
        parsed = float(value)
        return parsed if np.isfinite(parsed) else default
    except (TypeError, ValueError):
        return default


def normalize_to_uint16(arr: np.ndarray, slope: float = 1.0, intercept: float = 0.0) -> np.ndarray:
    """
    Herhangi bir sayısal array'i uint16 (0-65535) aralığına dönüştürür.
    DICOM rescale uygulanır, ardından display-normalized uint16 min-max
    dönüşümü yapılır. Bu bilinçli olarak fiziksel kalibrasyonlu RAW değildir;
    slope/intercept manifestte saklanır.
    """
    arr_float = np.asarray(arr, dtype=np.float64) * _safe_float(slope, 1.0) + _safe_float(intercept, 0.0)
    if not np.isfinite(arr_float).all():
        arr_float = np.nan_to_num(arr_float, nan=0.0, posinf=0.0, neginf=0.0)
    mn, mx = arr_float.min(), arr_float.max()
    if mx == mn:
        return np.zeros(arr.shape, dtype=np.uint16)
    return ((arr_float - mn) / (mx - mn) * 65535).astype(np.uint16)


def resample_uint16(arr: np.ndarray, target_h: int, target_w: int) -> np.ndarray:
    """
    uint16 array'i hedef boyuta yeniden örnekler.
    Piksel değer aralığını korur (0-65535).
    """
    if arr.shape == (target_h, target_w):
        return arr
    
    print(f"    Resample: {arr.shape[1]}x{arr.shape[0]} → {target_w}x{target_h}")
    
    # skimage float64 üzerinden çalışır; 0-1 normalize et, resize yap, geri ölçekle
    arr_float = arr.astype(np.float64) / 65535.0
    resized = sk_resize(
        arr_float,
        (target_h, target_w),
        anti_aliasing=True,
        order=3,        # bicubic — medikal görüntüler için ideal
        mode='reflect'
    )
    # Clamp + uint16'ya geri dön
    resized = np.clip(resized, 0.0, 1.0)
    return (resized * 65535).astype(np.uint16)


def read_dicom_pixels(dcm_path: str) -> tuple[np.ndarray, dict]:
    """
    DICOM dosyasından piksel array ve metadata okur.
    Döndürür: (uint16 2D array [H, W]), metadata dict
    """
    ds = pydicom.dcmread(dcm_path, force=True)
    
    # Piksel verisi var mı?
    if not hasattr(ds, 'PixelData'):
        raise ValueError("Bu DICOM dosyasında piksel verisi yok (SR, yapısal rapor olabilir).")
    
    try:
        raw = ds.pixel_array  # numpy array — pydicom otomatik decompress eder
    except Exception as e:
        raise ValueError(f"Piksel okunamadı: {e}") from e
    
    # Çok-frame DICOM (CT slice stack vb.) → sadece ortadaki frame'i al
    if raw.ndim == 3:
        mid = raw.shape[0] // 2
        print(f"    Çok-frame DICOM ({raw.shape[0]} frame) — orta frame [{mid}] alınıyor")
        raw = raw[mid]
    elif raw.ndim != 2:
        raise ValueError(f"Desteklenmeyen boyut: {raw.ndim}D")
    
    slope = _safe_float(getattr(ds, "RescaleSlope", 1.0), 1.0)
    intercept = _safe_float(getattr(ds, "RescaleIntercept", 0.0), 0.0)
    pixels = normalize_to_uint16(raw, slope, intercept)
    
    # Metadata topla
    def safe_get(tag, default=""):
        val = getattr(ds, tag, default)
        return str(val) if val else default
    
    meta = {
        "Modality":         safe_get("Modality"),
        "Rows":             int(getattr(ds, "Rows", pixels.shape[0])),
        "Columns":          int(getattr(ds, "Columns", pixels.shape[1])),
        "BitsAllocated":    int(getattr(ds, "BitsAllocated", 16)),
        "PixelSpacing":     str(getattr(ds, "PixelSpacing", "")),
        "KVP":              safe_get("KVP"),
        "ExposureTime":     safe_get("ExposureTime"),
        "XRayTubeCurrent":  safe_get("XRayTubeCurrent"),
        "PatientID":        safe_get("PatientID", "ANON"),
        "SourceFile":       os.path.basename(dcm_path),
        "RescaleSlope":     slope,
        "RescaleIntercept": intercept,
        "NormalizationPolicy": RAW_POLICY,
        "NormalizationPolicyVersion": RAW_POLICY_VERSION,
    }
    
    return pixels, meta


def save_raw(pixels: np.ndarray, out_path: str):
    """
    uint16 array'i little-endian binary .raw dosyası olarak kaydeder.
    C# tarafı şu şekilde okur: File.ReadAllBytes → MemoryMarshal.Cast<byte, ushort>
    """
    pixels.astype('<u2').tofile(out_path)  # '<u2' = little-endian uint16


def process_file(dcm_path: str, out_dir: str, snap_w: int, snap_h: int, live_w: int, live_h: int) -> dict | None:
    """
    Tek bir DICOM dosyasını işler: Snap + Live versiyonlarını üretir.
    Döndürür: Manifest entry dict veya None (hata durumunda).
    """
    stem = Path(dcm_path).stem
    print(f"\n[>] İşleniyor: {os.path.basename(dcm_path)}")
    
    try:
        pixels, meta = read_dicom_pixels(dcm_path)
        print(f"    Modality: {meta['Modality']} | Orijinal: {meta['Columns']}x{meta['Rows']} | Bit: {meta['BitsAllocated']}")
        print(f"    Piksel aralığı: [{pixels.min()}, {pixels.max()}]")
    except Exception as e:
        print(f"  [ATLA] {e}")
        return None
    
    # ── Snap (tam çözünürlük) ──────────────────────────────────────────────────
    snap_pixels = resample_uint16(pixels, snap_h, snap_w)
    snap_filename = f"{stem}_snap_{snap_w}x{snap_h}.raw"
    snap_path = os.path.join(out_dir, snap_filename)
    save_raw(snap_pixels, snap_path)
    snap_size = os.path.getsize(snap_path)
    print(f"    ✅ Snap: {snap_filename} ({snap_size / 1024 / 1024:.1f} MB)")
    
    # ── Live preview (küçük çözünürlük) ───────────────────────────────────────
    live_pixels = resample_uint16(pixels, live_h, live_w)
    live_filename = f"{stem}_live_{live_w}x{live_h}.raw"
    live_path = os.path.join(out_dir, live_filename)
    save_raw(live_pixels, live_path)
    print(f"    ✅ Live: {live_filename} ({os.path.getsize(live_path) / 1024:.0f} KB)")
    
    return {
        "stem":          stem,
        "snap_file":     snap_filename,
        "live_file":     live_filename,
        "snap_width":    snap_w,
        "snap_height":   snap_h,
        "live_width":    live_w,
        "live_height":   live_h,
        "modality":      meta["Modality"],
        "kvp":           meta["KVP"],
        "exposure_time": meta["ExposureTime"],
        "tube_current":  meta["XRayTubeCurrent"],
        "source_file":   meta["SourceFile"],
        "rescale_slope": meta["RescaleSlope"],
        "rescale_intercept": meta["RescaleIntercept"],
        "normalization_policy": meta["NormalizationPolicy"],
        "normalization_policy_version": meta["NormalizationPolicyVersion"],
    }


def main():
    parser = argparse.ArgumentParser(
        description="DICOM → XRaySystem .raw dönüştürücü",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog=__doc__
    )
    parser.add_argument("input",         help="DICOM dosyası veya klasörü")
    parser.add_argument("--out", "-o",   default="../../XRaySystem.Infrastructure/SampleData",
                        help="Çıktı klasörü (varsayılan: ../../XRaySystem.Infrastructure/SampleData)")
    parser.add_argument("--width",       type=int, default=DEFAULT_SNAP_WIDTH,   help=f"Snap genişliği (varsayılan: {DEFAULT_SNAP_WIDTH})")
    parser.add_argument("--height",      type=int, default=DEFAULT_SNAP_HEIGHT,  help=f"Snap yüksekliği (varsayılan: {DEFAULT_SNAP_HEIGHT})")
    parser.add_argument("--live-width",  type=int, default=DEFAULT_LIVE_WIDTH,   help=f"Live genişliği (varsayılan: {DEFAULT_LIVE_WIDTH})")
    parser.add_argument("--live-height", type=int, default=DEFAULT_LIVE_HEIGHT,  help=f"Live yüksekliği (varsayılan: {DEFAULT_LIVE_HEIGHT})")
    
    args = parser.parse_args()
    
    # Çıktı klasörünü oluştur
    out_dir = os.path.abspath(args.out)
    os.makedirs(out_dir, exist_ok=True)
    print(f"[XRaySystem DICOM→RAW Dönüştürücü]")
    print(f"Çıktı klasörü: {out_dir}")
    print(f"Snap: {args.width}×{args.height} | Live: {args.live_width}×{args.live_height}")
    
    # DICOM dosyalarını topla
    input_path = os.path.abspath(args.input)
    if os.path.isdir(input_path):
        dcm_files = (
            glob.glob(os.path.join(input_path, "**", "*.dcm"), recursive=True) +
            glob.glob(os.path.join(input_path, "**", "*.DCM"), recursive=True) +
            # Uzantısız DICOM dosyaları da var (PCIR vb.)
            [f for f in glob.glob(os.path.join(input_path, "**", "*"), recursive=True)
             if os.path.isfile(f) and not os.path.splitext(f)[1]]
        )
        dcm_files = list(set(dcm_files))
        print(f"Bulunan dosya: {len(dcm_files)}")
    elif os.path.isfile(input_path):
        dcm_files = [input_path]
    else:
        print(f"[HATA] Dosya/klasör bulunamadı: {input_path}")
        sys.exit(1)
    
    if not dcm_files:
        print("[HATA] Hiç DICOM dosyası bulunamadı.")
        sys.exit(1)
    
    # İşle
    manifest_entries = []
    for dcm_path in sorted(dcm_files):
        entry = process_file(dcm_path, out_dir, args.width, args.height, args.live_width, args.live_height)
        if entry:
            manifest_entries.append(entry)
    
    if not manifest_entries:
        print("\n[HATA] Hiçbir dosya başarıyla dönüştürülemedi.")
        sys.exit(1)
    
    # Manifest JSON — C# tarafı bunu okuyacak
    manifest_path = os.path.join(out_dir, "manifest.json")
    manifest = {
        "version": 2,
        "raw_representation": "uint16-little-endian",
        "normalization_policy": RAW_POLICY,
        "normalization_policy_version": RAW_POLICY_VERSION,
        "physical_calibration_in_raw": False,
        "snap_width":    args.width,
        "snap_height":   args.height,
        "live_width":    args.live_width,
        "live_height":   args.live_height,
        "images": manifest_entries
    }
    # Self-validation catches accidental metadata loss before the manifest is
    # consumed by the backwards-compatible C# reader.
    serialized = json.dumps(manifest, ensure_ascii=False)
    round_tripped = json.loads(serialized)
    required = ("rescale_slope", "rescale_intercept", "normalization_policy")
    if round_tripped.get("version") != 2 or any(k not in round_tripped["images"][0] for k in required):
        raise ValueError("Manifest metadata self-validation failed")
    with open(manifest_path, "w", encoding="utf-8") as f:
        json.dump(manifest, f, indent=2, ensure_ascii=False)
    
    print(f"\n{'='*60}")
    print(f"✅ {len(manifest_entries)} görüntü başarıyla dönüştürüldü.")
    print(f"📋 Manifest: {manifest_path}")
    print(f"\nSonraki adım:")
    print(f"  C# projesinde DiskImageSensorService'i DI'a kaydet:")
    print(f"  services.AddSingleton<ISensorService, DiskImageSensorService>();")
    print(f"  // veya appsettings.json'da: \"SensorMode\": \"Disk\"")


if __name__ == "__main__":
    main()
