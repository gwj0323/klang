using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NAudio.Wave;
using Windows.Graphics;
using Klang_WinUI.Models;
using Klang_WinUI.Pages;
using Klang_WinUI.Services;

namespace Klang_WinUI;

public sealed partial class MainWindow : Window
{
    private LibraryPage? _libraryPage;
    private FavoritesPage? _favoritesPage;
    private NetEasePage? _netEasePage;
    private LyricsPage? _lyricsPage;
    private SettingsPage? _settingsPage;

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.SetIcon("Assets/AppIcon.ico");

        AppWindow.Resize(new SizeInt32(1140, 750));

        // 初始化音频引擎调度器
        AudioEngineService.Instance.InitializeDispatcher(this.DispatcherQueue);

        // 监听音频播放引擎状态与进度
        AudioEngineService.Instance.PositionChanged += (current, total) =>
        {
            BottomTransportBar.UpdateProgress(current, total);
        };

        AudioEngineService.Instance.StateChanged += (state) =>
        {
            BottomTransportBar.IsPlaying = (state == PlaybackState.Playing);
        };

        // 绑定底栏控制事件
        BottomTransportBar.PlayPauseClicked += (s, e) =>
        {
            AudioEngineService.Instance.TogglePlayPause();
        };

        BottomTransportBar.UserSeekRequested += (s, pos) =>
        {
            AudioEngineService.Instance.Seek(pos);
        };

        BottomTransportBar.UserVolumeChanged += (s, vol) =>
        {
            AudioEngineService.Instance.Volume = vol;
        };

        // 默认定位到本地曲库
        NavigateTo("library");
    }

    private void TitleBar_PaneToggleRequested(TitleBar sender, object args)
    {
        NavView.IsPaneOpen = !NavView.IsPaneOpen;
    }

    private void TitleBar_BackRequested(TitleBar sender, object args)
    {
        if (NavFrame.CanGoBack)
        {
            NavFrame.GoBack();
        }
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.IsSettingsSelected)
        {
            NavigateTo("settings");
        }
        else if (args.SelectedItem is NavigationViewItem item && item.Tag is string tag)
        {
            NavigateTo(tag);
        }
    }

    private void NavigateTo(string tag)
    {
        switch (tag)
        {
            case "library":
                if (_libraryPage == null)
                {
                    _libraryPage = new LibraryPage();
                    _libraryPage.TrackSelected += OnTrackSelected;
                }
                NavFrame.Content = _libraryPage;
                break;

            case "favorites":
                _favoritesPage ??= new FavoritesPage();
                NavFrame.Content = _favoritesPage;
                break;

            case "netease":
                _netEasePage ??= new NetEasePage();
                NavFrame.Content = _netEasePage;
                break;

            case "lyrics":
                _lyricsPage ??= new LyricsPage();
                NavFrame.Content = _lyricsPage;
                break;

            case "settings":
                _settingsPage ??= new SettingsPage();
                NavFrame.Content = _settingsPage;
                break;
        }
    }

    private void OnTrackSelected(object? sender, TrackItem track)
    {
        BottomTransportBar.UpdateTrackInfo(
            track.Title,
            $"{track.Artist} · {track.Album}",
            track.FormatBadge,
            track.CoverPath
        );

        if (!string.IsNullOrEmpty(track.FilePath))
        {
            AudioEngineService.Instance.Play(track.FilePath);
        }
    }
}
