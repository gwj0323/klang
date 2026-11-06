import re
from dataclasses import dataclass, field
from typing import List, Optional, Dict, Tuple
from pathlib import Path


@dataclass
class SyllableToken:
    text: str
    start: float
    end: float


@dataclass
class LyricLine:
    index: int
    time: float
    end_time: float
    text: str
    sub: str = ""
    is_interlude: bool = False
    syllables: List[SyllableToken] = field(default_factory=list)


@dataclass
class NordicTrack:
    track_id: str
    title: str
    artist: str
    album: str = ""
    duration: float = 0.0
    file_path: Optional[str] = None
    cover_path: Optional[str] = None
    cover_bytes: Optional[bytes] = None
    is_favorite: bool = False
    lyric_offset: float = 0.0  # 歌词时间轴微调秒数（正数提前显示，负数延后显示）
    sample_rate: int = 44100
    bit_depth: int = 16
    bitrate_kbps: int = 0
    channels: int = 2
    audio_format: str = ""
    palette: dict = field(
        default_factory=lambda: {
            "bg_primary": "#E7EAE6",
            "bg_secondary": "#D7DFDA",
            "accent": "#6E8277",
            "wood": "#C9BBAE",
            "ink": "#26292E",
        }
    )
    chords: List[List[float]] = field(default_factory=list)
    lyrics: List[LyricLine] = field(default_factory=list)
    _cached_cover_pix: object = field(default=None, init=False, repr=False, compare=False)
    _cover_cache_loaded: bool = field(default=False, init=False, repr=False, compare=False)

    @property
    def is_hi_res(self) -> bool:
        """根据 JEITA/JAS 高解析度音频标准判定是否为 Hi-Res"""
        return (self.sample_rate or 44100) > 48000 or (self.bit_depth or 16) > 16

    @property
    def sample_rate_str(self) -> str:
        """精准格式化采样率（如 44.1kHz, 48kHz, 96kHz, 192kHz）"""
        sr = self.sample_rate or 44100
        if sr % 1000 == 0:
            return f"{sr // 1000}kHz"
        if sr % 100 == 0:
            return f"{sr / 1000.0:.1f}kHz"
        return f"{sr / 1000.0:.2f}kHz"

    def format_audio_badge(self, is_atmos: bool = False, actual_channels: int = 2) -> str:
        """生成精准母带级音频规格胶囊文案"""
        ext = (self.audio_format or (Path(self.file_path).suffix.upper().lstrip(".") if self.file_path else "")).upper()
        if not ext:
            ext = "PCM"
        sr_txt = self.sample_rate_str
        bit_txt = f"{self.bit_depth}-bit" if (self.bit_depth and self.bit_depth in (16, 24, 32)) else ""

        if is_atmos:
            if actual_channels == 6:
                return f"{ext} · DOLBY ATMOS 5.1"
            elif actual_channels == 8:
                return f"{ext} · DOLBY ATMOS 7.1"
            if self.is_hi_res:
                return f"{ext} HI-RES · {sr_txt} · DOLBY ATMOS"
            return f"{ext} · DOLBY ATMOS"

        # 无损音频格式
        if ext in ("FLAC", "WAV", "APE", "ALAC", "AIFF", "DSD"):
            spec = f"{sr_txt} / {bit_txt}" if bit_txt else sr_txt
            if self.is_hi_res:
                return f"{ext} HI-RES · {spec}"
            return f"{ext} LOSSLESS · {spec}"

        # 有损压缩格式
        if ext in ("MP3", "AAC", "M4A", "OGG", "WMA", "OPUS"):
            if self.bitrate_kbps > 0:
                return f"{ext} · {self.bitrate_kbps}kbps · {sr_txt}"
            return f"{ext} · {sr_txt}"

        return f"{ext} · {sr_txt}"

    def invalidate_cover_cache(self) -> None:
        self._cached_cover_pix = None
        self._cover_cache_loaded = False

    def get_cover_pixmap(self):
        if self._cover_cache_loaded:
            return self._cached_cover_pix
        self._cover_cache_loaded = True
        from PyQt6.QtCore import Qt
        from PyQt6.QtGui import QPixmap

        pix = QPixmap()
        loaded = False
        if self.cover_bytes and pix.loadFromData(self.cover_bytes):
            loaded = True
        elif self.cover_path and pix.load(self.cover_path):
            loaded = True
        if loaded and not pix.isNull():
            if pix.width() > 512 or pix.height() > 512:
                pix = pix.scaled(
                    512,
                    512,
                    Qt.AspectRatioMode.KeepAspectRatioByExpanding,
                    Qt.TransformationMode.SmoothTransformation,
                )
            self._cached_cover_pix = pix
        else:
            self._cached_cover_pix = None
        return self._cached_cover_pix


def build_syllables(text: str, start_time: float, end_time: float) -> List[SyllableToken]:
    """将单行歌词切分为带起止时间的音节/字词序列"""
    has_cjk = bool(re.search(r"[\u4e00-\u9fa5]", text))
    if has_cjk:
        raw_tokens = re.findall(r"[\u4e00-\u9fa5]|[a-zA-Z0-9]+|\s+|[^\s\u4e00-\u9fa5a-zA-Z0-9]+", text) or [text]
    else:
        raw_tokens = re.findall(r"\S+\s*", text) or [text]

    total_span = max(0.5, (end_time - start_time) * 0.88)
    count = max(1, len(raw_tokens))
    step = total_span / count

    return [
        SyllableToken(
            text=token,
            start=round(start_time + idx * step, 2),
            end=round(start_time + (idx + 1) * step, 2),
        )
        for idx, token in enumerate(raw_tokens)
    ]


def create_lyric_lines(raw_items: List[dict]) -> List[LyricLine]:
    result: List[LyricLine] = []
    for idx, item in enumerate(raw_items):
        start_t = float(item["time"])
        next_t = float(raw_items[idx + 1]["time"]) if idx + 1 < len(raw_items) else start_t + 5.0
        end_t = float(item.get("end", max(start_t + 1.5, next_t - 0.2)))
        is_interlude = bool(item.get("is_interlude", False))
        text = item["text"]
        sub = item.get("sub", "")
        syllables = [] if is_interlude else build_syllables(text, start_t, end_t)
        result.append(
            LyricLine(
                index=idx,
                time=start_t,
                end_time=end_t,
                text=text,
                sub=sub,
                is_interlude=is_interlude,
                syllables=syllables,
            )
        )
    return result


_LRC_TIME_PATTERN = re.compile(r"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")
_WORD_TAG_PATTERN = re.compile(r"<(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?>")
_INLINE_SEP_PATTERN = re.compile(r"\s+(?://|/|\||——|--)\s+")
_CJK_PATTERN = re.compile(r"[\u4e00-\u9fa5]")
_HEADER_NOISE_PATTERN = re.compile(r"^(//|作词|作曲|编曲|制作|监制|混音|母带|by:|ti:|ar:|al:|offset:|qq:)", re.I)


def _split_inline_bilingual(text: str) -> Tuple[str, str]:
    """拆分单行中的外语原文与中文译文（支持 //、/、|、—— 以及尾部中文括号等形式）"""
    text = text.strip()
    if not text:
        return "", ""

    # 1. 常见显式双语分隔符 " // ", " / ", " | ", " —— ", " -- "
    parts = _INLINE_SEP_PATTERN.split(text, maxsplit=1)
    if len(parts) == 2:
        p1, p2 = parts[0].strip(), parts[1].strip()
        if _CJK_PATTERN.search(p2) and p1:
            return p1, p2

    # 2. 尾部中文括号翻译: "Hello world (世界你好)" / "Hello world（世界你好）"
    m = re.search(r"^(.+?)\s*[（\(]([\u4e00-\u9fa5\s\d,，.。!！?？'’\"”]{2,})[）\)]\s*$", text)
    if m:
        p1, p2 = m.group(1).strip(), m.group(2).strip()
        if p1 and p2:
            return p1, p2

    return text, ""


def merge_lyrics_translations(lyrics: List[LyricLine], trans_lrc: str) -> bool:
    """将外部翻译文本（如 NetEase tlyric）智能融合至已有 LyricLine 列表，保持原始打点与音节不变"""
    if not lyrics or not trans_lrc.strip():
        return False

    trans_entries = _parse_raw_lrc_map(trans_lrc)
    clean_trans = [
        (t, txt.strip())
        for t, txt in trans_entries
        if txt.strip() and not _HEADER_NOISE_PATTERN.match(txt.strip())
    ]
    if not clean_trans:
        return False

    updated = False
    for line in lyrics:
        if line.is_interlude:
            continue
        if line.sub and _CJK_PATTERN.search(line.sub):
            continue

        best_txt = ""
        best_d = 999.0
        for tt, ttxt in clean_trans:
            d = abs(tt - line.time)
            if d < best_d:
                best_d = d
                best_txt = ttxt
        if best_d <= 2.8 and best_txt and best_txt != line.text:
            line.sub = best_txt
            updated = True

    return updated


def _parse_raw_lrc_map(lrc_content: str) -> List[Tuple[float, str]]:
    entries: List[Tuple[float, str]] = []
    if not lrc_content:
        return entries

    for raw_line in lrc_content.splitlines():
        line = raw_line.strip()
        if not line:
            continue
        matches = list(_LRC_TIME_PATTERN.finditer(line))
        if not matches:
            continue

        text_part = _LRC_TIME_PATTERN.sub("", line)
        text_part = _WORD_TAG_PATTERN.sub("", text_part).strip()

        for m in matches:
            mins = int(m.group(1))
            secs = int(m.group(2))
            frac_str = m.group(3) or "0"
            frac = int(frac_str) / (1000.0 if len(frac_str) == 3 else 100.0)
            t = round(mins * 60.0 + secs + frac, 2)
            entries.append((t, text_part))

    entries.sort(key=lambda x: x[0])
    return entries


def parse_lrc_text(primary_lrc: str, trans_lrc: str = "") -> List[LyricLine]:
    primary_entries = _parse_raw_lrc_map(primary_lrc)
    if not primary_entries:
        plain_lines = [ln.strip() for ln in (primary_lrc or "").splitlines() if ln.strip()]
        if not plain_lines:
            return []
        raw_items = []
        for idx, raw_t in enumerate(plain_lines):
            p_txt, s_txt = _split_inline_bilingual(raw_t)
            raw_items.append({"time": idx * 4.0, "text": p_txt, "sub": s_txt})
        return create_lyric_lines(raw_items)

    clean_trans: List[Tuple[float, str]] = []
    if trans_lrc:
        for t, txt in _parse_raw_lrc_map(trans_lrc):
            st = txt.strip()
            if st and not _HEADER_NOISE_PATTERN.match(st):
                clean_trans.append((t, st))

    merged_raw: List[dict] = []
    i = 0
    n = len(primary_entries)
    while i < n:
        t, txt = primary_entries[i]
        if not txt:
            i += 1
            continue

        # 1. 先检查行内是否已有双语: "Midnight // 午夜时分"
        p_txt, s_txt = _split_inline_bilingual(txt)

        # 2. 检查单文件双时间轴/相连行翻译（如英文行后紧接着中文译文行）
        if not s_txt and i + 1 < n:
            next_t, next_txt = primary_entries[i + 1]
            if abs(next_t - t) <= 1.25 and next_txt:
                has_cjk_curr = bool(_CJK_PATTERN.search(p_txt))
                has_cjk_next = bool(_CJK_PATTERN.search(next_txt))
                if not has_cjk_curr and has_cjk_next:
                    s_txt = next_txt
                    i += 1  # 消费合并下一行

        # 3. 若仍无译文且有外部 trans_lrc，按最近邻时间轴匹配（容差 2.8 秒）
        if not s_txt and clean_trans:
            best_sub = ""
            best_d = 999.0
            for tt, ttxt in clean_trans:
                d = abs(tt - t)
                if d < best_d:
                    best_d = d
                    best_sub = ttxt
            if best_d <= 2.8 and best_sub and best_sub != p_txt:
                s_txt = best_sub

        is_dots = p_txt in ("...", "· · ·", "•••", "~~~")
        merged_raw.append(
            {
                "time": t,
                "text": "· · ·" if is_dots else p_txt,
                "sub": s_txt,
                "is_interlude": is_dots,
            }
        )
        i += 1

    return create_lyric_lines(merged_raw)


NORDIC_TRACKS: List[NordicTrack] = []


def format_timecode(seconds: float) -> str:
    s = max(0, int(seconds))
    return f"{s // 60:02d}:{s % 60:02d}"
