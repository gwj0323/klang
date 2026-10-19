from pathlib import Path
from PIL import Image

assets_dir = Path("qt6_player/assets")
assets_dir.mkdir(parents=True, exist_ok=True)

# 1. Overwrite klang_icon.png with pure master
pure_img = Image.open(assets_dir / "klang_icon_pure.png")
pure_img.save(assets_dir / "klang_icon.png", format="PNG")
print("Saved klang_icon.png (512x512)")

# 2. Generate multi-resolution .ico for Windows
ico_sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
pure_img.save(assets_dir / "klang_icon.ico", format="ICO", sizes=ico_sizes)
print("Saved klang_icon.ico (16~256px)")

# 3. Generate SVG file with exact matching vectors
svg_content = """<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" width="512" height="512">
  <defs>
    <linearGradient id="fjordSlate" x1="0%" y1="0%" x2="100%" y2="100%">
      <stop offset="0%" stop-color="#242830"/>
      <stop offset="48%" stop-color="#191C22"/>
      <stop offset="100%" stop-color="#101216"/>
    </linearGradient>
    <radialGradient id="sageAura" cx="48%" cy="50%" r="50%">
      <stop offset="0%" stop-color="#A5C3B2" stop-opacity="0.22"/>
      <stop offset="50%" stop-color="#A5C3B2" stop-opacity="0.05"/>
      <stop offset="100%" stop-color="#000000" stop-opacity="0"/>
    </radialGradient>
    <filter id="dropShadow" x="-10%" y="-10%" width="120%" height="130%">
      <feDropShadow dx="0" dy="8" stdDeviation="12" flood-color="#000000" flood-opacity="0.45"/>
    </filter>
  </defs>

  <!-- Squircle Base -->
  <rect x="30" y="30" width="452" height="452" rx="112" ry="112" fill="url(#fjordSlate)" filter="url(#dropShadow)"/>
  <rect x="30" y="30" width="452" height="452" rx="112" ry="112" fill="url(#sageAura)"/>
  <rect x="30" y="30" width="452" height="452" rx="112" ry="112" fill="none" stroke="#FFFFFF" stroke-opacity="0.11" stroke-width="1.5"/>
  <rect x="31.5" y="31.5" width="449" height="449" rx="111" ry="111" fill="none" stroke="#000000" stroke-opacity="0.25" stroke-width="1"/>

  <!-- Vertical Spine -->
  <rect x="106" y="136" width="38" height="240" rx="19" ry="19" fill="#F8FAFC"/>

  <!-- Chevron K-Arms -->
  <path d="M 280 142 L 166 256 L 280 370" fill="none" stroke="#F8FAFC" stroke-width="38" stroke-linecap="round" stroke-linejoin="round"/>

  <!-- Acoustic Klang Resonance Waves (Danish Sage) -->
  <!-- Wave 1 (Mid Resonance) -->
  <path d="M 315 170 A 170 170 0 0 1 315 342" fill="none" stroke="#A5C6B4" stroke-width="17" stroke-linecap="round" stroke-opacity="0.92"/>
  <!-- Wave 2 (Outer Resonance) -->
  <path d="M 361 173 A 212 212 0 0 1 361 339" fill="none" stroke="#A5C6B4" stroke-width="13" stroke-linecap="round" stroke-opacity="0.55"/>
  <!-- Wave 3 (Ambient Spatial Ripple) -->
  <path d="M 408 186 A 252 252 0 0 1 408 326" fill="none" stroke="#A5C6B4" stroke-width="9" stroke-linecap="round" stroke-opacity="0.24"/>
</svg>"""

with open(assets_dir / "klang_icon.svg", "w", encoding="utf-8") as f:
    f.write(svg_content)
print("Saved klang_icon.svg")
