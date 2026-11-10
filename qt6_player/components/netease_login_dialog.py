import logging as _logging
import webbrowser
from typing import Optional

from PyQt6.QtCore import Qt, QTimer, pyqtSignal
from PyQt6.QtGui import QFont, QPixmap
from PyQt6.QtWidgets import (
    QWidget,
    QVBoxLayout,
    QHBoxLayout,
    QLabel,
    QPushButton,
    QLineEdit,
    QStackedWidget,
    QApplication,
)

from qt6_player.theme.nordic_palette import NordicThemeColors, DAYLIGHT_THEME, get_nordic_font
from qt6_player.components.fluent_widgets import WinUIModalDialog, WinUISegmentedControl
from qt6_player.components.title_bar import WinUIChipButton
from qt6_player.services.netease_service import netease_manager


class NeteaseLoginDialog(WinUIModalDialog):
    """
    WinUI 3 风格网易云音乐登录弹窗（双模登录架构）
    - 模式一：免密矢量二维码扫码登录（防风控自适应轮询）
    - 模式二：Cookie / MUSIC_U 凭证直连登录（100% 避开任何移动端验证与扫码风控）
    """
    loginSuccess = pyqtSignal(dict)

    def __init__(self, theme: NordicThemeColors = DAYLIGHT_THEME, parent: Optional[QWidget] = None):
        super().__init__("网易云音乐登录", 390, 480, theme=theme, parent=parent)
        self.theme = theme
        self.unikey: str = ""
        self._qr_pixmap: Optional[QPixmap] = None
        self._is_expired: bool = False
        self._is_polling: bool = False
        self._consecutive_risk_count: int = 0

        self._setup_ui()

        # 连接管理器信号
        netease_manager.qrCodeGenerated.connect(self._on_qr_ready)
        netease_manager.qrStatusChecked.connect(self._on_poll_result)
        netease_manager.cookieLoginChecked.connect(self._on_cookie_login_result)

        # 扫码轮询定时器 (2.5秒一次，符合网易云网关安全阈值)
        self._poll_timer = QTimer(self)
        self._poll_timer.setInterval(2500)
        self._poll_timer.timeout.connect(self._poll_status)

        # 启动加载二维码
        self._refresh_qrcode()

    def _setup_ui(self) -> None:
        vbox = self._root_vbox
        vbox.setSpacing(10)
        vbox.setAlignment(Qt.AlignmentFlag.AlignTop | Qt.AlignmentFlag.AlignHCenter)

        # 顶部模式切换分段控制器
        self.seg_ctrl = WinUISegmentedControl(["扫码登录", "Cookie 登录"], self)
        self.seg_ctrl.set_theme(self.theme)
        self.seg_ctrl.setFixedWidth(260)
        self.seg_ctrl.currentIndexChanged.connect(self._on_tab_changed)
        vbox.addWidget(self.seg_ctrl, 0, Qt.AlignmentFlag.AlignCenter)

        # 页面堆叠器
        self.pages = QStackedWidget(self)
        vbox.addWidget(self.pages, 1)

        # 创建两页
        self._init_qr_page()
        self._init_cookie_page()

    # ---------------- 扫码登录页 ----------------

    def _init_qr_page(self) -> None:
        self.page_qr = QWidget(self)
        qv = QVBoxLayout(self.page_qr)
        qv.setContentsMargins(10, 8, 10, 8)
        qv.setSpacing(10)
        qv.setAlignment(Qt.AlignmentFlag.AlignCenter)

        # 二维码承载卡片
        self.qr_label = QLabel(self.page_qr)
        self.qr_label.setFixedSize(196, 196)
        self.qr_label.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.qr_label.setStyleSheet(
            "background: #FFFFFF; border: 1px solid rgba(0, 0, 0, 0.12); "
            "border-radius: 12px; padding: 8px;"
        )
        qv.addWidget(self.qr_label, 0, Qt.AlignmentFlag.AlignCenter)

        # 状态提示文本
        self.lbl_status = QLabel("请使用网易云音乐 App 扫码登录", self.page_qr)
        self.lbl_status.setFont(get_nordic_font(12, QFont.Weight.Medium))
        self.lbl_status.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.lbl_status.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        qv.addWidget(self.lbl_status)

        self.lbl_hint = QLabel("支持同步「我喜欢的音乐」、个人创建与收藏歌单", self.page_qr)
        self.lbl_hint.setFont(get_nordic_font(10, QFont.Weight.Normal))
        self.lbl_hint.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.lbl_hint.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent;")
        qv.addWidget(self.lbl_hint)

        # 提示用户可切换 Cookie 登录
        self.lbl_switch_tip = QLabel("若手机端提示验证码或频繁，可切换上方「Cookie 登录」", self.page_qr)
        self.lbl_switch_tip.setFont(get_nordic_font(9, QFont.Weight.Normal))
        self.lbl_switch_tip.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.lbl_switch_tip.setStyleSheet(f"color: {self.theme.slate_muted.name()}; opacity: 0.8; background: transparent;")
        qv.addWidget(self.lbl_switch_tip)

        # 刷新二维码按钮（过期或重试时显式出现）
        self.btn_refresh = WinUIChipButton("重新加载二维码", icon_kind="play", parent=self.page_qr)
        self.btn_refresh.set_theme(self.theme)
        self.btn_refresh.clicked.connect(self._refresh_qrcode)
        self.btn_refresh.hide()
        qv.addWidget(self.btn_refresh, 0, Qt.AlignmentFlag.AlignCenter)

        self.pages.addWidget(self.page_qr)

    # ---------------- Cookie 登录页 ----------------

    def _init_cookie_page(self) -> None:
        self.page_cookie = QWidget(self)
        cv = QVBoxLayout(self.page_cookie)
        cv.setContentsMargins(14, 10, 14, 10)
        cv.setSpacing(12)
        cv.setAlignment(Qt.AlignmentFlag.AlignTop)

        lbl_desc = QLabel("免扫码直连模式，100% 避开移动端人机验证与风控", self.page_cookie)
        lbl_desc.setFont(get_nordic_font(11, QFont.Weight.Medium))
        lbl_desc.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        cv.addWidget(lbl_desc)

        # 输入框
        self.txt_cookie = QLineEdit(self.page_cookie)
        self.txt_cookie.setPlaceholderText("在此粘贴 MUSIC_U 或整段 Cookie…")
        self.txt_cookie.setClearButtonEnabled(True)
        self.txt_cookie.setFixedHeight(38)
        self.txt_cookie.setFont(get_nordic_font(11))

        is_dark = (getattr(self.theme, "name", "") == "dusk")
        input_bg = "rgba(255, 255, 255, 0.08)" if is_dark else "rgba(0, 0, 0, 0.04)"
        input_border = "rgba(255, 255, 255, 0.16)" if is_dark else "rgba(0, 0, 0, 0.15)"
        input_focus_bg = "#232426" if is_dark else "#FFFFFF"
        text_color = "#ECECEC" if is_dark else "#181B1E"
        placeholder_color = "rgba(255, 255, 255, 0.35)" if is_dark else "rgba(0, 0, 0, 0.35)"

        self.txt_cookie.setStyleSheet(f"""
            QLineEdit {{
                background: {input_bg};
                color: {text_color};
                border: 1px solid {input_border};
                border-radius: 8px;
                padding: 0 10px;
            }}
            QLineEdit:focus {{
                border: 1.5px solid {self.theme.sage_accent.name()};
                background: {input_focus_bg};
            }}
            QLineEdit::placeholder {{
                color: {placeholder_color};
            }}
        """)
        cv.addWidget(self.txt_cookie)

        # 辅助操作栏
        h_tools = QHBoxLayout()
        h_tools.setSpacing(8)

        self.btn_open_web = WinUIChipButton("网页版 ↗", icon_kind="play", parent=self.page_cookie)
        self.btn_open_web.set_theme(self.theme)
        self.btn_open_web.clicked.connect(lambda: webbrowser.open("https://music.163.com"))
        h_tools.addWidget(self.btn_open_web)

        self.btn_paste = WinUIChipButton("粘贴剪贴板 📋", icon_kind="play", parent=self.page_cookie)
        self.btn_paste.set_theme(self.theme)
        self.btn_paste.clicked.connect(self._paste_from_clipboard)
        h_tools.addWidget(self.btn_paste)

        h_tools.addStretch(1)
        cv.addLayout(h_tools)

        # 说明卡片
        tip_box = QWidget(self.page_cookie)
        tip_bg = "rgba(255, 255, 255, 0.05)" if is_dark else "rgba(0, 0, 0, 0.03)"
        tip_box.setStyleSheet(f"background: {tip_bg}; border-radius: 8px; padding: 8px;")
        tip_layout = QVBoxLayout(tip_box)
        tip_layout.setContentsMargins(10, 8, 10, 8)
        tip_layout.setSpacing(4)

        guide_title = QLabel("💡 如何获取 MUSIC_U（只需 30 秒）：", tip_box)
        guide_title.setFont(get_nordic_font(10, QFont.Weight.Bold))
        guide_title.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        tip_layout.addWidget(guide_title)

        guide_steps = QLabel(
            "1. 浏览器打开 music.163.com 并登录账号\n"
            "2. 按 F12 打开开发者工具 -> 应用程序 (Application) -> Cookie\n"
            "3. 找到 MUSIC_U 项并双击复制其值，粘贴到上方即可",
            tip_box
        )
        guide_steps.setFont(get_nordic_font(9))
        guide_steps.setStyleSheet(f"color: {self.theme.slate_muted.name()}; background: transparent; line-height: 140%;")
        tip_layout.addWidget(guide_steps)
        cv.addWidget(tip_box)

        # 状态提示
        self.lbl_cookie_status = QLabel("", self.page_cookie)
        self.lbl_cookie_status.setFont(get_nordic_font(11, QFont.Weight.Medium))
        self.lbl_cookie_status.setAlignment(Qt.AlignmentFlag.AlignCenter)
        self.lbl_cookie_status.setStyleSheet("color: #D36152; background: transparent;")
        cv.addWidget(self.lbl_cookie_status)

        # 立即登录按钮
        self.btn_cookie_login = QPushButton("立即验证并登录", self.page_cookie)
        self.btn_cookie_login.setFixedHeight(38)
        self.btn_cookie_login.setFont(get_nordic_font(12, QFont.Weight.Bold))
        self.btn_cookie_login.setCursor(Qt.CursorShape.PointingHandCursor)
        self.btn_cookie_login.setStyleSheet(f"""
            QPushButton {{
                background: {self.theme.sage_accent.name()};
                color: #FFFFFF;
                border: none;
                border-radius: 8px;
            }}
            QPushButton:hover {{
                background: {self.theme.sage_accent.lighter(110).name()};
            }}
            QPushButton:pressed {{
                background: {self.theme.sage_accent.darker(110).name()};
            }}
            QPushButton:disabled {{
                background: rgba(120, 120, 120, 0.3);
                color: rgba(255, 255, 255, 0.6);
            }}
        """)
        self.btn_cookie_login.clicked.connect(self._do_cookie_login)
        cv.addWidget(self.btn_cookie_login)

        cv.addStretch(1)
        self.pages.addWidget(self.page_cookie)

    def _paste_from_clipboard(self) -> None:
        clip = QApplication.clipboard()
        text = clip.text().strip()
        if text:
            self.txt_cookie.setText(text)
            self.lbl_cookie_status.setText("")

    def _on_tab_changed(self, index: int) -> None:
        self.pages.setCurrentIndex(index)
        if index == 0:
            if not self._is_expired:
                self._poll_timer.start(2500)
        else:
            self._poll_timer.stop()
            self.txt_cookie.setFocus()

    def _do_cookie_login(self) -> None:
        val = self.txt_cookie.text().strip()
        if not val:
            self.lbl_cookie_status.setText("请先粘贴 MUSIC_U 或完整 Cookie")
            self.lbl_cookie_status.setStyleSheet("color: #D36152; background: transparent;")
            return

        self.btn_cookie_login.setEnabled(False)
        self.btn_cookie_login.setText("正在验证凭证…")
        self.lbl_cookie_status.setText("正在连接网易云服务器鉴权…")
        self.lbl_cookie_status.setStyleSheet(f"color: {self.theme.sage_accent.name()}; background: transparent;")
        netease_manager.login_via_cookie_async(val)

    def _on_cookie_login_result(self, success: bool, msg: str, profile: dict) -> None:
        self.btn_cookie_login.setEnabled(True)
        self.btn_cookie_login.setText("立即验证并登录")
        if success:
            self.lbl_cookie_status.setText("✅ 登录成功，正在加载个人曲库…")
            self.lbl_cookie_status.setStyleSheet("color: #4CAF50; background: transparent;")
            if profile:
                self.loginSuccess.emit(profile)
            QTimer.singleShot(750, self.accept)
        else:
            self.lbl_cookie_status.setText(msg or "Cookie 无效或已过期，请重新获取")
            self.lbl_cookie_status.setStyleSheet("color: #D36152; background: transparent;")

    # ---------------- 扫码轮询核心逻辑 ----------------

    def _refresh_qrcode(self) -> None:
        self.btn_refresh.hide()
        self.lbl_status.setText("正在生成安全登录码…")
        self.lbl_status.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        self._is_expired = False
        self._consecutive_risk_count = 0
        self._poll_timer.stop()
        netease_manager.request_qr_login_async()

    def _on_qr_ready(self, unikey: str, png_bytes: bytes) -> None:
        if not unikey or not png_bytes:
            self.lbl_status.setText("获取登录码失败，请检查网络")
            self.btn_refresh.show()
            return

        pix = QPixmap()
        pix.loadFromData(png_bytes)
        self.unikey = unikey
        self._qr_pixmap = pix
        scaled = pix.scaled(
            176,
            176,
            Qt.AspectRatioMode.KeepAspectRatio,
            Qt.TransformationMode.SmoothTransformation,
        )
        self.qr_label.setPixmap(scaled)
        self.lbl_status.setText("请使用网易云音乐 App 扫码登录")
        self.lbl_status.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        if self.pages.currentIndex() == 0:
            self._poll_timer.start(2500)

    def _poll_status(self) -> None:
        if not self.unikey or self._is_expired or self._is_polling:
            return

        self._is_polling = True
        netease_manager.poll_qr_status_async(self.unikey)

    def _on_poll_result(self, code: int, msg: str, profile: dict) -> None:
        self._is_polling = False
        if code == 800:
            # 二维码已过期
            self._is_expired = True
            self._poll_timer.stop()
            self.lbl_status.setText("二维码已过期，请点击刷新")
            self.lbl_status.setStyleSheet("color: #D36152; background: transparent;")
            self.btn_refresh.show()
        elif code == 801:
            # 等待扫码
            self._consecutive_risk_count = 0
            self.lbl_status.setText("请使用网易云音乐 App 扫码登录")
            self.lbl_status.setStyleSheet(f"color: {self.theme.charcoal.name()}; background: transparent;")
        elif code == 802:
            # 待确认
            self._consecutive_risk_count = 0
            self.lbl_status.setText("📱 已扫码，请在手机上点击确认授权…")
            self.lbl_status.setStyleSheet(f"color: {self.theme.sage_accent.name()}; background: transparent;")
        elif code == 803:
            # 登录成功
            self._poll_timer.stop()
            self.lbl_status.setText("✅ 登录成功，正在加载个人曲库…")
            self.lbl_status.setStyleSheet("color: #4CAF50; background: transparent;")
            if profile:
                self.loginSuccess.emit(profile)
            QTimer.singleShot(750, self.accept)
        elif code in (8821, -460):
            # 网易云风控拦截 / 等待手机验证
            self._consecutive_risk_count += 1
            if self._consecutive_risk_count <= 8:
                # 保持轮询等待手机端完成验证或点击
                self.lbl_status.setText("📱 手机端正在安全验证，请在手机上点击允许…")
                self.lbl_status.setStyleSheet(f"color: {self.theme.sage_accent.name()}; background: transparent;")
            else:
                self._poll_timer.stop()
                self._is_expired = True
                self.lbl_status.setText("扫码受网易云风控拦截，推荐切换至「Cookie 登录」")
                self.lbl_status.setStyleSheet("color: #D36152; background: transparent;")
                self.btn_refresh.show()
        elif code < 0 and self._is_expired:
            self._poll_timer.stop()
            self.lbl_status.setText(f"登录提示: {msg or '网络连接超时，请重试'}")
            self.btn_refresh.show()

    def _cleanup(self) -> None:
        self._poll_timer.stop()
        try:
            netease_manager.qrCodeGenerated.disconnect(self._on_qr_ready)
        except Exception:
            _logging.getLogger(__name__).debug("ignored exception", exc_info=True)
        try:
            netease_manager.qrStatusChecked.disconnect(self._on_poll_result)
        except Exception:
            _logging.getLogger(__name__).debug("ignored exception", exc_info=True)
        try:
            netease_manager.cookieLoginChecked.disconnect(self._on_cookie_login_result)
        except Exception:
            _logging.getLogger(__name__).debug("ignored exception", exc_info=True)

    def closeEvent(self, event) -> None:
        self._cleanup()
        super().closeEvent(event)

    def reject(self) -> None:
        self._cleanup()
        super().reject()

    def accept(self) -> None:
        self._cleanup()
        super().accept()
