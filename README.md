<div align="center">
  <img src="Klang.WinUI/Assets/AppLogo.png" width="96" height="96" alt="Klang" />
  <h1>Klang</h1>
  <p>面向 Windows 的本地无损音乐播放器，集成网易云音乐</p>

  [![Release](https://img.shields.io/github/v/release/gwj0323/klang?label=release&color=blue)](https://github.com/gwj0323/klang/releases)
  [![License](https://img.shields.io/badge/license-GPL--3.0-green)](LICENSE)
  ![Platform](https://img.shields.io/badge/platform-Windows%2010%20%2F%2011%20x64-lightgrey)
</div>

---

Klang 是一款 Windows 桌面音乐播放器，支持本地音频文件和网易云音乐。

当前版本基于 WinUI 3 / .NET 10（`Klang.WinUI`）。早期的 PyQt6 版本保留在 `qt6_player`，见[文末](#pyqt6-旧版)。

## 名称与设计理念

Klang 取自丹麦语 klang，意为声音、音色、余音。

设计上遵循以下几点：

- 声音保持原样。默认不改动音频数据：按文件的原始采样率和位深解码，音效可以整体旁路，独占输出时不经过系统混音器。
- 处理过程可查看。信号路径面板列出每一级处理，比特完美有明确的判定条件；音质分析给出频谱，由使用者自己判断。
- 界面从简。使用系统材质与主题，功能按曲库、播放、歌词分层，需要时才出现。
- 数据留在本地。曲库、播放统计和设置只保存在本机，联网功能可以关闭。
- 自动行为给出依据。漫游选歌会显示理由；自动混音只在速度和节拍条件满足时进行，不满足就照常切歌。
## 下载与安装

前往 [GitHub Releases](https://github.com/gwj0323/klang/releases) 下载最新版本（标签以 `-winui` 结尾的为 WinUI 版）：

| 文件 | 说明 |
| :--- | :--- |
| `Klang-Setup-<版本>-x64.exe` | 安装版。默认安装到当前用户目录，无需管理员权限，可选择创建桌面快捷方式，支持卸载与覆盖升级 |
| `Klang-WinUI-win-x64.zip` | 绿色版。解压后运行 `Klang.WinUI.exe` |

- 系统要求：Windows 10 1809（17763）及以上，x64。应用自带运行时，无需另装 .NET。
- 安装包与程序目前未做代码签名，首次运行时 Windows SmartScreen 可能给出提示，选择“更多信息 → 仍要运行”即可。
- 用户数据保存在 `%LocalAppData%\Klang`，卸载不会删除。

## 功能

### 音频与信号路径
- 原始格式解码：基于 Media Foundation，按文件原始采样率与位深解码（实测可解码 24 bit / 192 kHz FLAC），支持 FLAC、WAV、ALAC、MP3、AAC、M4A、OGG、WMA 等常见格式以及 CUE 分轨。
- WASAPI 独占模式：按文件原始采样率输出，绕过 Windows 混音器；声卡不支持时自动回退到共享模式，并在信号路径面板中说明原因。
- 绕过全部音效：关闭均衡器、空间音效、混响和软限幅，音频原样输出。
- 信号路径面板：逐级显示源文件 → 解码 → 变速 → 音效 → 音量 → 输出，并判定当前是否满足比特完美的条件（独占输出、音效旁路、音量 100%、无变速、无回放增益、输出格式不低于源）。
- 变速不变调：0.5×–2.0×（WSOLA），回放增益（ReplayGain）。
- DSP：5 段均衡器、空间音效、混响、人声 / 伴奏平衡、黑胶质感；内置 AutoEq 耳机补偿预设。

### 音质与音乐分析
- 伪无损检测：分析平均频谱，识别 MP3 / AAC 转码、由 CD 音质升采样的 Hi-Res 文件以及位深填充，结果显示在曲库中，可按此筛选，并可在“音质分析”中查看频谱图。检测为启发式方法，结果仅供参考。
- 速度与调性：后台分析 BPM、拍点位置和调性（Camelot 编号）。
- 声学地图：将曲库按 10 维声学特征（响度、动态、频谱重心等）降维后绘制成二维图，点击即可从该曲开始播放；提供“播放相似歌曲”和按情绪（安静 / 专注 / 动感 / 明亮 / 沉稳）自动生成的智能歌单。

### 切歌与播放模式
- 列表循环、单曲循环、随机播放，以及漫游模式：根据收听历史（完整播放、跳过、顺序、时段等）学习偏好并选择下一首，学习数据保存在本地，可随时重置。
- 自动混音：歌曲临近结束时与下一首等功率交叉淡入淡出。两首速度相差 8% 以内时对齐节拍、匹配速度，并在中段做低频交接（同一时刻只保留一首歌的低音）；速度差过大或没有稳定节拍时，改为普通的交叉淡入淡出，并跳过结尾与开头的静音。混音时长可选短 / 标准 / 长，调性不相容时自动减半；开启回放增益时，增益随混音进度平滑过渡。可设为关闭 / 仅漫游模式 / 所有播放模式。网易云曲目在下一首预缓冲完成后同样参与混音；CUE 分轨不混音。

### 曲库管理
- 监视多个音乐文件夹，新增、修改、删除自动同步；元数据未变化的文件跳过解析，启动快。
- 按歌手 / 专辑 / 目录分组，按多种方式排序，多条件筛选（音质、缺封面、缺歌词、疑似伪无损、文件丢失等）。
- 标签编辑（单曲与整张专辑批量编辑，写回音频文件）、重复歌曲检查、丢失文件重新定位。
- 歌单、智能歌单（按音质、收藏、添加时间、播放次数、情绪等条件）、M3U / M3U8 导入与导出。

### 歌词
- 基于 Win2D 的歌词渲染：当前句放大、平滑滚动，支持逐字高亮（增强 LRC）、翻译与罗马音副行。
- 本地 `.lrc`、内嵌歌词、网易云、[LRCLIB](https://lrclib.net) 多个来源；联网获取的歌词缓存在本地。
- 歌词时间校准：整体提前或延后，也可点击“正在演唱的那一句”自动对齐，按歌曲分别保存。
- 桌面歌词、歌词海报生成。

### 网易云音乐
- 扫码或 Cookie 登录，Cookie 使用 Windows DPAPI 加密保存，仅当前 Windows 用户可解密。
- 同步“我喜欢的音乐”、个人与收藏的歌单、每日推荐；支持在线搜索与试听。
- 标准、极高、无损 FLAC、Hi-Res、超清母带等音质可选，不可用时自动降级；本地缓存有大小上限，超出后清除最久未播放的文件。

### 界面与系统集成
- Windows 11 Mica / Acrylic 背景，浅色 / 深色 / 跟随系统。
- 沉浸模式（随封面主色变化的动态背景）、迷你窗口、全屏。
- Windows 系统媒体控件（SMTC）与多媒体键。
- 界面语言：简体中文、English、Dansk；可在设置中检查 GitHub 上的新版本。

## 隐私与网络请求

Klang 仅在以下情况联网，均可在设置中控制或不使用相应功能：

| 功能 | 访问对象 | 发送的内容 |
| :--- | :--- | :--- |
| 自动补全封面与歌词（设置中可关闭） | 网易云音乐、LRCLIB | 歌名、歌手、专辑、时长 |
| 网易云账号与云端播放 | 网易云音乐 | 登录凭据与请求的曲目 |
| 检查更新 | GitHub | 无个人信息 |

曲库、播放统计和设置只保存在本机 `%LocalAppData%\Klang`。 完整说明见 [PRIVACY.md](PRIVACY.md)（简体中文 / English / Dansk）。

## 快捷键

| 按键 | 功能 |
| :--- | :--- |
| `Space` | 播放 / 暂停 |
| `←` / `→` | 快退 / 快进 5 秒 |
| `↑` / `↓` | 调节音量 |
| `Ctrl + ←` / `Ctrl + →` | 上一首 / 下一首 |
| `Ctrl + F` | 聚焦搜索框 |
| `Ctrl + I` | 沉浸模式（`Esc` 退出） |
| `Ctrl + M` | 迷你窗口 |
| `Ctrl + D` | 桌面歌词 |
| `Ctrl + T` | 切换歌词副行（翻译 / 罗马音） |
| `Ctrl + +` / `Ctrl + -` | 放大 / 缩小歌词字号 |
| `F11` | 全屏 |

## 从源码构建

环境：Windows 10 / 11 x64，[.NET 10 SDK](https://dotnet.microsoft.com/download)。

```bash
git clone https://github.com/gwj0323/klang.git
cd klang

# 运行
dotnet run --project Klang.WinUI -c Release

# 发布（自包含，win-x64）
dotnet publish Klang.WinUI/Klang.WinUI.csproj -c Release -r win-x64 -p:Platform=x64 -o publish/Klang.WinUI
```

生成安装包需要 [Inno Setup 6](https://jrsoftware.org/isinfo.php)：

```bash
ISCC.exe /DAppVersion=2.4.0 /DSourceDir=..\publish\Klang.WinUI installer\Klang.iss
```

输出位于 `dist-installer/`。仓库的 GitHub Actions 工作流（`.github/workflows/build-exe.yml`，手动触发）会同时产出绿色版压缩包与安装包。

### 目录结构

| 路径 | 内容 |
| :--- | :--- |
| `Klang.WinUI/` | WinUI 3 应用（当前主版本） |
| `Klang.WinUI/Services/` | 音频引擎、DSP、曲库、歌词、分析、网易云等服务 |
| `Klang.WinUI/Languages/` | 界面语言文件（`en.json`、`da.json`，以中文原文为键） |
| `installer/` | Inno Setup 安装脚本 |
| `qt6_player/`、`src/` | 早期的 PyQt6 版本 |

## PyQt6 旧版

早期版本基于 PyQt6，功能与 WinUI 版有差异（例如没有独占模式与信号路径面板）。源码保留在 `qt6_player`，不再积极开发，Release 中无 `-winui` 后缀的版本即为该版本。

```bash
# 需要 Python 3.10+
pip install -r requirements.txt
python run_qt6_player.py

# 打包为单文件（输出到 dist/Klang.exe）
pyinstaller Klang.spec --noconfirm
```

## 开源引用与致谢

### WinUI 版

| 项目 | 许可证 | 用途 |
| :--- | :--- | :--- |
| [Windows App SDK / WinUI 3](https://github.com/microsoft/WindowsAppSDK) | MIT | 界面框架 |
| [Win2D](https://github.com/microsoft/Win2D) | MIT | 歌词、频谱图与声学地图的绘制 |
| [NAudio](https://github.com/naudio/NAudio) | MIT | 音频解码、WASAPI 输出 |
| [TagLib#](https://github.com/mono/taglib-sharp) | LGPL-2.1 | 音频标签、封面与内嵌歌词读写 |
| [QRCoder](https://github.com/codebude/QRCoder) | MIT | 登录二维码生成 |
| [ComputeSharp](https://github.com/Sergio0694/ComputeSharp) | MIT | GPU 计算（动态背景） |
| [AutoEq](https://github.com/jaakkopasanen/AutoEq) | MIT | 耳机频响补偿数据 |
| [LRCLIB](https://lrclib.net) | — | 歌词数据来源（开放接口） |

### PyQt6 版

| 项目 | 许可证 | 用途 |
| :--- | :--- | :--- |
| [PyNCM](https://github.com/greats3an/pyncm) | GPL-3.0 | 网易云音乐 API |
| [PyQt-Frameless-Window](https://github.com/zhiyiYo/PyQt-Frameless-Window) | LGPL-3.0 | 无边框窗口与 Mica / Acrylic |
| [mutagen](https://github.com/quodlibet/mutagen) | GPL-2.0-or-later | 音频标签与封面 |
| [python-qrcode](https://github.com/lincolnloop/python-qrcode) | BSD-3-Clause | 登录二维码 |
| [Pillow](https://github.com/python-pillow/Pillow) | HPND | 图像处理 |
| [NumPy](https://github.com/numpy/numpy) / [SciPy](https://github.com/scipy/scipy) | BSD-3-Clause | DSP 与颜色聚类 |
| [PyQt6](https://www.riverbankcomputing.com/software/pyqt/) | GPL-3.0 | GUI 框架 |

## 开源协议

本项目采用 [GNU General Public License v3.0](LICENSE) 开源。
