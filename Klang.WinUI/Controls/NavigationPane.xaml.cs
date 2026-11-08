using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Klang_WinUI.Models;
using Klang_WinUI.Services;

namespace Klang_WinUI.Controls;

public sealed partial class NavigationPane : UserControl
{
    public ObservableCollection<TrackItem> DisplayTracks { get; } = new();
    private readonly List<TrackItem> _allTracks = new();
    private string _currentTab = "all";
    private string? _lastFolder;

    public event EventHandler<TrackItem>? TrackSelected;

    public NavigationPane()
    {
        InitializeComponent();
        TracksListView.ItemsSource = DisplayTracks;
        UpdateEmptyState();
    }

    public void LoadTracks(IEnumerable<TrackItem> tracks)
    {
        _allTracks.Clear();
        _allTracks.AddRange(tracks);
        ApplyFilterAndSort();
    }

    private void UpdateEmptyState()
    {
        bool isEmpty = DisplayTracks.Count == 0;
        EmptyStatePanel.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        TracksListView.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
        TrackCountText.Text = $"共 {DisplayTracks.Count} 首";
    }

    private async void AddFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folderPicker = new FolderPicker();
            folderPicker.SuggestedStartLocation = PickerLocationId.MusicLibrary;
            folderPicker.FileTypeFilter.Add("*");

            if (App.MainWindowInstance != null)
            {
                IntPtr hwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
                InitializeWithWindow.Initialize(folderPicker, hwnd);
            }

            var folder = await folderPicker.PickSingleFolderAsync();
            if (folder != null)
            {
                _lastFolder = folder.Path;
                AddFolderBtn.IsEnabled = false;

                var scanned = await LibraryService.Instance.ScanFolderAsync(folder.Path);
                LoadTracks(scanned);

                AddFolderBtn.IsEnabled = true;

                // 如果扫描出了曲目且当前无播放，默认选中第一首
                if (DisplayTracks.Count > 0)
                {
                    TracksListView.SelectedIndex = 0;
                    TrackSelected?.Invoke(this, DisplayTracks[0]);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Picker error: {ex.Message}");
            AddFolderBtn.IsEnabled = true;
        }
    }

    private void TabBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            _currentTab = tag;

            // 切换按钮高亮样式
            TabAllBtn.Style = (tag == "all") ? (Style)Resources["AccentButtonStyle"] : (Style)Resources["SubtleButtonStyle"];
            TabFavBtn.Style = (tag == "fav") ? (Style)Resources["AccentButtonStyle"] : (Style)Resources["SubtleButtonStyle"];
            TabPlaylistsBtn.Style = (tag == "playlists") ? (Style)Resources["AccentButtonStyle"] : (Style)Resources["SubtleButtonStyle"];
            TabNeteaseBtn.Style = (tag == "netease") ? (Style)Resources["AccentButtonStyle"] : (Style)Resources["SubtleButtonStyle"];

            ApplyFilterAndSort();
        }
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        ApplyFilterAndSort();
    }

    private void SortItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string sortKey)
        {
            SortDropDownBtn.Content = $"{item.Text} ▾";
            ApplyFilterAndSort(sortKey);
        }
    }

    private void ApplyFilterAndSort(string sortKey = "default")
    {
        var query = SearchBox.Text.Trim().ToLowerInvariant();

        IEnumerable<TrackItem> filtered = _allTracks;

        // 标签页过滤
        if (_currentTab == "fav")
        {
            filtered = filtered.Where(t => t.IsFavorite);
        }

        // 搜索关键词过滤
        if (!string.IsNullOrEmpty(query))
        {
            filtered = filtered.Where(t =>
                t.Title.ToLowerInvariant().Contains(query) ||
                t.Artist.ToLowerInvariant().Contains(query) ||
                t.Album.ToLowerInvariant().Contains(query));
        }

        // 排序规则
        filtered = sortKey switch
        {
            "title" => filtered.OrderBy(t => t.Title),
            "artist" => filtered.OrderBy(t => t.Artist),
            "album" => filtered.OrderBy(t => t.Album),
            "duration" => filtered.OrderByDescending(t => t.Duration),
            _ => filtered.OrderBy(t => t.Index)
        };

        DisplayTracks.Clear();
        foreach (var track in filtered)
        {
            DisplayTracks.Add(track);
        }

        UpdateEmptyState();
    }

    private void TracksListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TrackItem track)
        {
            TrackSelected?.Invoke(this, track);
        }
    }

    private void TracksListView_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        if (TracksListView.SelectedItem is TrackItem track)
        {
            TrackSelected?.Invoke(this, track);
        }
    }
}
