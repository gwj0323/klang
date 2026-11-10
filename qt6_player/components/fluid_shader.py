"""
沉浸模式 GPU 背景（Apple Music 新版做法：封面取色 + 流动网格渐变）

- CPU 端用 k-means 从封面提取至多 6 种区分明显的主色，按占比分配到若干种子点，
  做 Voronoi 分区后模糊，生成一张 64x64 的色块图作为纹理上传；
- 片元着色器把色块图放大成五层，各自以不同中心、尺度、速度旋转，再叠加两级流体扰动；
- 最后整体提饱和、压亮度，让白色歌词在任何封面上都清楚。
切歌时新旧色块图交叉淡入。拿不到封面时退回到调色盘渐变。
(make_blurred_cover 保留，供需要旧版"模糊封面"效果时使用。)
"""

import logging
from typing import Optional

import numpy as np
from PyQt6.QtCore import Qt, QTimer, QPointF
from PyQt6.QtGui import QColor, QImage, QPixmap, QSurfaceFormat
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

vec3 cover_stack(sampler2D tex, vec2 p, float t, float e) {
    // 底层：铺满全屏，自转并缓慢呼吸缩放
    float base_scale = 2.0 + 0.25 * sin(t * 0.11);
    vec3 col = cover_layer(tex, p, vec2(0.0), base_scale, t * 0.09);

    // 四个较小的层：每层只露出封面的一小块，所以颜色彼此不同；
    // 各自沿不同轨道公转、以不同速度自转，随低频能量轻微放大
    vec2 c2 = vec2(-0.55, 0.15) + vec2(0.40 * sin(t * 0.23), 0.28 * cos(t * 0.19));
    vec2 c3 = vec2(0.60, -0.10) + vec2(0.38 * cos(t * 0.21), 0.30 * sin(t * 0.27));
    vec2 c4 = vec2(0.05, 0.30) + vec2(0.45 * sin(t * 0.17 + 1.3), 0.22 * cos(t * 0.31));
    vec2 c5 = vec2(-0.10, -0.35) + vec2(0.50 * cos(t * 0.15 + 2.1), 0.20 * sin(t * 0.25));

    float pulse = 1.0 + e * 0.12;
    vec3 l2 = cover_layer(tex, p, c2, 1.25 * pulse, -t * 0.22 + 1.1);
    vec3 l3 = cover_layer(tex, p, c3, 1.10 * pulse, t * 0.26 + 2.3);
    vec3 l4 = cover_layer(tex, p, c4, 0.95 * pulse, -t * 0.30 + 4.0);
    vec3 l5 = cover_layer(tex, p, c5, 1.05 * pulse, t * 0.19 + 5.2);

    // 圆形软遮罩，层与层之间没有硬边
    float m2 = 1.0 - smoothstep(0.10, 0.75 * pulse, length(p - c2));
    float m3 = 1.0 - smoothstep(0.10, 0.70 * pulse, length(p - c3));
    float m4 = 1.0 - smoothstep(0.05, 0.60 * pulse, length(p - c4));
    float m5 = 1.0 - smoothstep(0.05, 0.65 * pulse, length(p - c5));

    col = mix(col, l2, m2 * 0.90);
    col = mix(col, l3, m3 * 0.90);
    col = mix(col, l4, m4 * 0.85);
    col = mix(col, l5, m5 * 0.80);
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

    // 两级流体扰动：大尺度慢涌 + 小尺度快涌，让颜色边界像液体一样翻卷
    vec2 warp = vec2(noise(p * 1.2 + t * 0.12), noise(p * 1.2 + vec2(5.2, 1.3) - t * 0.10));
    warp += 0.5 * vec2(noise(p * 2.6 - t * 0.20 + 3.7), noise(p * 2.6 + t * 0.18 + 8.1));
    p += warp * (0.16 + u_energy * 0.10);

    vec3 col;
    if (u_has_cover > 0.5) {
        vec3 a = cover_stack(u_tex_prev, p, t, u_energy);
        vec3 b = cover_stack(u_tex_cur, p, t, u_energy);
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

        kernel = _gaussian_kernel(size * 0.035)
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


def extract_cover_colors(pixmap: Optional[QPixmap], k: int = 6) -> Optional[np.ndarray]:
    """用 k-means 从封面提取主色，返回 (n, 4) 数组：RGB(0~255) + 占比，按占比降序。"""
    if pixmap is None or pixmap.isNull():
        return None
    try:
        img = pixmap.toImage().scaled(48, 48, Qt.AspectRatioMode.IgnoreAspectRatio,
                                      Qt.TransformationMode.SmoothTransformation)
        img = img.convertToFormat(QImage.Format.Format_RGBA8888)
        ptr = img.constBits()
        ptr.setsize(img.sizeInBytes())
        stride = img.bytesPerLine() // 4
        px = np.frombuffer(ptr, dtype=np.uint8).reshape(48, stride, 4)[:, :48, :3]
        px = px.reshape(-1, 3).astype(np.float32)

        # k-means++ 初始化（固定随机种子，同一张封面每次结果一致）
        rng = np.random.default_rng(int(px.sum()) % (2 ** 32))
        centers = [px[rng.integers(len(px))]]
        for _ in range(1, k):
            d = np.min(((px[:, None, :] - np.array(centers)[None]) ** 2).sum(-1), axis=1)
            total = float(d.sum())
            # 纯色封面时所有点到中心的距离都是 0，不能作为概率分布
            centers.append(px[rng.choice(len(px), p=(d / total) if total > 1e-6 else None)])
        centers = np.array(centers)

        for _ in range(12):
            labels = np.argmin(((px[:, None, :] - centers[None]) ** 2).sum(-1), axis=1)
            for i in range(k):
                sel = px[labels == i]
                if len(sel):
                    centers[i] = sel.mean(0)

        counts = np.bincount(labels, minlength=k).astype(np.float32)
        order = np.argsort(-counts)
        result = []
        for i in order:
            if counts[i] < len(px) * 0.02:
                continue
            c = centers[i]
            # 和已选颜色太接近的合并掉，保证颜色之间有区分度
            if any(np.linalg.norm(c - r[:3]) < 38.0 for r in result):
                continue
            result.append(np.array([c[0], c[1], c[2], counts[i] / len(px)], dtype=np.float32))
        return np.array(result) if result else None
    except Exception as e:
        logger.debug(f"[FluidShader] color extraction failed: {e}")
        return None


def make_color_field(pixmap: Optional[QPixmap], size: int = 64) -> Optional[QImage]:
    """用封面主色生成一张色块图：主色按占比分配种子点，做 Voronoi 分区后重度模糊。
    着色器再把它多层旋转、扰动，得到流动的网格渐变。"""
    colors = extract_cover_colors(pixmap)
    if colors is None:
        return None
    try:
        rng = np.random.default_rng(int(colors[:, :3].sum() * 1000) % (2 ** 32))
        n_seeds = 9
        weights = colors[:, 3] / colors[:, 3].sum()
        # 每种颜色至少一个种子点，其余按占比分
        seed_colors = list(range(len(colors)))
        seed_colors += list(rng.choice(len(colors), size=max(0, n_seeds - len(colors)), p=weights))
        seeds = rng.random((len(seed_colors), 2)) * size

        yy, xx = np.mgrid[0:size, 0:size].astype(np.float32)
        # 纹理是镜像平铺的，种子也按镜像考虑，避免边缘出现接缝
        best = np.full((size, size), np.inf, dtype=np.float32)
        owner = np.zeros((size, size), dtype=np.int32)
        for i, (sx, sy) in enumerate(seeds):
            for mx in (sx, -sx, 2 * size - sx):
                for my in (sy, -sy, 2 * size - sy):
                    d = (xx - mx) ** 2 + (yy - my) ** 2
                    m = d < best
                    best[m] = d[m]
                    owner[m] = i
        rgb = colors[np.array(seed_colors)][:, :3][owner]

        kernel = _gaussian_kernel(size * 0.045)
        for _ in range(2):
            rgb = _blur_axis(rgb, kernel, 0)
            rgb = _blur_axis(rgb, kernel, 1)

        out = np.empty((size, size, 4), dtype=np.uint8)
        out[:, :, :3] = np.clip(rgb, 0, 255).astype(np.uint8)
        out[:, :, 3] = 255
        out = np.ascontiguousarray(out)
        return QImage(out.data, size, size, size * 4, QImage.Format.Format_RGBA8888).copy()
    except Exception as e:
        logger.debug(f"[FluidShader] color field failed: {e}")
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

        # 垂直同步：交换间隔 1，让 frameSwapped 以显示器刷新率节拍触发
        fmt = QSurfaceFormat(self.format())
        fmt.setSwapInterval(1)
        self.setFormat(fmt)

        # 不再自带定时器：由沉浸窗口的统一帧循环调用 advance(dt) 驱动，避免与主窗口定时器相互错位
        self._animating = False
        self._initialized = False
        self._init_failed = False
        self._first_palette = True

    # ---------- 外部接口 ----------

    @property
    def gpu_active(self) -> bool:
        return self.isVisible() and self._initialized and not self._init_failed

    def start_animation(self):
        self._animating = True

    def stop_animation(self):
        self._animating = False

    def set_fps(self, fps: int):
        # 帧节拍由沉浸窗口统一驱动，此处保留接口兼容
        pass

    def set_energy(self, energy: float):
        self._target_energy = max(0.0, min(1.0, float(energy)))

    def set_cover(self, pixmap: Optional[QPixmap]):
        """设置当前封面：提取主色生成色块图，纹理在下一帧 paintGL 中上传（需要 GL 上下文）。"""
        img = make_color_field(pixmap)
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

    def advance(self, dt: float):
        """按真实帧间隔推进一帧并请求重绘（由沉浸窗口的统一帧循环调用）。"""
        if not self._animating:
            return
        dt = max(0.001, min(0.05, float(dt)))
        self._time += dt
        # 能量平滑原按 ~60Hz 固定系数设计，换算为与帧率无关的指数衰减
        k = dt * 60.0
        self._energy += (self._target_energy - self._energy) * (1.0 - (1.0 - 0.18) ** k)
        self._target_energy *= 0.94 ** k

        if self._tex_mix < 1.0:
            self._tex_mix = min(1.0, self._tex_mix + dt / 1.2)  # 1.2 秒交叉淡入

        # 色彩渐变保持与此前 ~120Hz 下每帧 0.08 相当的速度
        rate = 1.0 - (1.0 - 0.08) ** (dt * 120.0)
        self._c_base_cur = self._lerp_vec(self._c_base_cur, self._c_base_target, rate)
        self._c1_cur = self._lerp_vec(self._c1_cur, self._c1_target, rate)
        self._c2_cur = self._lerp_vec(self._c2_cur, self._c2_target, rate)
        self._c3_cur = self._lerp_vec(self._c3_cur, self._c3_target, rate)
        self._c4_cur = self._lerp_vec(self._c4_cur, self._c4_target, rate)
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
            # GL 初始化失败：隐藏控件，让沉浸窗口回退到 CPU 极光背景
            self._init_failed = True
            QTimer.singleShot(0, self.hide)

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
