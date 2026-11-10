"""本地敏感数据（如网易云登录 Cookie）的加密存储。

Windows 上使用 DPAPI（CryptProtectData）：密文只能由同一 Windows 用户在同一台机器上解开，
不需要额外依赖（直接用 ctypes）。其他平台没有等价的系统级方案，退化为 base64 编码并带 "plain:" 前缀，
只是为了统一数据格式，并不提供保密性。
"""
import base64
import ctypes
import json
import sys
from ctypes import wintypes
from typing import Any, Optional

_DPAPI_PREFIX = "dpapi:"
_PLAIN_PREFIX = "plain:"


class _DATA_BLOB(ctypes.Structure):
    _fields_ = [("cbData", wintypes.DWORD), ("pbData", ctypes.POINTER(ctypes.c_char))]


def _blob_from_bytes(data: bytes) -> "_DATA_BLOB":
    buf = ctypes.create_string_buffer(data, len(data))
    return _DATA_BLOB(len(data), ctypes.cast(buf, ctypes.POINTER(ctypes.c_char))), buf


def _dpapi(data: bytes, protect: bool) -> Optional[bytes]:
    crypt32 = ctypes.windll.crypt32
    kernel32 = ctypes.windll.kernel32
    blob_in, _keep = _blob_from_bytes(data)
    blob_out = _DATA_BLOB()
    fn = crypt32.CryptProtectData if protect else crypt32.CryptUnprotectData
    ok = fn(ctypes.byref(blob_in), None, None, None, None, 0, ctypes.byref(blob_out))
    if not ok:
        return None
    try:
        return ctypes.string_at(blob_out.pbData, blob_out.cbData)
    finally:
        kernel32.LocalFree(blob_out.pbData)


def encrypt_obj(obj: Any) -> str:
    """把可 JSON 序列化的对象加密成字符串，可直接写进 player_state.json"""
    raw = json.dumps(obj, ensure_ascii=False).encode("utf-8")
    if sys.platform == "win32":
        try:
            enc = _dpapi(raw, protect=True)
            if enc is not None:
                return _DPAPI_PREFIX + base64.b64encode(enc).decode("ascii")
        except Exception:
            pass
    return _PLAIN_PREFIX + base64.b64encode(raw).decode("ascii")


def decrypt_obj(token: Any) -> Optional[Any]:
    """解密 encrypt_obj 的结果；格式不对或无法解密（换了用户/机器）时返回 None"""
    if not isinstance(token, str):
        return None
    try:
        if token.startswith(_DPAPI_PREFIX):
            if sys.platform != "win32":
                return None
            raw = _dpapi(base64.b64decode(token[len(_DPAPI_PREFIX):]), protect=False)
        elif token.startswith(_PLAIN_PREFIX):
            raw = base64.b64decode(token[len(_PLAIN_PREFIX):])
        else:
            return None
        return json.loads(raw.decode("utf-8")) if raw is not None else None
    except Exception:
        return None
