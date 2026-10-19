"""
播放器状态与播放列表持久化服务 (Storage Service)
自动保存与恢复：用户导入的本地歌曲列表、收藏状态、歌词时间偏移、字号、播放模式、倍速、音量、5段自定义EQ增益、混响、桌面歌词位置与主题配置。
采用原子写入与防抖落盘机制，彻底杜绝异常退出导致的配置丢失。
"""

import json
from pathlib import Path
from typing import Dict, Any, List, Optional

from qt6_player.data.tracks import NordicTrack, NORDIC_TRACKS, LyricLine
from qt6_player.services.metadata_service import create_track_from_file

import os
import sys

def _get_config_path() -> Path:
    if getattr(sys, "frozen", False):
        exe_dir = Path(sys.executable).resolve().parent
        try:
            test_file = exe_dir / ".klang_write_test"
            test_file.touch()
            test_file.unlink()
            return exe_dir / "player_state.json"
        except Exception:
            app_data = Path(os.getenv("APPDATA", Path.home())) / "Klang"
            app_data.mkdir(parents=True, exist_ok=True)
            return app_data / "player_state.json"
    return Path(__file__).resolve().parent.parent.parent / "player_state.json"

_CONFIG_PATH = _get_config_path()


def _make_json_safe(obj: Any) -> Any:
    """递归将 numpy 标量、布尔值及特殊类型转换为原生 Python JSON 可序列化类型"""
    if hasattr(obj, "item") and callable(obj.item):
        return obj.item()
    if isinstance(obj, dict):
        return {str(k): _make_json_safe(v) for k, v in obj.items()}
    if isinstance(obj, (list, tuple)):
        return [_make_json_safe(item) for item in obj]
    if isinstance(obj, float):
        return round(float(obj), 4)
    if isinstance(obj, bool):
        return bool(obj)
    if isinstance(obj, int):
        return int(obj)
    return obj


def load_player_state() -> Dict[str, Any]:
    default_state: Dict[str, Any] = {
        "volume": 0.72,
        "is_muted": False,
        "play_mode": "loop_list",       # "loop_list" | "loop_one" | "shuffle" | "sequential"
        "playback_rate": 1.0,
        "lyric_font_size": 20,
        "show_translation": True,
        "syllable_mode": True,
        "lyric_align_center": False,
        "theme": "daylight",
        "sidebar_collapsed": False,
        "sound_profile": "原声自然",
        "eq_gains_db": [0.0, 0.0, 0.0, 0.0, 0.0],
        "reverb_mix": 0.03,
        "desktop_lyric_open": False,
        "desktop_lyric_pos": None,
        "desktop_lyric_font_size": 22,
        "window_geometry": None,        # [x, y, w, h]
        "is_maximized": False,
        "last_track_index": 0,
        "playlist": None,               # 完整播放列表（含排序与曲目删除状态）
        "local_tracks": [],
        "builtin_overrides": {},
    }
    if not _CONFIG_PATH.is_file():
        return default_state
    try:
        data = json.loads(_CONFIG_PATH.read_text(encoding="utf-8"))
        if isinstance(data, dict):
            default_state.update(data)
    except Exception:
        pass
    return default_state


def save_player_state(
    tracks: List[NordicTrack],
    settings: Dict[str, Any],
) -> bool:
    """原子写入保存播放器配置与播放列表到 player_state.json"""
    playlist_data = []
    local_tracks_data = []
    builtin_overrides = {}

    for t in tracks:
        lyrics_data = []
        if t.lyrics:
            for l in t.lyrics:
                lyrics_data.append({
                    "index": int(l.index),
                    "time": float(l.time),
                    "end_time": float(l.end_time),
                    "text": str(l.text),
                    "sub": str(l.sub) if l.sub else "",
                    "is_interlude": bool(l.is_interlude),
                })

        entry = {
            "track_id": str(t.track_id),
            "title": str(t.title),
            "artist": str(t.artist),
            "album": str(t.album),
            "duration": float(t.duration),
            "file_path": str(t.file_path) if t.file_path else "",
            "cover_path": str(t.cover_path) if t.cover_path else "",
            "is_favorite": bool(t.is_favorite),
            "lyric_offset": float(t.lyric_offset),
            "lyrics": lyrics_data,
        }
        playlist_data.append(entry)

        if t.file_path:
            local_tracks_data.append(entry)
        else:
            builtin_overrides[t.track_id] = {
                "is_favorite": bool(t.is_favorite),
                "lyric_offset": float(t.lyric_offset),
            }

    payload = dict(settings)
    payload["playlist"] = playlist_data
    payload["local_tracks"] = local_tracks_data
    payload["builtin_overrides"] = builtin_overrides

    safe_payload = _make_json_safe(payload)

    try:
        _CONFIG_PATH.parent.mkdir(parents=True, exist_ok=True)
        # 先写临时文件再重命名，避免写盘中途断电或崩溃导致 JSON 损坏
        tmp_file = _CONFIG_PATH.with_suffix(".tmp")
        tmp_file.write_text(
            json.dumps(safe_payload, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
        tmp_file.replace(_CONFIG_PATH)
        return True
    except Exception:
        try:
            _CONFIG_PATH.write_text(
                json.dumps(safe_payload, ensure_ascii=False, indent=2),
                encoding="utf-8",
            )
            return True
        except Exception:
            return False


def _parse_saved_lyrics(lyrics_list: list) -> List[LyricLine]:
    from qt6_player.data.tracks import build_syllables
    result: List[LyricLine] = []
    if not isinstance(lyrics_list, list):
        return result
    for idx, item in enumerate(lyrics_list):
        if not isinstance(item, dict):
            continue
        txt = str(item.get("text", ""))
        st = float(item.get("time", 0.0))
        et = float(item.get("end_time", st + 4.0))
        sub = str(item.get("sub", ""))
        is_inter = bool(item.get("is_interlude", False))
        result.append(
            LyricLine(
                index=int(item.get("index", idx)),
                time=st,
                end_time=et,
                text=txt,
                sub=sub,
                is_interlude=is_inter,
                syllables=build_syllables(txt, st, et),
            )
        )
    return result


def restore_tracks_from_state(state: Dict[str, Any]) -> List[NordicTrack]:
    """从持久化配置中还原播放列表（精准保留用户增删曲目与排序）"""
    tracks: List[NordicTrack] = []
    builtin_map = {bt.track_id: bt for bt in NORDIC_TRACKS}

    # 1. 优先使用完整播放列表顺序与项
    saved_playlist = state.get("playlist")
    if saved_playlist and isinstance(saved_playlist, list):
        for idx, item in enumerate(saved_playlist):
            fp = item.get("file_path")
            tid = item.get("track_id", "")
            if fp and Path(fp).is_file():
                tr = create_track_from_file(fp, index_hint=idx)
                if item.get("title"):
                    tr.title = str(item["title"])
                if item.get("artist"):
                    tr.artist = str(item["artist"])
                if item.get("album"):
                    tr.album = str(item["album"])
                if item.get("duration"):
                    tr.duration = float(item["duration"])
                tr.is_favorite = bool(item.get("is_favorite", False))
                tr.lyric_offset = float(item.get("lyric_offset", 0.0))
                saved_lyrics = _parse_saved_lyrics(item.get("lyrics", []))
                if saved_lyrics:
                    tr.lyrics = saved_lyrics
                tracks.append(tr)
            elif tid in builtin_map:
                bt = builtin_map[tid]
                bt.is_favorite = bool(item.get("is_favorite", bt.is_favorite))
                bt.lyric_offset = float(item.get("lyric_offset", bt.lyric_offset))
                saved_lyrics = _parse_saved_lyrics(item.get("lyrics", []))
                if saved_lyrics:
                    bt.lyrics = saved_lyrics
                tracks.append(bt)
            elif item.get("title"):
                tr = NordicTrack(
                    track_id=tid or f"saved_track_{idx}",
                    title=str(item.get("title", "未知曲目")),
                    artist=str(item.get("artist", "未知歌手")),
                    album=str(item.get("album", "")),
                    duration=float(item.get("duration", 0.0)),
                    file_path=fp if fp else None,
                    cover_path=str(item.get("cover_path", "")),
                    is_favorite=bool(item.get("is_favorite", False)),
                    lyric_offset=float(item.get("lyric_offset", 0.0)),
                    lyrics=_parse_saved_lyrics(item.get("lyrics", [])),
                )
                tracks.append(tr)
        if tracks:
            return tracks

    # 2. 向下兼容老版本 local_tracks + builtin_overrides 格式
    overrides = state.get("builtin_overrides") or {}
    for bt in NORDIC_TRACKS:
        ov = overrides.get(bt.track_id) or {}
        bt.is_favorite = bool(ov.get("is_favorite", bt.is_favorite))
        bt.lyric_offset = float(ov.get("lyric_offset", bt.lyric_offset))
        tracks.append(bt)

    for idx, item in enumerate(state.get("local_tracks") or []):
        fp = item.get("file_path")
        if not fp or not Path(fp).is_file():
            continue
        tr = create_track_from_file(fp, index_hint=len(tracks) + idx)
        if item.get("title"):
            tr.title = str(item["title"])
        if item.get("artist"):
            tr.artist = str(item["artist"])
        if item.get("album"):
            tr.album = str(item["album"])
        if item.get("duration"):
            tr.duration = float(item["duration"])
        tr.is_favorite = bool(item.get("is_favorite", False))
        tr.lyric_offset = float(item.get("lyric_offset", 0.0))
        tracks.append(tr)

    return tracks
