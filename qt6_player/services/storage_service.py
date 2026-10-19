"""
播放器状态与播放列表持久化服务 (Storage Service)
自动保存与恢复：用户导入的本地歌曲列表、收藏状态、歌词时间偏移、字号、播放模式、倍速、音量与主题配置。
"""

import json
from pathlib import Path
from typing import Dict, Any, List

from qt6_player.data.tracks import NordicTrack, NORDIC_TRACKS
from qt6_player.services.metadata_service import create_track_from_file

_CONFIG_PATH = Path(__file__).resolve().parent.parent.parent / "player_state.json"


def load_player_state() -> Dict[str, Any]:
    default_state = {
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
        "last_track_index": 0,
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
) -> None:
    local_tracks_data = []
    builtin_overrides = {}

    for t in tracks:
        if t.file_path:
            local_tracks_data.append(
                {
                    "file_path": t.file_path,
                    "title": t.title,
                    "artist": t.artist,
                    "album": t.album,
                    "duration": t.duration,
                    "cover_path": t.cover_path,
                    "is_favorite": t.is_favorite,
                    "lyric_offset": t.lyric_offset,
                }
            )
        else:
            builtin_overrides[t.track_id] = {
                "is_favorite": t.is_favorite,
                "lyric_offset": t.lyric_offset,
            }

    payload = dict(settings)
    payload["local_tracks"] = local_tracks_data
    payload["builtin_overrides"] = builtin_overrides

    try:
        _CONFIG_PATH.write_text(
            json.dumps(payload, ensure_ascii=False, indent=2),
            encoding="utf-8",
        )
    except Exception:
        pass


def restore_tracks_from_state(state: Dict[str, Any]) -> List[NordicTrack]:
    tracks: List[NordicTrack] = []
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
            tr.title = item["title"]
        if item.get("artist"):
            tr.artist = item["artist"]
        if item.get("album"):
            tr.album = item["album"]
        if item.get("duration"):
            tr.duration = float(item["duration"])
        tr.is_favorite = bool(item.get("is_favorite", False))
        tr.lyric_offset = float(item.get("lyric_offset", 0.0))
        tracks.append(tr)

    return tracks
