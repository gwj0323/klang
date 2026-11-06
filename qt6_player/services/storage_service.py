import json
import os
import sys
from pathlib import Path
from typing import Dict, Any, List, Optional

from qt6_player.data.tracks import NordicTrack, NORDIC_TRACKS, LyricLine
from qt6_player.services.metadata_service import create_track_from_file


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
        "play_mode": "loop_list",
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
        "window_geometry": None,
        "is_maximized": False,
        "last_track_index": 0,
        "playlist": None,
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
                "cover_path": str(t.cover_path) if t.cover_path else "",
            }

    payload = dict(settings)
    payload["playlist"] = playlist_data
    payload["local_tracks"] = local_tracks_data
    payload["builtin_overrides"] = builtin_overrides

    safe_payload = _make_json_safe(payload)

    try:
        _CONFIG_PATH.parent.mkdir(parents=True, exist_ok=True)
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
                syllables=[] if is_inter else build_syllables(txt, st, et),
            )
        )
    return result


_LEGACY_BUILTIN_IDS = {"bergen-mist", "oslo-atelier"}


def restore_tracks_from_state(state: Dict[str, Any]) -> List[NordicTrack]:
    tracks: List[NordicTrack] = []

    saved_playlist = state.get("playlist")
    if saved_playlist and isinstance(saved_playlist, list):
        for idx, item in enumerate(saved_playlist):
            tid = str(item.get("track_id", ""))
            if tid in _LEGACY_BUILTIN_IDS:
                continue
            fp = item.get("file_path")
            if not fp or not Path(fp).is_file():
                continue
            saved_cov = item.get("cover_path")
            valid_cov = str(saved_cov) if (saved_cov and Path(str(saved_cov)).is_file()) else None

            tr = create_track_from_file(fp, index_hint=idx)
            if item.get("title"):
                tr.title = str(item["title"])
            if item.get("artist"):
                tr.artist = str(item["artist"])
            if item.get("album"):
                tr.album = str(item["album"])
            if item.get("duration"):
                tr.duration = float(item["duration"])
            if valid_cov and not tr.cover_path:
                tr.cover_path = valid_cov
            tr.is_favorite = bool(item.get("is_favorite", False))
            tr.lyric_offset = float(item.get("lyric_offset", 0.0))
            saved_lyrics = _parse_saved_lyrics(item.get("lyrics", []))
            if saved_lyrics:
                tr.lyrics = saved_lyrics
            tracks.append(tr)
        return tracks

    for idx, item in enumerate(state.get("local_tracks") or []):
        tid = str(item.get("track_id", ""))
        if tid in _LEGACY_BUILTIN_IDS:
            continue
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
        saved_cov = item.get("cover_path")
        if saved_cov and Path(str(saved_cov)).is_file() and not tr.cover_path:
            tr.cover_path = str(saved_cov)
        tr.is_favorite = bool(item.get("is_favorite", False))
        tr.lyric_offset = float(item.get("lyric_offset", 0.0))
        tracks.append(tr)

    return tracks
