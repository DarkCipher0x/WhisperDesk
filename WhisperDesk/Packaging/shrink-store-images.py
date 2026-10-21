"""Makes smaller upload copies of the Store listing images in Packaging/StoreListing/upload.

Partner Center only accepts PNG, so the images keep their size in pixels and are reduced to a 256-colour palette
(8-bit PNG, alpha kept for the transparent tile icons), which typically shrinks them by well over half. Originals
in StoreListing/art and StoreListing/screenshots are left untouched.

    py Packaging/shrink-store-images.py
"""
from pathlib import Path
from PIL import Image

root = Path(__file__).resolve().parent / "StoreListing"
out = root / "upload"
out.mkdir(exist_ok=True)
for sub in ("screenshots", "art"):
    for src in sorted((root / sub).glob("*.png")):
        img = Image.open(src)
        has_alpha = img.mode in ("RGBA", "LA") and img.getextrema()[-1][0] < 255
        if has_alpha:
            small = img.convert("RGBA").quantize(colors=256, method=Image.Quantize.FASTOCTREE, dither=Image.Dither.NONE)
        else:
            small = img.convert("RGB").quantize(colors=256, method=Image.Quantize.MEDIANCUT, dither=Image.Dither.FLOYDSTEINBERG)
        dst = out / f"{sub}-{src.name}"
        small.save(dst, optimize=True)
        print(f"{dst.name:48} {src.stat().st_size // 1024:5} KB -> {dst.stat().st_size // 1024:4} KB  {img.size[0]}x{img.size[1]}")
