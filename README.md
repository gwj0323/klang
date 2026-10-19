<div align="center">
  <img src="qt6_player/assets/klang_icon.png" width="96" height="96" alt="Klang Icon" />
  <h1>Klang</h1>
  <p>一款北欧极简风格的 Windows 本地音乐播放器，基于 PyQt6 与 WinUI 3 设计语言。</p>
</div>

---

Klang（丹麦语 / 德语中意为「声音、音色」）最初是为了解决几个日常听歌的痛点而写：想要一个界面足够安静、不打扰人的本地播放器；想要类似流体般的平滑歌词滚动；并且在调节均衡器和音效时不会出现恼人的爆音与电平削波。

## 特性

### 界面与交互
- 采用 WinUI 3 风格组件与北欧低饱和度色彩体系（晨雾白、板岩灰、鼠尾草绿）。
- 顶底对称圆角与 1px 亚克力微描边，支持无边框八向窗口拖拽缩放。
- 支持晨雾浅色 / 极夜深色主题切换。

### 歌词动效
- 基于临界阻尼弹簧物理引擎实现的 60 FPS 平滑滚动，消除逐行切换时的生硬瞬移。
- 视窗上下边缘透明度渐变羽化，支持双语歌词对照与逐字柔和推进。
- 点击任意单行歌词快速跳转播放进度，支持歌词字号实时微调（`A-` / `A+`）。
- 附带独立置顶的极简桌面歌词悬浮窗。

### 音频与 DSP 引擎
- **连续硬件拉流设备（`ContinuousDspStreamDevice`）**：单通道 QAudioSink 持续拉取 PCM 块，调节 5 段 EQ 或混响滑块时声卡硬件不断流、零中断爆音。
- **状态保持双二阶滤波**：采用 SciPy `sosfilt` 严格保持块间延迟状态（$z_i$），频段增益变更时执行历史样本预热与 2048 样本余弦交叉淡化（Cosine Crossfade）。
- **动态防削波前置余量衰减（Auto-Headroom）**：针对 0 dBFS 高响度商业音频，根据正向低频增益动态前置衰减，结合 Soft-knee 柔膝限幅器，防止出现低频破音与电平失真。
- 内置 5 种调音方案：原声自然、北欧厅堂、温暖人声、深沉低音、清澈器乐。

### 音乐库与元数据
- 支持常见本地音频格式：MP3、FLAC、WAV、AAC、OGG、M4A。
- 支持直接拖拽单曲或整个文件夹批量导入。
- 内置在线刮削服务：根据 ID3 标签静默匹配双语 LRC 歌词与高清封面，亦可手动搜索替换。
- 本地持久化记录播放列表、播放进度、音量及自定义偏好。

## 运行

确保本地已安装 Python 3.9+：

```bash
git clone https://github.com/gwj0323/klang.git
cd klang
pip install -r requirements.txt
python run_qt6_player.py
```

## 快捷键

| 按键 | 功能 |
| :--- | :--- |
| `Space` | 播放 / 暂停 |
| `←` / `→` | 快退 5 秒 / 快进 5 秒 |
| `↑` / `↓` | 调节音量 |
| `Ctrl + 滚轮` | 缩放歌词字号 |
| 拖拽音频文件 | 拖入窗口自动加入播放列表 |

## 技术栈

- **GUI 框架**：[PyQt6](https://www.riverbankcomputing.com/software/pyqt/)
- **音频架构**：QtMultimedia (`QAudioSink` / `QMediaPlayer` / `QAudioDecoder`)
- **信号处理**：[SciPy](https://scipy.org/) (`scipy.signal.sosfilt`), [NumPy](https://numpy.org/)
- **标签解析**：[mutagen](https://github.com/quodlibet/mutagen)
- **图像处理**：Pillow

## 开源协议

[MIT License](LICENSE)
