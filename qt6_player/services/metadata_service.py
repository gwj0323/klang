"""
本地音乐导入与音频标签/同目录词图解析服务
支持解析文件名、MP3 ID3v2 内嵌标题/歌手/专辑/封面、FLAC Vorbis 注释与图片块，以及自动加载同目录同名 .lrc 与封面图。
"""

import os
import re
import struct
from pathlib import Path
from typing import List, Optional, Tuple

from qt6_player.data.tracks import NordicTrack, parse_lrc_text, create_lyric_lines

SUPPORTED_AUDIO_EXTS = {".mp3", ".flac", ".wav", ".ogg", ".m4a", ".aac", ".wma"}

# 柔和北欧色卡预设池（用于无封面时的默认色卡）
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


def _extract_flac_metadata(file_path: str) -> Tuple[str, str, str, Optional[bytes], str]:
    """轻量读取 FLAC Vorbis 注释与 Picture 块封面"""
    title, artist, album, lrc_text = "", "", "", ""
    cover_bytes: Optional[bytes] = None
    try:
        with open(file_path, "rb") as f:
            if f.read(4) != b"fLaC":
                return title, artist, album, cover_bytes, lrc_text
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
                    if block_type == 4 and len(data) >= 8:  # VORBIS_COMMENT
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
                        mime_len = struct.unpack(">I", data[p : p + 4])[0]
                        p += 4 + mime_len
                        desc_len = struct.unpack(">I", data[p : p + 4])[0]
                        p += 4 + desc_len + 16
                        pic_len = struct.unpack(">I", data[p : p + 4])[0]
                        p += 4
                        if 0 < pic_len <= len(data) - p:
                            cover_bytes = data[p : p + pic_len]
                if is_last:
                    break
    except Exception:
        pass
    return title, artist, album, cover_bytes, lrc_text


def _parse_filename_artist_title(stem: str) -> Tuple[str, str]:
    """从文件名智能拆分 '歌手 - 歌名'，并去除前导曲目编号"""
    cleaned = re.sub(r"^\s*\d{1,3}[\s._-]+", "", stem).strip()
    if " - " in cleaned:
        parts = cleaned.split(" - ", 1)
        return parts[0].strip(), parts[1].strip()
    if "-" in cleaned:
        parts = cleaned.split("-", 1)
        if len(parts[0].strip()) >= 2 and len(parts[1].strip()) >= 1:
            return parts[0].strip(), parts[1].strip()
    return "未知歌手", cleaned or stem


def _find_local_lrc_and_cover(audio_path: Path) -> Tuple[str, Optional[str]]:
    """查找音频同目录下的同名 .lrc 歌词与封面图片"""
    folder = audio_path.parent
    stem = audio_path.stem
    lrc_text = ""
    lrc_candidate = folder / f"{stem}.lrc"
    if lrc_candidate.is_file():
        for enc in ("utf-8", "utf-8-sig", "gb18030", "utf-16"):
            try:
                lrc_text = lrc_candidate.read_text(encoding=enc)
                if lrc_text.strip():
                    break
            except Exception:
                continue

    cover_path: Optional[str] = None
    for img_name in (f"{stem}.jpg", f"{stem}.png", f"{stem}.jpeg", "cover.jpg", "cover.png", "folder.jpg"):
        img_candidate = folder / img_name
        if img_candidate.is_file():
            cover_path = str(img_candidate)
            break

    return lrc_text, cover_path


def create_track_from_file(file_path: str, index_hint: int = 0) -> NordicTrack:
    """从本地音频文件构建 NordicTrack，自动解析标签、内嵌封面、同名 LRC"""
    path_obj = Path(file_path)
    ext = path_obj.suffix.lower()

    title, artist, album, cover_bytes, embedded_lrc = "", "", "", None, ""
    if ext == ".mp3":
        title, artist, album, cover_bytes, embedded_lrc = _extract_mp3_id3v2(file_path)
    elif ext == ".flac":
        title, artist, album, cover_bytes, embedded_lrc = _extract_flac_metadata(file_path)

    fn_artist, fn_title = _parse_filename_artist_title(path_obj.stem)
    final_title = title or fn_title
    final_artist = artist or fn_artist
    final_album = album or path_obj.parent.name

    local_lrc, local_cover_path = _find_local_lrc_and_cover(path_obj)
    final_lrc = local_lrc or embedded_lrc

    if final_lrc:
        lyrics = parse_lrc_text(final_lrc)
    else:
        lyrics = create_lyric_lines(
            [
                {"time": 0.0, "text": final_title, "sub": f"{final_artist}"},
                {"time": 4.0, "text": "正在自动匹配在线歌词与封面…", "sub": "也可点击顶部「匹配词图」手动搜索"},
            ]
        )

    palette = dict(_NORDIC_PALETTES[index_hint % len(_NORDIC_PALETTES)])
    return NordicTrack(
        track_id=f"local-{abs(hash(file_path))}",
        title=final_title,
        artist=final_artist,
        album=final_album,
        duration=max(30.0, (lyrics[-1].end_time + 4.0) if lyrics else 180.0),
        file_path=str(path_obj.resolve()),
        cover_path=local_cover_path,
        cover_bytes=cover_bytes,
        palette=palette,
        chords=[],
        lyrics=lyrics,
    )


def scan_folder_for_audio(folder_path: str) -> List[str]:
    """扫描文件夹下的所有受支持音频文件"""
    found: List[str] = []
    try:
        root = Path(folder_path)
        for p in sorted(root.rglob("*")):
            if p.is_file() and p.suffix.lower() in SUPPORTED_AUDIO_EXTS:
                found.append(str(p.resolve()))
    except Exception:
        pass
    return found
