import logging as _logging
import hashlib
import json
import os
import re
import shutil
import sys
import threading
import time
from pathlib import Path
from typing import Dict, Any, List, Optional

from qt6_player.data.tracks import NordicTrack, LyricLine
from qt6_player.services.metadata_service import create_track_from_file, _GENERIC_FOLDER_NAMES, _NORDIC_PALETTES


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


def _load_state_from_disk() -> Dict[str, Any]:
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
        "backdrop_material": "mica",
        "sidebar_collapsed": False,
        "sound_profile": "默认原声",
        "eq_gains_db": [0.0, 0.0, 0.0, 0.0, 0.0],
        "reverb_mix": 0.03,
        "dolby_atmos_mode": "dolby_atmos",
        "spatial_width": 1.35,
        "spatial_height": 0.65,
        "audio_output_mode": "binaural",
        "selected_device_id": "default",
        "crossfade_sec": 0.0,
        "replay_gain_enabled": False,
        "vocal_balance": 0.0,
        "vinyl_warmth": 0.0,
        "headphone_profile": "bypass",
        "desktop_lyric_open": False,
        "desktop_lyric_pos": None,
        "desktop_lyric_font_size": 22,
        "immersive_fps": 120,
        "window_geometry": None,
        "is_maximized": False,
        "last_track_index": 0,
        "custom_playlists": {},
        "watched_folders": [],
        "nav_sort_mode": "default",
        "nav_group_mode": "artist",
        "playlist": None,
        "local_tracks": [],
        "builtin_overrides": {},
    }
    if not _CONFIG_PATH.is_file():
        return default_state
    for candidate in (_CONFIG_PATH, _CONFIG_PATH.with_suffix(".bak")):
        try:
            data = json.loads(candidate.read_text(encoding="utf-8"))
        except Exception:
            if candidate == _CONFIG_PATH:
                # 主文件损坏：保留一份副本供人工恢复，并尝试 .bak
                try:
                    shutil.copy2(_CONFIG_PATH, _CONFIG_PATH.with_suffix(".corrupt"))
                except Exception:
                    _logging.getLogger(__name__).debug("ignored exception", exc_info=True)
            continue
        if isinstance(data, dict):
            default_state.update(data)
            break
    return default_state


_SAVE_LOCK = threading.Lock()

# 启动时文件暂时不可用（外接盘/NAS 未挂载等）的曲目原始条目：
# 不加入播放列表，但保存时原样保留，避免收藏、歌词偏移、歌词缓存被永久抹掉。
_UNAVAILABLE_ENTRIES: Dict[str, Dict[str, Any]] = {}


def _write_payload_to_disk(safe_payload: Dict[str, Any]) -> bool:
    with _SAVE_LOCK:
        try:
            _CONFIG_PATH.parent.mkdir(parents=True, exist_ok=True)
            tmp_file = _CONFIG_PATH.with_suffix(".tmp")
            tmp_file.write_text(
                json.dumps(safe_payload, ensure_ascii=False, indent=2),
                encoding="utf-8",
            )
            if _CONFIG_PATH.is_file():
                try:
                    shutil.copy2(_CONFIG_PATH, _CONFIG_PATH.with_suffix(".bak"))
                except Exception:
                    _logging.getLogger(__name__).debug("ignored exception", exc_info=True)
            tmp_file.replace(_CONFIG_PATH)
            return True
        except Exception:
            return False


# ── 单写线程 + 内存最新快照 ───────────────────────────────────────────
# 所有“读-改-写”都在 _STATE_LOCK 内基于内存缓存完成（不再重读可能尚未落盘的旧文件），
# 后台线程只写最新快照；连续多次保存会被合并，旧快照不会覆盖新状态。
_STATE_LOCK = threading.RLock()
_STATE_CACHE: Optional[Dict[str, Any]] = None
_WRITE_EVENT = threading.Event()
_WRITER_STARTED = False


def load_player_state() -> Dict[str, Any]:
    """返回最新状态：已有内存快照时用快照（可能尚未落盘），否则读磁盘。"""
    with _STATE_LOCK:
        if _STATE_CACHE is not None:
            return dict(_STATE_CACHE)
    return _load_state_from_disk()


def _current_state_locked() -> Dict[str, Any]:
    global _STATE_CACHE
    if _STATE_CACHE is None:
        _STATE_CACHE = _load_state_from_disk()
    return _STATE_CACHE


def _writer_loop() -> None:
    while True:
        _WRITE_EVENT.wait()
        _WRITE_EVENT.clear()
        with _STATE_LOCK:
            snapshot = _make_json_safe(dict(_current_state_locked()))
        # 写入失败（如杀毒软件短暂占用文件）时重试几次，避免这次改动要等下一次保存才落盘
        for _attempt in range(5):
            if _write_payload_to_disk(snapshot):
                break
            time.sleep(1.0)


def _commit_state(new_state: Dict[str, Any], sync: bool) -> bool:
    """在 _STATE_LOCK 内调用：更新内存快照，并同步写盘或交给后台写线程。"""
    global _STATE_CACHE, _WRITER_STARTED
    _STATE_CACHE = new_state
    if sync:
        return _write_payload_to_disk(_make_json_safe(dict(new_state)))
    if not _WRITER_STARTED:
        _WRITER_STARTED = True
        threading.Thread(target=_writer_loop, daemon=True, name="klang-state-writer").start()
    _WRITE_EVENT.set()
    return True


def _serialize_lyrics_cached(t: NordicTrack) -> list:
    """歌词序列化结果按（列表对象, 行数）缓存在曲目上：每次自动保存不必为整个曲库重复序列化所有歌词"""
    lyr = t.lyrics
    if not lyr:
        return []
    # 缓存同时持有列表引用（用 is 比较），避免旧列表被回收后 id 复用导致误命中；
    # 键里带上时间之和，原地修改时间轴时也能失效
    key = (
        len(lyr),
        sum(len(l.text or "") + len(l.sub or "") for l in lyr),
        round(sum(float(l.time) + float(l.end_time) for l in lyr), 3),
    )
    cached = getattr(t, "_lyrics_ser_cache", None)
    if cached is not None and cached[0] == key and cached[2] is lyr:
        return cached[1]
    data = [
        {
            "index": int(l.index),
            "time": float(l.time),
            "end_time": float(l.end_time),
            "text": str(l.text),
            "sub": str(l.sub) if l.sub else "",
            "is_interlude": bool(l.is_interlude),
        }
        for l in lyr
    ]
    t._lyrics_ser_cache = (key, data, lyr)
    return data


def save_player_state(
    tracks: List[NordicTrack],
    settings: Dict[str, Any],
    sync: bool = False,
) -> bool:
    playlist_data = []
    builtin_overrides = {}

    for t in tracks:
        # 严格隔离：网易云在线/缓存曲目绝不持久化进入本地音乐库核心数据
        if str(t.track_id).startswith("netease_"):
            continue

        lyrics_data = _serialize_lyrics_cached(t)

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
            "sample_rate": int(getattr(t, "sample_rate", 44100)),
            "bit_depth": int(getattr(t, "bit_depth", 16)),
            "channels": int(getattr(t, "channels", 2)),
            "bitrate_kbps": int(getattr(t, "bitrate_kbps", 0)),
            "audio_format": str(getattr(t, "audio_format", "")),
            "replay_gain_db": getattr(t, "replay_gain_db", None),
            "replay_peak": getattr(t, "replay_peak", None),
            "start_time": float(getattr(t, "start_time", 0.0) or 0.0),
            "end_time": float(getattr(t, "end_time", 0.0) or 0.0),
            "cue_path": str(getattr(t, "cue_path", "") or ""),
            "cue_track_num": int(getattr(t, "cue_track_num", 0) or 0),
            "added_timestamp": float(getattr(t, "added_timestamp", 0.0) or 0.0),
            "acoustic_dna": getattr(t, "acoustic_dna", None),
            "lyrics": lyrics_data,
        }
        playlist_data.append(entry)

        if not t.file_path:
            builtin_overrides[t.track_id] = {
                "is_favorite": bool(t.is_favorite),
                "lyric_offset": float(t.lyric_offset),
                "cover_path": str(t.cover_path) if t.cover_path else "",
            }

    present_ids = {str(t.track_id) for t in tracks}
    for tid, raw in list(_UNAVAILABLE_ENTRIES.items()):
        if tid in present_ids:
            _UNAVAILABLE_ENTRIES.pop(tid, None)
            continue
        playlist_data.append(raw)

    with _STATE_LOCK:
        payload = dict(_current_state_locked())
        payload.update(settings)
        payload["playlist"] = playlist_data
        payload["local_tracks"] = []  # 旧字段：只在没有 playlist 的老存档里才会被读取，不再重复保存整个曲库
        payload["builtin_overrides"] = builtin_overrides
        return _commit_state(payload, sync)


def update_player_state_fields(updates: Dict[str, Any], sync: bool = False) -> bool:
    """Safely update specific key-value fields in player_state.json without touching others."""
    with _STATE_LOCK:
        state = dict(_current_state_locked())
        state.update(updates)
        return _commit_state(state, sync)


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


def _apply_saved_meta_if_valid(tr: NordicTrack, item: Dict[str, Any]) -> None:
    saved_title = str(item.get("title") or "").strip()
    saved_artist = str(item.get("artist") or "").strip()
    saved_album = str(item.get("album") or "").strip()

    # 若旧版缓存将 '歌名 - 歌手[专辑]' 反向解析为 title='歌手[专辑]'，则自动使用新解析出的正确歌名、歌手与专辑
    # 标题与新解析结果一致时无需判断（带 [Live]/【Remastered】 的正常标题不应被误判为旧版反向解析）
    is_legacy_inverted = saved_title != tr.title and bool(
        re.search(r"[\[【].+?[\]】]\s*$", saved_title)
        or (saved_album.lower() in _GENERIC_FOLDER_NAMES and tr.album)
    )
    if not is_legacy_inverted:
        if saved_title:
            tr.title = saved_title
        if saved_artist:
            tr.artist = saved_artist
        if saved_album and saved_album.lower() not in _GENERIC_FOLDER_NAMES:
            tr.album = saved_album


def _try_fast_restore(item: Dict[str, Any], index_hint: int, fp: str) -> Optional[NordicTrack]:
    """用上次保存的元数据直接构造曲目，跳过逐文件解析标签/封面/歌词（启动时对大曲库非常慢）。
    仅当文件的修改时间与保存时记录的一致、且保存的数据完整时才走快速路径，否则返回 None 让调用方走完整解析。"""
    try:
        saved_ts = float(item.get("added_timestamp") or 0.0)
        if saved_ts <= 0.0 or not item.get("title") or float(item.get("duration") or 0.0) < 5.0:
            return None
        if not item.get("audio_format") or not item.get("sample_rate"):
            return None
        if not item.get("lyrics"):
            return None  # 没有保存过歌词的曲目走完整解析（会生成占位歌词/读取同名 .lrc）
        path_obj = Path(fp)
        if abs(float(path_obj.stat().st_mtime) - saved_ts) > 1e-3:
            return None
        saved_cov = item.get("cover_path")
        cover = str(saved_cov) if (saved_cov and Path(str(saved_cov)).is_file()) else None
        resolved = str(path_obj.resolve())
        digest = hashlib.md5(resolved.encode("utf-8", errors="ignore")).hexdigest()[:12]
        return NordicTrack(
            track_id=f"local-{digest}",
            title=str(item.get("title")),
            artist=str(item.get("artist") or ""),
            album=str(item.get("album") or ""),
            duration=float(item["duration"]),
            file_path=resolved,
            cover_path=cover,
            palette=dict(_NORDIC_PALETTES[index_hint % len(_NORDIC_PALETTES)]),
            lyrics=[],
        )
    except Exception:
        return None


def restore_tracks_from_state(state: Dict[str, Any]) -> List[NordicTrack]:
    tracks: List[NordicTrack] = []

    saved_playlist = state.get("playlist")
    if saved_playlist and isinstance(saved_playlist, list):
        for idx, item in enumerate(saved_playlist):
            tid = str(item.get("track_id", ""))
            if tid in _LEGACY_BUILTIN_IDS or tid.startswith("netease_"):
                continue
            fp = item.get("file_path")
            if not fp:
                continue
            if not Path(fp).is_file():
                _UNAVAILABLE_ENTRIES[tid] = dict(item)
                continue
            saved_cov = item.get("cover_path")
            valid_cov = str(saved_cov) if (saved_cov and Path(str(saved_cov)).is_file()) else None

            tr = None if tid.startswith("cue-") else _try_fast_restore(item, idx, fp)
            if tr is None:
                tr = create_track_from_file(fp, index_hint=idx)
            if tid.startswith("cue-"):
                tr.track_id = tid
            _apply_saved_meta_if_valid(tr, item)
            if item.get("start_time"):
                tr.start_time = float(item["start_time"])
            if item.get("end_time"):
                tr.end_time = float(item["end_time"])
            if item.get("cue_path"):
                tr.cue_path = str(item["cue_path"])
            if item.get("cue_track_num"):
                tr.cue_track_num = int(item["cue_track_num"])
            if item.get("added_timestamp"):
                tr.added_timestamp = float(item["added_timestamp"])
            if item.get("duration") and float(item["duration"]) >= 5.0:
                tr.duration = float(item["duration"])
            elif tr.end_time > tr.start_time:
                tr.duration = round(tr.end_time - tr.start_time, 2)
            if valid_cov and not tr.cover_path:
                tr.cover_path = valid_cov
            tr.is_favorite = bool(item.get("is_favorite", False))
            tr.lyric_offset = float(item.get("lyric_offset", 0.0))
            if item.get("sample_rate") and tr.sample_rate == 44100:
                tr.sample_rate = int(item["sample_rate"])
            if item.get("bit_depth") and tr.bit_depth == 16:
                tr.bit_depth = int(item["bit_depth"])
            if item.get("channels") and tr.channels == 2:
                tr.channels = int(item["channels"])
            if item.get("bitrate_kbps") and not tr.bitrate_kbps:
                tr.bitrate_kbps = int(item["bitrate_kbps"])
            if item.get("audio_format") and not tr.audio_format:
                tr.audio_format = str(item["audio_format"])
            if item.get("replay_gain_db") is not None and tr.replay_gain_db is None:
                tr.replay_gain_db = float(item["replay_gain_db"])
            if item.get("replay_peak") is not None and tr.replay_peak is None:
                tr.replay_peak = float(item["replay_peak"])
            if isinstance(item.get("acoustic_dna"), dict):
                tr.acoustic_dna = dict(item["acoustic_dna"])
            saved_lyrics = _parse_saved_lyrics(item.get("lyrics", []))
            if saved_lyrics:
                tr.lyrics = saved_lyrics
            tracks.append(tr)
        return tracks

    for idx, item in enumerate(state.get("local_tracks") or []):
        tid = str(item.get("track_id", ""))
        if tid in _LEGACY_BUILTIN_IDS or tid.startswith("netease_"):
            continue
        fp = item.get("file_path")
        if not fp:
            continue
        if not Path(fp).is_file():
            _UNAVAILABLE_ENTRIES.setdefault(tid, dict(item))
            continue
        tr = create_track_from_file(fp, index_hint=len(tracks) + idx)
        _apply_saved_meta_if_valid(tr, item)
        if item.get("duration") and float(item["duration"]) >= 15.0:
            tr.duration = float(item["duration"])
        saved_cov = item.get("cover_path")
        if saved_cov and Path(str(saved_cov)).is_file() and not tr.cover_path:
            tr.cover_path = str(saved_cov)
        tr.is_favorite = bool(item.get("is_favorite", False))
        tr.lyric_offset = float(item.get("lyric_offset", 0.0))
        tracks.append(tr)

    return tracks
