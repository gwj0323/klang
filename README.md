<div align="center">
  <img src="qt6_player/assets/klang_icon.png" width="96" height="96" alt="Klang Icon" />
  <h1>Klang</h1>
  <p>基于 PyQt6 的 Windows 本地音乐播放器</p>
</div>

---

Klang（德语 / 丹麦语中意为「声音、音色」）是一个轻量的 Windows 本地音乐播放器。做这个项目的初衷很简单：平时听本地无损音乐时，希望有一个界面干净、不塞满广告和社交功能、歌词滚动顺眼、同时能直接调用系统硬件材质的播放器。

直接使用的用户可以在 [Releases](https://github.com/gwj0323/klang/releases) 页面下载打包好的 `Klang.exe` 单文件版。

## 主要功能

### 界面与窗口形态
- **Windows 11 原生材质**：支持在「标准纯色」「Win11 云母 (Mica)」「亚克力 (Acrylic)」三种窗口背景间实时切换，所有交互药丸与控制按钮自适应半透明晶体玻璃质感，提供浅色 / 深色两套主题。
- **四种展示模式**：常规主窗口、全屏沉浸模式（支持封面或 B&O 风格黑胶唱机样式）、迷你悬浮窗、置顶桌面歌词。
- **120 FPS 极速高刷与极光呼吸**：沉浸模式支持 60 / 120 / 144 / 240 FPS 高帧率渲染；配备低频能量滤波器联动（Sub-Bass Aurora），在鼓点响起时背景光晕如真实空气般呼吸律动。
- **系统级集成**：接入 Windows SMTC 系统媒体控制（支持键盘多媒体键与系统音量弹窗联动）及托盘常驻控制。

### 歌词体验
- 支持 `.lrc` 本地歌词解析与在线双语歌词自动匹配。
- 支持逐字推进、双语译文开关、歌词时间轴偏移微调与字号缩放。
- 点击任意歌词行可直接跳转播放进度。
- **歌词卡片生成**：选中 2~4 句歌词可一键导出或复制封面歌词卡片图片。

### 专业声学 DSP 与特色玩法
- **知名耳机声学硬件校准 (AutoEq)**：集成开源 AutoEq 声学参数库（覆盖 Sennheiser HD600/HD650/HD800S、Sony WH-1000XM4/XM5、Apple AirPods Pro/Max、Bose、Beyerdynamic 等主流型号），一键加载哈曼听音曲线补偿滤镜拉平频响缺陷。
- **人声 ⟷ 伴奏 无极天平**：基于实时中侧声道（Mid/Side）频域掩蔽算法打造无极滑块，向左拉隐去人声将任意流行歌实时变为专注纯音乐 BGM，向右拉突出清唱细节。
- **黑胶物理起停与模拟味 (Turntable Physics)**：复刻真实黑胶唱盘切断电源时的 0.35s 自然降速降调惯性起停曲线，并支持叠加温润的黑胶接触底噪（Vinyl Crackle）与电子管偶次谐波（Tube Warmth）。
- **离线声学 DNA 情绪漫游 (Smart Acoustic Flow)**：完全离线分析曲目的节拍（BPM）、冷暖色彩与能量密度，开启漫游后下一首自动挑选情绪节奏最契合的曲目，告别随机播放割裂感。
- **音频格式与采样率**：完整支持 `FLAC`、`WAV`、`MP3`、`M4A`、`AAC`、`OGG`、`WMA` 及 `CUE` 分轨，原生识别并呈现采样率、位深与码率。
- 内置 5 段参量均衡器（EQ）、空间立体声 / 虚拟全景声混响渲染。

## 本地运行与打包

环境要求：Windows 10 / 11，Python 3.10+。

使用 `uv` 运行：

```bash
git clone https://github.com/gwj0323/klang.git
cd klang
uv sync
uv run python run_qt6_player.py
```

或者使用传统 `pip`：

```bash
pip install -r requirements.txt
python run_qt6_player.py
```

打包独立可执行文件（生成在 `dist/Klang.exe`）：

```bash
uv run pyinstaller Klang.spec --noconfirm
```

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
| `Ctrl + F` 或 `/` | 聚焦曲库搜索框 |
| `Ctrl + +` / `Ctrl + -` | 放大 / 缩小歌词字号 |

## 依赖组件

- **GUI**：[PyQt6](https://www.riverbankcomputing.com/software/pyqt/)、[PyQt-Frameless-Window](https://github.com/zhiyiYo/PyQt-Frameless-Window)
- **音频与 DSP**：`QtMultimedia`、[NumPy](https://numpy.org/)、[SciPy](https://scipy.org/)
- **音频标签解析**：[mutagen](https://github.com/quodlibet/mutagen)

## 开源协议

[MIT License](LICENSE)
