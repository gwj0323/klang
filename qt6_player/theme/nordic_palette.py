"""
Nordic Minimalist × WinUI 3 Qt6 Design System Palette
色彩规范：采用低饱和度自然冷色调与大地色系（晨雾白、浅桦木、炭黑、板岩灰、灰鼠尾草绿），严禁高饱和霓虹渐变。
"""

from dataclasses import dataclass
from PyQt6.QtGui import QColor, QFont, QFontDatabase


@dataclass(frozen=True)
class NordicThemeColors:
    name: str
    mist_white: QColor          # 主窗口底层 (Mica Base)
    warm_birch: QColor          # 次级暖白/浅桦木底色
    oat_surface: QColor         # Acrylic 磨砂浮层底色
    card_elevated: QColor       # 悬浮卡片底色
    subtle_hover: QColor        # WinUI 3 Subtle Fill Hover
    subtle_active: QColor       # WinUI 3 Subtle Fill Pressed

    charcoal: QColor            # 炭黑：主标题与当前高亮歌词色
    slate_dark: QColor          # 深板岩灰：副标题与控制图标
    slate_muted: QColor         # 板岩灰：未激活歌词基准色與次要信息
    slate_faint: QColor         # 浅板岩：时间戳与辅助刻度

    sage_accent: QColor         # 灰鼠尾草绿：唯一克制强调色
    sage_soft: QColor           # 鼠尾草柔光底衬
    sage_border: QColor         # 鼠尾草细描边
    wood_warm: QColor           # 浅木色装饰点缀

    stroke_outer: QColor        # 1px 超细外边框
    stroke_inner: QColor        # 1px 顶边内侧微高光
    divider: QColor             # 区域分割超细线


# 北欧晨雾白主题 (Scandinavian Daylight)
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

# 北欧峡湾极夜主题 (Nordic Fjord Dusk - 同样严守低饱和自然冷色调)
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


def get_nordic_font(
    pixel_size: int = 14,
    weight: QFont.Weight = QFont.Weight.Normal,
    mono: bool = False,
) -> QFont:
    """获取极简几何无衬线字体（配置中英文混合回退链与清晰抗锯齿渲染策略）"""
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

    ordered_families = [c for c in candidates if c in available]
    if not ordered_families:
        ordered_families = ["Microsoft YaHei UI", "Segoe UI", "Sans Serif"]

    font = QFont(ordered_families[0])
    font.setFamilies(ordered_families)
    font.setPixelSize(max(8, int(pixel_size)))
    font.setWeight(weight)
    font.setStyleStrategy(
        QFont.StyleStrategy.PreferAntialias | QFont.StyleStrategy.PreferQuality
    )
    # 小字号 UI 控件使用系统垂直微调保证笔画横平竖直，大字号歌词同样保持清晰垂直对齐
    if pixel_size <= 15:
        font.setHintingPreference(QFont.HintingPreference.PreferDefaultHinting)
    else:
        font.setHintingPreference(QFont.HintingPreference.PreferVerticalHinting)
    return font

