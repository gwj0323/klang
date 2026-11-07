"""
AutoEq 知名耳机声学硬件校准档案库 (Harman Target Acoustic Calibration)
基于国际公认开源声学测量数据 (Oratory1990 / Crinacle coupler measurements)
为各类主流耳机精准匹配参量均衡滤镜 (Parametric EQ Biquad Filter Bank)，
消除单元频响毛刺与频段缺陷，呈现发烧监听级纯净平衡声底。
"""

import math
from dataclasses import dataclass
from typing import Dict, List, Optional, Tuple
import numpy as np


@dataclass
class EqFilter:
    ftype: str  # "PK" (Peaking/Bell), "LSQ" (Low Shelf), "HSQ" (High Shelf)
    freq: float  # Hz
    gain_db: float  # dB (+/-)
    q: float  # Q factor


@dataclass
class HeadphoneProfile:
    id: str
    name: str
    brand: str
    category: str  # "头戴式" | "入耳式" | "开放式" | "真无线"
    target_curve: str  # "Harman Over-Ear" | "Harman In-Ear"
    preamp_db: float
    description: str
    filters: List[EqFilter]


HEADPHONE_PROFILES: Dict[str, HeadphoneProfile] = {
    "bypass": HeadphoneProfile(
        id="bypass",
        name="关闭 / 原声监听 (Bypass)",
        brand="通用",
        category="直通",
        target_curve="Native",
        preamp_db=0.0,
        description="未开启硬件校准，保持音频原始声底直通输出。",
        filters=[],
    ),
    "sony_wh1000xm5": HeadphoneProfile(
        id="sony_wh1000xm5",
        name="Sony WH-1000XM5",
        brand="Sony",
        category="头戴式",
        target_curve="Harman Over-Ear",
        preamp_db=-5.5,
        description="收敛 150~250Hz 过肥中低频，提亮中高频人声细节与泛音空气感。",
        filters=[
            EqFilter("PK", 75.0, -4.2, 0.70),
            EqFilter("PK", 210.0, -5.8, 1.10),
            EqFilter("PK", 1100.0, 2.8, 1.40),
            EqFilter("PK", 3200.0, 4.5, 1.60),
            EqFilter("PK", 6800.0, -3.2, 2.50),
            EqFilter("HSQ", 10000.0, 3.0, 0.70),
        ],
    ),
    "sony_wh1000xm4": HeadphoneProfile(
        id="sony_wh1000xm4",
        name="Sony WH-1000XM4",
        brand="Sony",
        category="头戴式",
        target_curve="Harman Over-Ear",
        preamp_db=-5.9,
        description="压制偏厚重的中低频混浊感，弥补 3kHz 人声区，通透度显著提升。",
        filters=[
            EqFilter("PK", 200.0, -6.2, 0.85),
            EqFilter("PK", 1200.0, 2.5, 1.50),
            EqFilter("PK", 3000.0, 4.8, 1.80),
            EqFilter("PK", 6500.0, -3.5, 3.00),
            EqFilter("HSQ", 10000.0, 2.5, 0.70),
        ],
    ),
    "sony_wf1000xm5": HeadphoneProfile(
        id="sony_wf1000xm5",
        name="Sony WF-1000XM5",
        brand="Sony",
        category="真无线",
        target_curve="Harman In-Ear",
        preamp_db=-4.0,
        description="优化入耳共鸣箱体低频，提亮 2.8kHz 人声清晰度与吉他弦音。",
        filters=[
            EqFilter("LSQ", 105.0, -2.5, 0.70),
            EqFilter("PK", 650.0, -1.8, 1.20),
            EqFilter("PK", 2800.0, 3.5, 1.50),
            EqFilter("PK", 5600.0, -4.0, 2.80),
            EqFilter("HSQ", 9000.0, 2.0, 0.70),
        ],
    ),
    "airpods_pro_2": HeadphoneProfile(
        id="airpods_pro_2",
        name="Apple AirPods Pro 2",
        brand="Apple",
        category="真无线",
        target_curve="Harman In-Ear",
        preamp_db=-3.5,
        description="平滑中高频衔接，改善 6kHz 齿音毛刺，增强声音纯净度与耐听感。",
        filters=[
            EqFilter("PK", 240.0, -1.8, 1.20),
            EqFilter("PK", 1300.0, 1.5, 1.80),
            EqFilter("PK", 3100.0, 2.8, 1.60),
            EqFilter("PK", 6000.0, -3.2, 2.50),
            EqFilter("HSQ", 10000.0, 2.5, 0.70),
        ],
    ),
    "airpods_max": HeadphoneProfile(
        id="airpods_max",
        name="Apple AirPods Max",
        brand="Apple",
        category="头戴式",
        target_curve="Harman Over-Ear",
        preamp_db=-4.2,
        description="填补 3~4kHz 中频微陷，收束略微松散的极低频，声场层次更立体。",
        filters=[
            EqFilter("PK", 180.0, -2.2, 1.10),
            EqFilter("PK", 1400.0, 2.0, 1.40),
            EqFilter("PK", 3600.0, 3.8, 1.80),
            EqFilter("PK", 5800.0, -2.8, 2.80),
            EqFilter("HSQ", 10000.0, 1.5, 0.70),
        ],
    ),
    "sennheiser_hd600": HeadphoneProfile(
        id="sennheiser_hd600",
        name="Sennheiser HD 600",
        brand="Sennheiser",
        category="开放式",
        target_curve="Harman Over-Ear",
        preamp_db=-5.0,
        description="弥补开放式动圈 60Hz 以下极低频滚降，平顺高频，带来丰满大动态下潜。",
        filters=[
            EqFilter("LSQ", 60.0, 5.5, 0.70),
            EqFilter("PK", 180.0, -1.5, 1.00),
            EqFilter("PK", 3200.0, -1.8, 2.20),
            EqFilter("PK", 5600.0, 1.5, 2.00),
            EqFilter("HSQ", 11000.0, 2.0, 0.70),
        ],
    ),
    "sennheiser_hd650": HeadphoneProfile(
        id="sennheiser_hd650",
        name="Sennheiser HD 650",
        brand="Sennheiser",
        category="开放式",
        target_curve="Harman Over-Ear",
        preamp_db=-5.2,
        description="补充次低频弹性与能量，微调中高频泛音，让人声更加温润通透。",
        filters=[
            EqFilter("LSQ", 60.0, 5.8, 0.70),
            EqFilter("PK", 220.0, -1.8, 1.10),
            EqFilter("PK", 3400.0, -1.5, 2.00),
            EqFilter("PK", 6000.0, 2.2, 1.80),
            EqFilter("HSQ", 10000.0, 2.5, 0.70),
        ],
    ),
    "sennheiser_hd560s": HeadphoneProfile(
        id="sennheiser_hd560s",
        name="Sennheiser HD 560S",
        brand="Sennheiser",
        category="开放式",
        target_curve="Harman Over-Ear",
        preamp_db=-3.8,
        description="抑制 4.5kHz 附近少许刺耳峰值，平衡极低频动态，达到专业监听级平直。",
        filters=[
            EqFilter("LSQ", 50.0, 3.5, 0.70),
            EqFilter("PK", 1300.0, -1.5, 2.00),
            EqFilter("PK", 4500.0, -3.0, 3.00),
            EqFilter("HSQ", 10000.0, 1.5, 0.70),
        ],
    ),
    "sennheiser_ie200": HeadphoneProfile(
        id="sennheiser_ie200",
        name="Sennheiser IE 200",
        brand="Sennheiser",
        category="入耳式",
        target_curve="Harman In-Ear",
        preamp_db=-4.0,
        description="充实低频击打力度，平抑 6kHz 尖锐感，还原纯正德系纯净音色。",
        filters=[
            EqFilter("LSQ", 85.0, 3.0, 0.70),
            EqFilter("PK", 260.0, -1.8, 1.20),
            EqFilter("PK", 2800.0, 2.5, 1.50),
            EqFilter("PK", 6200.0, -4.5, 3.00),
            EqFilter("HSQ", 10000.0, 2.0, 0.70),
        ],
    ),
    "beyer_dt990": HeadphoneProfile(
        id="beyer_dt990",
        name="Beyerdynamic DT 990 Pro (250Ω)",
        brand="Beyerdynamic",
        category="开放式",
        target_curve="Harman Over-Ear",
        preamp_db=-4.8,
        description="精准消除拜雅 8.5kHz 齿音尖峰，消除听觉疲劳，声场开阔舒适。",
        filters=[
            EqFilter("LSQ", 60.0, 3.0, 0.70),
            EqFilter("PK", 200.0, -2.0, 1.00),
            EqFilter("PK", 4200.0, 2.5, 1.50),
            EqFilter("PK", 8500.0, -6.8, 3.20),
            EqFilter("HSQ", 12000.0, -2.0, 0.70),
        ],
    ),
    "beyer_dt770": HeadphoneProfile(
        id="beyer_dt770",
        name="Beyerdynamic DT 770 Pro (80Ω)",
        brand="Beyerdynamic",
        category="头戴式",
        target_curve="Harman Over-Ear",
        preamp_db=-4.8,
        description="收束略肥的箱体低频，柔化 8.6kHz 刺耳高频，让中频人声更加饱满结实。",
        filters=[
            EqFilter("PK", 120.0, -3.5, 1.20),
            EqFilter("PK", 250.0, -2.8, 1.40),
            EqFilter("PK", 3800.0, 3.2, 1.60),
            EqFilter("PK", 8600.0, -5.5, 3.00),
            EqFilter("HSQ", 11000.0, -1.8, 0.70),
        ],
    ),
    "ath_m50x": HeadphoneProfile(
        id="ath_m50x",
        name="Audio-Technica ATH-M50x",
        brand="Audio-Technica",
        category="头戴式",
        target_curve="Harman Over-Ear",
        preamp_db=-4.5,
        description="减轻 150Hz 膨胀混浊感，平顺 9.5kHz 金属毛刺，还原母带监听真实本色。",
        filters=[
            EqFilter("PK", 150.0, -3.8, 1.20),
            EqFilter("PK", 400.0, 1.5, 1.80),
            EqFilter("PK", 3200.0, 2.8, 1.50),
            EqFilter("PK", 9500.0, -4.5, 2.50),
        ],
    ),
    "hifiman_sundara": HeadphoneProfile(
        id="hifiman_sundara",
        name="Hifiman Sundara",
        brand="Hifiman",
        category="开放式",
        target_curve="Harman Over-Ear",
        preamp_db=-4.2,
        description="为平板振膜补充 65Hz 以下深沉极低频，微调中频，展现晶莹剔透的高解析层次。",
        filters=[
            EqFilter("LSQ", 65.0, 4.2, 0.70),
            EqFilter("PK", 1500.0, -2.0, 1.80),
            EqFilter("PK", 6200.0, -3.0, 2.50),
            EqFilter("HSQ", 10000.0, 1.5, 0.70),
        ],
    ),
    "moondrop_aria": HeadphoneProfile(
        id="moondrop_aria",
        name="水月雨 Moondrop Aria",
        brand="水月雨",
        category="入耳式",
        target_curve="Harman In-Ear",
        preamp_db=-3.5,
        description="优化 VDSF 目标频响衔接，改善 6.5kHz 极高频毛糙，人声更加甜美纯澈。",
        filters=[
            EqFilter("LSQ", 80.0, -1.5, 0.70),
            EqFilter("PK", 1200.0, 1.8, 1.50),
            EqFilter("PK", 6500.0, -3.5, 3.00),
            EqFilter("HSQ", 11000.0, 2.0, 0.70),
        ],
    ),
    "shure_se215": HeadphoneProfile(
        id="shure_se215",
        name="Shure SE215",
        brand="Shure",
        category="入耳式",
        target_curve="Harman In-Ear",
        preamp_db=-5.0,
        description="大幅削减过厚糊闷的中低频，弥补缺失的高频延伸，瞬间扫清闷罐感。",
        filters=[
            EqFilter("PK", 200.0, -5.5, 0.90),
            EqFilter("PK", 2200.0, 4.0, 1.40),
            EqFilter("PK", 4800.0, 3.5, 2.00),
            EqFilter("HSQ", 9000.0, 4.5, 0.70),
        ],
    ),
}


def build_headphone_sos(profile: HeadphoneProfile, sample_rate: int) -> Tuple[Optional[np.ndarray], float]:
    """
    根据耳机声学校准档案构建 IIR 双二阶滤波级联 (Second-Order Sections, SOS) 矩阵与前置线性增益。
    若 profile 为 bypass 或无有效滤镜，返回 (None, 1.0)。
    """
    if not profile.filters or profile.id == "bypass":
        return None, 1.0

    fs = float(sample_rate)
    nyq = fs * 0.5
    sos_list = []

    for f in profile.filters:
        fc = max(20.0, min(f.freq, nyq * 0.92))
        db = f.gain_db
        q = max(0.2, min(10.0, f.q))

        if abs(db) < 0.05:
            continue

        w0 = 2.0 * math.pi * fc / fs
        cos_w0 = math.cos(w0)
        sin_w0 = math.sin(w0)
        A = 10.0 ** (db / 40.0)

        if f.ftype == "PK":
            alpha = sin_w0 / (2.0 * q)
            b0 = 1.0 + alpha * A
            b1 = -2.0 * cos_w0
            b2 = 1.0 - alpha * A
            a0 = 1.0 + alpha / A
            a1 = -2.0 * cos_w0
            a2 = 1.0 - alpha / A

        elif f.ftype == "LSQ":
            alpha = sin_w0 / (2.0 * q)
            two_sqrt_a_alpha = 2.0 * math.sqrt(A) * alpha
            b0 = A * ((A + 1.0) - (A - 1.0) * cos_w0 + two_sqrt_a_alpha)
            b1 = 2.0 * A * ((A - 1.0) - (A + 1.0) * cos_w0)
            b2 = A * ((A + 1.0) - (A - 1.0) * cos_w0 - two_sqrt_a_alpha)
            a0 = (A + 1.0) + (A - 1.0) * cos_w0 + two_sqrt_a_alpha
            a1 = -2.0 * ((A - 1.0) + (A + 1.0) * cos_w0)
            a2 = (A + 1.0) + (A - 1.0) * cos_w0 - two_sqrt_a_alpha

        elif f.ftype == "HSQ":
            alpha = sin_w0 / (2.0 * q)
            two_sqrt_a_alpha = 2.0 * math.sqrt(A) * alpha
            b0 = A * ((A + 1.0) + (A - 1.0) * cos_w0 + two_sqrt_a_alpha)
            b1 = -2.0 * A * ((A - 1.0) + (A + 1.0) * cos_w0)
            b2 = A * ((A + 1.0) - (A - 1.0) * cos_w0 - two_sqrt_a_alpha)
            a0 = (A + 1.0) - (A - 1.0) * cos_w0 + two_sqrt_a_alpha
            a1 = 2.0 * ((A - 1.0) - (A + 1.0) * cos_w0)
            a2 = (A + 1.0) - (A - 1.0) * cos_w0 - two_sqrt_a_alpha

        else:
            continue

        sos_list.append([b0 / a0, b1 / a0, b2 / a0, 1.0, a1 / a0, a2 / a0])

    if not sos_list:
        return None, 1.0

    sos_matrix = np.array(sos_list, dtype=np.float64)
    preamp_lin = float(10.0 ** (profile.preamp_db / 20.0))
    return sos_matrix, preamp_lin
