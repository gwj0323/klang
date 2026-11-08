using System;
using System.Collections.Generic;

namespace Klang_WinUI.Services.Dsp;

public enum FilterType
{
    Peaking,
    LowShelf,
    HighShelf
}

public record EqFilterDef(FilterType Type, double Freq, double GainDb, double Q);

public record HeadphoneProfile(
    string Id,
    string Name,
    string Brand,
    string Category,
    string TargetCurve,
    double PreampDb,
    string Description,
    List<EqFilterDef> Filters
);

public static class HeadphoneAutoEq
{
    public static readonly Dictionary<string, HeadphoneProfile> Profiles = new()
    {
        ["bypass"] = new HeadphoneProfile(
            "bypass",
            "关闭 / 原声监听 (Bypass)",
            "通用",
            "直通",
            "Native",
            0.0,
            "保持音频原始声底直通输出，未开启硬件声学校准。",
            new List<EqFilterDef>()
        ),
        ["sony_wh1000xm5"] = new HeadphoneProfile(
            "sony_wh1000xm5",
            "Sony WH-1000XM5",
            "Sony",
            "头戴式",
            "Harman Over-Ear",
            -5.5,
            "收敛 150~250Hz 过肥中低频，提亮中高频人声细节与泛音空气感。",
            new List<EqFilterDef>
            {
                new(FilterType.Peaking, 75.0, -4.2, 0.70),
                new(FilterType.Peaking, 210.0, -5.8, 1.10),
                new(FilterType.Peaking, 1100.0, 2.8, 1.40),
                new(FilterType.Peaking, 3200.0, 4.5, 1.60),
                new(FilterType.Peaking, 6800.0, -3.2, 2.50),
                new(FilterType.HighShelf, 10000.0, 3.0, 0.70)
            }
        ),
        ["sony_wh1000xm4"] = new HeadphoneProfile(
            "sony_wh1000xm4",
            "Sony WH-1000XM4",
            "Sony",
            "头戴式",
            "Harman Over-Ear",
            -5.9,
            "压制偏厚重的中低频混浊感，弥补 3kHz 人声区，通透度显著提升。",
            new List<EqFilterDef>
            {
                new(FilterType.Peaking, 200.0, -6.2, 0.85),
                new(FilterType.Peaking, 1200.0, 2.5, 1.50),
                new(FilterType.Peaking, 3000.0, 4.8, 1.80),
                new(FilterType.Peaking, 6500.0, -3.5, 3.00),
                new(FilterType.HighShelf, 10000.0, 2.5, 0.70)
            }
        ),
        ["airpods_pro_2"] = new HeadphoneProfile(
            "airpods_pro_2",
            "Apple AirPods Pro 2",
            "Apple",
            "真无线",
            "Harman In-Ear",
            -3.5,
            "平滑中高频衔接，改善 6kHz 齿音毛刺，增强声音纯净度与耐听感。",
            new List<EqFilterDef>
            {
                new(FilterType.Peaking, 240.0, -1.8, 1.20),
                new(FilterType.Peaking, 1300.0, 1.5, 1.80),
                new(FilterType.Peaking, 3100.0, 2.8, 1.60),
                new(FilterType.Peaking, 6000.0, -3.2, 2.50),
                new(FilterType.HighShelf, 10000.0, 2.5, 0.70)
            }
        ),
        ["airpods_max"] = new HeadphoneProfile(
            "airpods_max",
            "Apple AirPods Max",
            "Apple",
            "头戴式",
            "Harman Over-Ear",
            -4.2,
            "填补 3~4kHz 中频微陷，收束略微松散的极低频，声场层次更立体。",
            new List<EqFilterDef>
            {
                new(FilterType.Peaking, 180.0, -2.2, 1.10),
                new(FilterType.Peaking, 1400.0, 2.0, 1.40),
                new(FilterType.Peaking, 3600.0, 3.8, 1.80),
                new(FilterType.Peaking, 5800.0, -2.8, 2.80),
                new(FilterType.HighShelf, 10000.0, 1.5, 0.70)
            }
        ),
        ["sennheiser_hd600"] = new HeadphoneProfile(
            "sennheiser_hd600",
            "Sennheiser HD 600",
            "Sennheiser",
            "开放式",
            "Harman Over-Ear",
            -5.0,
            "弥补开放式动圈 60Hz 以下极低频滚降，平顺高频，带来丰满大动态下潜。",
            new List<EqFilterDef>
            {
                new(FilterType.LowShelf, 60.0, 5.5, 0.70),
                new(FilterType.Peaking, 180.0, -1.5, 1.00),
                new(FilterType.Peaking, 3200.0, -1.8, 2.20),
                new(FilterType.Peaking, 5600.0, 1.5, 2.00),
                new(FilterType.HighShelf, 11000.0, 2.0, 0.70)
            }
        ),
        ["sennheiser_hd650"] = new HeadphoneProfile(
            "sennheiser_hd650",
            "Sennheiser HD 650",
            "Sennheiser",
            "开放式",
            "Harman Over-Ear",
            -5.2,
            "补充次低频弹性与能量，微调中高频泛音，让人声更加温润通透。",
            new List<EqFilterDef>
            {
                new(FilterType.LowShelf, 60.0, 5.8, 0.70),
                new(FilterType.Peaking, 220.0, -1.8, 1.10),
                new(FilterType.Peaking, 3400.0, -1.5, 2.00),
                new(FilterType.Peaking, 6000.0, 2.2, 1.80),
                new(FilterType.HighShelf, 10000.0, 2.5, 0.70)
            }
        ),
        ["beyer_dt990"] = new HeadphoneProfile(
            "beyer_dt990",
            "Beyerdynamic DT 990 Pro",
            "Beyerdynamic",
            "开放式",
            "Harman Over-Ear",
            -4.8,
            "精准消除拜雅 8.5kHz 齿音尖峰，消除听觉疲劳，声场开阔舒适。",
            new List<EqFilterDef>
            {
                new(FilterType.LowShelf, 60.0, 3.0, 0.70),
                new(FilterType.Peaking, 200.0, -2.0, 1.00),
                new(FilterType.Peaking, 4200.0, 2.5, 1.50),
                new(FilterType.Peaking, 8500.0, -6.8, 3.20),
                new(FilterType.HighShelf, 12000.0, -2.0, 0.70)
            }
        ),
        ["beyer_dt770"] = new HeadphoneProfile(
            "beyer_dt770",
            "Beyerdynamic DT 770 Pro",
            "Beyerdynamic",
            "头戴式",
            "Harman Over-Ear",
            -4.8,
            "收束略肥的箱体低频，柔化 8.6kHz 刺耳高频，让中频人声更加饱满结实。",
            new List<EqFilterDef>
            {
                new(FilterType.Peaking, 120.0, -3.5, 1.20),
                new(FilterType.Peaking, 250.0, -2.8, 1.40),
                new(FilterType.Peaking, 3800.0, 3.2, 1.60),
                new(FilterType.Peaking, 8600.0, -5.5, 3.00),
                new(FilterType.HighShelf, 11000.0, -1.8, 0.70)
            }
        ),
        ["ath_m50x"] = new HeadphoneProfile(
            "ath_m50x",
            "Audio-Technica ATH-M50x",
            "Audio-Technica",
            "头戴式",
            "Harman Over-Ear",
            -4.5,
            "减轻 150Hz 膨胀混浊感，平顺 9.5kHz 金属毛刺，还原母带监听真实本色。",
            new List<EqFilterDef>
            {
                new(FilterType.Peaking, 150.0, -3.8, 1.20),
                new(FilterType.Peaking, 400.0, 1.5, 1.80),
                new(FilterType.Peaking, 3200.0, 2.8, 1.50),
                new(FilterType.Peaking, 9500.0, -4.5, 2.50)
            }
        )
    };

    public static List<BiquadFilter> BuildFilters(HeadphoneProfile profile, double sampleRate, out float preampGain)
    {
        var list = new List<BiquadFilter>();
        if (profile.Id == "bypass" || profile.Filters.Count == 0)
        {
            preampGain = 1.0f;
            return list;
        }

        preampGain = (float)Math.Pow(10.0, profile.PreampDb / 20.0);

        foreach (var def in profile.Filters)
        {
            var filter = def.Type switch
            {
                FilterType.LowShelf => BiquadFilter.CreateLowShelf(sampleRate, def.Freq, def.GainDb, def.Q),
                FilterType.HighShelf => BiquadFilter.CreateHighShelf(sampleRate, def.Freq, def.GainDb, def.Q),
                _ => BiquadFilter.CreatePeaking(sampleRate, def.Freq, def.GainDb, def.Q)
            };
            list.Add(filter);
        }

        return list;
    }
}
