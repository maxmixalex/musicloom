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
    private string _kind;
    private string _statusText;
    private bool _isPlaying;
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
        _kind = kind;
        Title = title;
        Artist = artist;
        Album = album;
        RelativePath = relativePath;
        _statusText = statusText;

        ApplyLongTextMode(
            LongTextModes.Hover);
    }

    public PlanEntry Entry { get; private set; }

    public string Kind => _kind;
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

    public bool IsPlaying =>
        _isPlaying;

    // Selection remains a ListView concern. Playback and sync problems are
    // independent, so neither changes the selection background or indicator.
    public Visibility PlayingIndicatorVisibility =>
        _isPlaying
            ? Visibility.Visible
            : Visibility.Collapsed;

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
        "warning" =>
            new SolidColorBrush(
                Color.FromArgb(
                    255, 202, 116, 0)),
        "syncing" =>
            new SolidColorBrush(
                Color.FromArgb(
                    255, 0, 120, 212)),
        _ =>
            new SolidColorBrush(
                Color.FromArgb(
                    255, 202, 116, 0)),
    };

    // Diagnostic colors are meaningful; ordinary rows retain the
    // native WinUI text-fill brushes, including the dim subtitle.
    public Brush TitleForeground => Kind switch
    {
        "warning" or "error" => StatusBrush,
        _ => ThemeBrush("TextFillColorPrimaryBrush",
            Color.FromArgb(255, 32, 32, 32)),
    };

    public Brush SecondaryForeground => Kind switch
    {
        "warning" =>
            new SolidColorBrush(Color.FromArgb(158, 202, 116, 0)),
        "error" =>
            new SolidColorBrush(Color.FromArgb(158, 196, 43, 28)),
        _ => ThemeBrush("TextFillColorSecondaryBrush",
            Color.FromArgb(158, 90, 90, 90)),
    };

    private static Brush ThemeBrush(string key, Color fallback)
    {
        if (Application.Current?.Resources is ResourceDictionary resources
            && resources.TryGetValue(key, out object? value)
            && value is Brush brush)
        {
            return brush;
        }

        return new SolidColorBrush(fallback);
    }

    public string StatusGlyph => Kind switch
    {
        "synced" => "\uE73E",
        "error" => "\uEA39",
        "warning" => "\uE7BA",
        "syncing" => "\uE895",
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

    public void UpdateEntry(
        PlanEntry entry)
    {
        Entry = entry;
        OnPropertyChanged(
            nameof(Entry));
    }

    public void SetStatus(
        string kind,
        string statusText)
    {
        bool kindChanged =
            !string.Equals(
                _kind,
                kind,
                StringComparison.Ordinal);

        _kind = kind;
        StatusText = statusText;

        if (kindChanged)
        {
            OnPropertyChanged(
                nameof(Kind));
            OnPropertyChanged(
                nameof(StatusBrush));
            OnPropertyChanged(
                nameof(StatusGlyph));
            OnPropertyChanged(nameof(TitleForeground));
            OnPropertyChanged(nameof(SecondaryForeground));
        }
    }

    public void SetPlaying(
        bool isPlaying)
    {
        if (_isPlaying == isPlaying)
        {
            return;
        }

        _isPlaying = isPlaying;
        OnPropertyChanged(
            nameof(IsPlaying));
        OnPropertyChanged(
            nameof(PlayingIndicatorVisibility));
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
