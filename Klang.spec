# -*- mode: python ; coding: utf-8 -*-


a = Analysis(
    ['run_qt6_player.py'],
    pathex=[],
    binaries=[],
    datas=[('qt6_player/assets', 'qt6_player/assets')],
    hiddenimports=[
        'qframelesswindow', 'win32gui', 'win32api', 'win32con', 'mutagen',
        'scipy.signal', 'qt6_player.services.headphone_eq',
        'pyncm', 'pyncm.apis', 'pyncm.apis.login', 'pyncm.apis.track', 'pyncm.apis.playlist', 'pyncm.apis.user',
        'qrcode', 'PIL', 'Crypto',
        'qt6_player.services.netease_service', 'qt6_player.components.netease_login_dialog'
    ],
    hookspath=[],
    hooksconfig={},
    runtime_hooks=[],
    excludes=[],
    noarchive=False,
    optimize=0,
)
pyz = PYZ(a.pure)

exe = EXE(
    pyz,
    a.scripts,
    a.binaries,
    a.datas,
    [],
    name='Klang',
    debug=False,
    bootloader_ignore_signals=False,
    strip=False,
    upx=True,
    upx_exclude=[],
    runtime_tmpdir=None,
    console=False,
    disable_windowed_traceback=False,
    argv_emulation=False,
    target_arch=None,
    codesign_identity=None,
    entitlements_file=None,
    icon=['qt6_player/assets/klang_icon.ico'],
)
