using System;

namespace Klang_WinUI.Services.Dsp;

/// <summary>
/// 高性能双二阶 IIR 数字滤波器 (Direct Form II Transposed)
/// 基于 Robert Bristow-Johnson Audio EQ Cookbook 精准构建
/// </summary>
public class BiquadFilter
{
    private double _b0, _b1, _b2;
    private double _a1, _a2;

    // 左右声道延迟状态 (Direct Form II Transposed)
    private double _z1L, _z2L;
    private double _z1R, _z2R;

    public BiquadFilter()
    {
        SetPassThrough();
    }

    public void Reset()
    {
        _z1L = 0;
        _z2L = 0;
        _z1R = 0;
        _z2R = 0;
    }

    public void SetPassThrough()
    {
        _b0 = 1.0;
        _b1 = 0.0;
        _b2 = 0.0;
        _a1 = 0.0;
        _a2 = 0.0;
    }

    public void SetCoefficients(double b0, double b1, double b2, double a0, double a1, double a2)
    {
        _b0 = b0 / a0;
        _b1 = b1 / a0;
        _b2 = b2 / a0;
        _a1 = a1 / a0;
        _a2 = a2 / a0;
    }

    public static BiquadFilter CreatePeaking(double sampleRate, double centerFreq, double gainDb, double q = 1.05)
    {
        var filter = new BiquadFilter();
        filter.UpdatePeaking(sampleRate, centerFreq, gainDb, q);
        return filter;
    }

    public void UpdatePeaking(double sampleRate, double centerFreq, double gainDb, double q = 1.05)
    {
        if (Math.Abs(gainDb) < 0.02)
        {
            SetPassThrough();
            return;
        }

        double fs = sampleRate;
        double fc = Math.Clamp(centerFreq, 20.0, fs * 0.45);
        double a = Math.Pow(10.0, gainDb / 40.0);
        double w0 = 2.0 * Math.PI * fc / fs;
        double cosW0 = Math.Cos(w0);
        double sinW0 = Math.Sin(w0);
        double alpha = sinW0 / (2.0 * Math.Max(0.1, q));

        double b0 = 1.0 + alpha * a;
        double b1 = -2.0 * cosW0;
        double b2 = 1.0 - alpha * a;
        double a0 = 1.0 + alpha / a;
        double a1 = -2.0 * cosW0;
        double a2 = 1.0 - alpha / a;

        SetCoefficients(b0, b1, b2, a0, a1, a2);
    }

    public static BiquadFilter CreateLowShelf(double sampleRate, double cutoffFreq, double gainDb, double q = 0.707)
    {
        var filter = new BiquadFilter();
        filter.UpdateLowShelf(sampleRate, cutoffFreq, gainDb, q);
        return filter;
    }

    public void UpdateLowShelf(double sampleRate, double cutoffFreq, double gainDb, double q = 0.707)
    {
        if (Math.Abs(gainDb) < 0.02)
        {
            SetPassThrough();
            return;
        }

        double fs = sampleRate;
        double fc = Math.Clamp(cutoffFreq, 20.0, fs * 0.45);
        double a = Math.Pow(10.0, gainDb / 40.0);
        double w0 = 2.0 * Math.PI * fc / fs;
        double cosW0 = Math.Cos(w0);
        double sinW0 = Math.Sin(w0);
        double alpha = sinW0 / (2.0 * Math.Max(0.1, q));
        double twoSqrtAAlpha = 2.0 * Math.Sqrt(a) * alpha;

        double b0 = a * ((a + 1.0) - (a - 1.0) * cosW0 + twoSqrtAAlpha);
        double b1 = 2.0 * a * ((a - 1.0) - (a + 1.0) * cosW0);
        double b2 = a * ((a + 1.0) - (a - 1.0) * cosW0 - twoSqrtAAlpha);
        double a0 = (a + 1.0) + (a - 1.0) * cosW0 + twoSqrtAAlpha;
        double a1 = -2.0 * ((a - 1.0) + (a + 1.0) * cosW0);
        double a2 = (a + 1.0) + (a - 1.0) * cosW0 - twoSqrtAAlpha;

        SetCoefficients(b0, b1, b2, a0, a1, a2);
    }

    public static BiquadFilter CreateHighShelf(double sampleRate, double cutoffFreq, double gainDb, double q = 0.707)
    {
        var filter = new BiquadFilter();
        filter.UpdateHighShelf(sampleRate, cutoffFreq, gainDb, q);
        return filter;
    }

    public void UpdateHighShelf(double sampleRate, double cutoffFreq, double gainDb, double q = 0.707)
    {
        if (Math.Abs(gainDb) < 0.02)
        {
            SetPassThrough();
            return;
        }

        double fs = sampleRate;
        double fc = Math.Clamp(cutoffFreq, 20.0, fs * 0.45);
        double a = Math.Pow(10.0, gainDb / 40.0);
        double w0 = 2.0 * Math.PI * fc / fs;
        double cosW0 = Math.Cos(w0);
        double sinW0 = Math.Sin(w0);
        double alpha = sinW0 / (2.0 * Math.Max(0.1, q));
        double twoSqrtAAlpha = 2.0 * Math.Sqrt(a) * alpha;

        double b0 = a * ((a + 1.0) + (a - 1.0) * cosW0 + twoSqrtAAlpha);
        double b1 = -2.0 * a * ((a - 1.0) + (a + 1.0) * cosW0);
        double b2 = a * ((a + 1.0) + (a - 1.0) * cosW0 - twoSqrtAAlpha);
        double a0 = (a + 1.0) - (a - 1.0) * cosW0 + twoSqrtAAlpha;
        double a1 = 2.0 * ((a - 1.0) - (a + 1.0) * cosW0);
        double a2 = (a + 1.0) - (a - 1.0) * cosW0 - twoSqrtAAlpha;

        SetCoefficients(b0, b1, b2, a0, a1, a2);
    }

    public static BiquadFilter CreateLowPass(double sampleRate, double cutoffFreq, double q = 0.707)
    {
        var filter = new BiquadFilter();
        filter.UpdateLowPass(sampleRate, cutoffFreq, q);
        return filter;
    }

    public void UpdateLowPass(double sampleRate, double cutoffFreq, double q = 0.707)
    {
        double fs = sampleRate;
        double fc = Math.Clamp(cutoffFreq, 20.0, fs * 0.45);
        double w0 = 2.0 * Math.PI * fc / fs;
        double cosW0 = Math.Cos(w0);
        double sinW0 = Math.Sin(w0);
        double alpha = sinW0 / (2.0 * Math.Max(0.1, q));

        double b0 = (1.0 - cosW0) / 2.0;
        double b1 = 1.0 - cosW0;
        double b2 = (1.0 - cosW0) / 2.0;
        double a0 = 1.0 + alpha;
        double a1 = -2.0 * cosW0;
        double a2 = 1.0 - alpha;

        SetCoefficients(b0, b1, b2, a0, a1, a2);
    }

    public static BiquadFilter CreateHighPass(double sampleRate, double cutoffFreq, double q = 0.707)
    {
        var filter = new BiquadFilter();
        filter.UpdateHighPass(sampleRate, cutoffFreq, q);
        return filter;
    }

    public void UpdateHighPass(double sampleRate, double cutoffFreq, double q = 0.707)
    {
        double fs = sampleRate;
        double fc = Math.Clamp(cutoffFreq, 20.0, fs * 0.45);
        double w0 = 2.0 * Math.PI * fc / fs;
        double cosW0 = Math.Cos(w0);
        double sinW0 = Math.Sin(w0);
        double alpha = sinW0 / (2.0 * Math.Max(0.1, q));

        double b0 = (1.0 + cosW0) / 2.0;
        double b1 = -(1.0 + cosW0);
        double b2 = (1.0 + cosW0) / 2.0;
        double a0 = 1.0 + alpha;
        double a1 = -2.0 * cosW0;
        double a2 = 1.0 - alpha;

        SetCoefficients(b0, b1, b2, a0, a1, a2);
    }

    public static BiquadFilter CreateNotch(double sampleRate, double centerFreq, double q = 2.2)
    {
        var filter = new BiquadFilter();
        filter.UpdateNotch(sampleRate, centerFreq, q);
        return filter;
    }

    public void UpdateNotch(double sampleRate, double centerFreq, double q = 2.2)
    {
        double fs = sampleRate;
        double fc = Math.Clamp(centerFreq, 20.0, fs * 0.45);
        double w0 = 2.0 * Math.PI * fc / fs;
        double cosW0 = Math.Cos(w0);
        double sinW0 = Math.Sin(w0);
        double alpha = sinW0 / (2.0 * Math.Max(0.1, q));

        double b0 = 1.0;
        double b1 = -2.0 * cosW0;
        double b2 = 1.0;
        double a0 = 1.0 + alpha;
        double a1 = -2.0 * cosW0;
        double a2 = 1.0 - alpha;

        SetCoefficients(b0, b1, b2, a0, a1, a2);
    }

    /// <summary>
    /// 处理单声道单样本
    /// </summary>
    public float ProcessSample(float inSample, ref double z1, ref double z2)
    {
        double x = inSample;
        double y = _b0 * x + z1;
        z1 = _b1 * x - _a1 * y + z2;
        z2 = _b2 * x - _a2 * y;
        return (float)y;
    }

    /// <summary>
    /// 处理左声道单样本
    /// </summary>
    public float ProcessLeft(float inL)
    {
        return ProcessSample(inL, ref _z1L, ref _z2L);
    }

    /// <summary>
    /// 处理右声道单样本
    /// </summary>
    public float ProcessRight(float inR)
    {
        return ProcessSample(inR, ref _z1R, ref _z2R);
    }

    /// <summary>
    /// 处理立体声左右声道单样本
    /// </summary>
    public void ProcessStereo(ref float inL, ref float inR)
    {
        inL = ProcessLeft(inL);
        inR = ProcessRight(inR);
    }
}
