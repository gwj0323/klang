<div align="center">
  <img src="qt6_player/assets/klang_icon.png" width="96" height="96" alt="Klang Icon" />
  <h1>Klang</h1>
  <p>轻量、现代的 Windows 本地与网易云无损音乐播放器</p>
</div>

---

Klang 是一款基于 PyQt6 的 Windows 桌面音乐播放器。支持本地无损音频播放与网易云音乐账号同步，原生适配 Windows 11 Mica / Acrylic 材质，提供类似 Apple Music 的全屏动态流动歌词背景。

已编译的单文件安装包可在 [GitHub Releases](https://github.com/gwj0323/klang/releases) 下载。

---

## 功能特性

### 本地播放与曲库
- **格式支持**：FLAC、WAV、APE、MP3、M4A、AAC、OGG、WMA 等常见音频格式，支持 CUE 分轨解析，可读取内嵌封面与元数据。
- **WASAPI 缓冲优化**：针对 Windows 桌面切换与后台运行优化环形缓冲区，减少切窗、锁屏或高负载时的卡顿，支持毫秒级拖拽即播。
- **曲库管理**：支持扫描本地文件夹、自定义歌单、按歌手 / 专辑 / 目录树分类浏览与实时过滤。

### 网易云音乐集成
- **扫码登录**：支持客户端扫码登录，Cookie 本地加密保存（Windows 使用 DPAPI，仅当前 Windows 用户可解密），启动时自动恢复会话。
- **歌单同步**：同步「我喜欢的音乐」、个人创建与收藏的歌单以及每日推荐。
- **在线搜索**：切到网易云页面后搜索框自动支持云端曲库搜索，即点即播。
- **多音质与离线缓存**：支持在标准、极高、无损 FLAC、Hi-Res 与超清母带间切换；支持设定本地缓存上限（500MB ~ 5GB 或不限），按 LRU 自动清理过期缓存。注意：音质等级（含 Hi-Res / 超清母带）指下载的音源规格，播放管线目前统一解码为 44.1 kHz / 16-bit PCM 再输出，高采样率不会原样到达声卡。

### 界面与沉浸模式
- **系统材质**：支持 Windows 11 Mica（云母）、Acrylic（亚克力）及经典纯色背景，支持深色 / 浅色主题切换。
- **动态流动背景**：沉浸模式下基于封面 k-means 聚类提取主色，通过 OpenGL 着色器实时计算多层流动网格渐变，并随低频能量呼吸律动。
- **歌词排版**：支持双语歌词、逐字卡拉OK动效与日文假名/罗马音注音；可一键生成歌词海报卡片。
- **多种视图形态**：常规窗口、全屏沉浸（支持大封面与极简黑胶唱盘模式）、迷你悬浮窗与置顶桌面歌词。
- **系统级集成**：支持 Windows SMTC 媒体按键联动、任务栏悬停缩略图快捷控件（上一首 / 播放暂停 / 下一首），适配高分屏 Per-Monitor DPI。

### 声音调节与 DSP
- **耳机校准 (AutoEq)**：集成开源 AutoEq 数据库，内置主流耳机型号（HD600/650/800S、WH-1000XM4/XM5、AirPods Pro/Max、Bose 等）的频响补偿预设。
- **人声 / 伴奏无极调节**：基于 Mid/Side 声道频域分离算法，支持实时淡化人声做伴奏，或凸显人声细节。
- **黑胶模拟声效**：可选黑胶唱盘切电起停惯性滑音、黑胶底噪与温暖度染色。
- **均衡器与混响**：内置 5 段参量均衡器与立体声空间混响调节。

---

## 快捷键

| 按键 | 功能 |
| :--- | :--- |
| `Space` | 播放 / 暂停 |
| `←` / `→` | 快退 5 秒 / 快进 5 秒 |
| `Ctrl + ←` / `Ctrl + →` | 上一首 / 下一首 |
| `↑` / `↓` | 调节音量 |
| `F11` | 切换全屏沉浸模式 |
| `Ctrl + M` | 切换迷你悬浮窗 |
| `Ctrl + D` | 开关桌面歌词 |
| `Ctrl + F` 或 `/` | 聚焦搜索框（支持本地与网易云搜索） |
| `Ctrl + +` / `Ctrl + -` | 放大 / 缩小歌词字号 |

---

## 运行与构建

### 环境要求
- Windows 10 / 11 (64-bit)
- Python 3.10+

### 从源码运行

推荐使用 `uv`（项目目前没有 `pyproject.toml`，用 requirements.txt 安装依赖）：
```bash
git clone https://github.com/gwj0323/klang.git
cd klang
uv venv
uv pip install -r requirements.txt
uv run python run_qt6_player.py
```

或使用普通 `pip`：
```bash
git clone https://github.com/gwj0323/klang.git
cd klang
pip install -r requirements.txt
python run_qt6_player.py
```

### 本地打包
使用 PyInstaller 打包单文件可执行文件（输出到 `dist/Klang.exe`）：
```bash
uv run pyinstaller Klang.spec --noconfirm
```

---

## 开源引用与致谢

| 项目名称 | 开源许可证 | 来源 | 使用场景 |
| :--- | :--- | :--- | :--- |
| **PyNCM** | GPL-3.0 | [greats3an/pyncm](https://github.com/greats3an/pyncm) | 网易云音乐 API 解析、会话管理与音频直链换取 |
| **PyQt-Frameless-Window** | LGPL-3.0 | [zhiyiYo/PyQt-Frameless-Window](https://github.com/zhiyiYo/PyQt-Frameless-Window) | Windows 11 无边框窗口与 Mica / Acrylic 材质支持 |
| **AutoEq** | MIT | [jaakkopasanen/AutoEq](https://github.com/jaakkopasanen/AutoEq) | 耳机频响校准数据与哈曼曲线补偿滤波参数 |
| **mutagen** | GPL-2.0-or-later | [quodlibet/mutagen](https://github.com/quodlibet/mutagen) | 本地音频标签、专辑封面与元数据解析 |
| **python-qrcode** | BSD-3-Clause | [lincolnloop/python-qrcode](https://github.com/lincolnloop/python-qrcode) | 网易云扫码登录二维码生成 |
| **Pillow** | HPND | [python-pillow/Pillow](https://github.com/python-pillow/Pillow) | 封面图像缩放、模糊与色块图生成 |
| **NumPy & SciPy** | BSD-3-Clause | [numpy/numpy](https://github.com/numpy/numpy), [scipy/scipy](https://github.com/scipy/scipy) | DSP 滤波、双二阶滤波器计算、Mid/Side 声道运算与颜色聚类 |
| **PyQt6** | GPL-3.0 | [Riverbank Computing](https://www.riverbankcomputing.com/software/pyqt/) | GUI 框架与底层音频输出管道 |

---

## 开源协议

本项目采用 [GNU General Public License v3.0 (GPL-3.0)](LICENSE) 协议开源。
