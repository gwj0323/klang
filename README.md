<div align="center">
  <img src="Klang.WinUI/Assets/AppLogo.png" width="96" height="96" alt="Klang" />
  <h1>Klang</h1>
  <p>面向 Windows 的本地无损音乐播放器，集成网易云音乐与 QQ 音乐</p>

  [![Release](https://img.shields.io/github/v/release/gwj0323/klang?label=release&color=blue)](https://github.com/gwj0323/klang/releases)
  [![License](https://img.shields.io/badge/license-GPL--3.0-green)](LICENSE)
  ![Platform](https://img.shields.io/badge/platform-Windows%2010%20%2F%2011%20x64-lightgrey)
  [![Contributors](https://img.shields.io/github/contributors/gwj0323/klang?color=orange)](#贡献者)
</div>

---

Klang 是一款 Windows 桌面音乐播放器，支持本地音频文件，以及网易云音乐、QQ 音乐的云端曲库。

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
| `Klang-Setup-<版本>-x64.exe` | 安装版。默认安装到当前用户目录，无需管理员权限，可选择创建桌面快捷方式，支持卸载与覆盖升级；安装或卸载时如果 Klang 正在运行，会提示关闭 |
| `Klang-WinUI-win-x64.zip` | 绿色版。解压后运行 `Klang.WinUI.exe` |

- 系统要求：Windows 10 1809（17763）及以上，x64。
- 需要先安装 [.NET 10 运行时](https://dotnet.microsoft.com/download/dotnet/10.0)（.NET Runtime 或 .NET Desktop Runtime 均可）；Windows App SDK 已随程序附带，无需另装。
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
- 自动混音：歌曲临近结束时与下一首等功率交叉淡入淡出。两首速度相差 8% 以内时对齐节拍、匹配速度，并在中段做低频交接（同一时刻只保留一首歌的低音）；速度差过大或没有稳定节拍时，改为普通的交叉淡入淡出，并跳过结尾与开头的静音。混音时长可选短 / 标准 / 长，调性不相容时自动减半；开启回放增益时，增益随混音进度平滑过渡。可设为关闭 / 仅漫游模式 / 所有播放模式。云端曲目在下一首预缓冲完成后同样参与混音；CUE 分轨不混音。

### 曲库管理
- 监视多个音乐文件夹，新增、修改、删除自动同步；元数据未变化的文件跳过解析，启动快。
- 按歌手 / 专辑 / 目录分组，按多种方式排序，多条件筛选（音质、缺封面、缺歌词、疑似伪无损、文件丢失等）。
- 标签编辑（单曲与整张专辑批量编辑，写回音频文件）、重复歌曲检查、丢失文件重新定位。
- 歌单、智能歌单（按音质、收藏、添加时间、播放次数、情绪等条件）、M3U / M3U8 导入与导出。

### 歌词
- 基于 Win2D 的歌词渲染：当前句放大、平滑滚动，支持逐字高亮（增强 LRC）、翻译与罗马音副行。
- 本地 `.lrc`、内嵌歌词、网易云、[LRCLIB](https://lrclib.net) 多个来源，QQ 音乐曲目使用 QQ 音乐的歌词；联网获取的歌词缓存在本地。
- 歌词时间校准：整体提前或延后，也可点击“正在演唱的那一句”自动对齐，按歌曲分别保存。
- 桌面歌词、歌词海报生成。

### AI 功能（可选，默认关闭）
在“设置 → AI 功能”中开启。所有模型都在本机运行，不上传任何音频或数据；模型按功能分别下载，不用的可以不装。有显卡时通过 DirectML 用显卡计算（NVIDIA / AMD / Intel 均可），没有时用 CPU；推理引擎（ONNX Runtime + DirectML，来自 Windows App SDK 的 Windows ML）随程序附带，不用另外下载。推理在独立的低优先级子进程中进行，不影响播放；任务做完会释放模型，子进程空闲后自动退出。

| 组件 | 模型 | 用途 |
| :--- | :--- | :--- |
| 听感分析 | LAION CLAP（music & speech） | 用一句话描述找歌（如“下雨天在咖啡馆听的爵士”）；自动标注流派、情绪、乐器、人声；声学地图、漫游、相似歌曲改用 AI 听感向量 |
| 歌词搜索 | multilingual-E5-small | 按意思搜索歌词，不需要记得原词，支持多语言；点击结果从那一句开始播放 |
| 人声分离 | MDX-Net（UVR Inst HQ 3） | K 歌模式：播放去掉人声的伴奏，可调保留的人声比例（0–100%），本地与云端歌曲均可；边分离边播放，开启后约 2 秒切换为伴奏，拖动进度时优先分离新位置。伴奏与人声可分别导出为 WAV |
| 歌词识别 | Whisper small | 为没有歌词的歌曲识别带时间轴的歌词；已有歌词时自动校准整体偏移 |

- 高潮连播：后台找出每首歌的高潮段落（重复最多、最响的一段，有时间轴歌词时对齐到副歌第一句），连播时每首只放高潮并交叉淡入下一首的高潮；右键菜单可“从高潮开始播放”。找高潮不使用模型，随“听感分析”一起启用。
- AI 任务的进度显示在标题栏，点击可以取消。
- NPU：检测到 NPU 的电脑可以在“计算设备”里选“后台分析用 NPU”，听感分析和歌词搜索改在 NPU 上运行（省电、不占显卡），人声分离和歌词识别仍用显卡。第一次使用时由 Windows ML 下载对应厂商的执行器（Intel OpenVINO / AMD Ryzen AI / 高通 QNN），不可用时自动退回显卡或 CPU。此功能尚未在实际的 NPU 设备上测试。
- 组件大小：使用显卡时全部约 900 MB，仅用 CPU 时约 630 MB。模型从 Hugging Face 和 GitHub 的官方地址下载，保存在 `%LocalAppData%\Klang\ai`。

### 云端音乐
云端页面顶部可以在网易云音乐和 QQ 音乐之间切换。两者都使用非官方接口，平台调整后可能暂时失效；会员歌曲与高音质需要账号有对应会员。

- 网易云音乐：扫码或 Cookie 登录；同步“我喜欢的音乐”、个人与收藏的歌单、每日推荐；标准、极高、无损 FLAC、Hi-Res、超清母带等音质可选。
- QQ 音乐：微信或 QQ 扫码登录；同步“我喜欢”与个人歌单、雷达推荐；标准 128k、高品质 320k、无损 FLAC 可选，不使用加密格式。
- 共同：支持在线搜索与试听；所选音质不可用时自动降级；登录凭据使用 Windows DPAPI 加密保存，仅当前 Windows 用户可解密；音频与封面只从对应平台的域名下载；本地缓存按平台分别设上限，超出后清除最久未播放的文件；重启后恢复上次播放的云端歌曲和队列。

### 任务栏播放条
- 在“视图”菜单开启（Ctrl+B）。支持透明背景、左右停靠、拖动吸附和空间避让；空间不足时显示紧凑布局，再不足则隐藏。点封面恢复主窗口。
- 使用 Windows 原生窗口与 UI Automation API，不注入或修改 Explorer。全屏、任务栏自动收起或竖向任务栏时隐藏；多显示器停靠位置和左右偏好会自动保存。

### 界面与系统集成
- Windows 11 Mica / Acrylic 背景，浅色 / 深色 / 跟随系统。
- 沉浸模式（随封面主色变化的动态背景）、迷你窗口、全屏。
- Windows 系统媒体控件（SMTC）与多媒体键。
- 界面语言：简体中文、English、Dansk；启动时自动检查 GitHub 上的新版本（可在设置中关闭）。

## 隐私与网络请求

Klang 仅在以下情况联网，均可在设置中控制或不使用相应功能：

| 功能 | 访问对象 | 发送的内容 |
| :--- | :--- | :--- |
| 自动补全封面与歌词（设置中可关闭） | 网易云音乐、LRCLIB | 歌名、歌手、专辑、时长 |
| 网易云账号与云端播放 | 网易云音乐 | 登录凭据与请求的曲目 |
| QQ 音乐账号与云端播放 | QQ 音乐；扫码时另访问 QQ 登录或微信开放平台 | 登录凭据与请求的曲目 |
| 天气效果（默认关闭） | Open-Meteo | 城市名；约 1 千米精度的经纬度 |
| 下载 AI 组件（默认关闭，手动下载） | Hugging Face、GitHub；选择“后台分析用 NPU”时，Windows ML 从 Microsoft 下载 NPU 执行器 | 无个人信息；模型下载后在本机运行，不上传音频或歌词 |
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
| `Klang.WinUI/Services/` | 音频引擎、DSP、曲库、歌词、分析等服务 |
| `Klang.WinUI/Services/Cloud/` | 云端来源接口；网易云（`Netease/`）、QQ 音乐（`QQMusic/`）各有一份实现 |
| `Klang.WinUI/Services/Ai/` | AI 功能：组件下载、推理子进程、模型前后处理、索引与高潮检测 |
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

## 贡献者

感谢每一位为 Klang 出力的人。

<div align="center">
<table>
  <tr>
    <td align="center" valign="top" width="150">
      <a href="https://github.com/gwj0323">
        <img src="https://github.com/gwj0323.png?size=200" width="88" height="88" alt="gwj0323" /><br />
        <sub><b>gwj0323</b></sub>
      </a><br />
      <sub>作者 · 维护</sub>
    </td>
    <td align="center" valign="top" width="150">
      <a href="https://github.com/Bigcheese233">
        <img src="https://github.com/Bigcheese233.png?size=200" width="88" height="88" alt="Bigcheese233" /><br />
        <sub><b>Bigcheese233</b></sub>
      </a><br />
      <sub>任务栏播放条 <a href="https://github.com/gwj0323/klang/pull/19">#19</a></sub>
    </td>
    <td align="center" valign="top" width="150">
      <a href="https://github.com/claude">
        <img src="https://github.com/claude.png?size=200" width="88" height="88" alt="claude" /><br />
        <sub><b>claude</b></sub>
      </a><br />
      <sub>开发协助</sub>
    </td>
  </tr>
</table>

<sub>完整名单见 <a href="https://github.com/gwj0323/klang/graphs/contributors">Contributors</a>。欢迎提交 <a href="https://github.com/gwj0323/klang/issues">Issue</a> 和 <a href="https://github.com/gwj0323/klang/pulls">Pull Request</a>。</sub>
</div>

## 开源引用与致谢

### WinUI 版

| 项目 | 许可证 | 用途 |
| :--- | :--- | :--- |
| [Windows App SDK / WinUI 3](https://github.com/microsoft/WindowsAppSDK) | MIT | 界面框架 |
| [Win2D](https://github.com/microsoft/Win2D) | MIT | 歌词、频谱图与声学地图的绘制 |
| [NAudio](https://github.com/naudio/NAudio) | MIT | 音频解码、WASAPI 输出 |
| [TagLib#](https://github.com/mono/taglib-sharp) | LGPL-2.1 | 音频标签、封面与内嵌歌词读写 |
| [QRCoder](https://github.com/codebude/QRCoder) | MIT | 登录二维码生成 |
| [PyNCM](https://github.com/greats3an/pyncm) | GPL-3.0 | 网易云音乐 WeAPI 加密与请求（移植为 C#） |
| [QQMusicApi](https://github.com/luren-dc/QQMusicApi)（luren-dc） | GPL-3.0 | QQ 音乐接口的请求格式、签名与扫码登录流程（参考后用 C# 重写） |
| [ComputeSharp](https://github.com/Sergio0694/ComputeSharp) | MIT | GPU 计算（动态背景） |
| [AutoEq](https://github.com/jaakkopasanen/AutoEq) | MIT | 耳机频响补偿数据 |
| [ONNX Runtime](https://github.com/microsoft/onnxruntime) / [DirectML](https://github.com/microsoft/DirectML)（随 Windows App SDK 的 Windows ML 附带） | MIT | AI 模型推理；NPU 执行器由 Windows ML 按机器提供 |
| [LAION CLAP](https://github.com/LAION-AI/CLAP)（[Xenova/larger_clap_music_and_speech](https://huggingface.co/Xenova/larger_clap_music_and_speech)） | Apache-2.0 | 听感分析、按描述找歌、自动标签 |
| [multilingual-e5-small](https://huggingface.co/intfloat/multilingual-e5-small)（[Xenova 转换版](https://huggingface.co/Xenova/multilingual-e5-small)） | MIT | 按意思搜索歌词 |
| [Ultimate Vocal Remover](https://github.com/Anjok07/ultimatevocalremovergui)（MDX-Net Inst HQ 3） | MIT | 人声分离（K 歌、导出伴奏） |
| [Whisper](https://github.com/openai/whisper)（[onnx-community/whisper-small](https://huggingface.co/onnx-community/whisper-small)） | MIT | 歌词识别与自动校准 |
| [LRCLIB](https://lrclib.net) | — | 歌词数据来源（开放接口） |
| [Open-Meteo](https://open-meteo.com) | CC BY 4.0（数据） | 沉浸背景的天气效果（可选，默认关闭） |
| [Codrops RainEffect](https://github.com/codrops/RainEffect)（Lucas Bebber） | [Codrops 许可](https://tympanus.net/codrops/licensing/) | 沉浸背景的雨水效果：参考其水珠模拟算法（水珠贴图为本项目程序生成） |

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
