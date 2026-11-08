using System;
using System.Collections.ObjectModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Klang_WinUI.Models;

namespace Klang_WinUI.Pages;

public sealed partial class LibraryPage : Page
{
    public ObservableCollection<TrackItem> Tracks { get; } = new();
    private ObservableCollection<TrackItem> _allTracks = new();

    public event EventHandler<TrackItem>? TrackSelected;

    public LibraryPage()
    {
        InitializeComponent();
        TracksListView.ItemsSource = Tracks;
        UpdateEmptyState();
    }

    private void UpdateEmptyState()
    {
        bool isEmpty = Tracks.Count == 0;
        EmptyStatePanel.Visibility = isEmpty ? Visibility.Visible : Visibility.Collapsed;
        TracksListView.Visibility = isEmpty ? Visibility.Collapsed : Visibility.Visible;
        TrackCountText.Text = $"{Tracks.Count} 首曲目";
    }

    private void AddFolderBtn_Click(object sender, RoutedEventArgs e)
    {
        // 示例数据展示
        if (_allTracks.Count == 0)
        {
            var samples = new[]
            {
                new TrackItem { Index = 1, Title = "Arrival of the Birds", Artist = "The Cinematic Orchestra", Album = "The Crimson Wing", Duration = 162, FormatBadge = "FLAC 24bit" },
                new TrackItem { Index = 2, Title = "Experience", Artist = "Ludovico Einaudi", Album = "In a Time Lapse", Duration = 315, FormatBadge = "Hi-Res 96kHz" },
                new TrackItem { Index = 3, Title = "Cornfield Chase", Artist = "Hans Zimmer", Album = "Interstellar OST", Duration = 126, FormatBadge = "Master" },
                new TrackItem { Index = 4, Title = "Nuvole Bianche", Artist = "Ludovico Einaudi", Album = "Una Mattina", Duration = 357, FormatBadge = "FLAC" },
                new TrackItem { Index = 5, Title = "Time", Artist = "Hans Zimmer", Album = "Inception OST", Duration = 275, FormatBadge = "DSD 2.8MHz" }
            };

            foreach (var item in samples)
            {
                _allTracks.Add(item);
                Tracks.Add(item);
            }
            UpdateEmptyState();
        }
    }

    private void ScanBtn_Click(object sender, RoutedEventArgs e)
    {
        UpdateEmptyState();
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        var query = sender.Text.Trim().ToLowerInvariant();
        Tracks.Clear();

        var filtered = string.IsNullOrEmpty(query)
            ? _allTracks
            : _allTracks.Where(t =>
                t.Title.ToLowerInvariant().Contains(query) ||
                t.Artist.ToLowerInvariant().Contains(query) ||
                t.Album.ToLowerInvariant().Contains(query));

        foreach (var track in filtered)
        {
            Tracks.Add(track);
        }

        UpdateEmptyState();
    }

    private void SortComboBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SortComboBox.SelectedIndex == 1) // 按歌名
        {
            var sorted = Tracks.OrderBy(t => t.Title).ToList();
            Tracks.Clear();
            foreach (var s in sorted) Tracks.Add(s);
        }
        else if (SortComboBox.SelectedIndex == 2) // 按歌手
        {
            var sorted = Tracks.OrderBy(t => t.Artist).ToList();
            Tracks.Clear();
            foreach (var s in sorted) Tracks.Add(s);
        }
        else if (SortComboBox.SelectedIndex == 3) // 按专辑
        {
            var sorted = Tracks.OrderBy(t => t.Album).ToList();
            Tracks.Clear();
            foreach (var s in sorted) Tracks.Add(s);
        }
        else if (SortComboBox.SelectedIndex == 4) // 按时长
        {
            var sorted = Tracks.OrderByDescending(t => t.Duration).ToList();
            Tracks.Clear();
            foreach (var s in sorted) Tracks.Add(s);
        }
    }

    private void TracksListView_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is TrackItem track)
        {
            TrackSelected?.Invoke(this, track);
        }
    }
}
