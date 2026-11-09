"""
Apple Music Style GPU Fluid Mesh Gradient Shader Widget for Klang.
Implements a real-time domain-warped liquid multi-core shader on OpenGL (QOpenGLWidget).
Renders 60/120 FPS audio-reactive liquid color meshes with ~0% CPU usage.
"""

import math
import logging
from typing import Optional, Tuple

from PyQt6.QtCore import Qt, QTimer, QPointF
from PyQt6.QtGui import QColor
from PyQt6.QtOpenGL import (
    QOpenGLShader,
    QOpenGLShaderProgram,
    QOpenGLBuffer,
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
uniform vec3 u_color1;
uniform vec3 u_color2;
uniform vec3 u_color3;
uniform vec3 u_color4;

// 2D Simplex-style pseudo noise
vec2 hash(vec2 p) {
    p = vec2(dot(p, vec2(127.1, 311.7)), dot(p, vec2(269.5, 183.3)));
    return -1.0 + 2.0 * fract(sin(p) * 43758.5453123);
}

float noise(vec2 p) {
    vec2 i = floor(p);
    vec2 f = fract(p);
    vec2 u = f * f * (3.0 - 2.0 * f);
    return mix(mix(dot(hash(i + vec2(0.0, 0.0)), f - vec2(0.0, 0.0)),
                   dot(hash(i + vec2(1.0, 0.0)), f - vec2(1.0, 0.0)), u.x),
               mix(dot(hash(i + vec2(0.0, 1.0)), f - vec2(0.0, 1.0)),
                   dot(hash(i + vec2(1.0, 1.0)), f - vec2(1.0, 1.0)), u.x), u.y);
}

float fbm(vec2 p) {
    float f = 0.0;
    mat2 m = mat2(1.6, 1.2, -1.2, 1.6);
    f += 0.5000 * noise(p); p = m * p;
    f += 0.2500 * noise(p); p = m * p;
    f += 0.1250 * noise(p); p = m * p;
    return f;
}

void main() {
    vec2 uv = v_uv;
    float aspect = u_resolution.x / max(1.0, u_resolution.y);
    vec2 p = (uv - 0.5) * vec2(aspect, 1.0);

    float t = u_time * 0.16;
    float energy_boost = u_energy * 0.45;

    // Apple Music style Domain Warping: liquid coordinate deformation
    vec2 q = vec2(fbm(p + vec2(0.0, 0.0) + t * 0.18),
                  fbm(p + vec2(5.2, 1.3) + t * 0.22));

    vec2 r = vec2(fbm(p + 3.2 * q + vec2(1.7, 9.2) + t * 0.14 + energy_boost * 0.25),
                  fbm(p + 3.2 * q + vec2(8.3, 2.8) + t * 0.18));

    float f = fbm(p + 3.4 * r + energy_boost * 0.3);

    // Multi-core organic color blending
    vec3 col = mix(u_color1, u_color2, clamp(f * f * 3.4, 0.0, 1.0));
    col = mix(col, u_color3, clamp(length(q) * 1.25, 0.0, 1.0));
    col = mix(col, u_color4, clamp(length(r.x) * 1.35, 0.0, 1.0));

    // Audio-reactive dynamic luminance pulse on bass kick
    col += u_color1 * (u_energy * 0.24);

    // Smooth midnight vignette
    float vig = 1.0 - smoothstep(0.45, 1.45, length(p * 1.08));
    col *= (0.32 + 0.68 * vig);

    // Velvet micro-dither grain to eradicate 8-bit banding on dark gradients
    float grain = (fract(sin(dot(uv, vec2(12.9898, 78.233))) * 43758.5453) - 0.5) * 0.016;
    col += grain;

    fragColor = vec4(clamp(col, 0.0, 1.0), 1.0);
}
"""


class FluidMeshShaderWidget(QOpenGLWidget):
    """
    Hardware-accelerated fluid liquid mesh gradient widget.
    Subclasses QOpenGLWidget to execute GLSL fragment shader at 60/120 FPS with ~0% CPU.
    """

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

        # Current interpolated colors (RGB 0.0 ~ 1.0)
        self._c1_cur = [0.18, 0.22, 0.28]
        self._c2_cur = [0.10, 0.14, 0.18]
        self._c3_cur = [0.08, 0.12, 0.15]
        self._c4_cur = [0.05, 0.07, 0.10]

        # Target colors from cover palette
        self._c1_target = [0.18, 0.22, 0.28]
        self._c2_target = [0.10, 0.14, 0.18]
        self._c3_target = [0.08, 0.12, 0.15]
        self._c4_target = [0.05, 0.07, 0.10]

        # Uniform locations
        self._loc_res = -1
        self._loc_time = -1
        self._loc_energy = -1
        self._loc_c1 = -1
        self._loc_c2 = -1
        self._loc_c3 = -1
        self._loc_c4 = -1

        self._anim_timer = QTimer(self)
        self._anim_timer.setInterval(16)  # ~60 FPS
        self._anim_timer.timeout.connect(self._on_tick)
        self._initialized = False

    def start_animation(self):
        if not self._anim_timer.isActive():
            self._anim_timer.start()

    def stop_animation(self):
        if self._anim_timer.isActive():
            self._anim_timer.stop()

    def set_fps(self, fps: int):
        """Set refresh rate between 60 and 120 FPS."""
        interval = 7 if fps >= 100 else 16
        self._anim_timer.setInterval(interval)

    def set_energy(self, energy: float):
        """Set dynamic audio energy from acoustic DNA or spectrum (0.0 ~ 1.0)."""
        self._target_energy = max(0.0, min(1.0, float(energy)))

    def set_palette(self, col1: QColor, col2: QColor, col3: QColor, col4: QColor):
        """Set target 4-color palette extracted from album cover."""
        def to_rgb_vec(c: QColor):
            return [c.redF(), c.greenF(), c.blueF()]

        self._c1_target = to_rgb_vec(col1)
        self._c2_target = to_rgb_vec(col2)
        self._c3_target = to_rgb_vec(col3)
        self._c4_target = to_rgb_vec(col4)

    def _lerp_vec(self, v_cur, v_target, rate=0.08):
        return [
            cur + (target - cur) * rate
            for cur, target in zip(v_cur, v_target)
        ]

    def _on_tick(self):
        self._time += 0.016
        # Smooth energy decay
        self._energy += (self._target_energy - self._energy) * 0.22
        self._target_energy *= 0.94

        # Smooth palette transition (prevents abrupt color jumps on song switch)
        self._c1_cur = self._lerp_vec(self._c1_cur, self._c1_target)
        self._c2_cur = self._lerp_vec(self._c2_cur, self._c2_target)
        self._c3_cur = self._lerp_vec(self._c3_cur, self._c3_target)
        self._c4_cur = self._lerp_vec(self._c4_cur, self._c4_target)

        self.update()

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

            self._loc_res = self._program.uniformLocation("u_resolution")
            self._loc_time = self._program.uniformLocation("u_time")
            self._loc_energy = self._program.uniformLocation("u_energy")
            self._loc_c1 = self._program.uniformLocation("u_color1")
            self._loc_c2 = self._program.uniformLocation("u_color2")
            self._loc_c3 = self._program.uniformLocation("u_color3")
            self._loc_c4 = self._program.uniformLocation("u_color4")

            import numpy as np
            quad_vertices = np.array([
                -1.0, -1.0,
                 1.0, -1.0,
                -1.0,  1.0,
                 1.0,  1.0,
            ], dtype=np.float32)

            self._vao = QOpenGLVertexArrayObject(self)
            self._vao.create()
            self._vao.bind()

            self._vbo = QOpenGLBuffer(QOpenGLBuffer.Type.VertexBuffer)
            self._vbo.create()
            self._vbo.bind()
            self._vbo.allocate(quad_vertices.tobytes(), len(quad_vertices) * 4)

            self._program.enableAttributeArray(0)
            self._program.setAttributeBuffer(0, 0x1406, 0, 2, 0)  # GL_FLOAT = 0x1406

            self._vbo.release()
            self._vao.release()

            self._initialized = True
            logger.info("[FluidShader] GPU Fluid Mesh Shader successfully initialized.")
        except Exception as e:
            logger.warning(f"[FluidShader] initializeGL error: {e}")

    def paintGL(self):
        if not self._initialized or not self._program or not self._vao or not self._funcs:
            return

        try:
            self._program.bind()
            w = float(max(1, self.width()))
            h = float(max(1, self.height()))

            self._program.setUniformValue(self._loc_res, QPointF(w, h))
            self._program.setUniformValue(self._loc_time, float(self._time))
            self._program.setUniformValue(self._loc_energy, float(self._energy))

            self._program.setUniformValue(self._loc_c1, self._c1_cur[0], self._c1_cur[1], self._c1_cur[2])
            self._program.setUniformValue(self._loc_c2, self._c2_cur[0], self._c2_cur[1], self._c2_cur[2])
            self._program.setUniformValue(self._loc_c3, self._c3_cur[0], self._c3_cur[1], self._c3_cur[2])
            self._program.setUniformValue(self._loc_c4, self._c4_cur[0], self._c4_cur[1], self._c4_cur[2])

            self._vao.bind()
            GL_TRIANGLE_STRIP = 0x0005
            self._funcs.glDrawArrays(GL_TRIANGLE_STRIP, 0, 4)
            self._vao.release()
            self._program.release()
        except Exception as e:
            logger.debug(f"[FluidShader] paintGL error: {e}")

    def resizeGL(self, w: int, h: int):
        if self._funcs:
            self._funcs.glViewport(0, 0, w, h)
