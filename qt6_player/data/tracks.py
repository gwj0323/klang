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
    roma: str = ""
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
    replay_gain_db: Optional[float] = None
    replay_peak: Optional[float] = None
    start_time: float = 0.0
    end_time: float = 0.0
    cue_path: str = ""
    cue_track_num: int = 0
    added_timestamp: float = 0.0
    acoustic_dna: Optional[Dict[str, float]] = None  # 离线声学指纹: {"bpm": 108.0, "energy": 0.55, "brightness": 0.48, "warmth": 0.62}
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

    def has_real_dna(self) -> bool:
        """是否已有从 PCM 真实提取的声学 DNA（未播放过/未分析过的曲目只有粗略估计，不能用于相似度选曲）"""
        return isinstance(self.acoustic_dna, dict) and "bpm" in self.acoustic_dna

    def get_acoustic_vector(self) -> Tuple[float, float, float, float]:
        """
        返回归一化 4 维声学指纹向量 (bpm_norm, energy, brightness, warmth)，均在 0.0 ~ 1.0。
        优先使用已从 PCM 提取的真实声学 DNA；若尚未解码，则根据歌词密度、时长与色彩特征智能估算。
        """
        if isinstance(self.acoustic_dna, dict) and "bpm" in self.acoustic_dna:
            bpm = float(self.acoustic_dna.get("bpm", 105.0))
            bpm_norm = max(0.0, min(1.0, (bpm - 60.0) / 120.0))
            energy = max(0.0, min(1.0, float(self.acoustic_dna.get("energy", 0.5))))
            brightness = max(0.0, min(1.0, float(self.acoustic_dna.get("brightness", 0.5))))
            warmth = max(0.0, min(1.0, float(self.acoustic_dna.get("warmth", 0.5))))
            return (bpm_norm, energy, brightness, warmth)

        # 未解码曲目的启发式先验估算（结合歌词行频密度、曲目时长与色调）
        dur = max(30.0, float(self.duration or 180.0))
        lyr_count = len([l for l in (self.lyrics or []) if not l.is_interlude])
        lines_per_min = (lyr_count / (dur / 60.0)) if lyr_count > 2 else 12.0
        est_bpm = max(68.0, min(168.0, 72.0 + lines_per_min * 2.6))
        bpm_norm = max(0.0, min(1.0, (est_bpm - 60.0) / 120.0))
        energy = max(0.15, min(0.88, 0.28 + (lines_per_min / 35.0) * 0.55))
        h_seed = sum(ord(c) for c in (self.artist + self.title)) % 100 / 100.0
        brightness = 0.30 + 0.45 * h_seed
        warmth = 1.0 - brightness * 0.65
        return (bpm_norm, energy, brightness, warmth)

    def format_dna_badge(self) -> str:
        """生成可读的声学 DNA 情绪标签（如 '104 BPM · 暖调舒缓'）"""
        bpm_norm, energy, brightness, warmth = self.get_acoustic_vector()
        bpm = int(round(60.0 + bpm_norm * 120.0))
        tone_str = "暖调" if warmth >= brightness else "清亮"
        if energy >= 0.62:
            mood_str = "律动"
        elif energy <= 0.36:
            mood_str = "静谧"
        else:
            mood_str = "舒缓"
        return f"{bpm} BPM · {tone_str}{mood_str}"

    def invalidate_cover_cache(self) -> None:
        self._cached_cover_pix = None
        self._cover_cache_loaded = False

    def get_cover_pixmap(self):
        if self._cover_cache_loaded:
            return self._cached_cover_pix
        from PyQt6.QtCore import Qt
        from PyQt6.QtGui import QPixmap

        pix = QPixmap()
        loaded = False
        if self.cover_bytes and pix.loadFromData(self.cover_bytes):
            loaded = True
        elif self.cover_path and not self.cover_path.startswith("http") and pix.load(self.cover_path):
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
            self._cover_cache_loaded = True
        else:
            self._cached_cover_pix = None
            # 若封面路径是远程 URL 或尚未准备就绪，不锁定缓存标记，允许后续下载完成后载入
            if not (self.cover_path and self.cover_path.startswith("http")):
                self._cover_cache_loaded = True
        return self._cached_cover_pix


def build_syllables(text: str, start_time: float, end_time: float) -> List[SyllableToken]:
    """将单行歌词切分为带起止时间的音节/字词序列"""
    has_cjk = bool(re.search(r"[\u4e00-\u9fa5]", text))
    if has_cjk:
        raw_tokens = re.findall(r"[\u4e00-\u9fa5]|[a-zA-Z0-9]+|\s+|[^\s\u4e00-\u9fa5a-zA-Z0-9]+", text) or [text]
    else:
        raw_tokens = re.findall(r"\S+\s*", text) or [text]

    count = max(1, len(raw_tokens))
    # 合成的逐字时间：一行后面若紧跟长间奏，不能把高亮拖满整个间奏；按字数估计演唱时长并封顶
    total_span = max(0.5, min((end_time - start_time) * 0.88, max(1.2, count * 0.5)))
    step = total_span / count

    return [
        SyllableToken(
            text=token,
            start=round(start_time + idx * step, 2),
            end=round(start_time + (idx + 1) * step, 2),
        )
        for idx, token in enumerate(raw_tokens)
    ]


def _syllables_from_word_tags(words: List[Tuple[float, str]], line_end: float) -> List[SyllableToken]:
    """由 LRC 逐字时间标签 <mm:ss.xx>字 生成真实的音节时间；末字结束于行结束时间"""
    result: List[SyllableToken] = []
    for i, (t, txt) in enumerate(words):
        nxt = words[i + 1][0] if i + 1 < len(words) else min(max(t + 0.1, line_end), t + 1.5)
        result.append(SyllableToken(text=txt, start=round(t, 2), end=round(max(t, nxt), 2)))
    return result


def create_lyric_lines(raw_items: List[dict]) -> List[LyricLine]:
    result: List[LyricLine] = []
    for idx, item in enumerate(raw_items):
        start_t = float(item["time"])
        next_t = float(raw_items[idx + 1]["time"]) if idx + 1 < len(raw_items) else start_t + 5.0
        end_t = float(item.get("end", max(start_t + 1.5, next_t - 0.2)))
        is_interlude = bool(item.get("is_interlude", False))
        text = item["text"]
        sub = item.get("sub", "")
        roma = item.get("roma", "")
        words = item.get("words")
        if is_interlude:
            syllables = []
        elif words:
            syllables = _syllables_from_word_tags(words, end_t)
        else:
            syllables = build_syllables(text, start_t, end_t)
        result.append(
            LyricLine(
                index=idx,
                time=start_t,
                end_time=end_t,
                text=text,
                sub=sub,
                roma=roma,
                is_interlude=is_interlude,
                syllables=syllables,
            )
        )
    return result


_LRC_TIME_PATTERN = re.compile(r"\[(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?\]")
_WORD_TAG_PATTERN = re.compile(r"<(\d{1,3}):(\d{1,2})(?:[.:](\d{1,3}))?>")
_INLINE_SEP_PATTERN = re.compile(r"\s+(?://|/|\||——|--)\s+")
_CJK_PATTERN = re.compile(r"[\u4e00-\u9fa5]")
_CREDIT_PATTERN = re.compile(r"^(作词|作曲|编曲|制作人?|监制|混音|母带|词|曲|lyrics?|music|composer|arranger|producer)\s*[:：]", re.I)
_OFFSET_PATTERN = re.compile(r"^\[offset:\s*([+-]?\d+)\s*\]", re.I)
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


def _frac_to_seconds(frac_str: str) -> float:
    """LRC 小数部分：1 位=十分之一秒，2 位=百分之一秒，3 位=毫秒（[00:12.5] 是 12.5 秒，不是 12.05）"""
    if not frac_str:
        return 0.0
    return int(frac_str) / (10.0 ** len(frac_str))


def _parse_raw_lrc_map_ex(lrc_content: str) -> List[Tuple[float, str, List[Tuple[float, str]]]]:
    entries: List[Tuple[float, str, List[Tuple[float, str]]]] = []
    if not lrc_content:
        return entries

    offset_sec = 0.0
    for raw_line in lrc_content.splitlines():
        om = _OFFSET_PATTERN.match(raw_line.strip())
        if om:
            # 规范：offset 为毫秒，正值表示歌词整体提前
            offset_sec = int(om.group(1)) / 1000.0
            break

    for raw_line in lrc_content.splitlines():
        line = raw_line.strip()
        if not line:
            continue
        matches = list(_LRC_TIME_PATTERN.finditer(line))
        if not matches:
            continue

        body = _LRC_TIME_PATTERN.sub("", line)
        words: List[Tuple[float, str]] = []
        wmatches = list(_WORD_TAG_PATTERN.finditer(body))
        for k, wm in enumerate(wmatches):
            seg_end = wmatches[k + 1].start() if k + 1 < len(wmatches) else len(body)
            seg = body[wm.end():seg_end]
            if not seg:
                continue
            wt = int(wm.group(1)) * 60.0 + int(wm.group(2)) + _frac_to_seconds(wm.group(3) or "")
            words.append((round(max(0.0, wt - offset_sec), 2), seg))
        text_part = _WORD_TAG_PATTERN.sub("", body).strip()

        for m in matches:
            t = int(m.group(1)) * 60.0 + int(m.group(2)) + _frac_to_seconds(m.group(3) or "")
            t = round(max(0.0, t - offset_sec), 2)
            entries.append((t, text_part, words if len(matches) == 1 else []))

    entries.sort(key=lambda x: x[0])
    return entries


def _parse_raw_lrc_map(lrc_content: str) -> List[Tuple[float, str]]:
    return [(t, txt) for t, txt, _w in _parse_raw_lrc_map_ex(lrc_content)]


def parse_lrc_text(primary_lrc: str, trans_lrc: str = "", roma_lrc: str = "") -> List[LyricLine]:
    primary_ex = _parse_raw_lrc_map_ex(primary_lrc)
    primary_entries = [(t, txt) for t, txt, _w in primary_ex]
    if not primary_entries:
        plain_lines = [ln.strip() for ln in (primary_lrc or "").splitlines() if ln.strip()]
        if not plain_lines:
            return []
        raw_items = []
        for idx, raw_t in enumerate(plain_lines):
            p_txt, s_txt = _split_inline_bilingual(raw_t)
            raw_items.append({"time": idx * 4.0, "text": p_txt, "sub": s_txt, "roma": ""})
        return create_lyric_lines(raw_items)

    clean_trans: List[Tuple[float, str]] = []
    if trans_lrc:
        for t, txt in _parse_raw_lrc_map(trans_lrc):
            st = txt.strip()
            if st and not _HEADER_NOISE_PATTERN.match(st):
                clean_trans.append((t, st))

    clean_roma: List[Tuple[float, str]] = []
    if roma_lrc:
        for t, txt in _parse_raw_lrc_map(roma_lrc):
            st = txt.strip()
            if st and not _HEADER_NOISE_PATTERN.match(st):
                clean_roma.append((t, st))

    merged_raw: List[dict] = []
    i = 0
    n = len(primary_entries)
    while i < n:
        t, txt = primary_entries[i]
        line_words = primary_ex[i][2]
        if not txt or _CREDIT_PATTERN.match(txt.strip()):
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

        # 4. 匹配罗马音 / 多语言注音 (roma_lrc)
        r_txt = ""
        if clean_roma:
            best_roma = ""
            best_rd = 999.0
            for rt, rtxt in clean_roma:
                rd = abs(rt - t)
                if rd < best_rd:
                    best_rd = rd
                    best_roma = rtxt
            if best_rd <= 2.8 and best_roma and best_roma != p_txt:
                r_txt = best_roma

        is_dots = p_txt in ("...", "· · ·", "•••", "~~~")
        merged_raw.append(
            {
                "time": t,
                "text": "· · ·" if is_dots else p_txt,
                "sub": s_txt,
                "roma": r_txt,
                "is_interlude": is_dots,
                "words": line_words if (line_words and "".join(w for _t, w in line_words).strip() == p_txt) else [],
            }
        )
        i += 1

    return create_lyric_lines(merged_raw)


NORDIC_TRACKS: List[NordicTrack] = []


def format_lyrics_to_lrc(lyrics: List[LyricLine]) -> str:
    """将 LyricLine 列表序列化为标准 LRC 文本，用于写入音频文件内嵌歌词标签"""
    if not lyrics:
        return ""
    # 若仅包含默认的占位歌词（如“暂无歌词”），不导出为内嵌歌词
    if len(lyrics) <= 2 and any("暂无歌词" in (l.text or "") for l in lyrics):
        return ""
    lines: List[str] = []
    for l in lyrics:
        t = max(0.0, float(l.time))
        mins = int(t // 60)
        secs = t - mins * 60
        tag = f"[{mins:02d}:{secs:05.2f}]"
        txt = (l.text or "").strip()
        sub = (l.sub or "").strip()
        if sub and sub != txt:
            lines.append(f"{tag}{txt} // {sub}")
        else:
            lines.append(f"{tag}{txt}")
    return "\n".join(lines)


def format_timecode(seconds: float) -> str:
    s = max(0, int(seconds))
    return f"{s // 60:02d}:{s % 60:02d}"
