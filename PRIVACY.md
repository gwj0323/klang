# Klang 隐私声明 · Privacy Statement · Privatlivspolitik

[简体中文](#简体中文) · [English](#english) · [Dansk](#dansk)

生效日期 / Effective date / Ikrafttrædelsesdato: 2026-11-14 · 适用版本 / Applies to / Gælder for: Klang (WinUI 3) 2.x

---

## 简体中文

### 1. 概述

Klang 是在用户本机运行的桌面应用。开发者不运营与本应用配套的服务器，不要求注册账号，也不在应用中集成任何使用统计、遥测或崩溃上报组件。开发者不会通过本应用收集、存储或接收用户的个人信息。

本应用会在用户使用特定功能时，直接向第三方服务（网易云音乐、汽水音乐及其认证和媒体服务、React CDN、LRCLIB、GitHub）发起网络请求。这些请求的内容、触发条件与控制方式见第 3 节。

本声明适用于 Klang 的 WinUI 3 版本（2.x）。早期的 PyQt6 版本不在本声明范围内。

### 2. 本地存储的数据

应用数据保存在 `%LocalAppData%\Klang`，仅用于应用自身功能。本地曲库索引与设置不会上传；账号与在线播放所需的请求数据见第 3 节。

| 位置 | 内容 |
| :--- | :--- |
| `player_state.json`（及 `.bak` 备份） | 曲库索引（文件路径、标签信息、时长、格式）、监视的文件夹、歌单与智能歌单、收藏、播放次数与最近播放时间、漫游模式的学习数据、各曲目的分析结果（声学特征、音质检测结果、BPM 与调性）、歌词时间偏移、界面与音频设置、窗口位置，以及加密后的网易云登录凭据 |
| `covers`、`CoversCache` | 从音频文件提取或从网络获取的封面图片 |
| `netease_cache` | 网易云音乐曲目的缓存音频文件 |
| `LyricsCache` | 从 LRCLIB 获取的歌词 |
| `soda/session.bin` | 独立的汽水账号 Cookie、账号编号、昵称与头像地址，整体使用当前 Windows 用户作用域的 DPAPI 加密 |
| `soda/device.json`、`soda/native/<版本-哈希>/` | Klang 汽水接入独立生成或注册的设备 / 安装标识、注册状态，以及从本机官方 3.7.0 安装目录暂存的公共原生组件 |
| `soda/u_<账号哈希>/audio`、`soda/u_<账号哈希>/covers`、`soda/u_<账号哈希>/lyrics-v2` | 按汽水账号隔离的音频、封面与归一化逐行歌词缓存；模式列表位于 `soda/shared/modes`，未登录缓存使用 `soda/guest` |
| `soda/login-temp-<随机编号>` | 本次二维码登录的临时 WebView2 配置与站点数据，关闭登录窗口后尝试删除 |

说明：
- 应用只读取用户添加到曲库的音频文件及其同名歌词文件，不会修改文件内容，除非用户主动使用“编辑标签”并确认写入。
- 当前播放的曲目信息（歌名、歌手、封面、播放进度）会提供给 Windows 系统媒体控件（SMTC），用于锁屏、音量浮层和多媒体键，仅在本机系统内使用。
- 卸载程序不会删除 `%LocalAppData%\Klang`。

### 3. 网络请求

| 功能 | 对象 | 发送的内容 | 触发条件与控制 |
| :--- | :--- | :--- | :--- |
| 自动补全封面、专辑、歌手 | 网易云音乐（music.163.com 及其图片服务器） | 以歌名和歌手组成的搜索关键字；匹配成功后按歌曲编号请求封面与元数据 | 设置 → 曲库 → “自动补全封面与歌词”，可关闭 |
| 自动补全歌词 | 网易云音乐；LRCLIB（lrclib.net） | 对 LRCLIB：歌名、歌手、专辑、时长。对网易云：同上一行 | 同上；歌词菜单中的“在线搜索歌词”为手动触发 |
| 网易云账号、云端曲库与播放 | 网易云音乐 | 登录凭据（见第 4 节）、所请求的歌单与曲目、搜索关键字 | 仅在用户登录或使用云端页面时发生 |
| 汽水二维码登录与安全验证 | WebView2 中的汽水官方认证服务（如 api.qishui.com、bff-pc.qishui.com、auth.zijieapi.com）及验证 / 静态资源 CDN；React CDN（unpkg.com） | 本次二维码登录、轮询与安全验证请求及该临时浏览器会话的站点数据 | 打开汽水登录窗口时；按官方验证流程需要加载资源 |
| 汽水账号、推荐、收藏读取与授权播放 | api.qishui.com 及其允许的媒体 / 图片 CDN | 当前 Klang 汽水会话凭据、独立设备与安装标识、系统 / 版本 / 时区信息、请求的曲目与歌单、推荐模式、批次与已播放曲目编号 | 使用汽水页面、播放或预缓冲时；不使用该功能即可避免相应请求 |
| 检查更新 | GitHub（api.github.com） | 不含个人信息的发布列表请求 | 仅在用户于设置 → 关于中点击“检查更新”时发生，应用不会在后台自动检查 |
| 下载新版本 | 系统默认浏览器 | 由浏览器访问 GitHub 发布页 | 仅在用户点击下载时发生 |

应用发起的接口请求使用 HTTPS；封面与音频文件的下载地址由网易云音乐的接口返回，以其提供的地址为准。上述服务端会像任何网络服务一样看到请求方的 IP 地址和通用请求头；其对这些信息的处理由各服务方的隐私政策规定，开发者无法控制。

汽水媒体与封面下载仅接受允许域名的 HTTPS 地址，并校验重定向；CDN 下载不附带账号 Cookie。汽水 API 客户端仅向 `api.qishui.com` 附带本次登录取得的会话 Cookie；登录 WebView2 本身仍会按官方认证流程与相应站点交换数据。收藏仅供读取和分页浏览，不向汽水写入或取消收藏。

### 4. 账号与登录凭据

#### 网易云音乐

- 登录方式为扫码或手动粘贴 Cookie。应用不会获取或保存用户的网易云密码。
- 登录后取得的 Cookie 使用 Windows 数据保护接口（DPAPI，当前用户作用域）加密后保存在 `player_state.json` 中，仅当前 Windows 用户账户可以解密。
- 在设置中退出登录会清除内存与磁盘上保存的凭据。
- 手动粘贴 Cookie 属于高权限凭据，请勿在不受信任的环境中操作，也不要将 `player_state.json` 与他人共享。

#### 汽水音乐

- 使用独立的临时 WebView2 会话完成二维码登录；只接受本次登录获得并经接口验证的 Cookie，不读取官方已安装客户端的账号数据库或登录状态。
- 会话与账号信息保存在独立的 `soda/session.bin`，使用 DPAPI 当前用户作用域加密；设备标识、原生组件暂存和缓存与网易云会话分离。
- 在汽水主页退出登录会清除内存中的凭据并删除 `session.bin`，不会同时删除设备标识、原生组件或按账号保存的音频、封面与歌词缓存。
- 如需清除全部汽水本地数据，先关闭 Klang，再删除数据目录中的 `soda` 文件夹。缓存为普通文件，账号哈希目录用于隔离，并不等于加密。

### 5. 第三方服务

Klang 使用的第三方服务各自有独立的隐私政策与服务条款，使用相应功能即表示这些数据会发送给对应服务方：

- 网易云音乐：<https://music.163.com>
- 汽水音乐 API 及其认证 / 媒体服务：<https://api.qishui.com>
- React CDN（登录验证所需资源）：<https://unpkg.com>
- LRCLIB：<https://lrclib.net>
- GitHub：<https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement>

### 6. 用户的控制权

- 关闭“自动补全封面与歌词”后，应用不会再为本地曲目联网查找元数据与歌词。
- 不登录网易云、不使用“检查更新”和“在线搜索歌词”，则应用不会产生相应的网络请求。
- 不打开汽水页面或登录窗口、不播放汽水曲目，则不会触发相应汽水请求。
- 需要清除全部本地数据时，退出应用后删除 `%LocalAppData%\Klang` 目录即可。
- 设置 → 网络与缓存 中可单独清理网易云音频缓存。

### 7. 安全

除第 4 节所述的凭据加密外，本地数据以普通文件形式保存，其安全性依赖于 Windows 账户与磁盘本身的保护。安装程序与发布包目前未做代码签名；请仅从本项目的 GitHub Releases 页面获取。

### 8. 声明的变更与联系方式

本声明随应用功能变化而更新，更新后的版本随代码仓库发布，生效日期见文首。如有疑问或发现问题，请在 <https://github.com/gwj0323/klang/issues> 提交 Issue。

---

## English

### 1. Overview

Klang is a desktop application that runs on the user's own computer. The developer does not operate any server for this application, does not require an account, and does not include usage analytics, telemetry or crash reporting. The developer does not collect, store or receive personal information from users through this application.

When a user uses certain features, the application connects directly to third-party services (NetEase Cloud Music, Soda Music and its authentication and media services, the React CDN, LRCLIB and GitHub). What is sent, when, and how to control it is described in Section 3.

This statement applies to the WinUI 3 version of Klang (2.x). The earlier PyQt6 version is not covered.

### 2. Data stored locally

Application data is kept in `%LocalAppData%\Klang` and used only for application features. The local library index and settings are not uploaded; data sent for accounts and online playback is described in Section 3.

| Location | Contents |
| :--- | :--- |
| `player_state.json` (and its `.bak` backup) | Library index (file paths, tag information, duration, format), watched folders, playlists and smart playlists, favourites, play counts and last-played times, roaming-mode learning data, per-track analysis results (acoustic features, audio quality verdict, BPM and key), lyric timing offsets, interface and audio settings, window positions, and the encrypted NetEase Cloud Music credentials |
| `covers`, `CoversCache` | Cover images extracted from audio files or downloaded from the network |
| `netease_cache` | Cached audio files of NetEase Cloud Music tracks |
| `LyricsCache` | Lyrics retrieved from LRCLIB |
| `soda/session.bin` | Separate Soda account cookie, account ID, nickname and avatar address, encrypted together with DPAPI in current-Windows-user scope |
| `soda/device.json`, `soda/native/<version-hash>/` | Independent device / installation identifiers generated or registered for Klang, registration state, and public native components staged from the local official 3.7.0 installation |
| `soda/u_<account-hash>/audio`, `soda/u_<account-hash>/covers`, `soda/u_<account-hash>/lyrics-v2` | Audio, cover and normalized line-lyric caches isolated by Soda account; mode lists are in `soda/shared/modes`, and signed-out caching uses `soda/guest` |
| `soda/login-temp-<random-id>` | Temporary WebView2 profile and site data for the current QR login; deletion is attempted when the login window closes |

Notes:
- The application reads the audio files the user adds to the library, and lyric files with matching names. It does not modify them unless the user chooses "Edit tags" and confirms the write.
- Information about the current track (title, artist, cover, progress) is provided to the Windows System Media Transport Controls (SMTC) for the lock screen, volume flyout and media keys. It is used within the local system only.
- Uninstalling the application does not delete `%LocalAppData%\Klang`.

### 3. Network requests

| Feature | Recipient | Data sent | Trigger and control |
| :--- | :--- | :--- | :--- |
| Auto-fill of covers, album and artist | NetEase Cloud Music (music.163.com and its image servers) | A search keyword made of title and artist; after a match, requests for the cover and metadata by song ID | Settings → Library → "Fill in covers and lyrics automatically"; can be turned off |
| Auto-fill of lyrics | NetEase Cloud Music; LRCLIB (lrclib.net) | To LRCLIB: title, artist, album, duration. To NetEase: as in the row above | As above; "Search lyrics online" in the lyrics menu is triggered manually |
| NetEase account, cloud library and playback | NetEase Cloud Music | Login credentials (see Section 4), requested playlists and tracks, search keywords | Only when the user signs in or uses the cloud page |
| Soda QR sign-in and security verification | Official Soda authentication services in WebView2 (such as api.qishui.com, bff-pc.qishui.com and auth.zijieapi.com), verification / static-resource CDNs, and the React CDN (unpkg.com) | Current QR login, polling and verification requests, plus site data in that temporary browser session | When the Soda sign-in window is opened; resources load as required by the official verification flow |
| Soda account, recommendations, reading favorites and authorized playback | api.qishui.com and permitted media / image CDNs | Current Klang Soda session credentials, independent device and installation IDs, system / version / timezone information, requested tracks and playlists, mode, batch and played-track IDs | When using the Soda page, playback or prefetch; avoid these requests by not using the feature |
| Update check | GitHub (api.github.com) | A request for the release list, containing no personal information | Only when the user clicks "Check for updates" in Settings → About; the application does not check in the background |
| Downloading a new version | The default web browser | The browser opens the GitHub release page | Only when the user clicks download |

API requests made by the application use HTTPS; download addresses for cover images and audio files are returned by the NetEase Cloud Music API and are used as provided. As with any network service, the recipients can see the requester's IP address and standard request headers. Their handling of this information is governed by their own privacy policies, which the developer does not control.

Soda media and cover downloads accept only HTTPS addresses on permitted domains and validate redirects; CDN downloads do not attach account cookies. The Soda API client attaches the cookie obtained in the current login only to `api.qishui.com`; the login WebView2 itself still exchanges data with the respective sites during the official authentication flow. Favorites can be read and paginated; Klang does not add or remove remote Soda favorites.

### 4. Accounts and credentials

#### NetEase Cloud Music

- Sign-in is by QR code or by pasting a cookie manually. The application does not obtain or store the user's NetEase password.
- The cookie obtained after sign-in is encrypted with the Windows Data Protection API (DPAPI, current-user scope) and stored in `player_state.json`. Only the same Windows user account can decrypt it.
- Signing out in Settings clears the credentials from memory and from disk.
- A pasted cookie is a high-privilege credential. Do not paste it on an untrusted machine, and do not share `player_state.json` with others.

#### Soda Music

- QR sign-in uses a separate temporary WebView2 session. Only cookies obtained in that login and verified by the API are accepted; the installed official client's account database and login state are never read.
- Session and account information is stored separately in `soda/session.bin`, encrypted with current-user DPAPI. Device identifiers, native staging and caches are separate from the NetEase session.
- Signing out on the Soda homepage clears credentials from memory and deletes `session.bin`. It leaves device identifiers, staged native components, and per-account audio, cover and lyric caches in place.
- To remove all local Soda data, close Klang and delete the `soda` folder inside the data directory. Caches are ordinary files; hashed account directories provide separation, not encryption.

### 5. Third-party services

The third-party services used by Klang have their own privacy policies and terms. By using the related feature, the data described above is sent to the respective provider:

- NetEase Cloud Music: <https://music.163.com>
- Soda Music API and its authentication / media services: <https://api.qishui.com>
- React CDN (resources used for login verification): <https://unpkg.com>
- LRCLIB: <https://lrclib.net>
- GitHub: <https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement>

### 6. User control

- With "Fill in covers and lyrics automatically" turned off, the application no longer looks up metadata or lyrics online for local tracks.
- If the user does not sign in to NetEase and does not use "Check for updates" or "Search lyrics online", the corresponding network requests are not made.
- Avoid opening the Soda page or sign-in window and playing Soda tracks to avoid the corresponding Soda requests.
- To remove all local data, close the application and delete the `%LocalAppData%\Klang` folder.
- The NetEase audio cache can be cleared separately under Settings → Network.

### 7. Security

Apart from the credential encryption described in Section 4, local data is stored as ordinary files, and its security depends on the protection of the Windows account and the disk. The installer and release packages are currently not code-signed; obtain them only from this project's GitHub Releases page.

### 8. Changes and contact

This statement is updated as the application changes. The updated version is published with the source repository, and the effective date is shown at the top. For questions or issues, please open an issue at <https://github.com/gwj0323/klang/issues>.

---

## Dansk

### 1. Oversigt

Klang er et skrivebordsprogram, der kører på brugerens egen computer. Udvikleren driver ingen servere til programmet, kræver ingen konto og har ikke indbygget brugsstatistik, telemetri eller fejlrapportering. Udvikleren indsamler, lagrer eller modtager ikke personoplysninger fra brugere gennem programmet.

Når brugeren benytter bestemte funktioner, opretter programmet direkte forbindelse til tredjepartstjenester (NetEase Cloud Music, Soda Music og dets godkendelses- og medietjenester, React-CDN, LRCLIB og GitHub). Hvad der sendes, hvornår, og hvordan det kan styres, er beskrevet i afsnit 3.

Denne politik gælder for WinUI 3-versionen af Klang (2.x). Den tidligere PyQt6-version er ikke omfattet.

### 2. Data, der gemmes lokalt

Programdata gemmes i `%LocalAppData%\Klang` og bruges kun til programmets funktioner. Det lokale biblioteksindeks og indstillinger uploades ikke; data til konti og onlineafspilning er beskrevet i afsnit 3.

| Placering | Indhold |
| :--- | :--- |
| `player_state.json` (og sikkerhedskopien `.bak`) | Bibliotekets indeks (filstier, tags, varighed, format), overvågede mapper, afspilningslister og smarte afspilningslister, favoritter, antal afspilninger og seneste afspilningstidspunkt, indlæringsdata til strejftilstand, analyseresultater pr. nummer (akustiske egenskaber, lydkvalitetsvurdering, BPM og toneart), tidsforskydning af sangtekster, indstillinger for grænseflade og lyd, vinduespositioner samt de krypterede loginoplysninger til NetEase Cloud Music |
| `covers`, `CoversCache` | Coverbilleder udtrukket fra lydfiler eller hentet fra internettet |
| `netease_cache` | Cachede lydfiler fra NetEase Cloud Music |
| `LyricsCache` | Sangtekster hentet fra LRCLIB |
| `soda/session.bin` | Separat Soda-cookie, konto-id, kaldenavn og avataradresse; samlet krypteret med DPAPI i den aktuelle Windows-brugers omfang |
| `soda/device.json`, `soda/native/<version-hash>/` | Uafhængige enheds- og installations-id'er oprettet eller registreret til Klang, registreringsstatus og offentlige native komponenter kopieret fra den lokale officielle 3.7.0-installation |
| `soda/u_<konto-hash>/audio`, `soda/u_<konto-hash>/covers`, `soda/u_<konto-hash>/lyrics-v2` | Lyd-, cover- og linjebaseret sangtekstcache adskilt pr. Soda-konto; tilstandslister ligger i `soda/shared/modes`, og cache uden login bruger `soda/guest` |
| `soda/login-temp-<tilfældigt-id>` | Midlertidig WebView2-profil og webstedsdata til det aktuelle QR-login; sletning forsøges, når loginvinduet lukkes |

Bemærkninger:
- Programmet læser de lydfiler, brugeren føjer til biblioteket, samt tekstfiler med samme navn. Det ændrer ikke filerne, medmindre brugeren vælger "Redigér tags" og bekræfter skrivningen.
- Oplysninger om det aktuelle nummer (titel, kunstner, cover, afspilningsposition) videregives til Windows' systemmediekontroller (SMTC) til låseskærm, lydstyrkeoverlay og medietaster. De bruges kun inden for det lokale system.
- Afinstallation af programmet sletter ikke `%LocalAppData%\Klang`.

### 3. Netværksforespørgsler

| Funktion | Modtager | Data, der sendes | Udløser og styring |
| :--- | :--- | :--- | :--- |
| Automatisk udfyldning af cover, album og kunstner | NetEase Cloud Music (music.163.com og dets billedservere) | Et søgeord bestående af titel og kunstner; ved match forespørges cover og metadata via sang-id | Indstillinger → Bibliotek → "Udfyld cover og tekster automatisk"; kan slås fra |
| Automatisk udfyldning af sangtekster | NetEase Cloud Music; LRCLIB (lrclib.net) | Til LRCLIB: titel, kunstner, album, varighed. Til NetEase: som i rækken ovenfor | Som ovenfor; "Søg sangtekst online" i tekstmenuen udløses manuelt |
| NetEase-konto, cloud-bibliotek og afspilning | NetEase Cloud Music | Loginoplysninger (se afsnit 4), forespurgte afspilningslister og numre, søgeord | Kun når brugeren logger ind eller bruger cloud-siden |
| Soda QR-login og sikkerhedsbekræftelse | Officielle Soda-godkendelsestjenester i WebView2 (f.eks. api.qishui.com, bff-pc.qishui.com og auth.zijieapi.com), CDN'er til bekræftelse / statiske ressourcer samt React-CDN (unpkg.com) | Aktuelle QR-login-, polling- og bekræftelsesforespørgsler samt webstedsdata i den midlertidige browsersession | Når Soda-loginvinduet åbnes; ressourcer indlæses efter behov i det officielle bekræftelsesforløb |
| Soda-konto, anbefalinger, læsning af favoritter og autoriseret afspilning | api.qishui.com og tilladte medie- / billed-CDN'er | Aktuelle Klang Soda-loginoplysninger, uafhængige enheds- og installations-id'er, system- / versions- / tidszoneoplysninger, forespurgte numre og afspilningslister, tilstand, batch og id'er på afspillede numre | Ved brug af Soda-siden, afspilning eller forhåndshentning; undgå forespørgslerne ved ikke at bruge funktionen |
| Søgning efter opdateringer | GitHub (api.github.com) | En forespørgsel om udgivelsesliste uden personoplysninger | Kun når brugeren klikker "Søg efter opdateringer" under Indstillinger → Om; programmet søger ikke i baggrunden |
| Download af ny version | Standardbrowseren | Browseren åbner udgivelsessiden på GitHub | Kun når brugeren klikker på download |

Programmets API-forespørgsler bruger HTTPS; downloadadresser til coverbilleder og lydfiler returneres af NetEase Cloud Music og bruges, som de leveres. Som ved enhver netværkstjeneste kan modtagerne se afsenderens IP-adresse og almindelige forespørgselshoveder. Deres behandling heraf er underlagt deres egne privatlivspolitikker, som udvikleren ikke har kontrol over.

Soda-medier og covere hentes kun via HTTPS fra tilladte domæner, og omdirigeringer kontrolleres. CDN-downloads vedhæfter ikke kontocookies. Soda API-klienten sender kun cookien fra det aktuelle login til `api.qishui.com`; login-WebView2 udveksler fortsat data med de relevante websteder under den officielle godkendelse. Favoritter kan læses og vises sidevis; Klang tilføjer eller fjerner ikke favoritter hos Soda.

### 4. Konti og loginoplysninger

#### NetEase Cloud Music

- Der logges ind med QR-kode eller ved manuelt at indsætte en cookie. Programmet indhenter eller gemmer ikke brugerens NetEase-adgangskode.
- Den cookie, der opnås ved login, krypteres med Windows Data Protection API (DPAPI, aktuel brugers omfang) og gemmes i `player_state.json`. Kun den samme Windows-brugerkonto kan dekryptere den.
- Når brugeren logger ud under Indstillinger, slettes oplysningerne fra hukommelsen og disken.
- En indsat cookie er en loginoplysning med vide rettigheder. Indsæt den ikke på en computer, du ikke har tillid til, og del ikke `player_state.json` med andre.

#### Soda Music

- QR-login bruger en separat, midlertidig WebView2-session. Kun cookies fra dette login, som er valideret via API'et, accepteres; den installerede officielle klients kontodatabase og loginstatus læses aldrig.
- Sessions- og kontooplysninger gemmes separat i `soda/session.bin`, krypteret med DPAPI for den aktuelle bruger. Enheds-id'er, kopierede native komponenter og cache er adskilt fra NetEase-sessionen.
- Når brugeren logger ud på Soda-startsiden, ryddes loginoplysningerne fra hukommelsen, og `session.bin` slettes. Enheds-id'er, native komponenter samt kontoens lyd-, cover- og sangtekstcache bevares.
- Alle lokale Soda-data fjernes ved at lukke Klang og slette mappen `soda` i datamappen. Cachefiler er almindelige filer; hashbaserede kontomapper giver adskillelse, ikke kryptering.

### 5. Tredjepartstjenester

De tredjepartstjenester, Klang bruger, har deres egne privatlivspolitikker og vilkår. Ved at bruge den pågældende funktion sendes de beskrevne data til den respektive udbyder:

- NetEase Cloud Music: <https://music.163.com>
- Soda Music API og dets godkendelses- / medietjenester: <https://api.qishui.com>
- React-CDN (ressourcer til loginbekræftelse): <https://unpkg.com>
- LRCLIB: <https://lrclib.net>
- GitHub: <https://docs.github.com/site-policy/privacy-policies/github-general-privacy-statement>

### 6. Brugerens kontrol

- Når "Udfyld cover og tekster automatisk" er slået fra, slår programmet ikke længere metadata eller sangtekster op online for lokale numre.
- Hvis brugeren ikke logger ind på NetEase og ikke bruger "Søg efter opdateringer" eller "Søg sangtekst online", sendes de tilsvarende forespørgsler ikke.
- Undlad at åbne Soda-siden eller loginvinduet og afspille Soda-numre for at undgå de tilsvarende forespørgsler.
- Alle lokale data fjernes ved at lukke programmet og slette mappen `%LocalAppData%\Klang`.
- NetEase-lydcachen kan ryddes særskilt under Indstillinger → Netværk.

### 7. Sikkerhed

Ud over den kryptering af loginoplysninger, der er beskrevet i afsnit 4, gemmes lokale data som almindelige filer, og deres sikkerhed afhænger af beskyttelsen af Windows-kontoen og disken. Installationsprogrammet og udgivelsespakkerne er i øjeblikket ikke kodesignerede; hent dem kun fra projektets GitHub Releases-side.

### 8. Ændringer og kontakt

Denne politik opdateres, når programmet ændres. Den opdaterede version udgives sammen med kildekoden, og ikrafttrædelsesdatoen står øverst. Ved spørgsmål eller problemer kan du oprette en issue på <https://github.com/gwj0323/klang/issues>.
