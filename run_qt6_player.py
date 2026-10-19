"""
KLANG · Nordic Minimalist WinUI 3 Music Player (Qt6 Edition)
启动入口脚本：直接运行 `py run_qt6_player.py` 即可启动原生 Qt6 桌面应用。
"""

import sys
from PyQt6.QtCore import Qt
from PyQt6.QtWidgets import QApplication

from qt6_player.main_window import NordicWinUI3Window


def main() -> int:
    QApplication.setHighDpiScaleFactorRoundingPolicy(
        Qt.HighDpiScaleFactorRoundingPolicy.PassThrough
    )
    app = QApplication(sys.argv)
    app.setApplicationName("Klang")

    from pathlib import Path
    from PyQt6.QtGui import QIcon
    icon_path = Path(__file__).resolve().parent / "qt6_player" / "assets" / "klang_icon.ico"
    if not icon_path.exists():
        icon_path = Path(__file__).resolve().parent / "qt6_player" / "assets" / "klang_icon.png"
    if icon_path.exists():
        app.setWindowIcon(QIcon(str(icon_path)))

    window = NordicWinUI3Window()
    window.show()
    return app.exec()


if __name__ == "__main__":
    sys.exit(main())
