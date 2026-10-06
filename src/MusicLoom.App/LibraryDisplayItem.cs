using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml.Media;
using MusicLoom.Core.Scanning;
using Windows.UI;

namespace MusicLoom.App;

internal sealed class LibraryDisplayItem : INotifyPropertyChanged
{
    private ImageSource? _artwork;

    public LibraryDisplayItem(
        PlanEntry entry,
        string kind,
        string title,
        string artist,
        string album,
        string relativePath,
        string statusText)
    {
        Entry = entry;
        Kind = kind;
        Title = title;
        Artist = artist;
        Album = album;
        RelativePath = relativePath;
        StatusText = statusText;
    }

    public PlanEntry Entry { get; }
    public string Kind { get; }
    public string Title { get; }
    public string Artist { get; }
    public string Album { get; }
    public string RelativePath { get; }
    public string StatusText { get; }

    public string ArtistAlbum =>
        string.IsNullOrWhiteSpace(Album)
            ? Artist
            : $"{Artist} · {Album}";

    public string SearchText =>
        $"{Title}\n{Artist}\n{Album}\n{RelativePath}\n" +
        $"{Entry.SourcePath}\n{Entry.DestinationPath}\n{Entry.Detail}";

    public Brush StatusBrush => Kind switch
    {
        "synced" => new SolidColorBrush(Color.FromArgb(255, 16, 124, 65)),
        "error" => new SolidColorBrush(Color.FromArgb(255, 196, 43, 28)),
        _ => new SolidColorBrush(Color.FromArgb(255, 202, 116, 0)),
    };

    public string StatusGlyph => Kind switch
    {
        "synced" => "\uE73E",
        "error" => "\uEA39",
        _ => "\uE823",
    };

    public ImageSource? Artwork
    {
        get => _artwork;
        set
        {
            if (ReferenceEquals(_artwork, value))
            {
                return;
            }

            _artwork = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(propertyName));
}
