import hashlib
import os
import re
import struct
import time
from pathlib import Path
from typing import List, Optional, Tuple, Dict, Any

from qt6_player.data.tracks import (
    NordicTrack,
    parse_lrc_text,
    create_lyric_lines,
    format_lyrics_to_lrc,
)

SUPPORTED_AUDIO_EXTS = {".mp3", ".flac", ".wav", ".ogg", ".m4a", ".aac", ".wma"}
SUPPORTED_IMPORT_EXTS = SUPPORTED_AUDIO_EXTS | {".cue"}

_NORDIC_PALETTES = [
    {"bg_primary": "#E7EAE6", "bg_secondary": "#D7DFDA", "accent": "#6E8277", "wood": "#C9BBAE", "ink": "#26292E"},
    {"bg_primary": "#EAE6DF", "bg_secondary": "#DED8CE", "accent": "#7B847E", "wood": "#BFAFB2", "ink": "#2A2D32"},
    {"bg_primary": "#E3E7E9", "bg_secondary": "#D2D9DD", "accent": "#65787E", "wood": "#BDB6AE", "ink": "#24282D"},
    {"bg_primary": "#E9E5E2", "bg_secondary": "#DDD6D0", "accent": "#7D7670", "wood": "#C4B5A6", "ink": "#282625"},
]


def _decode_id3_text(raw: bytes) -> str:
    if not raw:
        return ""
    enc = raw[0]
    payload = raw[1:]
    try:
        if enc == 0:
            return payload.decode("latin1", errors="ignore").strip("\x00 ").strip()
        if enc == 1:
            return payload.decode("utf-16", errors="ignore").strip("\x00 ").strip()
        if enc == 2:
            return payload.decode("utf-16-be", errors="ignore").strip("\x00 ").strip()
        return payload.decode("utf-8", errors="ignore").strip("\x00 ").strip()
    except Exception:
        return ""


def _extract_mp3_id3v2(file_path: str) -> Tuple[str, str, str, Optional[bytes], str]:
    """轻量读取 MP3 ID3v2 标题、歌手、专辑、内嵌封面字节与内嵌歌词"""
    title, artist, album, lrc_text = "", "", "", ""
    cover_bytes: Optional[bytes] = None
    try:
        with open(file_path, "rb") as f:
            header = f.read(10)
            if len(header) < 10 or header[:3] != b"ID3":
                return title, artist, album, cover_bytes, lrc_text
            ver = header[3]
            size = (
                ((header[6] & 0x7F) << 21)
                | ((header[7] & 0x7F) << 14)
                | ((header[8] & 0x7F) << 7)
                | (header[9] & 0x7F)
            )
            tag_data = f.read(min(size, 4 * 1024 * 1024))

        pos = 0
        limit = len(tag_data)
        while pos + 10 <= limit:
            frame_id = tag_data[pos : pos + 4].decode("latin1", errors="ignore")
            if not frame_id.strip("\x00"):
                break
            raw_fsize = tag_data[pos + 4 : pos + 8]
            if ver == 4:
                fsize = (
                    ((raw_fsize[0] & 0x7F) << 21)
                    | ((raw_fsize[1] & 0x7F) << 14)
                    | ((raw_fsize[2] & 0x7F) << 7)
                    | (raw_fsize[3] & 0x7F)
                )
            else:
                fsize = struct.unpack(">I", raw_fsize)[0]

            if fsize <= 0 or pos + 10 + fsize > limit:
                break
            body = tag_data[pos + 10 : pos + 10 + fsize]
            pos += 10 + fsize

            if frame_id == "TIT2" and not title:
                title = _decode_id3_text(body)
            elif frame_id == "TPE1" and not artist:
                artist = _decode_id3_text(body)
            elif frame_id == "TALB" and not album:
                album = _decode_id3_text(body)
            elif frame_id == "USLT" and not lrc_text and len(body) > 4:
                # USLT: 1 byte encoding + 3 bytes language + descriptor (null-terminated) + lyrics text
                enc = body[0]
                rest = body[4:]
                if enc in (1, 2):
                    sep = rest.find(b"\x00\x00")
                    lyric_bytes = rest[sep + 2 :] if sep != -1 else rest
                else:
                    sep = rest.find(b"\x00")
                    lyric_bytes = rest[sep + 1 :] if sep != -1 else rest
                lrc_text = _decode_id3_text(bytes([enc]) + lyric_bytes)
            elif frame_id == "APIC" and cover_bytes is None and len(body) > 32:
                # 查找 JPEG 或 PNG 魔数起点
                jpg_idx = body.find(b"\xff\xd8\xff")
                png_idx = body.find(b"\x89PNG")
                if jpg_idx != -1:
                    cover_bytes = body[jpg_idx:]
                elif png_idx != -1:
                    cover_bytes = body[png_idx:]
    except Exception:
        pass
    return title, artist, album, cover_bytes, lrc_text


def _extract_flac_metadata(file_path: str) -> Tuple[str, str, str, Optional[bytes], str, float]:
    """轻量读取 FLAC Vorbis 注释、Picture 块封面与 STREAMINFO 精准时长"""
    title, artist, album, lrc_text = "", "", "", ""
    cover_bytes: Optional[bytes] = None
    duration: float = 0.0
    try:
        with open(file_path, "rb") as f:
            if f.read(4) != b"fLaC":
                return title, artist, album, cover_bytes, lrc_text, duration
            while True:
                block_header = f.read(4)
                if len(block_header) < 4:
                    break
                is_last = (block_header[0] & 0x80) != 0
                block_type = block_header[0] & 0x7F
                length = (block_header[1] << 16) | (block_header[2] << 8) | block_header[3]
                if length <= 0 or length > 8 * 1024 * 1024:
                    f.seek(length, os.SEEK_CUR)
                else:
                    data = f.read(length)
                    if block_type == 0 and len(data) >= 18:  # STREAMINFO
                        val64 = struct.unpack(">Q", data[10:18])[0]
                        sr = (val64 >> 44) & 0xFFFFF
                        total_samples = val64 & 0xFFFFFFFFF
                        if sr > 0 and total_samples > 0:
                            duration = total_samples / float(sr)
                    elif block_type == 4 and len(data) >= 8:  # VORBIS_COMMENT
                        vendor_len = struct.unpack("<I", data[:4])[0]
                        p = 4 + vendor_len
                        if p + 4 <= len(data):
                            count = struct.unpack("<I", data[p : p + 4])[0]
                            p += 4
                            for _ in range(min(count, 128)):
                                if p + 4 > len(data):
                                    break
                                clen = struct.unpack("<I", data[p : p + 4])[0]
                                p += 4
                                item = data[p : p + clen].decode("utf-8", errors="ignore")
                                p += clen
                                if "=" in item:
                                    k, v = item.split("=", 1)
                                    ku = k.strip().upper()
                                    if ku == "TITLE" and not title:
                                        title = v.strip()
                                    elif ku == "ARTIST" and not artist:
                                        artist = v.strip()
                                    elif ku == "ALBUM" and not album:
                                        album = v.strip()
                                    elif ku == "LYRICS" and not lrc_text:
                                        lrc_text = v.strip()
                    elif block_type == 6 and cover_bytes is None and len(data) > 32:  # PICTURE
                        p = 4
                        if p + 4 <= len(data):
                            mime_len = struct.unpack(">I", data[p : p + 4])[0]
                            p += 4 + mime_len
                        if p + 4 <= len(data):
                            desc_len = struct.unpack(">I", data[p : p + 4])[0]
                            p += 4 + desc_len + 16
                        if p + 4 <= len(data):
                            pic_len = struct.unpack(">I", data[p : p + 4])[0]
                            p += 4
                            if 0 < pic_len <= len(data) - p:
                                cover_bytes = data[p : p + pic_len]
                if is_last:
                    break
    except Exception:
        pass
    return title, artist, album, cover_bytes, lrc_text, duration


_GENERIC_FOLDER_NAMES = {
    "downloads",
    "download",
    "music",
    "desktop",
    "documents",
    "audio",
    "songs",
    "flac",
    "mp3",
    "新建文件夹",
    "音乐",
    "下载",
    "桌面",
}


def _parse_filename_metadata(stem: str, lrc_hint: str = "") -> Tuple[str, str, str]:
    """
    从文件名智能拆分 (title, artist, album)：
    - 支持 '歌名 - 歌手[专辑名]'（如 'SEVENTH HEAVEN - milet[visions]'、'青花瓷 - 刘芳[再醉一次.精选二]'）
    - 支持 '歌手[专辑名] - 歌名'
    - 支持结合 LRC 歌词元信息判断 '歌名 - 歌手' 与 '歌手 - 歌名'
    """
    cleaned = re.sub(r"^\s*\d{1,3}[\s._-]+", "", stem).strip()
    if " - " in cleaned:
        left, right = [p.strip() for p in cleaned.split(" - ", 1)]
        # 情况 1：右侧带 [专辑名] -> '歌名 - 歌手[专辑名]'
        m_right = re.match(r"^(.+?)\s*[\[【](.+?)[\]】]\s*$", right)
        if m_right:
            title = left
            artist = m_right.group(1).strip()
            album = m_right.group(2).strip()
            return title, artist, album

        # 情况 2：左侧带 [专辑名] -> '歌手[专辑名] - 歌名'
        m_left = re.match(r"^(.+?)\s*[\[【](.+?)[\]】]\s*$", left)
        if m_left:
            artist = m_left.group(1).strip()
            album = m_left.group(2).strip()
            title = right
            return title, artist, album

        # 情况 3：根据 LRC 歌词前几行演职员信息辅助判断哪一侧是歌手
        if lrc_hint:
            head_lines = "\n".join(lrc_hint.splitlines()[:12])
            if re.search(rf"(?:作词|作曲|演唱|歌手|Rap|制作人|\[ar:)\s*[:：]?\s*.*{re.escape(right)}", head_lines, re.IGNORECASE):
                return left, right, ""
            if re.search(rf"(?:作词|作曲|演唱|歌手|Rap|制作人|\[ar:)\s*[:：]?\s*.*{re.escape(left)}", head_lines, re.IGNORECASE):
                return right, left, ""

        # 默认返回：左侧歌手，右侧歌名（或保持通用习惯）
        return right, left, ""

    bracket_m = re.match(r"^(.+?)\s*[\[【](.+?)[\]】]\s*$", cleaned)
    if bracket_m:
        return bracket_m.group(1).strip(), "未知歌手", bracket_m.group(2).strip()
    if "_" in cleaned:
        parts = cleaned.split("_", 1)
        if len(parts[0].strip()) >= 2 and len(parts[1].strip()) >= 1:
            return parts[1].strip(), parts[0].strip(), ""
    if "-" in cleaned:
        parts = cleaned.split("-", 1)
        if len(parts[0].strip()) >= 2 and len(parts[1].strip()) >= 1:
            return parts[1].strip(), parts[0].strip(), ""
    return cleaned or stem, "未知歌手", ""


def _parse_filename_artist_title(stem: str) -> Tuple[str, str]:
    """兼容旧接口：返回 (artist, title)"""
    title, artist, _ = _parse_filename_metadata(stem)
    return artist, title


def _decode_text_bytes(raw: bytes) -> str:
    """探测文本编码：BOM → UTF-8 → charset-normalizer → gb18030/big5/shift_jis。

    gb18030 几乎不会解码失败，必须放在最后，否则日文(Shift-JIS)与繁体(Big5)会被读成乱码。
    """
    if raw.startswith((b"\xff\xfe", b"\xfe\xff")):
        return raw.decode("utf-16", errors="replace")
    for enc in ("utf-8-sig",):
        try:
            return raw.decode(enc)
        except UnicodeDecodeError:
            pass
    # charset-normalizer 是 requests 的依赖，必定可用；文本较长时对 GBK/Big5/Shift-JIS 的区分很可靠
    try:
        from charset_normalizer import from_bytes
        best = from_bytes(raw).best()
        if best is not None:
            return str(best)
    except Exception:
        pass
    for enc in ("gb18030", "big5", "shift_jis"):
        try:
            return raw.decode(enc)
        except UnicodeDecodeError:
            continue
    return raw.decode("latin1", errors="replace")


def _find_local_lrc_and_cover(audio_path: Path) -> Tuple[str, str, Optional[str]]:
    """查找音频同目录下的同名 .lrc 歌词、伴生翻译 .tr.lrc 歌词与封面图片"""
    folder = audio_path.parent
    stem = audio_path.stem
    lrc_text = ""
    trans_text = ""

    def _read_file_safe(candidate_path: Path) -> str:
        try:
            txt = _decode_text_bytes(candidate_path.read_bytes())
        except Exception:
            return ""
        return txt if txt.strip() else ""

    lrc_candidate = folder / f"{stem}.lrc"
    if lrc_candidate.is_file():
        lrc_text = _read_file_safe(lrc_candidate)

    for trans_suffix in (f"{stem}.tr.lrc", f"{stem}.zh.lrc", f"{stem}.trans.lrc", f"{stem}.cn.lrc", f"{stem}_zh.lrc", f"{stem}_tr.lrc"):
        tr_candidate = folder / trans_suffix
        if tr_candidate.is_file():
            trans_text = _read_file_safe(tr_candidate)
            if trans_text.strip():
                break

    cover_path: Optional[str] = None
    for img_name in (f"{stem}.jpg", f"{stem}.png", f"{stem}.jpeg", "cover.jpg", "cover.png", "folder.jpg"):
        img_candidate = folder / img_name
        if img_candidate.is_file():
            cover_path = str(img_candidate)
            break

    return lrc_text, trans_text, cover_path




def _parse_db_value(raw_val: object, clamp: bool = True) -> Optional[float]:
    """从标签字符串（如 '-6.42 dB'、'+1.8 dB' 或列表）解析浮点数 dB 值"""
    if raw_val is None:
        return None
    if isinstance(raw_val, (list, tuple)) and raw_val:
        raw_val = raw_val[0]
    if hasattr(raw_val, "text") and raw_val.text:
        raw_val = raw_val.text[0] if isinstance(raw_val.text, list) else raw_val.text
    s = str(raw_val).strip()
    m = re.search(r"([+-]?\d+(?:\.\d+)?)", s)
    if m:
        try:
            val = float(m.group(1))
            return max(-24.0, min(18.0, val)) if clamp else val
        except Exception:
            return None
    return None


def _parse_peak_value(raw_val: object) -> Optional[float]:
    if raw_val is None:
        return None
    if isinstance(raw_val, (list, tuple)) and raw_val:
        raw_val = raw_val[0]
    if hasattr(raw_val, "text") and raw_val.text:
        raw_val = raw_val.text[0] if isinstance(raw_val.text, list) else raw_val.text
    try:
        val = float(str(raw_val).strip())
        if 0.01 <= val <= 4.0:
            return val
    except Exception:
        pass
    return None


def extract_audio_properties(file_path: str) -> dict:
    """
    精准解析音频底层工程参数：
    - sample_rate (如 44100, 48000, 96000, 192000)
    - bit_depth (如 16, 24, 32)
    - channels (如 1, 2, 6, 8)
    - bitrate_kbps (如 320, 1411, 2681)
    - duration (float 秒数)
    - audio_format (如 "FLAC", "WAV", "MP3", "AAC", "M4A")
    - replay_gain_db (Optional[float] 曲目/专辑响度增益 dB)
    - replay_peak (Optional[float] 归一化峰值)
    """
    path_obj = Path(file_path)
    ext = path_obj.suffix.lower()
    props = {
        "sample_rate": 44100,
        "bit_depth": 16,
        "channels": 2,
        "bitrate_kbps": 0,
        "duration": 0.0,
        "audio_format": ext.lstrip(".").upper() or "PCM",
        "replay_gain_db": None,
        "replay_peak": None,
    }

    # 1. 优先使用 mutagen 解析高保真参数与 ReplayGain 标签
    try:
        import mutagen
        m = mutagen.File(file_path)
        if m is not None:
            if hasattr(m, "tags") and m.tags:
                tags = m.tags
                for rg_key in (
                    "REPLAYGAIN_TRACK_GAIN",
                    "replaygain_track_gain",
                    "TXXX:REPLAYGAIN_TRACK_GAIN",
                    "TXXX:replaygain_track_gain",
                    "----:com.apple.iTunes:replaygain_track_gain",
                    "REPLAYGAIN_ALBUM_GAIN",
                    "replaygain_album_gain",
                    "TXXX:REPLAYGAIN_ALBUM_GAIN",
                    "TXXX:replaygain_album_gain",
                ):
                    if rg_key in tags:
                        parsed_rg = _parse_db_value(tags[rg_key])
                        if parsed_rg is not None:
                            props["replay_gain_db"] = parsed_rg
                            break
                if props["replay_gain_db"] is None:
                    for r128_key in ("R128_TRACK_GAIN", "r128_track_gain", "R128_ALBUM_GAIN"):
                        if r128_key in tags:
                            raw_q78 = _parse_db_value(tags[r128_key], clamp=False)
                            if raw_q78 is not None:
                                # EBU R128 Q7.8 (-23 LUFS) 转换为 ReplayGain 2.0 (-18 LUFS) 基准 (+5 dB)
                                props["replay_gain_db"] = max(-20.0, min(15.0, (raw_q78 / 256.0) + 5.0))
                                break
                for pk_key in (
                    "REPLAYGAIN_TRACK_PEAK",
                    "replaygain_track_peak",
                    "TXXX:REPLAYGAIN_TRACK_PEAK",
                    "TXXX:replaygain_track_peak",
                    "----:com.apple.iTunes:replaygain_track_peak",
                    "REPLAYGAIN_ALBUM_PEAK",
                    "replaygain_album_peak",
                ):
                    if pk_key in tags:
                        parsed_pk = _parse_peak_value(tags[pk_key])
                        if parsed_pk is not None:
                            props["replay_peak"] = parsed_pk
                            break

            if hasattr(m, "info") and m.info:
                if getattr(m.info, "sample_rate", None):
                    props["sample_rate"] = int(m.info.sample_rate)
                if getattr(m.info, "bits_per_sample", None):
                    props["bit_depth"] = int(m.info.bits_per_sample)
                if getattr(m.info, "channels", None):
                    props["channels"] = int(m.info.channels)
                if getattr(m.info, "bitrate", None) and m.info.bitrate > 0:
                    props["bitrate_kbps"] = int(round(m.info.bitrate / 1000.0))
                if getattr(m.info, "length", None) and m.info.length > 0:
                    props["duration"] = float(m.info.length)
                return props
    except Exception:
        pass

    # 2. 原生二进制快速回退解析 (针对 FLAC)
    if ext == ".flac":
        try:
            with open(file_path, "rb") as f:
                if f.read(4) == b"fLaC":
                    while True:
                        bh = f.read(4)
                        if len(bh) < 4:
                            break
                        is_last = (bh[0] & 0x80) != 0
                        btype = bh[0] & 0x7F
                        length = (bh[1] << 16) | (bh[2] << 8) | bh[3]
                        if btype == 0 and length >= 18:
                            data = f.read(length)
                            val64 = struct.unpack(">Q", data[10:18])[0]
                            sr = (val64 >> 44) & 0xFFFFF
                            ch = ((val64 >> 41) & 0x07) + 1
                            bps = ((val64 >> 36) & 0x1F) + 1
                            total_samples = val64 & 0xFFFFFFFFF
                            dur = total_samples / float(sr) if sr > 0 else 0.0
                            if sr > 0:
                                props["sample_rate"] = sr
                            if bps > 0:
                                props["bit_depth"] = bps
                            if ch > 0:
                                props["channels"] = ch
                            if dur > 0:
                                props["duration"] = dur
                            break
                        else:
                            f.seek(length, os.SEEK_CUR)
                        if is_last:
                            break
        except Exception:
            pass

    # 3. 原生二进制快速回退解析 (针对 WAV)
    elif ext == ".wav":
        try:
            with open(file_path, "rb") as f:
                if f.read(4) == b"RIFF":
                    f.seek(4, os.SEEK_CUR)
                    if f.read(4) == b"WAVE":
                        while True:
                            cid = f.read(4)
                            if len(cid) < 4:
                                break
                            csize = struct.unpack("<I", f.read(4))[0]
                            if cid == b"fmt " and csize >= 16:
                                fmt_data = f.read(csize)
                                _, ch, sr, _, _, bps = struct.unpack("<HHIIHH", fmt_data[:16])
                                if sr > 0:
                                    props["sample_rate"] = sr
                                if bps > 0:
                                    props["bit_depth"] = bps
                                if ch > 0:
                                    props["channels"] = ch
                                break
                            else:
                                f.seek(csize, os.SEEK_CUR)
        except Exception:
            pass

    return props


_COVER_CACHE_DIR = Path(os.getenv("APPDATA", str(Path.home()))) / "Klang" / "cover_cache"


def _spill_cover_to_cache(cover_bytes: Optional[bytes]) -> Optional[str]:
    """把内嵌封面原图写入缓存目录（按内容哈希去重）并返回路径，避免原始字节常驻内存。失败返回 None。"""
    if not cover_bytes:
        return None
    try:
        ext = ".png" if cover_bytes.startswith(b"\x89PNG") else ".jpg"
        target = _COVER_CACHE_DIR / (hashlib.sha1(cover_bytes).hexdigest()[:20] + ext)
        if not target.is_file() or target.stat().st_size != len(cover_bytes):
            _COVER_CACHE_DIR.mkdir(parents=True, exist_ok=True)
            tmp = target.with_suffix(target.suffix + ".tmp")
            tmp.write_bytes(cover_bytes)
            tmp.replace(target)
        return str(target)
    except Exception:
        return None


def create_track_from_file(file_path: str, index_hint: int = 0) -> NordicTrack:
    """从本地音频文件构建 NordicTrack，自动解析标签、内嵌封面、同名 LRC 与高解析度音频参数"""
    path_obj = Path(file_path)
    ext = path_obj.suffix.lower()

    title, artist, album, cover_bytes, embedded_lrc = "", "", "", None, ""
    parsed_dur: float = 0.0
    if ext == ".mp3":
        title, artist, album, cover_bytes, embedded_lrc = _extract_mp3_id3v2(file_path)
    elif ext == ".flac":
        title, artist, album, cover_bytes, embedded_lrc, parsed_dur = _extract_flac_metadata(file_path)

    # 提取真实的采样率、位深、声道数、比特率
    props = extract_audio_properties(file_path)

    local_lrc, local_trans_lrc, local_cover_path = _find_local_lrc_and_cover(path_obj)
    final_lrc = local_lrc or embedded_lrc

    fn_title, fn_artist, fn_album = _parse_filename_metadata(path_obj.stem, lrc_hint=final_lrc)
    final_title = title or fn_title
    final_artist = artist or fn_artist
    parent_folder = path_obj.parent.name
    fallback_album = "" if parent_folder.strip().lower() in _GENERIC_FOLDER_NAMES else parent_folder
    final_album = album or fn_album or fallback_album

    if final_lrc:
        lyrics = parse_lrc_text(final_lrc, trans_lrc=local_trans_lrc)
    else:
        lyrics = create_lyric_lines(
            [
                {"time": 0.0, "text": final_title, "sub": f"{final_artist}"},
                {"time": 4.0, "text": "暂无歌词", "sub": "可点击封面或设置中的搜索按钮匹配歌词"},
            ]
        )

    # 内嵌封面优先于文件夹封面（与原先 get_cover_pixmap 的取用顺序一致）；落盘后不再在内存里保留原始字节
    spilled_cover = _spill_cover_to_cache(cover_bytes)
    if spilled_cover:
        local_cover_path = spilled_cover
        cover_bytes = None

    palette = dict(_NORDIC_PALETTES[index_hint % len(_NORDIC_PALETTES)])
    resolved_str = str(path_obj.resolve())
    stable_digest = hashlib.md5(resolved_str.encode("utf-8", errors="ignore")).hexdigest()[:12]
    final_dur = parsed_dur if parsed_dur > 5.0 else (props["duration"] if props["duration"] > 5.0 else max(30.0, (lyrics[-1].end_time + 4.0) if (final_lrc and lyrics) else 180.0))
    try:
        mtime = float(path_obj.stat().st_mtime)
    except Exception:
        mtime = time.time()
    return NordicTrack(
        track_id=f"local-{stable_digest}",
        title=final_title,
        artist=final_artist,
        album=final_album,
        duration=final_dur,
        file_path=resolved_str,
        cover_path=local_cover_path,
        cover_bytes=cover_bytes,
        sample_rate=props["sample_rate"],
        bit_depth=props["bit_depth"],
        bitrate_kbps=props["bitrate_kbps"],
        channels=props["channels"],
        audio_format=props["audio_format"],
        replay_gain_db=props.get("replay_gain_db"),
        replay_peak=props.get("replay_peak"),
        added_timestamp=mtime,
        palette=palette,
        chords=[],
        lyrics=lyrics,
    )


def _read_cue_text(cue_file: Path) -> str:
    try:
        txt = _decode_text_bytes(cue_file.read_bytes())
    except Exception:
        return ""
    return txt if ("TRACK" in txt.upper() and "FILE" in txt.upper()) else ""


def _parse_cue_timecode(tc_str: str) -> float:
    """将 CUE 时间码 mm:ss:ff (75 帧/秒) 转换为浮点秒数"""
    parts = tc_str.strip().split(":")
    if len(parts) != 3:
        return 0.0
    try:
        mins = int(parts[0])
        secs = int(parts[1])
        frames = int(parts[2])
        return round(mins * 60.0 + secs + frames / 75.0, 3)
    except Exception:
        return 0.0


def _resolve_cue_audio_file(cue_dir: Path, ref_filename: str, cue_stem: str) -> Optional[Path]:
    """根据 CUE 内的 FILE 字段定位同目录实际音频文件（自动兼容 .wav -> .flac/.ape/.mp3 后缀变换）"""
    direct = cue_dir / ref_filename
    if direct.is_file() and direct.suffix.lower() in SUPPORTED_AUDIO_EXTS:
        return direct

    ref_stem = Path(ref_filename).stem
    for ext in (".flac", ".wav", ".mp3", ".m4a", ".ogg", ".aac", ".wma"):
        cand = cue_dir / f"{ref_stem}{ext}"
        if cand.is_file():
            return cand

    for ext in (".flac", ".wav", ".mp3", ".m4a", ".ogg", ".aac", ".wma"):
        cand = cue_dir / f"{cue_stem}{ext}"
        if cand.is_file():
            return cand

    return None


def parse_cue_file(cue_path: str, base_index_hint: int = 0) -> List[NordicTrack]:
    """
    解析整轨 .cue 索引文件，将单个大无损文件按分轨时间轴拆分为独立 NordicTrack 列表
    """
    cue_obj = Path(cue_path)
    if not cue_obj.is_file():
        return []

    raw_text = _read_cue_text(cue_obj)
    if not raw_text:
        return []

    cue_dir = cue_obj.parent
    resolved_cue_str = str(cue_obj.resolve())
    try:
        cue_mtime = float(cue_obj.stat().st_mtime)
    except Exception:
        cue_mtime = time.time()

    album_title = ""
    album_performer = ""
    current_file: Optional[Path] = None
    raw_tracks: List[Dict[str, Any]] = []
    cur_tr: Optional[Dict[str, Any]] = None

    for raw_line in raw_text.splitlines():
        line = raw_line.strip()
        if not line:
            continue
        upper = line.upper()

        if upper.startswith("FILE "):
            m = re.match(r'^FILE\s+"([^"]+)"', line, re.IGNORECASE)
            if not m:
                m = re.match(r"^FILE\s+(\S+)", line, re.IGNORECASE)
            if m:
                ref_name = m.group(1).strip()
                current_file = _resolve_cue_audio_file(cue_dir, ref_name, cue_obj.stem)
        elif upper.startswith("TRACK "):
            m = re.match(r"^TRACK\s+(\d+)\s+(\S+)", line, re.IGNORECASE)
            if m and m.group(2).upper() == "AUDIO":
                if cur_tr is not None:
                    raw_tracks.append(cur_tr)
                cur_tr = {
                    "num": int(m.group(1)),
                    "title": "",
                    "performer": "",
                    "file": current_file,
                    "index00": None,
                    "index01": None,
                }
        elif upper.startswith("TITLE "):
            m = re.match(r'^TITLE\s+"(.*)"$', line, re.IGNORECASE)
            val = m.group(1).strip() if m else line[6:].strip().strip('"')
            if cur_tr is not None:
                cur_tr["title"] = val
            elif not album_title:
                album_title = val
        elif upper.startswith("PERFORMER "):
            m = re.match(r'^PERFORMER\s+"(.*)"$', line, re.IGNORECASE)
            val = m.group(1).strip() if m else line[10:].strip().strip('"')
            if cur_tr is not None:
                cur_tr["performer"] = val
            elif not album_performer:
                album_performer = val
        elif upper.startswith("INDEX ") and cur_tr is not None:
            m = re.match(r"^INDEX\s+(\d+)\s+(\d+:\d+:\d+)", line, re.IGNORECASE)
            if m:
                idx_type = int(m.group(1))
                sec_val = _parse_cue_timecode(m.group(2))
                if idx_type == 0:
                    cur_tr["index00"] = sec_val
                elif idx_type == 1:
                    cur_tr["index01"] = sec_val

    if cur_tr is not None:
        raw_tracks.append(cur_tr)

    if not raw_tracks:
        return []

    if not album_title:
        album_title = cue_obj.stem

    # 缓存每个物理音频文件的属性与封面，避免对同一整轨文件重复解析
    file_props_cache: Dict[str, dict] = {}
    file_cover_cache: Dict[str, Tuple[Optional[bytes], Optional[str], str, str]] = {}

    result: List[NordicTrack] = []
    for i, tr_info in enumerate(raw_tracks):
        audio_file: Optional[Path] = tr_info.get("file") or current_file
        if audio_file is None or not audio_file.is_file():
            continue

        resolved_audio = str(audio_file.resolve())
        if resolved_audio not in file_props_cache:
            file_props_cache[resolved_audio] = extract_audio_properties(resolved_audio)
            cov_bytes: Optional[bytes] = None
            emb_lrc = ""
            ext_low = audio_file.suffix.lower()
            if ext_low == ".flac":
                _, _, _, cov_bytes, emb_lrc, f_dur = _extract_flac_metadata(resolved_audio)
                if f_dur > 5.0 and file_props_cache[resolved_audio]["duration"] <= 5.0:
                    file_props_cache[resolved_audio]["duration"] = f_dur
            elif ext_low == ".mp3":
                _, _, _, cov_bytes, emb_lrc = _extract_mp3_id3v2(resolved_audio)
            loc_lrc, loc_trans, loc_cov = _find_local_lrc_and_cover(audio_file)
            file_cover_cache[resolved_audio] = (cov_bytes, loc_cov, loc_lrc or emb_lrc, loc_trans)

        props = file_props_cache[resolved_audio]
        cov_bytes, loc_cov, full_lrc, full_trans = file_cover_cache[resolved_audio]

        start_sec = float(tr_info["index01"] if tr_info["index01"] is not None else (tr_info["index00"] or 0.0))
        end_sec = 0.0
        if i + 1 < len(raw_tracks):
            next_tr = raw_tracks[i + 1]
            next_file = next_tr.get("file") or current_file
            if next_file and str(next_file.resolve()) == resolved_audio:
                cand_end = next_tr["index01"] if next_tr["index01"] is not None else next_tr["index00"]
                if cand_end is not None and float(cand_end) > start_sec:
                    end_sec = float(cand_end)

        total_file_dur = float(props.get("duration") or 0.0)
        if end_sec <= start_sec and total_file_dur > start_sec:
            end_sec = total_file_dur

        sub_dur = (end_sec - start_sec) if end_sec > start_sec else max(30.0, total_file_dur - start_sec if total_file_dur > start_sec else 200.0)
        track_num = int(tr_info["num"])
        t_title = tr_info["title"] or f"Track {track_num:02d}"
        t_artist = tr_info["performer"] or album_performer or "未知歌手"

        # 查找是否有单曲同名 LRC，否则从整轨 LRC 中按时间段切片并平移时间戳
        track_lrc_path = cue_dir / f"{t_title}.lrc"
        if track_lrc_path.is_file():
            try:
                lyrics = parse_lrc_text(track_lrc_path.read_text(encoding="utf-8", errors="ignore"))
            except Exception:
                lyrics = []
        elif full_lrc:
            all_lines = parse_lrc_text(full_lrc, trans_lrc=full_trans)
            sliced_raw = []
            for ln in all_lines:
                if start_sec - 1.0 <= ln.time <= (end_sec + 1.0 if end_sec > start_sec else 999999.0):
                    rel_t = max(0.0, round(ln.time - start_sec, 2))
                    sliced_raw.append({"time": rel_t, "text": ln.text, "sub": ln.sub, "is_interlude": ln.is_interlude})
            lyrics = create_lyric_lines(sliced_raw) if sliced_raw else []
        else:
            lyrics = []

        if not lyrics:
            lyrics = create_lyric_lines(
                [
                    {"time": 0.0, "text": t_title, "sub": f"{t_artist} · {album_title}"},
                    {"time": 4.0, "text": "暂无歌词", "sub": "可点击封面或右键菜单匹配在线歌词"},
                ]
            )

        palette = dict(_NORDIC_PALETTES[(base_index_hint + i) % len(_NORDIC_PALETTES)])
        digest_key = f"{resolved_cue_str}#{track_num:02d}"
        stable_digest = hashlib.md5(digest_key.encode("utf-8", errors="ignore")).hexdigest()[:12]

        cue_cover_path = loc_cov
        cue_cover_bytes = cov_bytes
        spilled = _spill_cover_to_cache(cov_bytes)
        if spilled:
            cue_cover_path, cue_cover_bytes = spilled, None

        result.append(
            NordicTrack(
                track_id=f"cue-{stable_digest}-{track_num:02d}",
                title=t_title,
                artist=t_artist,
                album=album_title,
                duration=round(sub_dur, 2),
                file_path=resolved_audio,
                cover_path=cue_cover_path,
                cover_bytes=cue_cover_bytes,
                sample_rate=props["sample_rate"],
                bit_depth=props["bit_depth"],
                bitrate_kbps=props["bitrate_kbps"],
                channels=props["channels"],
                audio_format=props["audio_format"],
                replay_gain_db=props.get("replay_gain_db"),
                replay_peak=props.get("replay_peak"),
                start_time=round(start_sec, 3),
                end_time=round(end_sec, 3),
                cue_path=resolved_cue_str,
                cue_track_num=track_num,
                added_timestamp=cue_mtime + i * 0.001,
                palette=palette,
                chords=[],
                lyrics=lyrics,
            )
        )

    return result


def write_metadata_to_audio_file(
    track: NordicTrack,
    write_cover: bool = True,
    write_lyrics: bool = True,
) -> Tuple[bool, str]:
    """
    利用 mutagen 将曲目的歌名、歌手、专辑、封面图与 LRC 歌词真正写回本地音频文件 (.flac / .mp3 / .m4a / .ogg / .wav)
    返回 (是否成功, 状态提示信息)
    """
    if not track or not track.file_path:
        return False, "当前曲目没有关联本地音频文件"

    p = Path(track.file_path)
    if not p.is_file():
        return False, "本地音频文件不存在"

    ext = p.suffix.lower()
    is_cue_subtrack = bool(track.cue_path or track.start_time > 0.0 or track.end_time > 0.0)

    # 准备封面字节与 MIME 类型
    cov_data: Optional[bytes] = track.cover_bytes
    if not cov_data and track.cover_path and Path(track.cover_path).is_file():
        try:
            cov_data = Path(track.cover_path).read_bytes()
        except Exception:
            cov_data = None

    cov_mime = "image/jpeg"
    if cov_data and cov_data.startswith(b"\x89PNG"):
        cov_mime = "image/png"

    lrc_str = format_lyrics_to_lrc(track.lyrics) if write_lyrics else ""

    try:
        if ext == ".flac":
            from mutagen.flac import FLAC, Picture

            audio = FLAC(str(p))
            if not is_cue_subtrack and track.title:
                audio["TITLE"] = [track.title]
            if track.artist:
                audio["ARTIST"] = [track.artist]
            if track.album:
                audio["ALBUM"] = [track.album]
            if not is_cue_subtrack and lrc_str:
                audio["LYRICS"] = [lrc_str]
            if write_cover and cov_data:
                audio.clear_pictures()
                pic = Picture()
                pic.type = 3  # Front Cover
                pic.mime = cov_mime
                pic.desc = "Front Cover"
                pic.data = cov_data
                audio.add_picture(pic)
            audio.save()
            return True, f"已将标签与封面写入 {p.name}"

        elif ext == ".mp3":
            from mutagen.id3 import ID3, ID3NoHeaderError, TIT2, TPE1, TALB, USLT, APIC

            try:
                tags = ID3(str(p))
            except ID3NoHeaderError:
                tags = ID3()

            if not is_cue_subtrack and track.title:
                tags.delall("TIT2")
                tags.add(TIT2(encoding=3, text=track.title))
            if track.artist:
                tags.delall("TPE1")
                tags.add(TPE1(encoding=3, text=track.artist))
            if track.album:
                tags.delall("TALB")
                tags.add(TALB(encoding=3, text=track.album))
            if not is_cue_subtrack and lrc_str:
                tags.delall("USLT")
                tags.add(USLT(encoding=3, lang="chi", desc="", text=lrc_str))
            if write_cover and cov_data:
                tags.delall("APIC")
                tags.add(APIC(encoding=3, mime=cov_mime, type=3, desc="Cover", data=cov_data))
            tags.save(str(p), v2_version=3)
            return True, f"已将 ID3 标签与封面写入 {p.name}"

        elif ext in (".m4a", ".aac"):
            from mutagen.mp4 import MP4, MP4Cover

            audio = MP4(str(p))
            if audio.tags is None:
                audio.add_tags()
            if not is_cue_subtrack and track.title:
                audio.tags["\xa9nam"] = [track.title]
            if track.artist:
                audio.tags["\xa9ART"] = [track.artist]
            if track.album:
                audio.tags["\xa9alb"] = [track.album]
            if not is_cue_subtrack and lrc_str:
                audio.tags["\xa9lyr"] = [lrc_str]
            if write_cover and cov_data:
                img_fmt = MP4Cover.FORMAT_PNG if cov_mime == "image/png" else MP4Cover.FORMAT_JPEG
                audio.tags["covr"] = [MP4Cover(cov_data, imageformat=img_fmt)]
            audio.save()
            return True, f"已将标签与封面写入 {p.name}"

        elif ext == ".ogg":
            from mutagen.oggvorbis import OggVorbis

            audio = OggVorbis(str(p))
            if not is_cue_subtrack and track.title:
                audio["TITLE"] = [track.title]
            if track.artist:
                audio["ARTIST"] = [track.artist]
            if track.album:
                audio["ALBUM"] = [track.album]
            if not is_cue_subtrack and lrc_str:
                audio["LYRICS"] = [lrc_str]
            audio.save()
            return True, f"已将标签写入 {p.name}"

        elif ext == ".wav":
            from mutagen.wave import WAVE
            from mutagen.id3 import TIT2, TPE1, TALB, USLT, APIC

            audio = WAVE(str(p))
            if audio.tags is None:
                audio.add_tags()
            if not is_cue_subtrack and track.title:
                audio.tags.add(TIT2(encoding=3, text=track.title))
            if track.artist:
                audio.tags.add(TPE1(encoding=3, text=track.artist))
            if track.album:
                audio.tags.add(TALB(encoding=3, text=track.album))
            if not is_cue_subtrack and lrc_str:
                audio.tags.add(USLT(encoding=3, lang="chi", desc="", text=lrc_str))
            if write_cover and cov_data:
                audio.tags.add(APIC(encoding=3, mime=cov_mime, type=3, desc="Cover", data=cov_data))
            audio.save()
            return True, f"已将标签与封面写入 {p.name}"

        else:
            return False, f"暂不支持写入 {ext} 格式内嵌标签"
    except Exception as e:
        return False, f"写入标签失败：{e}"


def scan_folder_for_audio(folder_path: str) -> List[str]:
    """
    扫描文件夹下的所有受支持音频与 .cue 分轨文件。
    若文件夹内存在 .cue 索引文件，自动优先导入 .cue 并排除被该 .cue 引用的整轨音频文件，防止整轨与分轨重复。
    """
    cue_files: List[Path] = []
    audio_files: List[Path] = []
    try:
        root = Path(folder_path)
        for p in sorted(root.rglob("*")):
            if not p.is_file():
                continue
            suf = p.suffix.lower()
            if suf == ".cue":
                cue_files.append(p)
            elif suf in SUPPORTED_AUDIO_EXTS:
                audio_files.append(p)
    except Exception:
        return []

    cue_referenced_audio = set()
    valid_cues: List[str] = []
    for c_path in cue_files:
        raw_txt = _read_cue_text(c_path)
        if not raw_txt:
            continue
        has_ref = False
        for line in raw_txt.splitlines():
            s = line.strip()
            if s.upper().startswith("FILE "):
                m = re.match(r'^FILE\s+"([^"]+)"', s, re.IGNORECASE) or re.match(r"^FILE\s+(\S+)", s, re.IGNORECASE)
                if m:
                    resolved = _resolve_cue_audio_file(c_path.parent, m.group(1).strip(), c_path.stem)
                    if resolved is not None:
                        cue_referenced_audio.add(str(resolved.resolve()))
                        has_ref = True
        if has_ref:
            valid_cues.append(str(c_path.resolve()))

    found: List[str] = list(valid_cues)
    for a_path in audio_files:
        res_str = str(a_path.resolve())
        if res_str not in cue_referenced_audio:
            found.append(res_str)
    return found
