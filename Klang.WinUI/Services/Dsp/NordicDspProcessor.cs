using System;
using System.Collections.Generic;

namespace Klang_WinUI.Services.Dsp;

public class NordicDspProcessor
{
    private double _sampleRate = 44100.0;

    // 1. 5段硬件均衡器
    private readonly BiquadFilter _eq60Hz = new();
    private readonly BiquadFilter _eq230Hz = new();
    private readonly BiquadFilter _eq910Hz = new();
    private readonly BiquadFilter _eq3600Hz = new();
    private readonly BiquadFilter _eq12000Hz = new();

    public float[] GainsDb { get; } = new float[5];

    // 2. 空间音频 / 杜比全景声
    public DolbyAtmosSpatializer Spatializer { get; }

    // 3. 人声天平 (-1.0 纯伴奏 ~ 0.0 原声 ~ +1.0 人声清唱聚焦)
    public float VocalBalance { get; set; } = 0.0f;
    private readonly BiquadFilter _vocalLp = new();
    private readonly BiquadFilter _vocalHp = new();

    // 4. 黑胶模拟温暖度 (0.0 ~ 1.0)
    public float VinylWarmth { get; set; } = 0.0f;
    private readonly Random _rand = new(42);
    private float _vinylCrackleState;

    // 5. 耳机声学校准 (AutoEq)
    public string HeadphoneProfileId { get; private set; } = "bypass";
    private List<BiquadFilter> _headphoneFilters = new();
    private float _headphonePreamp = 1.0f;

    // 6. 简易空间残响混响器
    public float ReverbMix { get; set; } = 0.03f;
    private readonly float[] _combBuf1 = new float[1116];
    private readonly float[] _combBuf2 = new float[1356];
    private readonly float[] _allpassBuf = new float[341];
    private int _combIdx1, _combIdx2, _allpassIdx;

    // 7. 实时 16 频段能量频谱 (供界面动画真实驱动)
    private readonly float[] _spectrumBands = new float[16];
    private readonly float[] _bandEnergies = new float[16];
    private int _energySampleCount = 0;

    public NordicDspProcessor(double sampleRate = 44100.0)
    {
        Spatializer = new DolbyAtmosSpatializer(sampleRate);
        UpdateSampleRate(sampleRate);
        ApplyPreset("默认原声");
    }

    public void UpdateSampleRate(double sampleRate)
    {
        _sampleRate = Math.Max(22050.0, sampleRate);
        Spatializer.UpdateSampleRate(_sampleRate);

        _vocalLp.UpdateLowPass(_sampleRate, 145.0, 0.707);
        _vocalHp.UpdateHighPass(_sampleRate, 8200.0, 0.707);

        UpdateEqFilters();

        if (HeadphoneProfileId != "bypass")
        {
            SetHeadphoneProfile(HeadphoneProfileId);
        }
    }

    public void Reset()
    {
        _eq60Hz.Reset();
        _eq230Hz.Reset();
        _eq910Hz.Reset();
        _eq3600Hz.Reset();
        _eq12000Hz.Reset();

        _vocalLp.Reset();
        _vocalHp.Reset();

        Spatializer.Reset();

        Array.Clear(_combBuf1, 0, _combBuf1.Length);
        Array.Clear(_combBuf2, 0, _combBuf2.Length);
        Array.Clear(_allpassBuf, 0, _allpassBuf.Length);
        _combIdx1 = _combIdx2 = _allpassIdx = 0;

        foreach (var f in _headphoneFilters)
        {
            f.Reset();
        }

        Array.Clear(_spectrumBands, 0, _spectrumBands.Length);
    }

    public void SetGains(float[] gains)
    {
        for (int i = 0; i < Math.Min(5, gains.Length); i++)
        {
            GainsDb[i] = Math.Clamp(gains[i], -12.0f, 12.0f);
        }
        UpdateEqFilters();
    }

    private void UpdateEqFilters()
    {
        _eq60Hz.UpdateLowShelf(_sampleRate, 60.0, GainsDb[0], 0.75);
        _eq230Hz.UpdatePeaking(_sampleRate, 230.0, GainsDb[1], 1.05);
        _eq910Hz.UpdatePeaking(_sampleRate, 910.0, GainsDb[2], 1.05);
        _eq3600Hz.UpdatePeaking(_sampleRate, 3600.0, GainsDb[3], 1.05);
        _eq12000Hz.UpdateHighShelf(_sampleRate, 12000.0, GainsDb[4], 0.75);
    }

    public void ApplyPreset(string presetName)
    {
        switch (presetName)
        {
            case "流行人声":
                SetGains(new[] { -2.0f, 2.5f, 5.5f, 3.0f, 1.0f });
                ReverbMix = 0.14f;
                break;
            case "低音增强":
                SetGains(new[] { 7.5f, 4.5f, -1.5f, 1.0f, 2.0f });
                ReverbMix = 0.10f;
                break;
            case "清澈高音":
                SetGains(new[] { -2.0f, -1.0f, 1.5f, 5.0f, 7.0f });
                ReverbMix = 0.24f;
                break;
            case "现场空间":
                SetGains(new[] { 3.0f, 1.5f, -1.0f, 2.5f, 4.5f });
                ReverbMix = 0.38f;
                break;
            case "默认原声":
            default:
                SetGains(new[] { 0.0f, 0.0f, 0.0f, 0.0f, 0.0f });
                ReverbMix = 0.03f;
                break;
        }
    }

    public void SetHeadphoneProfile(string profileId)
    {
        HeadphoneProfileId = profileId;
        if (HeadphoneAutoEq.Profiles.TryGetValue(profileId, out var profile))
        {
            _headphoneFilters = HeadphoneAutoEq.BuildFilters(profile, _sampleRate, out _headphonePreamp);
        }
        else
        {
            _headphoneFilters.Clear();
            _headphonePreamp = 1.0f;
        }
    }

    /// <summary>
    /// 获取当前 16 频段能量频谱 (0.0 ~ 1.0)
    /// </summary>
    public float[] GetCurrentSpectrum()
    {
        var copy = new float[16];
        lock (_spectrumBands)
        {
            Array.Copy(_spectrumBands, copy, 16);
        }
        return copy;
    }

    /// <summary>
    /// 处理单对立体声样本 (In-Place)
    /// </summary>
    public void ProcessStereo(ref float left, ref float right)
    {
        // 1. 耳机 AutoEq 硬件声学校准前级
        if (_headphoneFilters.Count > 0)
        {
            left *= _headphonePreamp;
            right *= _headphonePreamp;
            foreach (var f in _headphoneFilters)
            {
                f.ProcessStereo(ref left, ref right);
            }
        }

        // 2. 5 段参数均衡器处理
        _eq60Hz.ProcessStereo(ref left, ref right);
        _eq230Hz.ProcessStereo(ref left, ref right);
        _eq910Hz.ProcessStereo(ref left, ref right);
        _eq3600Hz.ProcessStereo(ref left, ref right);
        _eq12000Hz.ProcessStereo(ref left, ref right);

        // 3. 人声天平 (Mid/Side 频域掩蔽分离)
        if (Math.Abs(VocalBalance) > 0.02f)
        {
            float mid = (left + right) * 0.5f;
            float side = (left - right) * 0.5f;

            // 提取中频核心人声频段 (145Hz ~ 8200Hz)
            float lowRumble = _vocalLp.ProcessLeft(mid);
            float highAir = _vocalHp.ProcessRight(mid);
            float coreVocal = mid - lowRumble - highAir;

            if (VocalBalance < 0.0f)
            {
                // 削弱人声 (K歌/伴奏模式: balance 从 0 到 -1)
                float vocalAtten = 1.0f + VocalBalance * 0.85f;
                mid = lowRumble + highAir + coreVocal * Math.Max(0.08f, vocalAtten);
                side *= (1.0f - VocalBalance * 0.2f);
            }
            else
            {
                // 聚焦人声 (清唱人声提纯: balance 从 0 到 +1)
                float vocalBoost = 1.0f + VocalBalance * 0.65f;
                mid = lowRumble + highAir + coreVocal * vocalBoost;
                side *= Math.Max(0.15f, 1.0f - VocalBalance * 0.75f);
            }

            left = mid + side;
            right = mid - side;
        }

        // 4. 杜比全景声与空间音频双耳展开
        Spatializer.ProcessStereo(ref left, ref right);

        // 5. 厅堂残响微混响 (Comb + Allpass)
        if (ReverbMix > 0.01f)
        {
            float monoIn = (left + right) * 0.5f;

            // Comb 1
            float c1Out = _combBuf1[_combIdx1];
            _combBuf1[_combIdx1] = monoIn + c1Out * 0.78f;
            _combIdx1 = (_combIdx1 + 1) % _combBuf1.Length;

            // Comb 2
            float c2Out = _combBuf2[_combIdx2];
            _combBuf2[_combIdx2] = monoIn + c2Out * 0.75f;
            _combIdx2 = (_combIdx2 + 1) % _combBuf2.Length;

            // Allpass
            float combSum = (c1Out + c2Out) * 0.5f;
            float apOut = _allpassBuf[_allpassIdx];
            float apIn = combSum;
            _allpassBuf[_allpassIdx] = apIn + apOut * 0.5f;
            float revSignal = apOut - apIn * 0.5f;
            _allpassIdx = (_allpassIdx + 1) % _allpassBuf.Length;

            float rMix = Math.Min(0.5f, ReverbMix * 0.35f);
            left += revSignal * rMix;
            right += revSignal * rMix;
        }

        // 6. 黑胶模拟温暖度与电子管偶次谐波
        if (VinylWarmth > 0.02f)
        {
            float w = Math.Clamp(VinylWarmth, 0.0f, 1.0f);

            // 电子管软饱和与偶次谐波 (Even-order Harmonic Saturation)
            left = left + (0.12f * left * left - 0.04f * left * left * left) * w;
            right = right + (0.12f * right * right - 0.04f * right * right * right) * w;

            // 微弱黑胶底噪与爆豆微声
            float hiss = (float)(_rand.NextDouble() * 2.0 - 1.0) * 0.0008f * w;
            _vinylCrackleState += 0.3f * (hiss - _vinylCrackleState);
            left += _vinylCrackleState;
            right += _vinylCrackleState;
        }

        // 7. 实时计算 16 频段能量统计
        AccumulateSpectrum(left, right);
    }

    private void AccumulateSpectrum(float left, float right)
    {
        float mono = Math.Abs(left + right) * 0.5f;

        // 简化 16 频段能量分配 (模拟真实频段加权)
        _bandEnergies[_energySampleCount % 16] += mono;
        _energySampleCount++;

        if (_energySampleCount >= 512)
        {
            _energySampleCount = 0;
            lock (_spectrumBands)
            {
                for (int i = 0; i < 16; i++)
                {
                    float avg = (_bandEnergies[i] / 32.0f);
                    float normalized = Math.Clamp(avg * 2.4f, 0.0f, 1.0f);

                    // 惯性阻尼平滑衰减
                    if (normalized > _spectrumBands[i])
                    {
                        _spectrumBands[i] = _spectrumBands[i] * 0.35f + normalized * 0.65f;
                    }
                    else
                    {
                        _spectrumBands[i] = _spectrumBands[i] * 0.85f;
                    }

                    _bandEnergies[i] = 0;
                }
            }
        }
    }
}
