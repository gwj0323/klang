"""
曲目数据结构与 LRC 歌词解析器（支持标准 LRC、双语翻译合并、逐字时间轴生成、收藏与歌词时间偏移）
"""

import re
from dataclasses import dataclass, field
from typing import List, Optional, Dict


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


def _parse_raw_lrc_map(lrc_content: str) -> List[tuple]:
    entries = []
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
        raw_items = [{"time": idx * 4.0, "text": txt} for idx, txt in enumerate(plain_lines)]
        return create_lyric_lines(raw_items)

    trans_map: Dict[int, str] = {}
    if trans_lrc:
        for t, txt in _parse_raw_lrc_map(trans_lrc):
            if txt and not txt.startswith("//"):
                trans_map[int(round(t * 2))] = txt

    merged_raw: List[dict] = []
    for t, txt in primary_entries:
        if not txt:
            continue
        if merged_raw and abs(merged_raw[-1]["time"] - t) < 0.08:
            if not merged_raw[-1].get("sub"):
                merged_raw[-1]["sub"] = txt
            continue

        bucket = int(round(t * 2))
        sub_txt = (
            trans_map.get(bucket)
            or trans_map.get(bucket - 1)
            or trans_map.get(bucket + 1)
            or ""
        )
        if sub_txt == txt:
            sub_txt = ""

        is_dots = txt in ("...", "· · ·", "•••", "~~~")
        merged_raw.append(
            {
                "time": t,
                "text": "· · ·" if is_dots else txt,
                "sub": sub_txt,
                "is_interlude": is_dots,
            }
        )

    return create_lyric_lines(merged_raw)


NORDIC_TRACKS: List[NordicTrack] = [
    NordicTrack(
        track_id="bergen-mist",
        title="Morgendugg over Fjorden",
        artist="Soren Lindqvist",
        album="Stillhet i Nord",
        duration=76.0,
        is_favorite=True,
        palette={
            "bg_primary": "#E7EAE6",
            "bg_secondary": "#D7DFDA",
            "accent": "#6E8277",
            "wood": "#C9BBAE",
            "ink": "#26292E",
        },
        chords=[
            [220.0, 261.63, 329.63, 392.00],
            [174.61, 220.0, 261.63, 349.23],
            [130.81, 196.0, 246.94, 329.63],
            [146.83, 220.0, 261.63, 329.63],
        ],
        lyrics=create_lyric_lines(
            [
                {"time": 0.0, "text": "· · ·", "sub": "", "is_interlude": True},
                {"time": 4.5, "text": "Pale light settles on the slate-grey water", "sub": "微光静静落向板岩灰色的峡湾水面"},
                {"time": 10.2, "text": "Pine needles hold the weight of morning mist", "sub": "松针托起清晨薄雾微凉的重量"},
                {"time": 16.0, "text": "Nothing hurries through the birchwood valley", "sub": "白桦林谷之中，万物皆不仓促"},
                {"time": 21.8, "text": "Only the slow tide breathing against stone", "sub": "唯有缓慢的潮汐，轻叩冷冽的礁石"},
                {"time": 27.5, "text": "We leave the windows open to the north wind", "sub": "我们向北风敞开木窗，任清冽穿堂而过"},
                {"time": 33.4, "text": "White linen drifting in a quiet room", "sub": "素白亚麻窗纱在静谧的房间里微微浮动"},
                {"time": 39.2, "text": "Every unspoken word becomes transparent", "sub": "每一句未曾言说的话语，都澄澈如水"},
                {"time": 45.0, "text": "· · ·", "sub": "", "is_interlude": True},
                {"time": 50.5, "text": "Shadows lengthen across untreated timber", "sub": "漫射的光影掠过未上漆的浅色原木"},
                {"time": 56.4, "text": "Cold air carries the scent of wet moss", "sub": "清冷空气里弥漫着湿润苔藓与雪松的气息"},
                {"time": 62.2, "text": "Stay until the horizon turns to silver", "sub": "请驻足片刻，直到远方天际化作柔和的银灰"},
                {"time": 68.5, "text": "In the gentle geometry of winter light", "sub": "在这冬日光影克制而温柔的秩序之中"},
            ]
        ),
    ),
    NordicTrack(
        track_id="oslo-atelier",
        title="Kveldssol i Bjørkeskog",
        artist="Freja Lind",
        album="Nordic Sessions",
        duration=68.0,
        palette={
            "bg_primary": "#EAE6DF",
            "bg_secondary": "#DED8CE",
            "accent": "#7B847E",
            "wood": "#BFAFB2",
            "ink": "#2A2D32",
        },
        chords=[
            [196.00, 246.94, 293.66, 369.99],
            [164.81, 220.00, 246.94, 329.63],
            [130.81, 164.81, 196.00, 246.94],
            [146.83, 196.00, 220.00, 293.66],
        ],
        lyrics=create_lyric_lines(
            [
                {"time": 0.0, "text": "· · ·", "sub": "", "is_interlude": True},
                {"time": 4.0, "text": "Dust motes float in a beam of amber frost", "sub": "微尘在琥珀色的清霜光束中缓慢悬浮"},
                {"time": 9.8, "text": "Ceramic cups still warm upon the table", "sub": "粗陶杯盏在原木桌面上尚存余温"},
                {"time": 15.6, "text": "Footsteps fade along the gravel courtyard", "sub": "脚步声沿着碎石庭院渐行渐远"},
                {"time": 21.4, "text": "Silence is not empty, it is full of space", "sub": "寂静并非空无一物，而是盛满了呼吸的留白"},
                {"time": 27.2, "text": "A single line drawn across handmade paper", "sub": "手工棉纸上轻轻落下一道克制的线条"},
                {"time": 33.0, "text": "Sage-green hills beneath a porcelain sky", "sub": "灰鼠尾草绿的山丘枕在白瓷般的天幕之下"},
                {"time": 39.0, "text": "Time unfolds without a sharp edge", "sub": "时间缓缓舒展，不带一丝生硬的棱角"},
                {"time": 45.2, "text": "Let the evening settle like soft snow", "sub": "任暮色如初雪般悄然落定"},
                {"time": 51.5, "text": "Breathing slow between two quiet notes", "sub": "在两个轻柔音符的间隙里，自在呼吸"},
                {"time": 58.0, "text": "Until only warmth remains in the room", "sub": "直至室内唯余一抹温润的微光"},
            ]
        ),
    ),
]


def format_timecode(seconds: float) -> str:
    s = max(0, int(seconds))
    return f"{s // 60:02d}:{s % 60:02d}"
