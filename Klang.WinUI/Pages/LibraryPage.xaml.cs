using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Klang_WinUI.Models;
using Klang_WinUI.Services;

namespace Klang_WinUI.Pages;

public sealed partial class LibraryPage : Page
{
    public ObservableCollection<TrackItem> Tracks { get; } = new();
    private ObservableCollection<TrackItem> _allTracks = new();
    private string? _lastScannedFolder;

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
                _lastScannedFolder = folder.Path;
                await ScanAndLoadFolder(folder.Path);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"FolderPicker error: {ex.Message}");
        }
    }

    private async void ScanBtn_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrEmpty(_lastScannedFolder) && Directory.Exists(_lastScannedFolder))
        {
            await ScanAndLoadFolder(_lastScannedFolder);
        }
        else
        {
            // 如果未选择过文件夹，尝试扫描系统「音乐」目录
            string myMusic = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (Directory.Exists(myMusic))
            {
                _lastScannedFolder = myMusic;
                await ScanAndLoadFolder(myMusic);
            }
        }
    }

    private async System.Threading.Tasks.Task ScanAndLoadFolder(string folderPath)
    {
        ScanBtn.IsEnabled = false;
        AddFolderBtn.IsEnabled = false;

        var scanned = await LibraryService.Instance.ScanFolderAsync(folderPath);

        _allTracks.Clear();
        Tracks.Clear();

        foreach (var t in scanned)
        {
            _allTracks.Add(t);
            Tracks.Add(t);
        }

        UpdateEmptyState();

        ScanBtn.IsEnabled = true;
        AddFolderBtn.IsEnabled = true;
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
