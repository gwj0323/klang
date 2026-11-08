using System;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;
using Klang_WinUI.Models;
using Klang_WinUI.Pages;

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

        // 设置合理的默认窗口尺寸 (1120 x 740)
        AppWindow.Resize(new SizeInt32(1120, 740));

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
        BottomTransportBar.UpdateTrackInfo(track.Title, $"{track.Artist} · {track.Album}", track.FormatBadge);
        BottomTransportBar.UpdateProgress(0, track.Duration);
        BottomTransportBar.IsPlaying = true;
    }
}
