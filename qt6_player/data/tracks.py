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


NORDIC_TRACKS: List[NordicTrack] = []


def format_timecode(seconds: float) -> str:
    s = max(0, int(seconds))
    return f"{s // 60:02d}:{s % 60:02d}"
