<div align="center">
  <img src="qt6_player/assets/klang_icon.png" width="96" height="96" alt="Klang Icon" />
  <h1>Klang</h1>
  <p>基于 PyQt6 的 Windows 本地与网易云无损音乐播放器</p>
</div>

---

Klang（德语 / 丹麦语中意为「声音、音色」）是一个运行在 Windows 平台上的现代音乐播放器。最初为了在本地听无损音乐时拥有一个纯净、无广告、歌词滚动顺滑且能完美调用系统材质的播放器而写；现已扩展支持网易云音乐同步与云端无损流媒体播放。

用户可以在 [Releases](https://github.com/gwj0323/klang/releases) 页面直接下载已编译打包的单文件版本 `Klang.exe`。

---

## 主要功能

### 1. 本地曲库与音频播放
- **广泛的格式支持**：支持 `FLAC`、`WAV`、`APE`、`MP3`、`M4A`、`AAC`、`OGG`、`WMA` 及 `CUE` 分轨，识别展示真实采样率、位深与码率。
- **WASAPI 256KB 抗欠载环形缓冲**：重构音频输出缓冲机制，彻底解决系统切换窗口、Alt+Tab 或后台调度时的爆音与断流；配合声卡输出硬件延迟智能补偿，保证歌词滚动毫秒级同步。
- **0ms 定位即播**：进度条跳转瞬间重置声卡硬件缓冲，消除拖拽滞后。
- **曲库管理**：支持自定义歌单创建、按歌手 / 专辑 / 文件夹自动分组折叠、多维度排序与本地曲库监控。

### 2. 网易云音乐深度集成
- **扫码免密登录**：支持客户端直接扫码登录，Cookie 与凭据安全保存在本地，启动时自动恢复会话。
- **歌单与推荐同步**：自动同步「我喜欢的音乐」、用户自建与收藏的歌单以及「每日推荐」。
- **云端曲库在线搜索**：切到网易云标签页后，搜索栏自动切换为在线搜索模式，内置 400ms 防抖，支持直接搜索云端歌曲并即点即播；清空搜索框无缝还原原歌单。
- **VIP 无损与超清母带**：支持在标准 128k、极高 320k、无损 FLAC、Hi-Res 与超清母带之间自由切换，具备直链智能降级回退机制。
- **本地缓存配额管理 (LRU)**：设置中可自由配置缓存上限（500MB ~ 5GB 或不限），当占用超出时按最近最少使用原则自动清理最旧音频，并提供一键清空缓存按钮。
- **双入口账号退出**：在左侧工具栏与设置面板均提供便捷的「退出登录」按钮。

### 3. 专业声学 DSP 与特色玩法
- **知名耳机声学硬件校准 (AutoEq)**：集成开源 AutoEq 声学参数库（覆盖 Sennheiser HD600/HD650/HD800S、Sony WH-1000XM4/XM5、Apple AirPods Pro/Max、Bose、Beyerdynamic 等主流型号），一键加载哈曼听音曲线补偿滤镜拉平频响缺陷。
- **人声 ⟷ 伴奏 无极天平**：基于实时中侧声道（Mid/Side）频域掩蔽算法打造无极滑块，向左拉隐去人声将任意流行歌实时变为纯音乐伴奏，向右拉突出清唱人声细节。
- **黑胶物理起停与模拟味 (Turntable Physics)**：复刻黑胶唱盘切断电源时的 0.35s 自然降速降调惯性滑音与起动扭矩曲线，并支持叠加温润的黑胶接触底噪（Vinyl Crackle）与电子管偶次谐波（Tube Warmth）。
- **离线声学 DNA 情绪漫游 (Smart Acoustic Flow)**：完全离线分析曲目的节拍（BPM）、冷暖色彩与能量密度，开启漫游后下一首自动挑选情绪节奏最契合的曲目，告别随机播放割裂感。
- **均衡器与混响**：内置 5 段参量均衡器（EQ）与空间立体声混响算法。

### 4. 界面形态与交互
- **Windows 11 原生材质**：支持在「标准纯色」「Win11 云母 (Mica)」「亚克力 (Acrylic)」三种背景材质间实时热切换，弹窗与菜单自适应半透明晶体玻璃质感，提供浅色 / 深色主题。
- **四种展示模式**：常规主窗口、全屏沉浸模式（支持专辑大图或 B&O 极简黑胶唱盘）、迷你悬浮窗、置顶桌面歌词。
- **120 FPS 极速高刷与极光呼吸**：沉浸模式支持 60 / 120 / 144 / 240 FPS 高帧率渲染；配备低频能量滤波器联动（Sub-Bass Aurora），在鼓点响起时背景光晕呼吸律动。
- **歌词卡片生成**：选中 2~4 句歌词可一键导出或复制封面歌词卡片图片。
- **系统级集成**：接入 Windows SMTC 系统媒体控制（支持键盘多媒体键与系统音量弹窗联动）及托盘常驻控制。

---

## 本地运行与打包

### 环境要求
- Windows 10 / 11 (64-bit)
- Python 3.10+

### 安装运行

使用 `uv`（推荐）：
```bash
git clone https://github.com/gwj0323/klang.git
cd klang
uv sync
uv run python run_qt6_player.py
```

或者使用传统 `pip`：
```bash
git clone https://github.com/gwj0323/klang.git
cd klang
pip install -r requirements.txt
python run_qt6_player.py
```

### 打包可执行文件
通过 PyInstaller 打包单文件可执行程序（生成在 `dist/Klang.exe`）：
```bash
uv run pyinstaller Klang.spec --noconfirm
```

---

## 常用快捷键

| 按键 | 功能 |
| :--- | :--- |
| `Space` | 播放 / 暂停 |
| `←` / `→` | 快退 5 秒 / 快进 5 秒 |
| `Ctrl + ←` / `Ctrl + →` | 上一首 / 下一首 |
| `↑` / `↓` | 调节音量 |
| `F11` | 切换全屏沉浸模式 |
| `Ctrl + M` | 切换迷你悬浮窗 |
| `Ctrl + D` | 开关桌面歌词 |
| `Ctrl + F` 或 `/` | 聚焦搜索框（支持本地与网易云在线检索） |
| `Ctrl + +` / `Ctrl + -` | 放大 / 缩小歌词字号 |

---

## 开源引用与致谢 (Third-Party Notices)

Klang 的实现借鉴和使用了以下优秀的开源项目，特此致谢：

| 项目名称 | 开源许可证 | 仓库 / 来源 | 说明与使用场景 |
| :--- | :--- | :--- | :--- |
| **PyNCM** | GPL-3.0 | [greats3an/pyncm](https://github.com/greats3an/pyncm) | 网易云音乐 WeAPI 加密通道解析、登录会话管理与音频直链换取 |
| **PyQt-Frameless-Window** | LGPL-3.0 | [zhiyiYo/PyQt-Frameless-Window](https://github.com/zhiyiYo/PyQt-Frameless-Window) | Windows 11 无边框窗口基类、原生 Snap Layouts 与云母 (Mica) / 亚克力 (Acrylic) 硬件模糊支持 |
| **AutoEq** | MIT | [jaakkopasanen/AutoEq](https://github.com/jaakkopasanen/AutoEq) | 耳机硬件声学校准频响补偿数据与哈曼曲线（Harman Target）目标滤波参数 |
| **mutagen** | GPL-2.0-or-later | [quodlibet/mutagen](https://github.com/quodlibet/mutagen) | 本地音频标签、专辑封面与内嵌元数据提取 |
| **python-qrcode** | BSD-3-Clause | [lincolnloop/python-qrcode](https://github.com/lincolnloop/python-qrcode) | 网易云扫码登录二维码生成 |
| **Pillow** | HPND | [python-pillow/Pillow](https://github.com/python-pillow/Pillow) | 专辑封面图像处理、裁切与模糊渲染 |
| **NumPy & SciPy** | BSD-3-Clause | [numpy/numpy](https://github.com/numpy/numpy), [scipy/scipy](https://github.com/scipy/scipy) | 实时音频 DSP 滤波、双二阶滤波器计算、中侧声道运算与 FFT 频谱分析 |
| **PyQt6** | GPL-3.0 | [Riverbank Computing](https://www.riverbankcomputing.com/software/pyqt/) | Qt6 Python 绑定与底层多媒体音频输出管道 |

---

## 开源协议

本项目采用 [GNU General Public License v3.0 (GPL-3.0)](LICENSE) 协议开源。
