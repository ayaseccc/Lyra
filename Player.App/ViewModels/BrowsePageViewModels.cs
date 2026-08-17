using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Player.Core.Library;

namespace Player.App.ViewModels;

/// <summary>专辑视图：一行一张专辑，点进去看曲目。</summary>
public sealed partial class AlbumPageViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _filterDebounce;
    private readonly Action<AlbumGroup> _openAlbum;
    private readonly Action<AlbumGroup> _playAlbum;

    private string _appliedFilter = string.Empty;

    public AlbumPageViewModel(IEnumerable<AlbumGroup> albums, Action<AlbumGroup> openAlbum, Action<AlbumGroup> playAlbum)
    {
        _openAlbum = openAlbum;
        _playAlbum = playAlbum;

        Items = new ObservableCollection<AlbumGroup>(albums);
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => _appliedFilter.Length == 0 ||
                           (o is AlbumGroup album &&
                            (album.Album.Contains(_appliedFilter, StringComparison.OrdinalIgnoreCase) ||
                             album.AlbumArtist.Contains(_appliedFilter, StringComparison.OrdinalIgnoreCase)));

        _filterDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _filterDebounce.Tick += OnFilterDebounceTick;
    }

    public string Title => "专辑";

    public ObservableCollection<AlbumGroup> Items { get; }

    public ICollectionView View { get; }

    public string Subtitle => $"{Items.Count} 张专辑";

    public bool IsEmpty => Items.Count == 0;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private AlbumGroup? _selectedAlbum;

    partial void OnFilterTextChanged(string value)
    {
        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    public void Open(AlbumGroup album) => _openAlbum(album);

    [RelayCommand]
    private void PlaySelected()
    {
        if (SelectedAlbum is not null) _playAlbum(SelectedAlbum);
    }

    private void OnFilterDebounceTick(object? sender, EventArgs e)
    {
        _filterDebounce.Stop();
        _appliedFilter = FilterText.Trim();
        View.Refresh();
    }

    public void Dispose()
    {
        _filterDebounce.Stop();
        _filterDebounce.Tick -= OnFilterDebounceTick;
    }
}

/// <summary>艺术家视图：一行一位艺术家，点进去看其全部曲目。</summary>
public sealed partial class ArtistPageViewModel : ObservableObject, IDisposable
{
    private readonly DispatcherTimer _filterDebounce;
    private readonly Action<ArtistGroup> _openArtist;
    private readonly Action<ArtistGroup> _playArtist;

    private string _appliedFilter = string.Empty;

    public ArtistPageViewModel(IEnumerable<ArtistGroup> artists, Action<ArtistGroup> openArtist, Action<ArtistGroup> playArtist)
    {
        _openArtist = openArtist;
        _playArtist = playArtist;

        Items = new ObservableCollection<ArtistGroup>(artists);
        View = CollectionViewSource.GetDefaultView(Items);
        View.Filter = o => _appliedFilter.Length == 0 ||
                           (o is ArtistGroup artist &&
                            artist.Name.Contains(_appliedFilter, StringComparison.OrdinalIgnoreCase));

        _filterDebounce = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _filterDebounce.Tick += OnFilterDebounceTick;
    }

    public string Title => "艺术家";

    public ObservableCollection<ArtistGroup> Items { get; }

    public ICollectionView View { get; }

    public string Subtitle => $"{Items.Count} 位艺术家";

    public bool IsEmpty => Items.Count == 0;

    [ObservableProperty]
    private string _filterText = string.Empty;

    [ObservableProperty]
    private ArtistGroup? _selectedArtist;

    partial void OnFilterTextChanged(string value)
    {
        _filterDebounce.Stop();
        _filterDebounce.Start();
    }

    public void Open(ArtistGroup artist) => _openArtist(artist);

    [RelayCommand]
    private void PlaySelected()
    {
        if (SelectedArtist is not null) _playArtist(SelectedArtist);
    }

    private void OnFilterDebounceTick(object? sender, EventArgs e)
    {
        _filterDebounce.Stop();
        _appliedFilter = FilterText.Trim();
        View.Refresh();
    }

    public void Dispose()
    {
        _filterDebounce.Stop();
        _filterDebounce.Tick -= OnFilterDebounceTick;
    }
}
