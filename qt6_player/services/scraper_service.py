"""
在线歌词与专辑封面刮削服务 (Multi-Source Scraper Service)
集成音源：
1. 网易云音乐接口 (支持双语滚动 LRC 歌词 + 高清专辑封面)
2. LRCLIB 开放歌词库接口 (全球曲目精准时间轴 LRC)
3. Apple iTunes Search 接口 (600x600 官方高清专辑封面)
支持后台自动静默刮削与手动搜索匹配弹窗，并支持将刮削到的 .lrc 自动保存至本地音频同目录。
"""

from dataclasses import dataclass
from pathlib import Path
from typing import List, Optional, Tuple
import requests

from PyQt6.QtCore import Qt, QThread, pyqtSignal, QRectF
from PyQt6.QtGui import QPainter, QPen, QColor, QFont, QPainterPath, QPixmap
from PyQt6.QtWidgets import (
    QVBoxLayout,
    QHBoxLayout,
    QListWidget,
    QListWidgetItem,
    QLabel,
    QWidget,
)

from qt6_player.data.tracks import NordicTrack, LyricLine, parse_lrc_text
from qt6_player.theme.nordic_palette import NordicThemeColors, DAYLIGHT_THEME, get_nordic_font
from qt6_player.components.fluent_widgets import (
    WinUIModalDialog,
    WinUISearchBox,
    WinUICheckChip,
)
from qt6_player.components.title_bar import WinUIChipButton


_HEADERS = {
    "User-Agent": (
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) "
        "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36"
    ),
    "Referer": "https://music.163.com/",
}


@dataclass
class ScrapeCandidate:
    source: str           # "网易云" | "LRCLIB" | "iTunes"
    song_id: str
    title: str
    artist: str
    album: str
    duration: float
    cover_url: str = ""
    synced_lrc: str = ""
    trans_lrc: str = ""


def search_netease_candidates(keyword: str, limit: int = 8) -> List[ScrapeCandidate]:
    """从网易云搜索歌曲候选列表"""
    results: List[ScrapeCandidate] = []
    if not keyword.strip():
        return results
    try:
        resp = requests.post(
            "https://music.163.com/api/search/get/web",
            data={"s": keyword.strip(), "type": 1, "offset": 0, "total": "true", "limit": limit},
            headers=_HEADERS,
            timeout=6,
        )
        data = resp.json()
        songs = data.get("result", {}).get("songs") or []
        for s in songs:
            sid = str(s.get("id", ""))
            if not sid:
                continue
            artists = " / ".join(a.get("name", "") for a in (s.get("artists") or []) if a.get("name"))
            album_obj = s.get("album") or {}
            album_name = album_obj.get("name", "")
            pic_url = album_obj.get("picUrl", "")
            dur = float(s.get("duration", 0)) / 1000.0
            results.append(
                ScrapeCandidate(
                    source="网易云",
                    song_id=sid,
                    title=s.get("name", ""),
                    artist=artists or "未知歌手",
                    album=album_name,
                    duration=dur,
                    cover_url=f"{pic_url}?param=600y600" if pic_url else "",
                )
            )
    except Exception:
        pass
    return results


def fetch_netease_lyric_and_cover(song_id: str, existing_cover_url: str = "") -> Tuple[str, str, str]:
    """根据网易云 song_id 获取原版 LRC、翻译 LRC 与高清专辑封面 URL"""
    primary_lrc, trans_lrc, cover_url = "", "", existing_cover_url
    try:
        lrc_resp = requests.get(
            f"https://music.163.com/api/song/lyric?os=pc&id={song_id}&lv=-1&kv=-1&tv=-1",
            headers=_HEADERS,
            timeout=6,
        )
        lrc_json = lrc_resp.json()
        primary_lrc = (lrc_json.get("lrc") or {}).get("lyric") or ""
        trans_lrc = (lrc_json.get("tlyric") or {}).get("lyric") or ""
    except Exception:
        pass

    if not cover_url:
        try:
            det_resp = requests.get(
                f"https://music.163.com/api/song/detail/?id={song_id}&ids=%5B{song_id}%5D",
                headers=_HEADERS,
                timeout=6,
            )
            songs = det_resp.json().get("songs") or []
            if songs:
                pic = (songs[0].get("album") or {}).get("picUrl") or ""
                if pic:
                    cover_url = f"{pic}?param=600y600"
        except Exception:
            pass

    return primary_lrc, trans_lrc, cover_url


def search_lrclib_candidates(keyword: str, limit: int = 6) -> List[ScrapeCandidate]:
    """从 LRCLIB 搜索全球同步 LRC 歌词候选"""
    results: List[ScrapeCandidate] = []
    if not keyword.strip():
        return results
    try:
        resp = requests.get(
            "https://lrclib.net/api/search",
            params={"q": keyword.strip()},
            headers={"User-Agent": "NordicPlayerQt6/1.0"},
            timeout=6,
        )
        items = resp.json()
        if isinstance(items, list):
            for item in items[:limit]:
                synced = item.get("syncedLyrics") or item.get("plainLyrics") or ""
                if not synced:
                    continue
                results.append(
                    ScrapeCandidate(
                        source="LRCLIB",
                        song_id=str(item.get("id", "")),
                        title=item.get("trackName") or "",
                        artist=item.get("artistName") or "",
                        album=item.get("albumName") or "",
                        duration=float(item.get("duration") or 0.0),
                        synced_lrc=synced,
                    )
                )
    except Exception:
        pass
    return results


def fetch_itunes_cover_url(keyword: str) -> str:
    """从 Apple iTunes Search 获取 600x600 高清专辑封面 URL"""
    if not keyword.strip():
        return ""
    try:
        resp = requests.get(
            "https://itunes.apple.com/search",
            params={"term": keyword.strip(), "entity": "song", "limit": 3},
            timeout=6,
        )
        results = resp.json().get("results") or []
        for r in results:
            art = r.get("artworkUrl100") or ""
            if art:
                return art.replace("100x100bb", "600x600bb")
    except Exception:
        pass
    return ""


def download_image_bytes(url: str) -> Optional[bytes]:
    if not url:
        return None
    try:
        r = requests.get(url, headers=_HEADERS, timeout=7)
        if r.status_code == 200 and len(r.content) > 512:
            return r.content
    except Exception:
        pass
    return None


def save_scraped_files_alongside_audio(
    audio_file_path: Optional[str],
    primary_lrc: str,
    cover_bytes: Optional[bytes],
) -> Optional[str]:
    """若为本地音频文件，将刮削到的 .lrc 歌词和同名封面保存到音频所在目录"""
    if not audio_file_path:
        return None
    saved_cover_path: Optional[str] = None
    try:
        p = Path(audio_file_path)
        if not p.parent.is_dir():
            return None
        if primary_lrc.strip():
            lrc_path = p.parent / f"{p.stem}.lrc"
            if not lrc_path.exists():
                lrc_path.write_text(primary_lrc, encoding="utf-8")
        if cover_bytes:
            cov_path = p.parent / f"{p.stem}.jpg"
            if not cov_path.exists():
                cov_path.write_bytes(cover_bytes)
            saved_cover_path = str(cov_path)
    except Exception:
        pass
    return saved_cover_path


class AutoScrapeWorker(QThread):
    """后台静默刮削线程：自动为缺失歌词或封面的曲目匹配在线词图"""

    scrapeCompleted = pyqtSignal(str, list, object, str)  # track_id, List[LyricLine], Optional[bytes], album_name

    def __init__(self, track: NordicTrack, need_lyrics: bool = True, need_cover: bool = True, parent=None):
        super().__init__(parent)
        self.track = track
        self.need_lyrics = need_lyrics
        self.need_cover = need_cover

    def run(self) -> None:
        artist_clean = "" if self.track.artist in ("未知歌手", "Unknown") else self.track.artist
        query = f"{artist_clean} {self.track.title}".strip()
        if not query:
            return

        primary_lrc, trans_lrc = "", ""
        cover_url = ""
        album_name = self.track.album

        # 1. 优先通过网易云搜索获取双语歌词 + 封面
        ne_list = search_netease_candidates(query, limit=3)
        if ne_list:
            best = ne_list[0]
            if not album_name and best.album:
                album_name = best.album
            p_lrc, t_lrc, c_url = fetch_netease_lyric_and_cover(best.song_id, best.cover_url)
            if self.need_lyrics and p_lrc.strip():
                primary_lrc, trans_lrc = p_lrc, t_lrc
            if self.need_cover and c_url:
                cover_url = c_url

        # 2. 若网易云未命中歌词，回退至 LRCLIB
        if self.need_lyrics and not primary_lrc.strip():
            lrc_list = search_lrclib_candidates(query, limit=2)
            if not lrc_list and artist_clean:
                lrc_list = search_lrclib_candidates(self.track.title, limit=2)
            if lrc_list:
                primary_lrc = lrc_list[0].synced_lrc
                if not album_name and lrc_list[0].album:
                    album_name = lrc_list[0].album

        # 3. 若封面仍缺失，回退至 iTunes Search
        if self.need_cover and not cover_url:
            cover_url = fetch_itunes_cover_url(query) or fetch_itunes_cover_url(self.track.title)

        cover_bytes = download_image_bytes(cover_url) if (self.need_cover and cover_url) else None
        parsed_lyrics: List[LyricLine] = parse_lrc_text(primary_lrc, trans_lrc) if primary_lrc.strip() else []

        if primary_lrc.strip() or cover_bytes:
            save_scraped_files_alongside_audio(self.track.file_path, primary_lrc, cover_bytes)

        self.scrapeCompleted.emit(self.track.track_id, parsed_lyrics, cover_bytes, album_name)


class SearchWorker(QThread):
    resultsReady = pyqtSignal(list)  # List[ScrapeCandidate]

    def __init__(self, keyword: str, parent=None):
        super().__init__(parent)
        self.keyword = keyword

    def run(self) -> None:
        candidates: List[ScrapeCandidate] = []
        candidates.extend(search_netease_candidates(self.keyword, limit=6))
        candidates.extend(search_lrclib_candidates(self.keyword, limit=5))
        self.resultsReady.emit(candidates)


class FetchCandidateDetailWorker(QThread):
    detailReady = pyqtSignal(list, object, str, str)  # List[LyricLine], Optional[bytes], raw_lrc, album

    def __init__(self, candidate: ScrapeCandidate, parent=None):
        super().__init__(parent)
        self.candidate = candidate

    def run(self) -> None:
        cand = self.candidate
        primary_lrc = cand.synced_lrc
        trans_lrc = cand.trans_lrc
        cover_url = cand.cover_url

        if cand.source == "网易云" and cand.song_id:
            primary_lrc, trans_lrc, cover_url = fetch_netease_lyric_and_cover(cand.song_id, cover_url)

        if not cover_url:
            q = f"{cand.artist} {cand.title}".strip()
            cover_url = fetch_itunes_cover_url(q) or fetch_itunes_cover_url(cand.title)

        cover_bytes = download_image_bytes(cover_url) if cover_url else None
        lyrics = parse_lrc_text(primary_lrc, trans_lrc) if primary_lrc.strip() else []
        self.detailReady.emit(lyrics, cover_bytes, primary_lrc, cand.album)


class OnlineScraperDialog(WinUIModalDialog):
    """
    在线歌词与专辑封面刮削匹配窗口 (无边框圆角 WinUI 3 模态浮层)
    支持自定义搜索关键词、查看多源候选列表、勾选是否同时更新歌词/封面并一键应用。
    """

    matchApplied = pyqtSignal(list, object, str)  # List[LyricLine], Optional[bytes], album_name

    def __init__(self, track: NordicTrack, theme: NordicThemeColors = DAYLIGHT_THEME, parent: Optional[QWidget] = None):
        super().__init__("在线匹配滚动歌词与高清封面", 580, 446, theme=theme, parent=parent)
        self.track = track
        self.candidates: List[ScrapeCandidate] = []
        self._search_worker: Optional[SearchWorker] = None
        self._detail_worker: Optional[FetchCandidateDetailWorker] = None

        self._setup_ui()
        self._apply_theme_styles()

        # 自动填入当前曲目信息并发起搜索
        init_kw = (
            f"{track.artist} {track.title}".strip()
            if track.artist not in ("未知歌手", "Unknown")
            else track.title
        )
        self.search_input.setText(init_kw)
        self._start_search()

    def _setup_ui(self) -> None:
        layout = self._root_vbox

        # 顶部搜索行
        top_row = QHBoxLayout()
        top_row.setSpacing(8)
        self.search_input = WinUISearchBox("输入 歌名 或 歌手 - 歌名 搜索在线歌词与封面…", self)
        self.search_input.set_theme(self.theme)
        self.search_input.returnPressed.connect(self._start_search)

        self.btn_search = WinUIChipButton("搜索音源", active=True, icon_kind="search", fixed_w=88, parent=self)
        self.btn_search.setFixedHeight(32)
        self.btn_search.set_theme(self.theme)
        self.btn_search.clicked.connect(self._start_search)

        top_row.addWidget(self.search_input, 1)
        top_row.addWidget(self.btn_search)
        layout.addLayout(top_row)

        self.status_label = QLabel("正在搜索在线音源…", self)
        self.status_label.setFont(get_nordic_font(11))
        layout.addWidget(self.status_label)

        # 候选结果列表
        self.list_widget = QListWidget(self)
        self.list_widget.setFont(get_nordic_font(12))
        self.list_widget.itemDoubleClicked.connect(lambda _: self._apply_selected())
        layout.addWidget(self.list_widget, 1)

        # 底部选项与操作按钮（使用现代矢量勾选胶囊替代老旧 QCheckBox）
        bottom_row = QHBoxLayout()
        bottom_row.setSpacing(8)
        self.chk_lyrics = WinUICheckChip("更新滚动歌词", checked=True, parent=self)
        self.chk_lyrics.set_theme(self.theme)

        self.chk_cover = WinUICheckChip("更新专辑封面", checked=True, parent=self)
        self.chk_cover.set_theme(self.theme)

        self.chk_save_local = WinUICheckChip("保存至本地目录", checked=True, parent=self)
        self.chk_save_local.set_theme(self.theme)

        bottom_row.addWidget(self.chk_lyrics)
        bottom_row.addWidget(self.chk_cover)
        bottom_row.addWidget(self.chk_save_local)
        bottom_row.addStretch(1)

        self.btn_apply = WinUIChipButton("应用选中项", active=True, icon_kind="check", fixed_w=108, parent=self)
        self.btn_apply.setFixedHeight(32)
        self.btn_apply.set_theme(self.theme)
        self.btn_apply.clicked.connect(self._apply_selected)
        bottom_row.addWidget(self.btn_apply)

        layout.addLayout(bottom_row)

    def _apply_theme_styles(self) -> None:
        t = self.theme
        card = t.card_elevated.name()
        fg = t.charcoal.name()
        muted = t.slate_muted.name()
        sage = t.sage_accent.name()
        border = "rgba(38, 41, 46, 0.14)" if t.name == "daylight" else "rgba(255, 255, 255, 0.14)"

        self.list_widget.setStyleSheet(
            f"""
            QListWidget {{
                background-color: {card};
                color: {fg};
                border: 1px solid {border};
                border-radius: 8px;
                padding: 4px;
                outline: none;
            }}
            QListWidget::item {{
                padding: 9px 12px;
                border-radius: 6px;
                margin: 1px 0;
            }}
            QListWidget::item:hover {{
                background-color: rgba(110, 130, 119, 0.12);
            }}
            QListWidget::item:selected {{
                background-color: {sage};
                color: #FAF9F6;
            }}
            """
        )
        self.status_label.setStyleSheet(f"color: {muted}; background: transparent;")

    def _start_search(self) -> None:
        kw = self.search_input.text().strip()
        if not kw:
            return
        self.list_widget.clear()
        self.status_label.setText(f"正在从网易云音乐、LRCLIB、iTunes 搜索「{kw}」…")
        self.btn_search.setEnabled(False)

        self._search_worker = SearchWorker(kw, self)
        self._search_worker.resultsReady.connect(self._on_search_results)
        self._search_worker.start()

    def _on_search_results(self, candidates: List[ScrapeCandidate]) -> None:
        self.btn_search.setEnabled(True)
        self.candidates = candidates
        self.list_widget.clear()

        if not candidates:
            self.status_label.setText("未找到匹配项，请尝试调整关键词（如只输入歌名）。")
            return

        self.status_label.setText(f"找到 {len(candidates)} 个匹配结果，双击或点击「应用选中项」即可刮削：")
        for c in candidates:
            mins = int(c.duration) // 60
            secs = int(c.duration) % 60
            dur_str = f"{mins:02d}:{secs:02d}" if c.duration > 0 else "--:--"
            album_part = f" · 《{c.album}》" if c.album else ""
            item_text = f"[{c.source}]  {c.title} - {c.artist}{album_part}   ({dur_str})"
            self.list_widget.addItem(QListWidgetItem(item_text))

        self.list_widget.setCurrentRow(0)

    def _apply_selected(self) -> None:
        row = self.list_widget.currentRow()
        if not (0 <= row < len(self.candidates)):
            return
        cand = self.candidates[row]
        self.status_label.setText(f"正在下载「{cand.title}」的滚动歌词与高清封面…")
        self.btn_apply.setEnabled(False)

        self._detail_worker = FetchCandidateDetailWorker(cand, self)
        self._detail_worker.detailReady.connect(self._on_detail_ready)
        self._detail_worker.start()

    def _on_detail_ready(
        self,
        lyrics: List[LyricLine],
        cover_bytes: Optional[bytes],
        raw_lrc: str,
        album_name: str,
    ) -> None:
        self.btn_apply.setEnabled(True)
        out_lyrics = lyrics if self.chk_lyrics.isChecked() else []
        out_cover = cover_bytes if self.chk_cover.isChecked() else None

        if self.chk_save_local.isChecked() and self.track.file_path:
            save_scraped_files_alongside_audio(
                self.track.file_path,
                raw_lrc if self.chk_lyrics.isChecked() else "",
                out_cover,
            )

        self.matchApplied.emit(out_lyrics, out_cover, album_name)
        self.accept()

