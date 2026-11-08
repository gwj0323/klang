using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using WinRT.Interop;
using Klang_WinUI.Models;
using Klang_WinUI.Services;

namespace Klang_WinUI.Controls;

public sealed partial class NavigationPane : UserControl
{
    public ObservableCollection<TrackItem> DisplayTracks { get; } = new();
    private readonly List<TrackItem> _allTracks = new();
    private string _currentTab = "all";
    private string _currentSort = "default";

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
        ApplyFilterAndSort(_currentSort);
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
            IntPtr hwnd = IntPtr.Zero;
            if (App.MainWindowInstance != null)
            {
                hwnd = WindowNative.GetWindowHandle(App.MainWindowInstance);
            }

            string? folder = FolderPickerHelper.PickFolder(hwnd);
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder))
            {
                AddFolderBtn.IsEnabled = false;

                var scanned = await LibraryService.Instance.ScanFolderAsync(folder);
                LoadTracks(scanned);

                AddFolderBtn.IsEnabled = true;

                if (DisplayTracks.Count > 0)
                {
                    TracksListView.SelectedIndex = 0;
                    TrackSelected?.Invoke(this, DisplayTracks[0]);
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Pick folder error: {ex.Message}");
            AddFolderBtn.IsEnabled = true;
        }
    }

    private void TabBtn_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string tag)
        {
            _currentTab = tag;
            UpdateTabStyles(tag);
            ApplyFilterAndSort(_currentSort);
        }
    }

    private void UpdateTabStyles(string activeTag)
    {
        try
        {
            var accentStyle = Application.Current.Resources["AccentButtonStyle"] as Style;
            var subtleStyle = Application.Current.Resources["SubtleButtonStyle"] as Style;

            if (accentStyle != null && subtleStyle != null)
            {
                TabAllBtn.Style = (activeTag == "all") ? accentStyle : subtleStyle;
                TabFavBtn.Style = (activeTag == "fav") ? accentStyle : subtleStyle;
                TabPlaylistsBtn.Style = (activeTag == "playlists") ? accentStyle : subtleStyle;
                TabNeteaseBtn.Style = (activeTag == "netease") ? accentStyle : subtleStyle;
            }
        }
        catch { }
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        ApplyFilterAndSort(_currentSort);
    }

    private void SortItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuFlyoutItem item && item.Tag is string sortKey)
        {
            _currentSort = sortKey;
            SortDropDownBtn.Content = $"{item.Text} ▾";
            ApplyFilterAndSort(sortKey);
        }
    }

    private void ApplyFilterAndSort(string sortKey = "default")
    {
        var query = SearchBox.Text.Trim().ToLowerInvariant();

        IEnumerable<TrackItem> filtered = _allTracks;

        if (_currentTab == "fav")
        {
            filtered = filtered.Where(t => t.IsFavorite);
        }

        if (!string.IsNullOrEmpty(query))
        {
            filtered = filtered.Where(t =>
                t.Title.ToLowerInvariant().Contains(query) ||
                t.Artist.ToLowerInvariant().Contains(query) ||
                t.Album.ToLowerInvariant().Contains(query));
        }

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
