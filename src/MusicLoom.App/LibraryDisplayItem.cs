using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using MusicLoom.Core.Configuration;
using MusicLoom.Core.Scanning;
using Windows.UI;

namespace MusicLoom.App;

internal sealed class LibraryDisplayItem : INotifyPropertyChanged
{
    private ImageSource? _artwork;
    private string _statusText;
    private TextWrapping _rowTextWrapping =
        TextWrapping.NoWrap;
    private TextTrimming _rowTextTrimming =
        TextTrimming.CharacterEllipsis;
    private int _rowMaxLines = 1;
    private string? _titleToolTip;
    private string? _subtitleToolTip;

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
        _statusText = statusText;

        ApplyLongTextMode(
            LongTextModes.Hover);
    }

    public PlanEntry Entry { get; }
    public string Kind { get; }
    public string Title { get; }
    public string Artist { get; }
    public string Album { get; }
    public string RelativePath { get; }

    public string StatusText
    {
        get => _statusText;
        set
        {
            if (string.Equals(
                    _statusText,
                    value,
                    StringComparison.Ordinal))
            {
                return;
            }

            _statusText = value;
            OnPropertyChanged();
        }
    }

    public string ArtistAlbum =>
        string.IsNullOrWhiteSpace(Album)
            ? Artist
            : $"{Artist} · {Album}";

    public string SearchText =>
        $"{Title}\n{Artist}\n{Album}\n{RelativePath}\n" +
        $"{Entry.SourcePath}\n{Entry.DestinationPath}\n{Entry.Detail}";

    public TextWrapping RowTextWrapping =>
        _rowTextWrapping;

    public TextTrimming RowTextTrimming =>
        _rowTextTrimming;

    public int RowMaxLines =>
        _rowMaxLines;

    public string? TitleToolTip =>
        _titleToolTip;

    public string? SubtitleToolTip =>
        _subtitleToolTip;

    public Brush StatusBrush => Kind switch
    {
        "synced" =>
            new SolidColorBrush(
                Color.FromArgb(
                    255, 16, 124, 65)),
        "error" =>
            new SolidColorBrush(
                Color.FromArgb(
                    255, 196, 43, 28)),
        _ =>
            new SolidColorBrush(
                Color.FromArgb(
                    255, 202, 116, 0)),
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
            if (ReferenceEquals(
                    _artwork,
                    value))
            {
                return;
            }

            _artwork = value;
            OnPropertyChanged();
        }
    }

    public void ApplyLongTextMode(
        string mode)
    {
        string normalized =
            LongTextModes.Normalize(
                mode);

        if (normalized
            == LongTextModes.Wrap)
        {
            _rowTextWrapping =
                TextWrapping.WrapWholeWords;
            _rowTextTrimming =
                TextTrimming.None;
            _rowMaxLines = 2;
            _titleToolTip = null;
            _subtitleToolTip = null;
        }
        else
        {
            // Running a marquee in every virtualized list row would waste
            // resources and look noisy. The selected track receives the real
            // auto-scroll treatment in the detail pane; rows remain compact.
            _rowTextWrapping =
                TextWrapping.NoWrap;
            _rowTextTrimming =
                TextTrimming.CharacterEllipsis;
            _rowMaxLines = 1;
            _titleToolTip = Title;
            _subtitleToolTip = ArtistAlbum;
        }

        OnPropertyChanged(
            nameof(RowTextWrapping));
        OnPropertyChanged(
            nameof(RowTextTrimming));
        OnPropertyChanged(
            nameof(RowMaxLines));
        OnPropertyChanged(
            nameof(TitleToolTip));
        OnPropertyChanged(
            nameof(SubtitleToolTip));
    }

    public event PropertyChangedEventHandler?
        PropertyChanged;

    private void OnPropertyChanged(
        [CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(
            this,
            new PropertyChangedEventArgs(
                propertyName));
}
