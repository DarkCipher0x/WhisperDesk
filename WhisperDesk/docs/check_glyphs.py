"""Lists non-ASCII characters used in make_guide.py and whether Segoe UI can render them."""
import os
from reportlab.pdfbase.ttfonts import TTFontFile

src = open(os.path.join(os.path.dirname(__file__), "make_guide.py"), encoding="utf-8").read()
font = TTFontFile(r"C:\Windows\Fonts\segoeui.ttf")
for ch in sorted(set(c for c in src if ord(c) > 127)):
    ok = ord(ch) in font.charToGlyph
    print(f"U+{ord(ch):04X} {ch!r:8} {'ok' if ok else 'MISSING in Segoe UI'}")
