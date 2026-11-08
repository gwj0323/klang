using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Klang_WinUI.Services;
using Klang_WinUI.Services.Dsp;

namespace Klang_WinUI.Dialogs;

public sealed partial class EqualizerDialog : ContentDialog
{
    private bool _isInitializing = true;

    public EqualizerDialog()
    {
        InitializeComponent();

        PopulateHeadphoneProfiles();
        LoadCurrentState();

        this.PrimaryButtonClick += (s, e) =>
        {
            AudioEngineService.Instance.SetPreset("默认原声");
            LoadCurrentState();
        };

        _isInitializing = false;
    }

    private void PopulateHeadphoneProfiles()
    {
        HeadphoneCombo.Items.Clear();
        foreach (var (id, profile) in HeadphoneAutoEq.Profiles)
        {
            HeadphoneCombo.Items.Add(new ComboBoxItem
            {
                Content = $"{profile.Name} ({profile.Category})",
                Tag = id
            });
        }
    }

    private void LoadCurrentState()
    {
        var eng = AudioEngineService.Instance;

        // 1. 预设
        CurrentPresetLabel.Text = $"当前：{eng.CurrentPreset}";
        UpdatePresetButtonStyles(eng.CurrentPreset);

        // 2. 5 段 EQ
        _isInitializing = true;
        Slider60Hz.Value = eng.EqGains[0];
        Slider230Hz.Value = eng.EqGains[1];
        Slider910Hz.Value = eng.EqGains[2];
        Slider3600Hz.Value = eng.EqGains[3];
        Slider12000Hz.Value = eng.EqGains[4];

        Val60Hz.Text = $"{eng.EqGains[0]:+0.0;-0.0;0.0}dB";
        Val230Hz.Text = $"{eng.EqGains[1]:+0.0;-0.0;0.0}dB";
        Val910Hz.Text = $"{eng.EqGains[2]:+0.0;-0.0;0.0}dB";
        Val3600Hz.Text = $"{eng.EqGains[3]:+0.0;-0.0;0.0}dB";
        Val12000Hz.Text = $"{eng.EqGains[4]:+0.0;-0.0;0.0}dB";

        // 3. 空间音频
        SpatialCombo.SelectedIndex = eng.SpatialAudioMode switch
        {
            SpatialMode.DolbyAtmos => 0,
            SpatialMode.SpatialStereo => 1,
            _ => 2
        };

        // 4. 人声天平
        VocalBalanceSlider.Value = eng.VocalBalance;
        UpdateVocalBalanceText(eng.VocalBalance);

        // 5. 黑胶模拟
        VinylWarmthSlider.Value = eng.VinylWarmth * 100.0f;
        VinylWarmthLabel.Text = $"{(int)(eng.VinylWarmth * 100)}%";

        // 6. 耳机校准
        string hpId = eng.HeadphoneProfileId;
        for (int i = 0; i < HeadphoneCombo.Items.Count; i++)
        {
            if (HeadphoneCombo.Items[i] is ComboBoxItem item && item.Tag is string tag && tag == hpId)
            {
                HeadphoneCombo.SelectedIndex = i;
                break;
            }
        }
        if (HeadphoneCombo.SelectedIndex < 0) HeadphoneCombo.SelectedIndex = 0;

        _isInitializing = false;
    }

    private void UpdatePresetButtonStyles(string activePreset)
    {
        try
        {
            var accentStyle = Application.Current.Resources["AccentButtonStyle"] as Style;
            var defaultStyle = Application.Current.Resources["DefaultButtonStyle"] as Style;

            if (accentStyle != null && defaultStyle != null)
            {
                PresetDefaultBtn.Style = (activePreset == "默认原声") ? accentStyle : defaultStyle;
                PresetVocalBtn.Style = (activePreset == "流行人声") ? accentStyle : defaultStyle;
                PresetBassBtn.Style = (activePreset == "低音增强") ? accentStyle : defaultStyle;
                PresetTrebleBtn.Style = (activePreset == "清澈高音") ? accentStyle : defaultStyle;
                PresetLiveBtn.Style = (activePreset == "现场空间") ? accentStyle : defaultStyle;
            }
        }
        catch { }
    }

    private void PresetBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string preset)
        {
            AudioEngineService.Instance.SetPreset(preset);
            LoadCurrentState();
        }
    }

    private void EqSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isInitializing) return;

        float g0 = (float)Slider60Hz.Value;
        float g1 = (float)Slider230Hz.Value;
        float g2 = (float)Slider910Hz.Value;
        float g3 = (float)Slider3600Hz.Value;
        float g4 = (float)Slider12000Hz.Value;

        Val60Hz.Text = $"{g0:+0.0;-0.0;0.0}dB";
        Val230Hz.Text = $"{g1:+0.0;-0.0;0.0}dB";
        Val910Hz.Text = $"{g2:+0.0;-0.0;0.0}dB";
        Val3600Hz.Text = $"{g3:+0.0;-0.0;0.0}dB";
        Val12000Hz.Text = $"{g4:+0.0;-0.0;0.0}dB";

        CurrentPresetLabel.Text = "当前：自定义";
        UpdatePresetButtonStyles("自定义");

        AudioEngineService.Instance.SetEqGains(new[] { g0, g1, g2, g3, g4 });
    }

    private void SpatialCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        var mode = SpatialCombo.SelectedIndex switch
        {
            0 => SpatialMode.DolbyAtmos,
            1 => SpatialMode.SpatialStereo,
            _ => SpatialMode.Off
        };

        AudioEngineService.Instance.SetSpatialMode(mode);
    }

    private void VocalBalanceSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isInitializing) return;

        float val = (float)e.NewValue;
        UpdateVocalBalanceText(val);
        AudioEngineService.Instance.SetVocalBalance(val);
    }

    private void UpdateVocalBalanceText(float val)
    {
        if (Math.Abs(val) < 0.05f)
        {
            VocalBalanceLabel.Text = "均衡 0.0";
        }
        else if (val < 0)
        {
            VocalBalanceLabel.Text = $"伴奏优先 ({val:F2})";
        }
        else
        {
            VocalBalanceLabel.Text = $"人声聚焦 (+{val:F2})";
        }
    }

    private void VinylWarmthSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_isInitializing) return;

        float w = (float)(e.NewValue / 100.0);
        VinylWarmthLabel.Text = $"{(int)e.NewValue}%";
        AudioEngineService.Instance.SetVinylWarmth(w);
    }

    private void HeadphoneCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_isInitializing) return;

        if (HeadphoneCombo.SelectedItem is ComboBoxItem item && item.Tag is string id)
        {
            AudioEngineService.Instance.SetHeadphoneProfile(id);
            if (HeadphoneAutoEq.Profiles.TryGetValue(id, out var prof))
            {
                HeadphoneDescLabel.Text = prof.Description;
            }
        }
    }
}
