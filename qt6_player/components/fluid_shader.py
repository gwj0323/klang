"""
沉浸模式 GPU 背景（Apple Music 做法）

背景不是凭空合成的色块，而是专辑封面本身：
- CPU 端把封面缩到 64x64 并做重度高斯模糊，作为纹理上传；
- 片元着色器把这张纹理放大成四层，各自以不同中心、尺度、速度缓慢旋转，再叠加轻微的流体扰动；
- 最后整体提饱和、压亮度，让白色歌词在任何封面上都清楚。
切歌时新旧两张封面纹理交叉淡入。没有封面时退回到调色盘渐变。
"""

import logging
from typing import Optional

import numpy as np
from PyQt6.QtCore import Qt, QTimer, QPointF
from PyQt6.QtGui import QColor, QImage, QPixmap
from PyQt6.QtOpenGL import (
    QOpenGLShader,
    QOpenGLShaderProgram,
    QOpenGLBuffer,
    QOpenGLTexture,
    QOpenGLVertexArrayObject,
    QOpenGLVersionFunctionsFactory,
    QOpenGLVersionProfile,
)
from PyQt6.QtOpenGLWidgets import QOpenGLWidget

logger = logging.getLogger(__name__)

VERTEX_SHADER_SRC = """#version 330 core
layout(location = 0) in vec2 a_pos;
out vec2 v_uv;

void main() {
    v_uv = a_pos * 0.5 + 0.5;
    gl_Position = vec4(a_pos, 0.0, 1.0);
}
"""

FRAGMENT_SHADER_SRC = """#version 330 core
in vec2 v_uv;
out vec4 fragColor;

uniform vec2 u_resolution;
uniform float u_time;
uniform float u_energy;

uniform sampler2D u_tex_prev;
uniform sampler2D u_tex_cur;
uniform float u_tex_mix;      // 0 = 旧封面, 1 = 新封面
uniform float u_has_cover;    // 0 = 无封面, 使用调色盘

uniform vec3 u_color_base;
uniform vec3 u_color1;
uniform vec3 u_color2;
uniform vec3 u_color3;
uniform vec3 u_color4;

vec2 hash(vec2 p) {
    p = vec2(dot(p, vec2(127.1, 311.7)), dot(p, vec2(269.5, 183.3)));
    return -1.0 + 2.0 * fract(sin(p) * 43758.5453123);
}

float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(dot(hash(i), f),
                   dot(hash(i + vec2(1.0, 0.0)), f - vec2(1.0, 0.0)), u.x),
               mix(dot(hash(i + vec2(0.0, 1.0)), f - vec2(0.0, 1.0)),
                   dot(hash(i + vec2(1.0, 1.0)), f - vec2(1.0, 1.0)), u.x), u.y);
}

mat2 rot(float a) {
    float c = cos(a), s = sin(a);
    return mat2(c, -s, s, c);
}

// 以 center 为中心、scale 为尺寸、angle 为角度贴一层封面
vec3 cover_layer(sampler2D tex, vec2 p, vec2 center, float scale, float angle) {
    vec2 q = rot(angle) * (p - center) / scale + 0.5;
    return texture(tex, q).rgb;
}

vec3 cover_stack(sampler2D tex, vec2 p, float t) {
    // 底层：整屏铺满，最慢
    vec3 col = cover_layer(tex, p, vec2(0.0), 2.4, t * 0.05);

    // 三个较小的层，各自绕着屏幕上不同位置缓慢公转、自转
    vec2 c2 = vec2(-0.45, 0.20) + 0.18 * vec2(sin(t * 0.21), cos(t * 0.17));
    vec2 c3 = vec2(0.55, -0.15) + 0.20 * vec2(cos(t * 0.19), sin(t * 0.23));
    vec2 c4 = vec2(0.10, 0.35) + 0.16 * vec2(sin(t * 0.15 + 1.3), cos(t * 0.27));

    vec3 l2 = cover_layer(tex, p, c2, 1.6, -t * 0.08 + 1.1);
    vec3 l3 = cover_layer(tex, p, c3, 1.4, t * 0.10 + 2.3);
    vec3 l4 = cover_layer(tex, p, c4, 1.1, -t * 0.12 + 4.0);

    // 圆形软遮罩，层与层之间没有任何硬边
    float m2 = 1.0 - smoothstep(0.15, 0.85, length(p - c2));
    float m3 = 1.0 - smoothstep(0.15, 0.80, length(p - c3));
    float m4 = 1.0 - smoothstep(0.10, 0.60, length(p - c4));

    col = mix(col, l2, m2 * 0.85);
    col = mix(col, l3, m3 * 0.80);
    col = mix(col, l4, m4 * 0.70);
    return col;
}

vec3 palette_field(vec2 p, float t) {
    vec2 c1 = vec2(-0.5, 0.2) + 0.2 * vec2(sin(t * 0.3), cos(t * 0.25));
    vec2 c2 = vec2(0.5, -0.2) + 0.2 * vec2(cos(t * 0.27), sin(t * 0.33));
    vec2 c3 = vec2(0.0, 0.4) + 0.2 * vec2(sin(t * 0.21), cos(t * 0.31));
    vec2 c4 = vec2(0.1, -0.4) + 0.2 * vec2(cos(t * 0.23), sin(t * 0.19));
    float w1 = exp(-2.2 * dot(p - c1, p - c1));
    float w2 = exp(-2.2 * dot(p - c2, p - c2));
    float w3 = exp(-2.6 * dot(p - c3, p - c3));
    float w4 = exp(-2.6 * dot(p - c4, p - c4));
    float s = w1 + w2 + w3 + w4 + 0.15;
    return (u_color1 * w1 + u_color2 * w2 + u_color3 * w3 + u_color4 * w4 + u_color_base * 0.15) / s;
}

void main() {
    vec2 uv = v_uv;
    float aspect = u_resolution.x / max(1.0, u_resolution.y);
    vec2 p = (uv - 0.5) * vec2(aspect, 1.0);

    float t = u_time;

    // 轻微的流体扰动，让纹理像液体一样缓慢涌动
    vec2 warp = vec2(noise(p * 1.3 + t * 0.06), noise(p * 1.3 + vec2(5.2, 1.3) - t * 0.05));
    p += warp * (0.10 + u_energy * 0.06);

    vec3 col;
    if (u_has_cover > 0.5) {
        vec3 a = cover_stack(u_tex_prev, p, t);
        vec3 b = cover_stack(u_tex_cur, p, t);
        col = mix(a, b, u_tex_mix);
    } else {
        col = palette_field(p, t);
    }

    // 提饱和：模糊会把颜色洗灰，这里补回来
    float luma = dot(col, vec3(0.299, 0.587, 0.114));
    col = mix(vec3(luma), col, 1.7);
    col = max(col, 0.0);

    // 压亮度：整体降到约六成，亮部再额外压一点，保证白字在浅色封面上也清楚
    col = col * 0.62;
    col = col / (1.0 + col * 0.45) * 1.15;

    // 随低频能量轻微提亮
    col *= 1.0 + u_energy * 0.08;

    // 很弱的边缘收暗
    float vig = smoothstep(1.35, 0.35, length((uv - 0.5) * vec2(1.0, 1.2)));
    col *= mix(0.78, 1.0, vig);

    // 抖动，避免暗部色带
    col += (fract(sin(dot(uv * u_resolution, vec2(12.9898, 78.233))) * 43758.5453) - 0.5) / 255.0;

    fragColor = vec4(clamp(col, 0.0, 1.0), 1.0);
}
"""


def _gaussian_kernel(sigma: float) -> np.ndarray:
    radius = max(1, int(sigma * 3.0))
    x = np.arange(-radius, radius + 1, dtype=np.float32)
    k = np.exp(-(x * x) / (2.0 * sigma * sigma))
    return k / k.sum()


def _blur_axis(arr: np.ndarray, kernel: np.ndarray, axis: int) -> np.ndarray:
    r = len(kernel) // 2
    pad = [(0, 0)] * arr.ndim
    pad[axis] = (r, r)
    padded = np.pad(arr, pad, mode="reflect")
    out = np.zeros_like(arr)
    n = arr.shape[axis]
    for i, w in enumerate(kernel):
        sl = [slice(None)] * arr.ndim
        sl[axis] = slice(i, i + n)
        out += padded[tuple(sl)] * w
    return out


def make_blurred_cover(pixmap: Optional[QPixmap], size: int = 64) -> Optional[QImage]:
    """把封面缩小并做重度高斯模糊，返回可直接上传为纹理的 RGBA8888 QImage。"""
    if pixmap is None or pixmap.isNull():
        return None
    try:
        img = pixmap.toImage()
        # 分两步缩小，避免大图直接缩到 64px 时产生摩尔纹
        if img.width() > size * 4:
            img = img.scaled(size * 4, size * 4, Qt.AspectRatioMode.IgnoreAspectRatio,
                             Qt.TransformationMode.SmoothTransformation)
        img = img.scaled(size, size, Qt.AspectRatioMode.IgnoreAspectRatio,
                         Qt.TransformationMode.SmoothTransformation)
        img = img.convertToFormat(QImage.Format.Format_RGBA8888)

        ptr = img.constBits()
        ptr.setsize(img.sizeInBytes())
        stride = img.bytesPerLine() // 4
        arr = np.frombuffer(ptr, dtype=np.uint8).reshape(size, stride, 4)[:, :size, :3].astype(np.float32)

        kernel = _gaussian_kernel(size * 0.06)
        for _ in range(2):
            arr = _blur_axis(arr, kernel, 0)
            arr = _blur_axis(arr, kernel, 1)

        out = np.empty((size, size, 4), dtype=np.uint8)
        out[:, :, :3] = np.clip(arr, 0, 255).astype(np.uint8)
        out[:, :, 3] = 255
        out = np.ascontiguousarray(out)
        return QImage(out.data, size, size, size * 4, QImage.Format.Format_RGBA8888).copy()
    except Exception as e:
        logger.debug(f"[FluidShader] cover blur failed: {e}")
        return None


class FluidMeshShaderWidget(QOpenGLWidget):
    """沉浸模式全屏背景：模糊封面多层旋转（GPU）。"""

    def __init__(self, parent=None):
        super().__init__(parent)
        self.setAttribute(Qt.WidgetAttribute.WA_TransparentForMouseEvents, True)

        self._program: Optional[QOpenGLShaderProgram] = None
        self._vao: Optional[QOpenGLVertexArrayObject] = None
        self._vbo: Optional[QOpenGLBuffer] = None
        self._funcs = None

        self._time: float = 0.0
        self._energy: float = 0.0
        self._target_energy: float = 0.0

        # 封面纹理（双缓冲交叉淡入）
        self._tex_prev: Optional[QOpenGLTexture] = None
        self._tex_cur: Optional[QOpenGLTexture] = None
        self._pending_cover: Optional[QImage] = None
        self._pending_clear: bool = False
        self._tex_mix: float = 1.0

        # 调色盘（无封面时使用）
        self._c_base_cur = [0.03, 0.04, 0.06]
        self._c1_cur = [0.12, 0.16, 0.20]
        self._c2_cur = [0.08, 0.11, 0.14]
        self._c3_cur = [0.06, 0.09, 0.12]
        self._c4_cur = [0.04, 0.06, 0.08]
        self._c_base_target = list(self._c_base_cur)
        self._c1_target = list(self._c1_cur)
        self._c2_target = list(self._c2_cur)
        self._c3_target = list(self._c3_cur)
        self._c4_target = list(self._c4_cur)

        self._loc = {}

        self._anim_timer = QTimer(self)
        self._anim_timer.setInterval(16)
        self._anim_timer.timeout.connect(self._on_tick)
        self._initialized = False
        self._first_palette = True

    # ---------- 外部接口 ----------

    def start_animation(self):
        if not self._anim_timer.isActive():
            self._anim_timer.start()

    def stop_animation(self):
        if self._anim_timer.isActive():
            self._anim_timer.stop()

    def set_fps(self, fps: int):
        self._anim_timer.setInterval(7 if fps >= 100 else 16)

    def set_energy(self, energy: float):
        self._target_energy = max(0.0, min(1.0, float(energy)))

    def set_cover(self, pixmap: Optional[QPixmap]):
        """设置当前封面。纹理在下一帧 paintGL 中上传（需要 GL 上下文）。"""
        img = make_blurred_cover(pixmap)
        if img is None:
            self._pending_cover = None
            self._pending_clear = True
        else:
            self._pending_cover = img
            self._pending_clear = False
        self.update()

    def set_palette(self, col1: QColor, col2: QColor, col3: QColor, col4: QColor, col_base: Optional[QColor] = None):
        def v(c: QColor):
            return [c.redF(), c.greenF(), c.blueF()]

        self._c1_target, self._c2_target = v(col1), v(col2)
        self._c3_target, self._c4_target = v(col3), v(col4)
        if col_base is not None:
            self._c_base_target = v(col_base)
        if self._first_palette:
            self._first_palette = False
            self._c_base_cur = list(self._c_base_target)
            self._c1_cur, self._c2_cur = list(self._c1_target), list(self._c2_target)
            self._c3_cur, self._c4_cur = list(self._c3_target), list(self._c4_target)

    # ---------- 动画 ----------

    @staticmethod
    def _lerp_vec(cur, target, rate=0.08):
        return [c + (t - c) * rate for c, t in zip(cur, target)]

    def _on_tick(self):
        dt = self._anim_timer.interval() / 1000.0
        self._time += dt
        self._energy += (self._target_energy - self._energy) * 0.18
        self._target_energy *= 0.94

        if self._tex_mix < 1.0:
            self._tex_mix = min(1.0, self._tex_mix + dt / 1.2)  # 1.2 秒交叉淡入

        self._c_base_cur = self._lerp_vec(self._c_base_cur, self._c_base_target)
        self._c1_cur = self._lerp_vec(self._c1_cur, self._c1_target)
        self._c2_cur = self._lerp_vec(self._c2_cur, self._c2_target)
        self._c3_cur = self._lerp_vec(self._c3_cur, self._c3_target)
        self._c4_cur = self._lerp_vec(self._c4_cur, self._c4_target)
        self.update()

    # ---------- GL ----------

    def _make_texture(self, img: QImage) -> QOpenGLTexture:
        tex = QOpenGLTexture(img, QOpenGLTexture.MipMapGeneration.DontGenerateMipMaps)
        tex.setMinificationFilter(QOpenGLTexture.Filter.Linear)
        tex.setMagnificationFilter(QOpenGLTexture.Filter.Linear)
        tex.setWrapMode(QOpenGLTexture.WrapMode.MirroredRepeat)
        return tex

    def _apply_pending_cover(self):
        if self._pending_cover is not None:
            new_tex = self._make_texture(self._pending_cover)
            self._pending_cover = None
            if self._tex_prev is not None:
                self._tex_prev.destroy()
            if self._tex_cur is None:
                # 第一张封面直接显示，不做淡入
                self._tex_prev = None
                self._tex_cur = new_tex
                self._tex_mix = 1.0
            else:
                self._tex_prev = self._tex_cur
                self._tex_cur = new_tex
                self._tex_mix = 0.0
        elif self._pending_clear:
            self._pending_clear = False
            for tex in (self._tex_prev, self._tex_cur):
                if tex is not None:
                    tex.destroy()
            self._tex_prev = None
            self._tex_cur = None

    def initializeGL(self):
        try:
            vp = QOpenGLVersionProfile()
            vp.setVersion(2, 0)
            self._funcs = QOpenGLVersionFunctionsFactory.get(vp, self.context())
            if self._funcs:
                self._funcs.initializeOpenGLFunctions()

            self._program = QOpenGLShaderProgram(self)
            self._program.addShaderFromSourceCode(QOpenGLShader.ShaderTypeBit.Vertex, VERTEX_SHADER_SRC)
            self._program.addShaderFromSourceCode(QOpenGLShader.ShaderTypeBit.Fragment, FRAGMENT_SHADER_SRC)
            if not self._program.link():
                logger.warning(f"[FluidShader] Shader link failed: {self._program.log()}")
                return

            for name in ("u_resolution", "u_time", "u_energy", "u_tex_prev", "u_tex_cur", "u_tex_mix",
                         "u_has_cover", "u_color_base", "u_color1", "u_color2", "u_color3", "u_color4"):
                self._loc[name] = self._program.uniformLocation(name)

            quad = np.array([-1.0, -1.0, 1.0, -1.0, -1.0, 1.0, 1.0, 1.0], dtype=np.float32)
            self._vao = QOpenGLVertexArrayObject(self)
            self._vao.create()
            self._vao.bind()
            self._vbo = QOpenGLBuffer(QOpenGLBuffer.Type.VertexBuffer)
            self._vbo.create()
            self._vbo.bind()
            self._vbo.allocate(quad.tobytes(), quad.nbytes)
            self._program.enableAttributeArray(0)
            self._program.setAttributeBuffer(0, 0x1406, 0, 2, 0)  # GL_FLOAT
            self._vbo.release()
            self._vao.release()

            self._initialized = True
            logger.info("[FluidShader] GPU cover background initialized.")
        except Exception as e:
            logger.warning(f"[FluidShader] initializeGL error: {e}")

    def paintGL(self):
        if not self._initialized or not self._program or not self._vao or not self._funcs:
            return
        try:
            self._apply_pending_cover()

            prog = self._program
            loc = self._loc
            prog.bind()
            w = float(max(1, self.width()))
            h = float(max(1, self.height()))
            prog.setUniformValue(loc["u_resolution"], QPointF(w, h))
            prog.setUniformValue(loc["u_time"], float(self._time))
            prog.setUniformValue(loc["u_energy"], float(self._energy))

            has_cover = self._tex_cur is not None
            prog.setUniformValue(loc["u_has_cover"], 1.0 if has_cover else 0.0)
            if has_cover:
                prev = self._tex_prev if self._tex_prev is not None else self._tex_cur
                prev.bind(0)
                self._tex_cur.bind(1)
                self._funcs.glUniform1i(loc["u_tex_prev"], 0)
                self._funcs.glUniform1i(loc["u_tex_cur"], 1)
                prog.setUniformValue(loc["u_tex_mix"], float(self._tex_mix if self._tex_prev is not None else 1.0))

            for key, vec in (("u_color_base", self._c_base_cur), ("u_color1", self._c1_cur),
                             ("u_color2", self._c2_cur), ("u_color3", self._c3_cur), ("u_color4", self._c4_cur)):
                prog.setUniformValue(loc[key], vec[0], vec[1], vec[2])

            self._vao.bind()
            self._funcs.glDrawArrays(0x0005, 0, 4)  # GL_TRIANGLE_STRIP
            self._vao.release()
            prog.release()
        except Exception as e:
            logger.debug(f"[FluidShader] paintGL error: {e}")

    def resizeGL(self, w: int, h: int):
        if self._funcs:
            self._funcs.glViewport(0, 0, w, h)
