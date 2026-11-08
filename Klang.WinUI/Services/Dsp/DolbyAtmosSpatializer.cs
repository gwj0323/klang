using System;

namespace Klang_WinUI.Services.Dsp;

public enum SpatialMode
{
    Off,
    SpatialStereo,
    DolbyAtmos
}

/// <summary>
/// 杜比全景声与空间音频双耳渲染器 (Dolby Atmos & Spatial Audio Binauralizer)
/// 包含：
/// 1. M/S (Mid-Side) 声学解耦
/// 2. Schroeder Allpass 相位去相关环形扩散网络
/// 3. ITD (0.25ms 双耳时间差) 与 ILD 头影效应前置人声重构
/// 4. 8.6kHz 天空声道耳廓高度感知陷波滤波器
/// </summary>
public class DolbyAtmosSpatializer
{
    private const int RingLen = 8192;
    private const int RingMask = 8191;

    private readonly float[] _delayRingMid = new float[RingLen];
    private readonly float[] _delayRingSide = new float[RingLen];
    private int _writePos;

    private float _headShadowL;
    private float _headShadowR;

    private readonly BiquadFilter _elevationNotchL = new();
    private readonly BiquadFilter _elevationNotchR = new();

    public SpatialMode Mode { get; private set; } = SpatialMode.DolbyAtmos;
    public float SpatialWidth { get; set; } = 1.35f; // 0.8 ~ 2.0
    public float SpatialHeight { get; set; } = 0.65f; // 0.0 ~ 1.0

    private float _curWetGain = 1.0f;
    private float _targetWetGain = 1.0f;

    private double _sampleRate = 44100.0;

    public DolbyAtmosSpatializer(double sampleRate = 44100.0)
    {
        UpdateSampleRate(sampleRate);
        SetMode(SpatialMode.DolbyAtmos);
    }

    public void UpdateSampleRate(double sampleRate)
    {
        _sampleRate = Math.Max(22050.0, sampleRate);
        _elevationNotchL.UpdateNotch(_sampleRate, 8600.0, 2.2);
        _elevationNotchR.UpdateNotch(_sampleRate, 8600.0, 2.2);
        Reset();
    }

    public void Reset()
    {
        Array.Clear(_delayRingMid, 0, RingLen);
        Array.Clear(_delayRingSide, 0, RingLen);
        _writePos = 0;
        _headShadowL = 0;
        _headShadowR = 0;
        _elevationNotchL.Reset();
        _elevationNotchR.Reset();
        _curWetGain = _targetWetGain;
    }

    public void SetMode(SpatialMode mode)
    {
        Mode = mode;
        _targetWetGain = mode switch
        {
            SpatialMode.Off => 0.0f,
            SpatialMode.SpatialStereo => 0.65f,
            SpatialMode.DolbyAtmos => 1.0f,
            _ => 0.0f
        };
    }

    public void ProcessStereo(ref float inL, ref float inR)
    {
        // 增益平滑过渡
        if (Math.Abs(_curWetGain - _targetWetGain) > 0.0005f)
        {
            _curWetGain += (_targetWetGain - _curWetGain) * 0.005f;
        }
        else
        {
            _curWetGain = _targetWetGain;
        }

        if (_curWetGain <= 0.001f && _targetWetGain <= 0.001f)
        {
            return; // 旁路直通
        }

        float dryL = inL;
        float dryR = inR;

        // 1. M/S (Mid-Side) 解耦
        float mid = (inL + inR) * 0.5f;
        float side = (inL - inR) * 0.5f;

        // 2. 写入延迟环
        int w = _writePos;
        _delayRingMid[w] = mid;
        _delayRingSide[w] = side;

        // 3. 侧向环绕全通相位去相关扩散 (Schroeder Allpass Decorrelation)
        int tap1 = (int)Math.Round(_sampleRate * 0.0015); // 1.5ms
        int tap2 = (int)Math.Round(_sampleRate * 0.0036); // 3.6ms

        int idxL = (w - tap1) & RingMask;
        int idxR = (w - tap2) & RingMask;

        float sDelayedL = _delayRingSide[idxL];
        float sDelayedR = _delayRingSide[idxR];

        // 4. 前置人声声学交叉馈送 (ITD ~0.25ms + 头影衰减 ILD)
        int itdSamples = Math.Max(1, (int)Math.Round(_sampleRate * 0.00025));
        int itdIdx = (w - itdSamples) & RingMask;
        float mCross = _delayRingMid[itdIdx];

        const float alphaShadow = 0.35f;
        _headShadowL += alphaShadow * (mCross - _headShadowL);
        _headShadowR += alphaShadow * (mCross - _headShadowR);

        _writePos = (w + 1) & RingMask;

        // 5. 空间双耳声场合成
        float width = SpatialWidth;
        float wetL = (mid + _headShadowR * 0.22f) + sDelayedL * width;
        float wetR = (mid + _headShadowL * 0.22f) - sDelayedR * width;

        // 6. 天空声道 8.6kHz 仰角陷波滤镜
        float notchL = _elevationNotchL.ProcessLeft(wetL);
        float notchR = _elevationNotchR.ProcessRight(wetR);

        float height = SpatialHeight;
        float elevL = wetL * (1.0f - height * 0.5f) + notchL * (height * 0.5f);
        float elevR = wetR * (1.0f - height * 0.5f) + notchR * (height * 0.5f);

        // 7. 湿声/干声线性融合
        inL = dryL * (1.0f - _curWetGain) + elevL * _curWetGain;
        inR = dryR * (1.0f - _curWetGain) + elevR * _curWetGain;
    }
}
