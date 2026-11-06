import sys
from pathlib import Path
PROJECT_ROOT = Path(__file__).resolve().parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

# 在 Windows 平台显式注册 AppUserModelID，使 Windows 任务栏与 SMTC 媒体浮窗正确绑定 Klang 应用图标与名称
if sys.platform == "win32":
    try:
        import ctypes
        ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID("Klang.MusicPlayer")
    except Exception:
        pass

from PyQt6.QtCore import Qt
from PyQt6.QtGui import QIcon
from PyQt6.QtWidgets import QApplication

from qt6_player.theme.nordic_palette import apply_apple_sans_globally
from qt6_player.main_window import NordicWinUI3Window


def main() -> int:
    QApplication.setHighDpiScaleFactorRoundingPolicy(
        Qt.HighDpiScaleFactorRoundingPolicy.PassThrough
    )
    app = QApplication(sys.argv)
    app.setApplicationName("Klang")

    # 全局生效 Apple Sans (SF Pro Display / SF Mono) 字体体系
    apply_apple_sans_globally(app)

    if getattr(sys, "frozen", False) and hasattr(sys, "_MEIPASS"):
        base_dir = Path(sys._MEIPASS)
    else:
        base_dir = Path(__file__).resolve().parent

    icon_path = base_dir / "qt6_player" / "assets" / "klang_icon.ico"
    if not icon_path.exists():
        icon_path = base_dir / "qt6_player" / "assets" / "klang_icon.png"
    if icon_path.exists():
        app.setWindowIcon(QIcon(str(icon_path)))

    window = NordicWinUI3Window()
    app.aboutToQuit.connect(window._save_current_state)
    window.show()
    return app.exec()


if __name__ == "__main__":
    sys.exit(main())
