import sys
from pathlib import Path
PROJECT_ROOT = Path(__file__).resolve().parent
if str(PROJECT_ROOT) not in sys.path:
    sys.path.insert(0, str(PROJECT_ROOT))

# 在 Windows 平台显式注册 AppUserModelID 与单实例互斥锁，彻底杜绝多开冲突
_instance_mutex = None
if sys.platform == "win32":
    try:
        import ctypes
        ctypes.windll.shell32.SetCurrentProcessExplicitAppUserModelID("Klang.MusicPlayer")
        
        # 单实例检测：创建 Win32 命名互斥体
        _MUTEX_NAME = "Local\\Klang.MusicPlayer.SingleInstance.Mutex"
        kernel32 = ctypes.windll.kernel32
        user32 = ctypes.windll.user32
        _instance_mutex = kernel32.CreateMutexW(None, False, _MUTEX_NAME)
        if kernel32.GetLastError() == 183:  # ERROR_ALREADY_EXISTS
            hwnd = user32.FindWindowW(None, "Klang")
            if hwnd:
                user32.ShowWindow(hwnd, 9)  # SW_RESTORE = 9
                user32.SetForegroundWindow(hwnd)
            sys.exit(0)
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
