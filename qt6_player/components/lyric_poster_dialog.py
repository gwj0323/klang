import os
import math
from typing import List, Optional, Tuple
from pathlib import Path

from PyQt6.QtCore import (
    Qt,
    QRectF,
    QPointF,
    QTimer,
    pyqtSignal,
)
from PyQt6.QtGui import (
    QPainter,
    QPen,
    QColor,
    QFont,
    QFontMetrics,
    QPainterPath,
    QRadialGradient,
    QLinearGradient,
    QPixmap,
    QImage,
    QMouseEvent,
    QGuiApplication,
)
from PyQt6.QtWidgets import (
    QWidget,
    QVBoxLayout,
    QHBoxLayout,
    QLabel,
    QPushButton,
    QScrollArea,
    QCheckBox,
    QFileDialog,
    QApplication,
    QFrame,
)

from qt6_player.data.tracks import NordicTrack, LyricLine
from qt6_player.theme.nordic_palette import (
    NordicThemeColors,
    DAYLIGHT_THEME,
    DUSK_THEME,
    get_nordic_font,
)
from qt6_player.components.fluent_widgets import (
    WinUIModalDialog,
    WinUISegmentedControl,
    WinUIToggleSwitch,
)
from qt6_player.components.title_bar import WinUIChipButton


class LyricPosterPreviewWidget(QWidget):
    """
    歌词海报实时矢量高保真渲染画布。
    支持 1:1 方形艺术卡片与 3:4 极简长海报两种构图比例。
    """

    def __init__(self, parent: Optional[QWidget] = None):
        super().__init__(parent)
        self.track: Optional[NordicTrack] = None
        self.selected_lines: List[LyricLine] = []
        self.ratio: str = "1:1"  # "1:1" | "3:4"
        self.theme_mode: str = "daylight"  # "daylight" | "dusk" | "ambient"
        self.show_translation: bool = True
        self.show_cover: bool = True
        self.cover_pixmap: Optional[QPixmap] = None

        self.setMinimumSize(320, 320)

    def set_data(
        self,
        track: Optional[NordicTrack],
        selected_lines: List[LyricLine],
        ratio: str = "1:1",
        theme_mode: str = "daylight",
        show_translation: bool = True,
        show_cover: bool = True,
    ) -> None:
        self.track = track
        self.selected_lines = selected_lines
        self.ratio = ratio
        self.theme_mode = theme_mode
        self.show_translation = show_translation
        self.show_cover = show_cover
        if track:
            self.cover_pixmap = track.get_cover_pixmap()
        else:
            self.cover_pixmap = None
        self.update()

    def render_poster_image(self, target_width: int = 1080) -> QImage:
        """生成 1080P 高清位图，用于剪贴板直接复制与无损图片导出。"""
        if self.ratio == "3:4":
            target_height = int(target_width * 4.0 / 3.0)
        else:
            target_height = target_width

        img = QImage(target_width, target_height, QImage.Format.Format_ARGB32_Premultiplied)
        img.fill(Qt.GlobalColor.transparent)

        painter = QPainter(img)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        painter.setRenderHint(QPainter.RenderHint.SmoothPixmapTransform)
        painter.setRenderHint(QPainter.RenderHint.TextAntialiasing)

        self._paint_card(painter, QRectF(0, 0, target_width, target_height), scale=target_width / 400.0)
        painter.end()
        return img

    def _paint_card(self, painter: QPainter, rect: QRectF, scale: float = 1.0) -> None:
        w, h = rect.width(), rect.height()
        radius = 24.0 * scale

        # 1. 确定配色系统
        if self.theme_mode == "dusk":
            bg_base = QColor("#111513")
            border_col = QColor(255, 255, 255, 28)
            text_title_col = QColor("#FFFFFF")
            text_meta_col = QColor("#9AA8A1")
            text_lyric_col = QColor("#F0F4F2")
            text_sub_col = QColor("#82948A")
            accent_col = QColor("#7EE0B6")
            brand_col = QColor("#687870")
            quote_col = QColor(126, 224, 182, 35)
            card_fill_path = QPainterPath()
            card_fill_path.addRoundedRect(rect, radius, radius)
            painter.fillPath(card_fill_path, bg_base)

            # 微弱环境光晕
            glow = QRadialGradient(QPointF(w * 0.8, h * 0.2), w * 0.8)
            glow.setColorAt(0.0, QColor(126, 224, 182, 30))
            glow.setColorAt(1.0, Qt.GlobalColor.transparent)
            painter.fillPath(card_fill_path, glow)

        elif self.theme_mode == "ambient":
            pal = (self.track.palette if self.track else None) or {}
            c1 = QColor(pal.get("bg_primary", "#1E2A24"))
            c2 = QColor(pal.get("bg_secondary", "#0E1512"))
            acc = pal.get("accent", "#5B9E83")

            border_col = QColor(255, 255, 255, 42)
            text_title_col = QColor("#FFFFFF")
            text_meta_col = QColor(255, 255, 255, 180)
            text_lyric_col = QColor("#FFFFFF")
            text_sub_col = QColor(255, 255, 255, 160)
            accent_col = QColor(acc)
            brand_col = QColor(255, 255, 255, 140)
            quote_col = QColor(accent_col.red(), accent_col.green(), accent_col.blue(), 45)

            card_fill_path = QPainterPath()
            card_fill_path.addRoundedRect(rect, radius, radius)

            bg_grad = QLinearGradient(rect.topLeft(), rect.bottomRight())
            bg_grad.setColorAt(0.0, c1)
            bg_grad.setColorAt(1.0, c2)
            painter.fillPath(card_fill_path, bg_grad)

            # 流光磨砂层
            frost = QRadialGradient(QPointF(w * 0.25, h * 0.35), w * 0.7)
            frost.setColorAt(0.0, QColor(accent_col.red(), accent_col.green(), accent_col.blue(), 55))
            frost.setColorAt(1.0, Qt.GlobalColor.transparent)
            painter.fillPath(card_fill_path, frost)

        else:  # daylight (北欧晨曦)
            bg_base = QColor("#F7F8F6")
            border_col = QColor("#DFE3DE")
            text_title_col = QColor("#1C201E")
            text_meta_col = QColor("#5A6560")
            text_lyric_col = QColor("#141816")
            text_sub_col = QColor("#6B7872")
            accent_col = QColor("#5B9E83")
            brand_col = QColor("#86948D")
            quote_col = QColor(91, 158, 131, 30)

            card_fill_path = QPainterPath()
            card_fill_path.addRoundedRect(rect, radius, radius)
            painter.fillPath(card_fill_path, bg_base)

            # 晨曦温润微渐变
            glow = QRadialGradient(QPointF(w * 0.85, h * 0.15), w * 0.75)
            glow.setColorAt(0.0, QColor(91, 158, 131, 24))
            glow.setColorAt(1.0, Qt.GlobalColor.transparent)
            painter.fillPath(card_fill_path, glow)

        # 外轮廓描边
        painter.setPen(QPen(border_col, 1.2 * scale))
        painter.setBrush(Qt.BrushStyle.NoBrush)
        painter.drawRoundedRect(rect.adjusted(0.6 * scale, 0.6 * scale, -0.6 * scale, -0.6 * scale), radius, radius)

        pad_x = 32.0 * scale
        pad_top = 34.0 * scale
        cur_y = pad_top

        # 2. 头部：封面画作 + 歌曲信息
        title_text = self.track.title if self.track else "Klang Audio"
        artist_text = self.track.artist if self.track else "Nordic Sound"
        album_text = self.track.album if self.track else "Original Soundtrack"

        cover_sz = 68.0 * scale if self.show_cover else 0.0
        if self.show_cover:
            cover_rect = QRectF(pad_x, cur_y, cover_sz, cover_sz)
            c_radius = 12.0 * scale
            c_path = QPainterPath()
            c_path.addRoundedRect(cover_rect, c_radius, c_radius)

            # 封面投影
            sh_c_path = QPainterPath()
            sh_c_path.addRoundedRect(cover_rect.adjusted(-2.0 * scale, 3.0 * scale, 2.0 * scale, 6.0 * scale), c_radius, c_radius)
            painter.fillPath(sh_c_path, QColor(0, 0, 0, 32))

            painter.save()
            painter.setClipPath(c_path)
            if self.cover_pixmap and not self.cover_pixmap.isNull():
                painter.drawPixmap(cover_rect.toRect(), self.cover_pixmap)
            else:
                painter.fillPath(c_path, accent_col)
            painter.restore()

            # 封面细边框
            painter.setPen(QPen(QColor(255, 255, 255, 45), 1.0 * scale))
            painter.setBrush(Qt.BrushStyle.NoBrush)
            painter.drawRoundedRect(cover_rect, c_radius, c_radius)

            text_left = pad_x + cover_sz + 16.0 * scale
        else:
            text_left = pad_x

        # 歌曲名与艺术家
        painter.setPen(text_title_col)
        painter.setFont(get_nordic_font(int(round(18 * scale)), QFont.Weight.Bold))
        fm_t = painter.fontMetrics()
        elided_title = fm_t.elidedText(title_text, Qt.TextElideMode.ElideRight, int(w - text_left - pad_x))
        painter.drawText(QPointF(text_left, cur_y + 24.0 * scale), elided_title)

        painter.setPen(text_meta_col)
        painter.setFont(get_nordic_font(int(round(12 * scale)), QFont.Weight.Medium))
        sub_info = f"{artist_text} · {album_text}" if album_text else artist_text
        fm_s = painter.fontMetrics()
        elided_sub = fm_s.elidedText(sub_info, Qt.TextElideMode.ElideRight, int(w - text_left - pad_x))
        painter.drawText(QPointF(text_left, cur_y + 46.0 * scale), elided_sub)

        # 头部高度推进
        head_h = max(cover_sz, 52.0 * scale)
        cur_y += head_h + 26.0 * scale

        # 3. 装饰分割线
        painter.setPen(QPen(quote_col, 1.0 * scale))
        painter.drawLine(QPointF(pad_x, cur_y), QPointF(w - pad_x, cur_y))
        cur_y += 24.0 * scale

        # 4. 巨型装饰引号 (北欧极简美学标志)
        painter.save()
        painter.setFont(QFont("Georgia", int(round(46 * scale)), QFont.Weight.Bold))
        painter.setPen(quote_col)
        painter.drawText(QPointF(pad_x - 4.0 * scale, cur_y + 20.0 * scale), "“")
        painter.restore()

        cur_y += 18.0 * scale

        # 5. 精选歌词正文区域 (垂直居中留白计算)
        footer_h = 44.0 * scale
        content_bottom = h - footer_h - 20.0 * scale
        available_lyric_h = max(80.0 * scale, content_bottom - cur_y)

        # 动态根据行数计算最佳字号
        line_count = max(1, len(self.selected_lines))
        has_trans = self.show_translation and any(bool(l.sub) for l in self.selected_lines)

        if line_count <= 2:
            main_fs = int(round(18 * scale))
            sub_fs = int(round(11 * scale))
            line_spacing = 16.0 * scale
        elif line_count <= 4:
            main_fs = int(round(15 * scale))
            sub_fs = int(round(10 * scale))
            line_spacing = 13.0 * scale
        else:
            main_fs = int(round(13 * scale))
            sub_fs = int(round(9 * scale))
            line_spacing = 9.0 * scale

        # 测量总文本高度以垂直居中摆放
        font_main = get_nordic_font(main_fs, QFont.Weight.DemiBold)
        font_sub = get_nordic_font(sub_fs, QFont.Weight.Normal)
        fm_m = QFontMetrics(font_main)
        fm_sb = QFontMetrics(font_sub)

        total_text_h = 0.0
        for l in self.selected_lines:
            total_text_h += fm_m.height()
            if self.show_translation and l.sub:
                total_text_h += fm_sb.height() + 3.0 * scale
            total_text_h += line_spacing

        # 如果空间允许，向下偏移居中
        if available_lyric_h > total_text_h:
            cur_y += (available_lyric_h - total_text_h) * 0.42

        # 渲染歌词行
        for l in self.selected_lines:
            # 原文行
            painter.setFont(font_main)
            painter.setPen(text_lyric_col)
            lyric_rect = QRectF(pad_x + 8.0 * scale, cur_y, w - 2 * pad_x - 16.0 * scale, fm_m.height())
            painter.drawText(
                lyric_rect,
                int(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter),
                l.text,
            )
            cur_y += fm_m.height()

            # 译文行
            if self.show_translation and l.sub:
                painter.setFont(font_sub)
                painter.setPen(text_sub_col)
                sub_rect = QRectF(pad_x + 8.0 * scale, cur_y + 2.0 * scale, w - 2 * pad_x - 16.0 * scale, fm_sb.height())
                painter.drawText(
                    sub_rect,
                    int(Qt.AlignmentFlag.AlignLeft | Qt.AlignmentFlag.AlignVCenter),
                    l.sub,
                )
                cur_y += fm_sb.height() + 3.0 * scale

            cur_y += line_spacing

        # 6. 底部品牌与音质徽标
        footer_y = h - 34.0 * scale
        painter.setPen(QPen(border_col, 0.8 * scale))
        painter.drawLine(QPointF(pad_x, footer_y - 12.0 * scale), QPointF(w - pad_x, footer_y - 12.0 * scale))

        # 品牌字样
        painter.setFont(get_nordic_font(int(round(11 * scale)), QFont.Weight.Medium))
        painter.setPen(brand_col)
        painter.drawText(QPointF(pad_x, footer_y + 6.0 * scale), "Klang · Nordic Audio")

        # 高清音质标签胶囊
        if self.track and getattr(self.track, "is_hi_res", False):
            badge_text = f"Hi-Res · {self.track.bit_depth}-bit/{self.track.sample_rate_str}"
        elif self.track and getattr(self.track, "sample_rate", None):
            badge_text = f"Lossless · {self.track.sample_rate_str}"
        else:
            badge_text = "Lossless · 24-bit" 
        painter.setFont(get_nordic_font(int(round(9 * scale)), QFont.Weight.Bold, mono=True))
        fm_b = painter.fontMetrics()
        bw = fm_b.horizontalAdvance(badge_text) + 14.0 * scale
        bh = 18.0 * scale
        bx = w - pad_x - bw
        by = footer_y - 6.0 * scale

        b_rect = QRectF(bx, by, bw, bh)
        b_path = QPainterPath()
        b_path.addRoundedRect(b_rect, bh / 2.0, bh / 2.0)
        painter.fillPath(b_path, quote_col)
        painter.setPen(accent_col)
        painter.drawText(b_rect, int(Qt.AlignmentFlag.AlignCenter), badge_text)

    def paintEvent(self, event) -> None:
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        painter.setRenderHint(QPainter.RenderHint.SmoothPixmapTransform)
        painter.setRenderHint(QPainter.RenderHint.TextAntialiasing)

        # 计算在控件中的自适应居中尺寸
        w, h = self.width(), self.height()
        target_aspect = 1.0 if self.ratio == "1:1" else (3.0 / 4.0)
        widget_aspect = w / max(1.0, float(h))

        if widget_aspect > target_aspect:
            card_h = h - 20.0
            card_w = card_h * target_aspect
        else:
            card_w = w - 20.0
            card_h = card_w / target_aspect

        card_x = (w - card_w) / 2.0
        card_y = (h - card_h) / 2.0

        card_rect = QRectF(card_x, card_y, card_w, card_h)
        scale = card_w / 400.0
        self._paint_card(painter, card_rect, scale=scale)


class LyricPosterDialog(WinUIModalDialog):
    """
    歌词海报 / 卡片一键生成与复制分享对话框。
    具备：
    1. 2~4 句歌词自由多选，智能默认选中当前正在播放的精选高潮句。
    2. 1:1 方形艺术卡片与 3:4 极简长海报双构图切换。
    3. 北欧晨曦 (Daylight)、极夜深黑 (Dusk)、唱片流光 (Ambient) 三重设计质感。
    4. 一键复制高清图片到剪贴板，支持直接在微信、QQ、笔记软件中 Ctrl+V 粘贴分享。
    5. 无损 PNG 导出保存。
    """

    def __init__(
        self,
        track: Optional[NordicTrack],
        lyrics: List[LyricLine],
        current_index: int = 0,
        theme: NordicThemeColors = DAYLIGHT_THEME,
        parent: Optional[QWidget] = None,
    ):
        super().__init__("歌词海报卡片", 860, 620, theme=theme, parent=parent)
        self.track = track
        self.all_lyrics = list(lyrics) if lyrics else []
        self.selected_indices = set()

        # 智能默认选中：优先选中当前句和其后 1~2 句 (总共 2~3 句精美歌词)
        if self.all_lyrics:
            idx = max(0, min(len(self.all_lyrics) - 1, current_index))
            self.selected_indices.add(idx)
            if idx + 1 < len(self.all_lyrics):
                self.selected_indices.add(idx + 1)
            if idx + 2 < len(self.all_lyrics) and len(self.selected_indices) < 3:
                self.selected_indices.add(idx + 2)

        self._setup_content()

    def _setup_content(self) -> None:
        vbox = self._root_vbox
        vbox.setSpacing(12)

        # 主工作区分双栏：左侧参数配置与歌词选行，右侧高保真预览
        hbox = QHBoxLayout()
        hbox.setSpacing(20)

        # ----------------- 左栏：选项控制与歌词列表 -----------------
        left_widget = QWidget(self)
        left_vbox = QVBoxLayout(left_widget)
        left_vbox.setContentsMargins(0, 0, 0, 0)
        left_vbox.setSpacing(10)

        # 1. 构图比例
        lbl_ratio = QLabel("构图比例", left_widget)
        lbl_ratio.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        lbl_ratio.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        left_vbox.addWidget(lbl_ratio)

        self.ratio_tabs = WinUISegmentedControl(["经典方卡 (1:1)", "极简竖屏 (3:4)"], left_widget)
        self.ratio_tabs.set_theme(self.theme)
        self.ratio_tabs.currentIndexChanged.connect(self._on_ratio_changed)
        left_vbox.addWidget(self.ratio_tabs)

        # 2. 视觉风格
        lbl_style = QLabel("视觉风格", left_widget)
        lbl_style.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        lbl_style.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        left_vbox.addWidget(lbl_style)

        self.style_tabs = WinUISegmentedControl(["北欧晨曦", "极夜深黑", "唱片流光"], left_widget)
        self.style_tabs.set_theme(self.theme)
        if self.theme.name == "dusk":
            self.style_tabs.set_current_index(1, animate=False)
        self.style_tabs.currentIndexChanged.connect(self._on_style_changed)
        left_vbox.addWidget(self.style_tabs)

        # 3. 勾选选项 (双语翻译 & 专辑封面)
        opt_row = QHBoxLayout()
        self.cb_trans = QCheckBox("包含双语翻译", left_widget)
        self.cb_trans.setChecked(True)
        self.cb_trans.setStyleSheet(
            f"QCheckBox {{ color: {self.theme.slate_dark.name()}; font-size: 12px; font-weight: 500; }} "
            f"QCheckBox::indicator {{ width: 16px; height: 16px; border-radius: 4px; border: 1px solid {self.theme.sage_border.name()}; }} "
            f"QCheckBox::indicator:checked {{ background: {self.theme.sage_accent.name()}; border-color: {self.theme.sage_accent.name()}; }}"
        )
        self.cb_trans.toggled.connect(self._on_option_changed)
        opt_row.addWidget(self.cb_trans)

        self.cb_cover = QCheckBox("显示封面画作", left_widget)
        self.cb_cover.setChecked(True)
        self.cb_cover.setStyleSheet(self.cb_trans.styleSheet())
        self.cb_cover.toggled.connect(self._on_option_changed)
        opt_row.addWidget(self.cb_cover)
        opt_row.addStretch(1)
        left_vbox.addLayout(opt_row)

        # 4. 歌词选行区
        lyric_header = QHBoxLayout()
        lbl_lines = QLabel("选择歌词", left_widget)
        lbl_lines.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        lbl_lines.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        lyric_header.addWidget(lbl_lines)

        self.lbl_counter = QLabel(f"已选 {len(self.selected_indices)} 句 (建议 2~4 句)", left_widget)
        self.lbl_counter.setFont(get_nordic_font(10, QFont.Weight.Normal))
        self.lbl_counter.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        lyric_header.addWidget(self.lbl_counter)
        lyric_header.addStretch(1)

        btn_clear = QPushButton("清空", left_widget)
        btn_clear.setFont(get_nordic_font(11, QFont.Weight.Medium))
        btn_clear.setCursor(Qt.CursorShape.PointingHandCursor)
        btn_clear.setStyleSheet(f"QPushButton {{ background: transparent; border: none; color: {self.theme.sage_accent.name()}; }} QPushButton:hover {{ text-decoration: underline; }}")
        btn_clear.clicked.connect(self._clear_selection)
        lyric_header.addWidget(btn_clear)
        left_vbox.addLayout(lyric_header)

        # 歌词列表滚动容器
        scroll = QScrollArea(left_widget)
        scroll.setWidgetResizable(True)
        scroll.setStyleSheet(
            f"QScrollArea {{ border: 1px solid {self.theme.stroke_outer.name()}; border-radius: 8px; background: {self.theme.card_elevated.name()}; }}"
            "QScrollBar:vertical { width: 6px; background: transparent; }"
            "QScrollBar::handle:vertical { background: rgba(120, 130, 125, 0.35); border-radius: 3px; }"
            "QScrollBar::add-line:vertical, QScrollBar::sub-line:vertical { height: 0px; }"
        )

        scroll_content = QWidget()
        scroll_content.setStyleSheet("background: transparent;")
        self.scroll_layout = QVBoxLayout(scroll_content)
        self.scroll_layout.setContentsMargins(8, 8, 8, 8)
        self.scroll_layout.setSpacing(4)

        self.checkboxes = []
        if not self.all_lyrics:
            lbl_empty = QLabel("当前曲目暂无歌词数据", scroll_content)
            lbl_empty.setFont(get_nordic_font(12, QFont.Weight.Normal))
            lbl_empty.setStyleSheet(f"color: {self.theme.slate_muted.name()}; padding: 20px;")
            self.scroll_layout.addWidget(lbl_empty)
        else:
            for idx, line in enumerate(self.all_lyrics):
                cb = QCheckBox(f"{line.text}", scroll_content)
                cb.setChecked(idx in self.selected_indices)
                cb.setFont(get_nordic_font(12, QFont.Weight.Normal))
                cb.setStyleSheet(
                    f"QCheckBox {{ color: {self.theme.charcoal.name()}; padding: 4px 6px; border-radius: 4px; }} "
                    f"QCheckBox:hover {{ background: {self.theme.oat_surface.name()}; }} "
                    f"QCheckBox::indicator {{ width: 14px; height: 14px; border-radius: 3px; border: 1px solid {self.theme.sage_border.name()}; }} "
                    f"QCheckBox::indicator:checked {{ background: {self.theme.sage_accent.name()}; border-color: {self.theme.sage_accent.name()}; }}"
                )
                cb.toggled.connect(lambda chk, i=idx: self._on_line_toggled(i, chk))
                self.checkboxes.append(cb)
                self.scroll_layout.addWidget(cb)

        self.scroll_layout.addStretch(1)
        scroll.setWidget(scroll_content)
        left_vbox.addWidget(scroll, 1)

        hbox.addWidget(left_widget, 1)

        # ----------------- 右栏：高保真矢量画布预览 -----------------
        right_widget = QWidget(self)
        right_vbox = QVBoxLayout(right_widget)
        right_vbox.setContentsMargins(0, 0, 0, 0)
        right_vbox.setSpacing(8)

        preview_header = QHBoxLayout()
        lbl_pv = QLabel("卡片预览", right_widget)
        lbl_pv.setFont(get_nordic_font(12, QFont.Weight.DemiBold))
        lbl_pv.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        preview_header.addWidget(lbl_pv)

        lbl_pv_tag = QLabel("1080P 超清渲染", right_widget)
        lbl_pv_tag.setFont(get_nordic_font(9, QFont.Weight.Bold, mono=True))
        lbl_pv_tag.setStyleSheet(f"color: {self.theme.sage_accent.name()}; background: transparent;")
        preview_header.addWidget(lbl_pv_tag)
        preview_header.addStretch(1)
        right_vbox.addLayout(preview_header)

        self.preview_canvas = LyricPosterPreviewWidget(right_widget)
        right_vbox.addWidget(self.preview_canvas, 1)

        hbox.addWidget(right_widget, 1)
        vbox.addLayout(hbox, 1)

        # ----------------- 底部操作栏与提示 -----------------
        bottom_bar = QHBoxLayout()
        bottom_bar.setContentsMargins(0, 4, 0, 0)

        self.lbl_toast = QLabel("", self)
        self.lbl_toast.setFont(get_nordic_font(11, QFont.Weight.Medium))
        self.lbl_toast.setStyleSheet(f"color: {self.theme.sage_accent.name()}; background: transparent;")
        bottom_bar.addWidget(self.lbl_toast)
        bottom_bar.addStretch(1)

        self.btn_copy = WinUIChipButton("复制到剪贴板", active=True, icon_kind="copy", parent=self)
        self.btn_copy.set_theme(self.theme)
        self.btn_copy.clicked.connect(self._on_copy_to_clipboard)
        bottom_bar.addWidget(self.btn_copy)

        self.btn_save = WinUIChipButton("保存图片...", active=False, icon_kind="download", parent=self)
        self.btn_save.set_theme(self.theme)
        self.btn_save.clicked.connect(self._on_save_as_image)
        bottom_bar.addWidget(self.btn_save)

        vbox.addLayout(bottom_bar)

        self._refresh_preview()

    def _get_current_selected_lines(self) -> List[LyricLine]:
        sorted_indices = sorted(self.selected_indices)
        return [self.all_lyrics[i] for i in sorted_indices if 0 <= i < len(self.all_lyrics)]

    def _on_line_toggled(self, index: int, checked: bool) -> None:
        if checked:
            self.selected_indices.add(index)
        else:
            self.selected_indices.discard(index)
        self.lbl_counter.setText(f"已选 {len(self.selected_indices)} 句 (建议 2~4 句)")
        self._refresh_preview()

    def _clear_selection(self) -> None:
        self.selected_indices.clear()
        for cb in self.checkboxes:
            cb.blockSignals(True)
            cb.setChecked(False)
            cb.blockSignals(False)
        self.lbl_counter.setText("已选 0 句 (建议 2~4 句)")
        self._refresh_preview()

    def _on_ratio_changed(self, idx: int) -> None:
        self._refresh_preview()

    def _on_style_changed(self, idx: int) -> None:
        self._refresh_preview()

    def _on_option_changed(self) -> None:
        self._refresh_preview()

    def _refresh_preview(self) -> None:
        ratio = "3:4" if self.ratio_tabs.current_index == 1 else "1:1"
        style_map = ["daylight", "dusk", "ambient"]
        theme_mode = style_map[self.style_tabs.current_index] if 0 <= self.style_tabs.current_index < len(style_map) else "daylight"
        lines = self._get_current_selected_lines()

        self.preview_canvas.set_data(
            track=self.track,
            selected_lines=lines,
            ratio=ratio,
            theme_mode=theme_mode,
            show_translation=self.cb_trans.isChecked(),
            show_cover=self.cb_cover.isChecked(),
        )

    def _show_toast(self, text: str) -> None:
        self.lbl_toast.setText(text)
        QTimer.singleShot(4000, lambda: self.lbl_toast.setText(""))

    def _on_copy_to_clipboard(self) -> None:
        lines = self._get_current_selected_lines()
        if not lines:
            self._show_toast("请至少选择 1 句歌词进行生成")
            return

        try:
            img = self.preview_canvas.render_poster_image(target_width=1080)
            clipboard = QGuiApplication.clipboard()
            if clipboard is not None:
                clipboard.setImage(img)
                self._show_toast("✓ 歌词海报已复制至剪贴板，可直接在微信/QQ/备忘录粘贴！")
        except Exception as e:
            self._show_toast(f"复制失败: {e}")

    def _on_save_as_image(self) -> None:
        lines = self._get_current_selected_lines()
        if not lines:
            self._show_toast("请至少选择 1 句歌词进行保存")
            return

        default_name = f"{self.track.title if self.track else 'Klang'} - 歌词卡片.png"
        path, _ = QFileDialog.getSaveFileName(
            self,
            "保存歌词海报图片",
            default_name,
            "PNG 图像 (*.png);;所有文件 (*.*)",
        )
        if not path:
            return

        try:
            img = self.preview_canvas.render_poster_image(target_width=1080)
            img.save(path, "PNG")
            self._show_toast("✓ 海报图片已成功保存至本地！")
        except Exception as e:
            self._show_toast(f"保存失败: {e}")
