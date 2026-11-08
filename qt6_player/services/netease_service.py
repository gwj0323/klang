import io
import json
import logging
import os
import sys
import time
import threading
from pathlib import Path
from typing import Dict, Any, List, Optional, Tuple
import requests

logger = logging.getLogger("NeteaseService")

from PyQt6.QtCore import QObject, pyqtSignal
from PyQt6.QtGui import QPixmap, QImage
import qrcode
from PIL import Image

import pyncm
from pyncm.apis import (
    WeapiCryptoRequest,
    GetCurrentSession,
    SetCurrentSession,
    track,
    playlist,
    user,
)

from qt6_player.data.tracks import NordicTrack, LyricLine, parse_lrc_text
from qt6_player.services.storage_service import load_player_state, update_player_state_fields


# --- 网易云底层 WeAPI 接口补充封装 ---

@WeapiCryptoRequest
def LoginQrcodeKey(type=1):
    """获取网易云扫码登录 unikey"""
    return '/weapi/login/qrcode/unikey', {'type': type}


@WeapiCryptoRequest
def LoginQrcodeCheck(key, type=1):
    """轮询扫码状态：800过期，801等待扫码，802待确认，803成功"""
    return '/weapi/login/qrcode/client/login', {'key': str(key), 'type': type}


@WeapiCryptoRequest
def GetLoginStatus():
    """获取当前登录用户信息"""
    return '/weapi/w/nuser/account/get', {}


@WeapiCryptoRequest
def GetDailyRecommendSongs():
    """获取每日推荐歌曲列表"""
    return '/weapi/v3/discovery/recommend/songs', {}


@WeapiCryptoRequest
def GetCustomTrackAudio(song_ids: list, level='lossless', encodeType='aac'):
    """
    网易云高规格音频直链 WeAPI（直接突破 PyNCM 内部白名单校验，原生直通 hires 与 jymaster 超清母带）
    支持 level: 'standard', 'higher', 'lossless', 'hires', 'jymaster'
    """
    ids = song_ids if isinstance(song_ids, list) else [song_ids]
    return '/weapi/song/enhance/player/url/v1', {'ids': ids, 'level': str(level), 'encodeType': encodeType}


@WeapiCryptoRequest
def CloudSearchSongs(keyword: str, limit: int = 30, offset: int = 0):
    """网易云在线搜索单曲"""
    return '/weapi/search/get', {'s': str(keyword), 'type': '1', 'limit': str(limit), 'offset': str(offset)}


class NeteaseCloudManager(QObject):
    """
    网易云音乐集成管理器 (基于 PyNCM 原生加密引擎)
    支持：免密二维码扫码登录、Cookie 本地持久化、同步个人歌单、每日推荐、在线无缝播放与缓存
    """
    loginStatusChanged = pyqtSignal(bool, dict)  # is_logged_in, profile
    playlistsLoaded = pyqtSignal(list)           # list of playlist dicts
    qrCodeGenerated = pyqtSignal(str, bytes)     # unikey, png_bytes
    qrStatusChecked = pyqtSignal(int, str, dict) # code, msg, profile
    cookieLoginChecked = pyqtSignal(bool, str, dict) # success, msg, profile
    dailyRecommendLoaded = pyqtSignal(list)      # list of NordicTrack
    playlistTracksLoaded = pyqtSignal(int, list) # playlist_id, list of NordicTrack
    searchResultsLoaded = pyqtSignal(str, list)  # keyword, list of NordicTrack

    QUALITY_LABELS = {
        "standard": "标准 128k",
        "higher": "极高 320k",
        "lossless": "无损 FLAC",
        "hires": "Hi-Res 无损",
        "jymaster": "超清母带",
    }

    def __init__(self, parent: Optional[QObject] = None):
        super().__init__(parent)
        self.is_logged_in: bool = False
        self.user_profile: Dict[str, Any] = {}
        self.user_playlists: List[Dict[str, Any]] = []
        self._cache_dir = self._get_cache_dir()

        state = load_player_state()
        self.preferred_quality: str = state.get("netease_quality", "lossless")
        if self.preferred_quality not in self.QUALITY_LABELS:
            self.preferred_quality = "lossless"
        self.cache_limit_mb: int = int(state.get("netease_cache_limit_mb", 2048))

        # 初始化 Session 并恢复本地存储的 Cookie
        self._init_session()
        self._enforce_cache_quota()

    def set_preferred_quality(self, quality: str) -> None:
        if quality in self.QUALITY_LABELS:
            self.preferred_quality = quality
            update_player_state_fields({"netease_quality": quality})

    def get_cache_size_bytes(self) -> int:
        total = 0
        try:
            if self._cache_dir.exists():
                for f in self._cache_dir.iterdir():
                    if f.is_file():
                        total += f.stat().st_size
        except Exception:
            pass
        return total

    def get_cache_count(self) -> int:
        count = 0
        try:
            if self._cache_dir.exists():
                for f in self._cache_dir.iterdir():
                    if f.is_file():
                        count += 1
        except Exception:
            pass
        return count

    def get_cache_size_str(self) -> str:
        b = self.get_cache_size_bytes()
        c = self.get_cache_count()
        mb = b / (1024 * 1024)
        if mb >= 1024:
            return f"{mb / 1024:.2f} GB ({c} 首曲目)"
        return f"{mb:.1f} MB ({c} 首曲目)"

    def clear_cache(self) -> Tuple[int, int]:
        """清除所有缓存文件，返回 (删除文件数, 释放字节数)"""
        count = 0
        freed = 0
        try:
            if self._cache_dir.exists():
                for f in list(self._cache_dir.iterdir()):
                    if f.is_file():
                        try:
                            sz = f.stat().st_size
                            f.unlink(missing_ok=True)
                            count += 1
                            freed += sz
                        except Exception:
                            pass
        except Exception:
            pass
        return count, freed

    def set_cache_limit_mb(self, limit_mb: int) -> None:
        """设置缓存配额上限 (MB)，0 表示不限"""
        self.cache_limit_mb = max(0, int(limit_mb))
        update_player_state_fields({"netease_cache_limit_mb": self.cache_limit_mb})
        self._enforce_cache_quota()

    def _enforce_cache_quota(self) -> None:
        """当总缓存超出配额时，按 LRU (最旧访问/修改时间) 淘汰清理至配额的 85%"""
        if self.cache_limit_mb <= 0:
            return
        limit_bytes = self.cache_limit_mb * 1024 * 1024
        try:
            if not self._cache_dir.exists():
                return
            files = []
            total_size = 0
            for f in self._cache_dir.iterdir():
                if f.is_file() and not f.name.endswith(".tmp"):
                    st = f.stat()
                    total_size += st.st_size
                    access_time = max(st.st_atime, st.st_mtime)
                    files.append((access_time, st.st_size, f))

            if total_size <= limit_bytes:
                return

            target_bytes = int(limit_bytes * 0.85)
            files.sort(key=lambda x: x[0])
            for _, size, f in files:
                try:
                    f.unlink(missing_ok=True)
                    total_size -= size
                    if total_size <= target_bytes:
                        break
                except Exception:
                    pass
        except Exception as e:
            logger.warning(f"淘汰网易云缓存异常: {e}")

    def _get_cache_dir(self) -> Path:
        if getattr(sys, "frozen", False):
            app_data = Path(os.getenv("APPDATA", Path.home())) / "Klang" / "cache" / "netease"
        else:
            app_data = Path(__file__).resolve().parent.parent.parent / "cache" / "netease"
        app_data.mkdir(parents=True, exist_ok=True)
        return app_data

    def _init_session(self) -> None:
        sess = GetCurrentSession()
        sess.headers["User-Agent"] = (
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
            "AppleWebKit/537.36 (KHTML, like Gecko) "
            "Chrome/126.0.0.0 Safari/537.36"
        )
        # 预先注入 PC 客户端标识 Cookie，降低网易云网关风控与 8821 频次
        sess.cookies.set("os", "pc", domain=".music.163.com")
        sess.cookies.set("appver", "3.0.18.203152", domain=".music.163.com")
        sess.cookies.set("channel", "netease", domain=".music.163.com")
        sess.cookies.set("os", "pc")
        sess.cookies.set("appver", "3.0.18.203152")

        state = load_player_state()
        saved_cookies = state.get("netease_cookies")
        saved_profile = state.get("netease_profile")

        if saved_cookies and isinstance(saved_cookies, dict):
            for k, v in saved_cookies.items():
                sess.cookies.set(str(k), str(v), domain=".music.163.com")
                sess.cookies.set(str(k), str(v))
            if "__csrf" in saved_cookies:
                sess.csrf_token = str(saved_cookies["__csrf"])

        if saved_profile and isinstance(saved_profile, dict) and saved_profile.get("userId"):
            self.user_profile = saved_profile
            self.is_logged_in = True
            sess.login_info = {
                "tick": time.time(),
                "success": True,
                "content": {
                    "account": {"id": saved_profile.get("userId", 0)},
                    "profile": saved_profile,
                },
            }

        # 后台静默校验当前登录状态
        threading.Thread(target=self._verify_login_async, daemon=True).start()

    def _verify_login_async(self) -> None:
        try:
            res = GetLoginStatus()
            if res.get("code") == 200 and res.get("profile"):
                self.user_profile = res["profile"]
                self.is_logged_in = True
                sess = GetCurrentSession()
                account = res.get("account") or {"id": self.user_profile.get("userId", 0)}
                sess.login_info = {
                    "tick": time.time(),
                    "success": True,
                    "content": {
                        "account": account,
                        "profile": self.user_profile,
                    },
                }
                self._save_session_state()
                self.loginStatusChanged.emit(True, self.user_profile)
                self.fetch_user_playlists()
            else:
                # 仅在接口明确返回未登录错误码时重置，网络断开时不强制踢出
                if self.is_logged_in and res.get("code") in (301, 400, 401, 403):
                    self.is_logged_in = False
                    self.user_profile = {}
                    self._save_session_state()
                    self.loginStatusChanged.emit(False, {})
        except Exception:
            pass

    def _save_session_state(self) -> None:
        sess = GetCurrentSession()
        cookies = sess.cookies.get_dict() if self.is_logged_in else {}
        profile = self.user_profile if self.is_logged_in else {}
        update_player_state_fields({
            "netease_cookies": cookies,
            "netease_profile": profile,
        })

    # ----------------- 二维码登录流程 -----------------

    def request_qr_login_async(self) -> None:
        """异步请求二维码并在主线程发射 qrCodeGenerated 信号"""
        def _worker():
            try:
                unikey, png_bytes = self.generate_qr_login()
                self.qrCodeGenerated.emit(unikey, png_bytes)
            except Exception:
                self.qrCodeGenerated.emit("", b"")

        threading.Thread(target=_worker, daemon=True).start()

    def poll_qr_status_async(self, unikey: str) -> None:
        """异步轮询状态并在主线程发射 qrStatusChecked 信号"""
        def _worker():
            try:
                code, msg, prof = self.poll_qr_status(unikey)
                self.qrStatusChecked.emit(code, msg, prof or {})
            except Exception as e:
                self.qrStatusChecked.emit(-1, str(e), {})

        threading.Thread(target=_worker, daemon=True).start()

    def generate_qr_login(self) -> Tuple[str, bytes]:
        """
        申请二维码 Unikey 并生成适合显示的 PNG 原始数据字节
        （可在子线程安全调用，由主线程直接 loadFromData 转换为 QPixmap）
        """
        res = LoginQrcodeKey()
        unikey = res.get("unikey", "")
        if not unikey:
            return "", b""

        qr_url = f"https://music.163.com/login?codekey={unikey}"
        qr = qrcode.QRCode(
            version=1,
            error_correction=qrcode.constants.ERROR_CORRECT_M,
            box_size=6,
            border=2,
        )
        qr.add_data(qr_url)
        qr.make(fit=True)
        pil_img = qr.make_image(fill_color="#181B1E", back_color="#FFFFFF").convert("RGBA")

        buf = io.BytesIO()
        pil_img.save(buf, format="PNG")
        return unikey, buf.getvalue()

    def poll_qr_status(self, unikey: str) -> Tuple[int, str, Optional[Dict[str, Any]]]:
        """
        检查二维码状态：
        800: 二维码已过期
        801: 等待用户扫码
        802: 待手机确认授权
        803: 授权登录成功
        """
        try:
            res = LoginQrcodeCheck(key=unikey)
            code = int(res.get("code", 0))
            msg = res.get("message", "")

            sess = GetCurrentSession()

            if code == 803:
                # 登录成功，从返回中提取并写入 Cookie
                raw_cookie = res.get("cookie", "")
                if raw_cookie:
                    try:
                        from http.cookies import SimpleCookie
                        sc = SimpleCookie()
                        sc.load(raw_cookie)
                        for k, m in sc.items():
                            sess.cookies.set(k, m.value, domain=".music.163.com")
                            sess.cookies.set(k, m.value)
                    except Exception:
                        pass
                    for item in raw_cookie.split(";"):
                        item = item.strip()
                        if "=" in item:
                            k, v = item.split("=", 1)
                            k, v = k.strip(), v.strip()
                            if k and k.lower() not in ("path", "domain", "expires", "max-age", "secure", "httponly", "samesite"):
                                sess.cookies.set(k, v, domain=".music.163.com")
                                sess.cookies.set(k, v)

                if "__csrf" in sess.cookies:
                    sess.csrf_token = sess.cookies["__csrf"]

                # 校验并提取登录账号资料
                status_res = GetLoginStatus()
                if status_res.get("code") == 200 and status_res.get("profile"):
                    self.user_profile = status_res["profile"]
                    account = status_res.get("account") or {"id": self.user_profile.get("userId", 0)}
                else:
                    self.user_profile = {
                        "nickname": "网易云用户",
                        "avatarUrl": "",
                        "userId": res.get("userId", 0),
                    }
                    account = {"id": res.get("userId", 0)}

                sess.login_info = {
                    "tick": time.time(),
                    "success": True,
                    "content": {
                        "account": account,
                        "profile": self.user_profile,
                    },
                }

                self.is_logged_in = True
                self._save_session_state()
                self.loginStatusChanged.emit(True, self.user_profile)
                self.fetch_user_playlists()
                return 803, "登录成功", self.user_profile

            return code, msg, None
        except Exception as e:
            return -1, str(e), None

    def login_via_cookie_async(self, cookie_input: str) -> None:
        """异步执行 Cookie 鉴权并在主线程发射 cookieLoginChecked 信号"""
        def _worker():
            try:
                ok, msg, prof = self.login_via_cookie(cookie_input)
                self.cookieLoginChecked.emit(ok, msg, prof or {})
            except Exception as e:
                self.cookieLoginChecked.emit(False, str(e), {})

        threading.Thread(target=_worker, daemon=True).start()

    def login_via_cookie(self, cookie_input: str) -> Tuple[bool, str, Dict[str, Any]]:
        """
        通过 Cookie / MUSIC_U 登录鉴权（100% 绕过任何移动端扫码风控与 8821 限制）
        """
        raw = (cookie_input or "").strip()
        if not raw:
            return False, "请输入有效 Cookie 或 MUSIC_U 凭证", {}

        # 兼容用户直接粘贴 MUSIC_U 值
        if "=" not in raw:
            raw = f"MUSIC_U={raw}"

        sess = GetCurrentSession()
        cookie_dict: Dict[str, str] = {}
        for item in raw.split(";"):
            item = item.strip()
            if "=" in item:
                k, v = item.split("=", 1)
                k, v = k.strip(), v.strip()
                if k and k.lower() not in ("path", "domain", "expires", "max-age", "secure", "httponly", "samesite"):
                    cookie_dict[k] = v

        if not cookie_dict:
            return False, "未能识别到有效凭证键值", {}

        for k, v in cookie_dict.items():
            sess.cookies.set(k, v, domain=".music.163.com")
            sess.cookies.set(k, v)

        if "__csrf" in cookie_dict:
            sess.csrf_token = cookie_dict["__csrf"]

        try:
            status_res = GetLoginStatus()
            if status_res.get("code") == 200 and status_res.get("profile"):
                self.user_profile = status_res["profile"]
                account = status_res.get("account") or {"id": self.user_profile.get("userId", 0)}
                sess.login_info = {
                    "tick": time.time(),
                    "success": True,
                    "content": {
                        "account": account,
                        "profile": self.user_profile,
                    },
                }
                self.is_logged_in = True
                self._save_session_state()
                self.loginStatusChanged.emit(True, self.user_profile)
                self.fetch_user_playlists()
                return True, "登录成功", self.user_profile
            else:
                return False, "Cookie 无效或已过期，请重新在网页端获取", {}
        except Exception as e:
            return False, f"验证失败: {str(e)}", {}

    def logout(self) -> None:
        """退出当前网易云账号"""
        sess = GetCurrentSession()
        sess.cookies.clear()
        sess.login_info = {"success": False, "tick": time.time(), "content": None}
        self.is_logged_in = False
        self.user_profile = {}
        self.user_playlists = []
        self._save_session_state()
        self.loginStatusChanged.emit(False, {})
        self.playlistsLoaded.emit([])

    # ----------------- 歌单与歌曲数据获取 -----------------

    def fetch_user_playlists(self) -> List[Dict[str, Any]]:
        """获取当前登录用户的歌单列表"""
        user_id = self.user_profile.get("userId")
        if not user_id:
            return []

        def _do_fetch():
            try:
                res = user.GetUserPlaylists(user_id=user_id, limit=50)
                if res.get("code") == 200:
                    raw_plists = res.get("playlist", [])
                    cleaned = []
                    for pl in raw_plists:
                        cleaned.append({
                            "id": pl.get("id"),
                            "name": pl.get("name", "未命名歌单"),
                            "cover_url": pl.get("coverImgUrl", ""),
                            "track_count": pl.get("trackCount", 0),
                            "play_count": pl.get("playCount", 0),
                            "is_liked": bool(pl.get("specialType") == 5),
                        })
                    self.user_playlists = cleaned
                    self.playlistsLoaded.emit(cleaned)
            except Exception:
                pass

        threading.Thread(target=_do_fetch, daemon=True).start()
        return self.user_playlists

    def get_daily_recommend_tracks(self) -> List[NordicTrack]:
        """获取每日推荐歌曲列表"""
        try:
            res = GetDailyRecommendSongs()
            if res.get("code") == 200 and "data" in res and "dailySongs" in res["data"]:
                songs = res["data"]["dailySongs"]
                return self._parse_songs_to_tracks(songs)
        except Exception:
            pass
        return []

    def get_playlist_tracks(self, playlist_id: int) -> List[NordicTrack]:
        """获取指定歌单的所有歌曲（支持完整分批全量拉取）"""
        try:
            res = playlist.GetPlaylistInfo(playlist_id=playlist_id)
            if res.get("code") == 200 and "playlist" in res:
                pl = res["playlist"]
                track_ids = [t.get("id") for t in pl.get("trackIds", []) if t.get("id")]
                if track_ids:
                    all_songs = []
                    chunk_size = 100
                    # 分批获取完整歌曲详情，最多获取前 500 首
                    for i in range(0, min(len(track_ids), 500), chunk_size):
                        chunk = track_ids[i:i + chunk_size]
                        d_res = track.GetTrackDetail(chunk)
                        if d_res.get("code") == 200 and "songs" in d_res:
                            all_songs.extend(d_res["songs"])
                    if all_songs:
                        return self._parse_songs_to_tracks(all_songs)

                tracks_data = pl.get("tracks", [])
                return self._parse_songs_to_tracks(tracks_data)
        except Exception:
            pass
        return []

    def fetch_daily_recommend_async(self) -> None:
        """异步获取每日推荐并在主线程发射 dailyRecommendLoaded"""
        def _worker():
            try:
                tracks = self.get_daily_recommend_tracks()
                self.dailyRecommendLoaded.emit(tracks)
            except Exception:
                self.dailyRecommendLoaded.emit([])
        threading.Thread(target=_worker, daemon=True).start()

    def fetch_playlist_tracks_async(self, playlist_id: int) -> None:
        """异步获取歌单并在主线程发射 playlistTracksLoaded"""
        def _worker():
            try:
                tracks = self.get_playlist_tracks(playlist_id)
                self.playlistTracksLoaded.emit(int(playlist_id), tracks)
            except Exception:
                self.playlistTracksLoaded.emit(int(playlist_id), [])
        threading.Thread(target=_worker, daemon=True).start()

    def search_tracks(self, keyword: str, limit: int = 50) -> List[NordicTrack]:
        """通过网易云云端接口搜索单曲"""
        kw = (keyword or "").strip()
        if not kw:
            return []
        try:
            res = CloudSearchSongs(keyword=kw, limit=limit, offset=0)
            if res.get("code") == 200:
                result = res.get("result", {})
                songs = result.get("songs") or []
                return self._parse_songs_to_tracks(songs)
        except Exception as e:
            logger.warning(f"网易云在线搜索异常 [{kw}]: {e}")
        return []

    def search_tracks_async(self, keyword: str, limit: int = 50) -> None:
        """异步搜索单曲并在主线程发射 searchResultsLoaded"""
        def _worker():
            try:
                tracks = self.search_tracks(keyword, limit=limit)
                self.searchResultsLoaded.emit(keyword, tracks)
            except Exception:
                self.searchResultsLoaded.emit(keyword, [])
        threading.Thread(target=_worker, daemon=True).start()

    def get_cached_cover_path(self, song_id: str) -> Optional[str]:
        p = self._cache_dir / f"{song_id}_cover.jpg"
        if p.exists() and p.stat().st_size > 1024:
            return str(p.resolve())
        return None

    def cache_cover_image(self, song_id: str, url: str) -> Optional[str]:
        """将封面下载缓存到本地（原子写入，防并发竞争读写导致损坏）"""
        try:
            p = self._cache_dir / f"{song_id}_cover.jpg"
            if p.exists() and p.stat().st_size > 1024:
                return str(p.resolve())
            img_url = url + "?param=400y400" if "?" not in url else url
            resp = requests.get(img_url, timeout=6)
            if resp.status_code == 200 and len(resp.content) > 1024:
                tmp_p = p.with_suffix(".tmp")
                with open(tmp_p, "wb") as f:
                    f.write(resp.content)
                tmp_p.replace(p)
                return str(p.resolve())
        except Exception:
            pass
        return None

    def _parse_songs_to_tracks(self, songs_data: List[Dict[str, Any]]) -> List[NordicTrack]:
        tracks: List[NordicTrack] = []
        for s in songs_data:
            song_id = str(s.get("id", ""))
            if not song_id:
                continue
            title = s.get("name", "未知曲目")
            artists = s.get("ar") or s.get("artists") or []
            artist_name = " / ".join(a.get("name", "") for a in artists if a.get("name")) or "未知歌手"
            album_info = s.get("al") or s.get("album") or {}
            album_name = album_info.get("name", "")
            cover_url = album_info.get("picUrl") or s.get("picUrl", "")
            duration_ms = s.get("dt") or s.get("duration") or 0
            duration_sec = float(duration_ms) / 1000.0 if duration_ms > 0 else 0.0

            local_cover = self.get_cached_cover_path(song_id)
            final_cover = local_cover if local_cover else cover_url

            cache_file = self._cache_dir / f"{song_id}.mp3"
            local_audio = str(cache_file.resolve()) if cache_file.exists() and cache_file.stat().st_size > 10240 else None

            nt = NordicTrack(
                track_id=f"netease_{song_id}",
                title=title,
                artist=artist_name,
                album=album_name,
                duration=duration_sec,
                file_path=local_audio,
                cover_path=final_cover,
                audio_format="网易云在线" if not local_audio else "网易云缓存",
            )
            # 挂载网易云原生属性，方便后续操作
            nt._netease_song_id = song_id
            nt._cover_url = cover_url
            tracks.append(nt)
        return tracks

    # ----------------- 音频直链与离线缓存 -----------------

    def resolve_track_playback_path(self, track_obj: NordicTrack, quality: Optional[str] = None) -> Optional[str]:
        """
        为网易云曲目获取指定音质的可播放本地绝对路径（激活硬件 DSP / 均衡器 / 逐字音浪）
        支持音质：'standard' (标准 128k), 'higher' (极高 320k), 'lossless' (无损 FLAC), 'hires' (Hi-Res), 'jymaster' (超清母带)
        """
        song_id = getattr(track_obj, "_netease_song_id", None)
        if not song_id and track_obj.track_id.startswith("netease_"):
            song_id = track_obj.track_id.replace("netease_", "")
        if not song_id:
            return track_obj.file_path

        req_quality = quality if quality in self.QUALITY_LABELS else self.preferred_quality

        # 尝试缓存封面大图
        cover_url = getattr(track_obj, "_cover_url", None) or (track_obj.cover_path if track_obj.cover_path and track_obj.cover_path.startswith("http") else None)
        if cover_url:
            cached_c = self.cache_cover_image(song_id, cover_url)
            if cached_c:
                track_obj.cover_path = cached_c
                track_obj._cached_cover_pix = None
                track_obj._cover_cache_loaded = False

        # 检查是否已存在当前请求音质等级的本地缓存
        for ext in ("flac", "m4a", "mp3", "ogg"):
            q_cache = self._cache_dir / f"{song_id}_{req_quality}.{ext}"
            if q_cache.exists() and q_cache.stat().st_size > 10240:
                track_obj.file_path = str(q_cache.resolve())
                self._apply_quality_metadata(track_obj, req_quality, ext)
                return track_obj.file_path

        # 换取真实音频直链并自动向下级降级
        stream_info = self._get_stream_info(song_id, quality=req_quality)
        url = stream_info.get("url", "")
        file_ext = (stream_info.get("type") or "mp3").lower()
        br = stream_info.get("br", 0)
        actual_level = stream_info.get("level") or req_quality

        if not url:
            return None

        final_cache = self._cache_dir / f"{song_id}_{actual_level}.{file_ext}"
        if final_cache.exists() and final_cache.stat().st_size > 10240:
            track_obj.file_path = str(final_cache.resolve())
            self._apply_quality_metadata(track_obj, actual_level, file_ext, br)
            return track_obj.file_path

        tmp_cache = final_cache.with_suffix(".tmp")
        try:
            resp = requests.get(
                url,
                headers={
                    "User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36",
                    "Referer": "https://music.163.com/",
                },
                stream=True,
                timeout=(8, 45),
            )
            if resp.status_code in (200, 206):
                bytes_written = 0
                with open(tmp_cache, "wb") as f:
                    for chunk in resp.iter_content(chunk_size=65536):
                        if chunk:
                            f.write(chunk)
                            bytes_written += len(chunk)
                if bytes_written > 10240 and tmp_cache.exists():
                    tmp_cache.replace(final_cache)
                    track_obj.file_path = str(final_cache.resolve())
                    self._apply_quality_metadata(track_obj, actual_level, file_ext, br)
                    self._enforce_cache_quota()
                    return track_obj.file_path
        except Exception as e:
            logger.warning(f"下载网易云音轨异常 [{song_id}]: {e}")
            if tmp_cache.exists():
                try:
                    tmp_cache.unlink()
                except Exception:
                    pass

        return None

    def _apply_quality_metadata(self, track_obj: NordicTrack, quality: str, ext: str, br: int = 0) -> None:
        if br > 0:
            track_obj.bitrate_kbps = int(br // 1000)
        ext_upper = ext.upper()
        if quality in ("jymaster", "jyeffect"):
            track_obj.audio_format = "FLAC 超清母带"
            track_obj.bit_depth = 24
            track_obj.sample_rate = 96000
        elif quality == "hires":
            track_obj.audio_format = "FLAC Hi-Res"
            track_obj.bit_depth = 24
            track_obj.sample_rate = 96000
        elif quality == "lossless":
            track_obj.audio_format = f"{ext_upper} 无损"
            track_obj.bit_depth = 16
            track_obj.sample_rate = 44100
        elif quality == "higher":
            track_obj.audio_format = f"{ext_upper} 320k"
            track_obj.bit_depth = 16
            track_obj.sample_rate = 44100
        else:
            track_obj.audio_format = f"{ext_upper} 标准"
            track_obj.bit_depth = 16
            track_obj.sample_rate = 44100

    def _get_stream_info(self, song_id: str, quality: str = "lossless") -> Dict[str, Any]:
        ordered_levels = ["jymaster", "hires", "lossless", "higher", "standard"]
        if quality in ordered_levels:
            start_idx = ordered_levels.index(quality)
            levels_to_try = ordered_levels[start_idx:]
        else:
            levels_to_try = ["lossless", "higher", "standard"]
        if "standard" not in levels_to_try:
            levels_to_try.append("standard")

        for lvl in levels_to_try:
            try:
                res = GetCustomTrackAudio(song_ids=[song_id], level=lvl)
                if res.get("code") == 200 and "data" in res and res["data"]:
                    item = res["data"][0]
                    url = item.get("url", "")
                    if url and "http" in url:
                        return {
                            "url": url,
                            "type": str(item.get("type") or "mp3").lower(),
                            "br": int(item.get("br") or 0),
                            "size": int(item.get("size") or 0),
                            "level": str(item.get("level") or lvl),
                        }
            except Exception:
                pass

        try:
            res = track.GetTrackAudio([song_id], quality="standard")
            if res.get("code") == 200 and "data" in res and res["data"]:
                item = res["data"][0]
                url = item.get("url", "")
                if url and "http" in url:
                    return {
                        "url": url,
                        "type": str(item.get("type") or "mp3").lower(),
                        "br": int(item.get("br") or 0),
                        "size": int(item.get("size") or 0),
                        "level": "standard",
                    }
        except Exception:
            pass

        return {}

    def fetch_track_lyrics(self, track_obj: NordicTrack) -> List[LyricLine]:
        """获取网易云曲目的双语逐行歌词"""
        song_id = getattr(track_obj, "_netease_song_id", None)
        if not song_id and track_obj.track_id.startswith("netease_"):
            song_id = track_obj.track_id.replace("netease_", "")
        if not song_id:
            return []

        try:
            res = track.GetTrackLyrics(song_id=song_id)
            if res.get("code") == 200:
                lrc_text = res.get("lrc", {}).get("lyric", "")
                tlyric_text = res.get("tlyric", {}).get("lyric", "")
                if lrc_text:
                    parsed = parse_lrc_text(lrc_text, trans_lrc=tlyric_text)
                    track_obj.lyrics = parsed
                    return parsed
        except Exception:
            pass
        return []


# 单例管理实例
netease_manager = NeteaseCloudManager()
