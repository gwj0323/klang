from dataclasses import dataclass
from typing import Dict, List, Tuple
from PyQt6.QtGui import QColor, QFont, QFontDatabase


@dataclass(frozen=True)
class NordicThemeColors:
    name: str
    mist_white: QColor
    warm_birch: QColor
    oat_surface: QColor
    card_elevated: QColor
    subtle_hover: QColor
    subtle_active: QColor

    charcoal: QColor
    slate_dark: QColor
    slate_muted: QColor
    slate_faint: QColor

    sage_accent: QColor
    sage_soft: QColor
    sage_border: QColor
    wood_warm: QColor

    stroke_outer: QColor
    stroke_inner: QColor
    divider: QColor


DAYLIGHT_THEME = NordicThemeColors(
    name="daylight",
    mist_white=QColor("#F6F5F1"),
    warm_birch=QColor("#EDEAE2"),
    oat_surface=QColor(250, 249, 246, 242),
    card_elevated=QColor(255, 254, 251, 228),
    subtle_hover=QColor(38, 41, 46, 12),
    subtle_active=QColor(38, 41, 46, 22),
    charcoal=QColor("#26292E"),
    slate_dark=QColor("#4A5058"),
    slate_muted=QColor("#767D86"),
    slate_faint=QColor("#9DA3AB"),
    sage_accent=QColor("#6E8277"),
    sage_soft=QColor(110, 130, 119, 34),
    sage_border=QColor(110, 130, 119, 76),
    wood_warm=QColor("#C8BBAE"),
    stroke_outer=QColor(38, 41, 46, 20),
    stroke_inner=QColor(255, 255, 255, 195),
    divider=QColor(38, 41, 46, 16),
)

DUSK_THEME = NordicThemeColors(
    name="dusk",
    mist_white=QColor("#1C1F23"),
    warm_birch=QColor("#16181C"),
    oat_surface=QColor(32, 36, 42, 242),
    card_elevated=QColor(42, 47, 54, 225),
    subtle_hover=QColor(234, 231, 225, 14),
    subtle_active=QColor(234, 231, 225, 24),
    charcoal=QColor("#EAE7E1"),
    slate_dark=QColor("#B8BEC6"),
    slate_muted=QColor("#858D96"),
    slate_faint=QColor("#5D646D"),
    sage_accent=QColor("#8CA196"),
    sage_soft=QColor(140, 161, 150, 38),
    sage_border=QColor(140, 161, 150, 82),
    wood_warm=QColor("#8B7E72"),
    stroke_outer=QColor(255, 255, 255, 18),
    stroke_inner=QColor(255, 255, 255, 12),
    divider=QColor(255, 255, 255, 15),
)

_CACHED_FAMILIES_SANS: List[str] = []
_CACHED_FAMILIES_MONO: List[str] = []
_FONT_CACHE: Dict[Tuple[int, int, bool], QFont] = {}


def _resolve_ordered_families(mono: bool) -> List[str]:
    global _CACHED_FAMILIES_SANS, _CACHED_FAMILIES_MONO
    if mono and _CACHED_FAMILIES_MONO:
        return _CACHED_FAMILIES_MONO
    if not mono and _CACHED_FAMILIES_SANS:
        return _CACHED_FAMILIES_SANS

    available = set(QFontDatabase.families())
    if mono:
        candidates = [
            "JetBrains Mono",
            "Cascadia Code",
            "Consolas",
            "Segoe UI",
            "Microsoft YaHei UI",
        ]
    else:
        candidates = [
            "Inter",
            "Segoe UI Variable Display",
            "Segoe UI Variable Text",
            "Segoe UI",
            "Microsoft YaHei UI",
            "PingFang SC",
        ]

    ordered = [c for c in candidates if c in available]
    if not ordered:
        ordered = ["Microsoft YaHei UI", "Segoe UI", "Sans Serif"]

    if mono:
        _CACHED_FAMILIES_MONO = ordered
    else:
        _CACHED_FAMILIES_SANS = ordered
    return ordered


def get_nordic_font(
    pixel_size: int = 14,
    weight: QFont.Weight = QFont.Weight.Normal,
    mono: bool = False,
) -> QFont:
    px = max(8, int(pixel_size))
    w_val = int(weight.value) if hasattr(weight, "value") else int(weight)
    key = (px, w_val, bool(mono))
    cached = _FONT_CACHE.get(key)
    if cached is not None:
        return QFont(cached)

    ordered_families = _resolve_ordered_families(bool(mono))

    font = QFont(ordered_families[0])
    font.setFamilies(ordered_families)
    font.setPixelSize(px)
    font.setWeight(weight)
    font.setStyleStrategy(
        QFont.StyleStrategy.PreferAntialias | QFont.StyleStrategy.PreferQuality
    )
    if px <= 15:
        font.setHintingPreference(QFont.HintingPreference.PreferDefaultHinting)
    else:
        font.setHintingPreference(QFont.HintingPreference.PreferVerticalHinting)
    _FONT_CACHE[key] = font
    return QFont(font)
