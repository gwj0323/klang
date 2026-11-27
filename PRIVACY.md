# Klang 隐私声明 · Privacy Statement · Privatlivspolitik

[简体中文](#简体中文) · [English](#english) · [Dansk](#dansk)

生效日期 / Effective date / Ikrafttrædelsesdato: 2026-11-26 · 适用版本 / Applies to / Gælder for: Klang (WinUI 3) 2.x

---

## 简体中文

### 1. 概述

Klang 是在用户本机运行的桌面应用。开发者不运营与本应用配套的服务器，不要求注册账号，也不在应用中集成任何使用统计、遥测或崩溃上报组件。开发者不会通过本应用收集、存储或接收用户的个人信息。

本应用会在用户使用特定功能时，直接向第三方服务（网易云音乐、QQ 音乐、LRCLIB、Open-Meteo、GitHub，以及下载 AI 组件时的 Hugging Face；选择用 NPU 时，Windows 还会从 Microsoft 下载执行器）发起网络请求。这些请求的内容、触发条件与控制方式见第 3 节。

本声明适用于 Klang 的 WinUI 3 版本（2.x）。早期的 PyQt6 版本不在本声明范围内。

### 2. 本地存储的数据

应用数据保存在 `%LocalAppData%\Klang`，仅用于应用自身功能，不会被上传。

| 位置 | 内容 |
| :--- | :--- |
| `player_state.json`（及 `.bak` 备份） | 曲库索引（文件路径、标签信息、时长、格式）、监视的文件夹、歌单与智能歌单、收藏、播放次数与最近播放时间、漫游模式的学习数据、各曲目的分析结果（声学特征、音质检测结果、BPM 与调性）、歌词时间偏移、界面与音频设置、窗口位置，以及加密后的网易云音乐、QQ 音乐登录凭据 |
| `covers`、`CoversCache` | 从音频文件提取或从网络获取的封面图片 |
| `netease_cache`、`qqmusic_cache` | 网易云音乐、QQ 音乐曲目的缓存音频文件与封面 |
| `LyricsCache` | 从 LRCLIB 获取的歌词 |
| `ai` | 可选的 AI 功能：下载的模型（`models`）；曲库的听感向量、歌词语义索引与高潮位置（`index`）；K 歌时分离出的伴奏与人声（`karaoke`，超过上限自动清理） |

说明：
- AI 功能默认关闭。开启后，听感分析、歌词搜索、人声分离、歌词识别和找高潮都在本机的子进程中完成，音频、歌词和分析结果不会上传。
- 应用只读取用户添加到曲库的音频文件及其同名歌词文件，不会修改文件内容，除非用户主动使用“编辑标签”并确认写入。
- 当前播放的曲目信息（歌名、歌手、封面、播放进度）会提供给 Windows 系统媒体控件（SMTC），用于锁屏、音量浮层和多媒体键，仅在本机系统内使用。
- 卸载程序不会删除 `%LocalAppData%\Klang`。

### 3. 网络请求

| 功能 | 对象 | 发送的内容 | 触发条件与控制 |
| :--- | :--- | :--- | :--- |
| 自动补全封面、专辑、歌手 | 网易云音乐（music.163.com 及其图片服务器） | 以歌名和歌手组成的搜索关键字；匹配成功后按歌曲编号请求封面与元数据 | 设置 → 曲库与云端 → “自动补全封面与歌词”，可关闭 |
| 自动补全歌词 | 网易云音乐；LRCLIB（lrclib.net） | 对 LRCLIB：歌名、歌手、专辑、时长。对网易云：同上一行 | 同上；歌词菜单中的“在线搜索歌词”为手动触发 |
| 网易云账号、云端曲库与播放 | 网易云音乐 | 登录凭据（见第 4 节）、所请求的歌单与曲目、搜索关键字 | 仅在用户登录或使用云端页面时发生 |
| QQ 音乐账号、云端曲库与播放 | QQ 音乐（y.qq.com 及其接口、音频与图片服务器）；扫码登录时另会访问 QQ 登录（ptlogin2.qq.com、graph.qq.com）或微信开放平台（open.weixin.qq.com） | 登录凭据（见第 4 节）、所请求的歌单与曲目、搜索关键字 | 仅在用户于云端页面切换到 QQ 音乐并登录或使用时发生 |
| 天气效果 | Open-Meteo（api.open-meteo.com、geocoding-api.open-meteo.com） | 搜索城市时：输入的城市名与界面语言。之后每 30 分钟：所选城市的经纬度（保留两位小数，约 1 千米精度） | 仅在设置 → 外观中开启“天气效果”并选择城市后发生，默认关闭；关闭后即停止请求 |
| 检查更新 | GitHub（api.github.com） | 不含个人信息的发布列表请求 | 每次启动应用后自动检查一次（可在设置 → 关于中关闭“启动时自动检查更新”），以及用户点击“检查更新”时；不会自动下载或安装 |
| 下载新版本 | 系统默认浏览器 | 由浏览器访问 GitHub 发布页 | 仅在用户点击下载时发生 |
| 下载 AI 组件 | Hugging Face（huggingface.co）、GitHub（github.com）；选择“后台分析用 NPU”时，Windows ML 从 Microsoft 下载对应硬件的 NPU 执行器 | 不含个人信息的文件下载请求 | 仅在用户于设置 → AI 功能中点击下载、或选择 NPU 后第一次使用时发生；AI 功能默认关闭 |

应用发起的接口请求使用 HTTPS；封面与音频文件的下载地址由网易云音乐、QQ 音乐的接口返回，应用只接受对应平台域名下的地址。上述服务端会像任何网络服务一样看到请求方的 IP 地址和通用请求头；其对这些信息的处理由各服务方的隐私政策规定，开发者无法控制。

### 4. 云端账号与登录凭据

- 网易云音乐的登录方式为扫码或手动粘贴 Cookie；QQ 音乐的登录方式为 QQ 或微信扫码。应用不会获取或保存用户的网易云、QQ 或微信密码。
- 登录后取得的凭据（网易云的 Cookie、QQ 音乐的登录令牌）使用 Windows 数据保护接口（DPAPI，当前用户作用域）加密后保存在 `player_state.json` 中，仅当前 Windows 用户账户可以解密。
- 在设置中退出登录会清除内存与磁盘上保存的凭据。
- 手动粘贴 Cookie 属于高权限凭据，请勿在不受信任的环境中操作，也不要将 `player_state.json` 与他人共享。

### 5. 第三方服务

Klang 使用的第三方服务各自有独立的隐私政策与服务条款，使用相应功能即表示这些数据会发送给对应服务方：

- 网易云音乐：<https://music.163.com>
- QQ 音乐：<https://y.qq.com>
- LRCLIB：<https://lrclib.net>
- Open-Meteo：<https://open-meteo.com/en/terms>
- GitHub：<https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement>
- Hugging Face：<https://huggingface.co/privacy>
- Microsoft（Windows ML 执行器下载）：<https://privacy.microsoft.com/privacystatement>

### 6. 用户的控制权

- 关闭“自动补全封面与歌词”后，应用不会再为本地曲目联网查找元数据与歌词。
- 不登录网易云音乐或 QQ 音乐、不开启“天气效果”、关闭“启动时自动检查更新”且不使用“检查更新”和“在线搜索歌词”，则应用不会产生相应的网络请求。
- 需要清除全部本地数据时，退出应用后删除 `%LocalAppData%\Klang` 目录即可。
- 设置 → 曲库与云端 中可单独清理云端音频缓存；设置 → AI 功能 中可删除已下载的 AI 组件、清空伴奏缓存。

### 7. 安全

除第 4 节所述的凭据加密外，本地数据以普通文件形式保存，其安全性依赖于 Windows 账户与磁盘本身的保护。安装程序与发布包目前未做代码签名；请仅从本项目的 GitHub Releases 页面获取。

### 8. 声明的变更与联系方式

本声明随应用功能变化而更新，更新后的版本随代码仓库发布，生效日期见文首。如有疑问或发现问题，请在 <https://github.com/gwj0323/klang/issues> 提交 Issue。

---

## English

### 1. Overview

Klang is a desktop application that runs on the user's own computer. The developer does not operate any server for this application, does not require an account, and does not include usage analytics, telemetry or crash reporting. The developer does not collect, store or receive personal information from users through this application.

When a user uses certain features, the application connects directly to third-party services (NetEase Cloud Music, QQ Music, LRCLIB, Open-Meteo and GitHub, plus Hugging Face when AI components are downloaded; when the NPU is chosen, Windows also downloads a runtime from Microsoft). What is sent, when, and how to control it is described in Section 3.

This statement applies to the WinUI 3 version of Klang (2.x). The earlier PyQt6 version is not covered.

### 2. Data stored locally

Application data is kept in `%LocalAppData%\Klang`. It is used only by the application itself and is not uploaded.

| Location | Contents |
| :--- | :--- |
| `player_state.json` (and its `.bak` backup) | Library index (file paths, tag information, duration, format), watched folders, playlists and smart playlists, favourites, play counts and last-played times, roaming-mode learning data, per-track analysis results (acoustic features, audio quality verdict, BPM and key), lyric timing offsets, interface and audio settings, window positions, and the encrypted NetEase Cloud Music and QQ Music credentials |
| `covers`, `CoversCache` | Cover images extracted from audio files or downloaded from the network |
| `netease_cache`, `qqmusic_cache` | Cached audio files and covers of NetEase Cloud Music and QQ Music tracks |
| `LyricsCache` | Lyrics retrieved from LRCLIB |
| `ai` | Optional AI features: downloaded models (`models`); the library's listening vectors, lyric search index and highlight positions (`index`); instrumentals and vocals separated for karaoke (`karaoke`, cleaned up above a size limit) |

Notes:
- AI features are off by default. When turned on, sound analysis, lyric search, vocal separation, lyric transcription and highlight detection all run in a local child process. Audio, lyrics and analysis results are not uploaded.
- The application reads the audio files the user adds to the library, and lyric files with matching names. It does not modify them unless the user chooses "Edit tags" and confirms the write.
- Information about the current track (title, artist, cover, progress) is provided to the Windows System Media Transport Controls (SMTC) for the lock screen, volume flyout and media keys. It is used within the local system only.
- Uninstalling the application does not delete `%LocalAppData%\Klang`.

### 3. Network requests

| Feature | Recipient | Data sent | Trigger and control |
| :--- | :--- | :--- | :--- |
| Auto-fill of covers, album and artist | NetEase Cloud Music (music.163.com and its image servers) | A search keyword made of title and artist; after a match, requests for the cover and metadata by song ID | Settings → Library & cloud → "Fill in covers and lyrics automatically"; can be turned off |
| Auto-fill of lyrics | NetEase Cloud Music; LRCLIB (lrclib.net) | To LRCLIB: title, artist, album, duration. To NetEase: as in the row above | As above; "Search lyrics online" in the lyrics menu is triggered manually |
| NetEase account, cloud library and playback | NetEase Cloud Music | Login credentials (see Section 4), requested playlists and tracks, search keywords | Only when the user signs in or uses the cloud page |
| QQ Music account, cloud library and playback | QQ Music (y.qq.com and its API, audio and image servers); QR sign-in also contacts QQ login (ptlogin2.qq.com, graph.qq.com) or the WeChat Open Platform (open.weixin.qq.com) | Login credentials (see Section 4), requested playlists and tracks, search keywords | Only when the user switches the cloud page to QQ Music and signs in or uses it |
| Weather effects | Open-Meteo (api.open-meteo.com, geocoding-api.open-meteo.com) | When searching for a city: the city name entered and the interface language. Then every 30 minutes: the latitude and longitude of the chosen city, rounded to two decimals (about 1 km) | Only after "Weather effects" is turned on in Settings → Appearance and a city is chosen; off by default; turning it off stops the requests |
| Update check | GitHub (api.github.com) | A request for the release list, containing no personal information | Once each time the application starts (can be turned off with "Check for updates on startup" in Settings → About), and when the user clicks "Check for updates"; nothing is downloaded or installed automatically |
| Downloading a new version | The default web browser | The browser opens the GitHub release page | Only when the user clicks download |
| Downloading AI components | Hugging Face (huggingface.co), GitHub (github.com); when "NPU for background analysis" is chosen, Windows ML downloads the NPU runtime for the hardware from Microsoft | File download requests containing no personal information | Only when the user clicks download in Settings → AI features, or on first use after choosing the NPU; AI features are off by default |

API requests made by the application use HTTPS; download addresses for cover images and audio files are returned by the NetEase Cloud Music and QQ Music APIs; only addresses on the respective platform's domains are accepted. As with any network service, the recipients can see the requester's IP address and standard request headers. Their handling of this information is governed by their own privacy policies, which the developer does not control.

### 4. Cloud accounts and credentials

- NetEase Cloud Music sign-in is by QR code or by pasting a cookie manually; QQ Music sign-in is by scanning a QR code with QQ or WeChat. The application does not obtain or store the user's NetEase, QQ or WeChat password.
- The credentials obtained after sign-in (the NetEase cookie, the QQ Music login token) are encrypted with the Windows Data Protection API (DPAPI, current-user scope) and stored in `player_state.json`. Only the same Windows user account can decrypt it.
- Signing out in Settings clears the credentials from memory and from disk.
- A pasted cookie is a high-privilege credential. Do not paste it on an untrusted machine, and do not share `player_state.json` with others.

### 5. Third-party services

The third-party services used by Klang have their own privacy policies and terms. By using the related feature, the data described above is sent to the respective provider:

- NetEase Cloud Music: <https://music.163.com>
- QQ Music: <https://y.qq.com>
- LRCLIB: <https://lrclib.net>
- Open-Meteo: <https://open-meteo.com/en/terms>
- GitHub: <https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement>
- Hugging Face: <https://huggingface.co/privacy>
- Microsoft (Windows ML runtime downloads): <https://privacy.microsoft.com/privacystatement>

### 6. User control

- With "Fill in covers and lyrics automatically" turned off, the application no longer looks up metadata or lyrics online for local tracks.
- If the user does not sign in to NetEase Cloud Music or QQ Music, does not turn on "Weather effects", turns off "Check for updates on startup" and does not use "Check for updates" or "Search lyrics online", the corresponding network requests are not made.
- To remove all local data, close the application and delete the `%LocalAppData%\Klang` folder.
- The cloud audio cache can be cleared separately under Settings → Library & cloud. Downloaded AI components and the karaoke cache can be removed under Settings → AI features.

### 7. Security

Apart from the credential encryption described in Section 4, local data is stored as ordinary files, and its security depends on the protection of the Windows account and the disk. The installer and release packages are currently not code-signed; obtain them only from this project's GitHub Releases page.

### 8. Changes and contact

This statement is updated as the application changes. The updated version is published with the source repository, and the effective date is shown at the top. For questions or issues, please open an issue at <https://github.com/gwj0323/klang/issues>.

---

## Dansk

### 1. Oversigt

Klang er et skrivebordsprogram, der kører på brugerens egen computer. Udvikleren driver ingen servere til programmet, kræver ingen konto og har ikke indbygget brugsstatistik, telemetri eller fejlrapportering. Udvikleren indsamler, lagrer eller modtager ikke personoplysninger fra brugere gennem programmet.

Når brugeren benytter bestemte funktioner, opretter programmet direkte forbindelse til tredjepartstjenester (NetEase Cloud Music, QQ Music, LRCLIB, Open-Meteo og GitHub samt Hugging Face, når AI-komponenter hentes; når NPU'en vælges, henter Windows desuden en runtime fra Microsoft). Hvad der sendes, hvornår, og hvordan det kan styres, er beskrevet i afsnit 3.

Denne politik gælder for WinUI 3-versionen af Klang (2.x). Den tidligere PyQt6-version er ikke omfattet.

### 2. Data, der gemmes lokalt

Programdata gemmes i `%LocalAppData%\Klang`. De bruges kun af programmet selv og uploades ikke.

| Placering | Indhold |
| :--- | :--- |
| `player_state.json` (og sikkerhedskopien `.bak`) | Bibliotekets indeks (filstier, tags, varighed, format), overvågede mapper, afspilningslister og smarte afspilningslister, favoritter, antal afspilninger og seneste afspilningstidspunkt, indlæringsdata til strejftilstand, analyseresultater pr. nummer (akustiske egenskaber, lydkvalitetsvurdering, BPM og toneart), tidsforskydning af sangtekster, indstillinger for grænseflade og lyd, vinduespositioner samt de krypterede loginoplysninger til NetEase Cloud Music og QQ Music |
| `covers`, `CoversCache` | Coverbilleder udtrukket fra lydfiler eller hentet fra internettet |
| `netease_cache`, `qqmusic_cache` | Cachede lydfiler og coverbilleder fra NetEase Cloud Music og QQ Music |
| `LyricsCache` | Sangtekster hentet fra LRCLIB |
| `ai` | Valgfrie AI-funktioner: hentede modeller (`models`); bibliotekets lydvektorer, søgeindeks for sangtekster og placering af højdepunkter (`index`); instrumental og vokal adskilt til karaoke (`karaoke`, ryddes op over en størrelsesgrænse) |

Bemærkninger:
- AI-funktionerne er slået fra som standard. Når de er slået til, foregår lydanalyse, tekstsøgning, vokaladskillelse, tekstgenkendelse og søgning efter højdepunkter i en lokal underproces. Lyd, sangtekster og analyseresultater uploades ikke.
- Programmet læser de lydfiler, brugeren føjer til biblioteket, samt tekstfiler med samme navn. Det ændrer ikke filerne, medmindre brugeren vælger "Redigér tags" og bekræfter skrivningen.
- Oplysninger om det aktuelle nummer (titel, kunstner, cover, afspilningsposition) videregives til Windows' systemmediekontroller (SMTC) til låseskærm, lydstyrkeoverlay og medietaster. De bruges kun inden for det lokale system.
- Afinstallation af programmet sletter ikke `%LocalAppData%\Klang`.

### 3. Netværksforespørgsler

| Funktion | Modtager | Data, der sendes | Udløser og styring |
| :--- | :--- | :--- | :--- |
| Automatisk udfyldning af cover, album og kunstner | NetEase Cloud Music (music.163.com og dets billedservere) | Et søgeord bestående af titel og kunstner; ved match forespørges cover og metadata via sang-id | Indstillinger → Bibliotek og sky → "Udfyld cover og tekster automatisk"; kan slås fra |
| Automatisk udfyldning af sangtekster | NetEase Cloud Music; LRCLIB (lrclib.net) | Til LRCLIB: titel, kunstner, album, varighed. Til NetEase: som i rækken ovenfor | Som ovenfor; "Søg sangtekst online" i tekstmenuen udløses manuelt |
| NetEase-konto, cloud-bibliotek og afspilning | NetEase Cloud Music | Loginoplysninger (se afsnit 4), forespurgte afspilningslister og numre, søgeord | Kun når brugeren logger ind eller bruger cloud-siden |
| QQ Music-konto, cloud-bibliotek og afspilning | QQ Music (y.qq.com og dets API-, lyd- og billedservere); ved login med QR-kode kontaktes også QQ-login (ptlogin2.qq.com, graph.qq.com) eller WeChat Open Platform (open.weixin.qq.com) | Loginoplysninger (se afsnit 4), forespurgte afspilningslister og numre, søgeord | Kun når brugeren skifter cloud-siden til QQ Music og logger ind eller bruger den |
| Vejreffekter | Open-Meteo (api.open-meteo.com, geocoding-api.open-meteo.com) | Ved søgning efter by: det indtastede bynavn og grænsefladens sprog. Derefter hvert 30. minut: den valgte bys bredde- og længdegrad, afrundet til to decimaler (ca. 1 km) | Kun når "Vejreffekter" er slået til under Indstillinger → Udseende, og en by er valgt; slået fra som standard; når den slås fra, stopper forespørgslerne |
| Søgning efter opdateringer | GitHub (api.github.com) | En forespørgsel om udgivelsesliste uden personoplysninger | Én gang hver gang programmet starter (kan slås fra med "Søg efter opdateringer ved start" under Indstillinger → Om), og når brugeren klikker "Søg efter opdateringer"; intet downloades eller installeres automatisk |
| Download af ny version | Standardbrowseren | Browseren åbner udgivelsessiden på GitHub | Kun når brugeren klikker på download |
| Download af AI-komponenter | Hugging Face (huggingface.co), GitHub (github.com); når "NPU til baggrundsanalyse" er valgt, henter Windows ML NPU-runtimen til hardwaren fra Microsoft | Filforespørgsler uden personoplysninger | Kun når brugeren klikker på download under Indstillinger → AI-funktioner, eller første gang efter at NPU'en er valgt; AI-funktionerne er slået fra som standard |

Programmets API-forespørgsler bruger HTTPS; downloadadresser til coverbilleder og lydfiler returneres af NetEase Cloud Music og QQ Music; kun adresser på den pågældende platforms domæner accepteres. Som ved enhver netværkstjeneste kan modtagerne se afsenderens IP-adresse og almindelige forespørgselshoveder. Deres behandling heraf er underlagt deres egne privatlivspolitikker, som udvikleren ikke har kontrol over.

### 4. Cloud-konti og loginoplysninger

- NetEase Cloud Music logges ind med QR-kode eller ved manuelt at indsætte en cookie; QQ Music logges ind ved at scanne en QR-kode med QQ eller WeChat. Programmet indhenter eller gemmer ikke brugerens adgangskode til NetEase, QQ eller WeChat.
- De loginoplysninger, der opnås ved login (NetEase-cookien, QQ Musics login-token), krypteres med Windows Data Protection API (DPAPI, aktuel brugers omfang) og gemmes i `player_state.json`. Kun den samme Windows-brugerkonto kan dekryptere den.
- Når brugeren logger ud under Indstillinger, slettes oplysningerne fra hukommelsen og disken.
- En indsat cookie er en loginoplysning med vide rettigheder. Indsæt den ikke på en computer, du ikke har tillid til, og del ikke `player_state.json` med andre.

### 5. Tredjepartstjenester

De tredjepartstjenester, Klang bruger, har deres egne privatlivspolitikker og vilkår. Ved at bruge den pågældende funktion sendes de beskrevne data til den respektive udbyder:

- NetEase Cloud Music: <https://music.163.com>
- QQ Music: <https://y.qq.com>
- LRCLIB: <https://lrclib.net>
- Open-Meteo: <https://open-meteo.com/en/terms>
- GitHub: <https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement>
- Hugging Face: <https://huggingface.co/privacy>
- Microsoft (Windows ML-runtime): <https://privacy.microsoft.com/privacystatement>

### 6. Brugerens kontrol

- Når "Udfyld cover og tekster automatisk" er slået fra, slår programmet ikke længere metadata eller sangtekster op online for lokale numre.
- Hvis brugeren ikke logger ind på NetEase Cloud Music eller QQ Music, ikke slår "Vejreffekter" til, slår "Søg efter opdateringer ved start" fra og ikke bruger "Søg efter opdateringer" eller "Søg sangtekst online", sendes de tilsvarende forespørgsler ikke.
- Alle lokale data fjernes ved at lukke programmet og slette mappen `%LocalAppData%\Klang`.
- Cloud-lydcachen kan ryddes særskilt under Indstillinger → Bibliotek og sky. Hentede AI-komponenter og karaokecachen kan fjernes under Indstillinger → AI-funktioner.

### 7. Sikkerhed

Ud over den kryptering af loginoplysninger, der er beskrevet i afsnit 4, gemmes lokale data som almindelige filer, og deres sikkerhed afhænger af beskyttelsen af Windows-kontoen og disken. Installationsprogrammet og udgivelsespakkerne er i øjeblikket ikke kodesignerede; hent dem kun fra projektets GitHub Releases-side.

### 8. Ændringer og kontakt

Denne politik opdateres, når programmet ændres. Den opdaterede version udgives sammen med kildekoden, og ikrafttrædelsesdatoen står øverst. Ved spørgsmål eller problemer kan du oprette en issue på <https://github.com/gwj0323/klang/issues>.
