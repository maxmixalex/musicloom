using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MusicLoom.App;

/// <summary>
/// Native WinUI accent-color playback affordance. Only an active, realized
/// row runs the two small, seamless wave animations.
/// </summary>
public sealed partial class PlayingWaveform : UserControl
{
    private bool _loaded;

    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(
            nameof(IsActive),
            typeof(bool),
            typeof(PlayingWaveform),
            new PropertyMetadata(false, OnIsActiveChanged));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public PlayingWaveform()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _loaded = true;
            UpdateAnimation();
        };
        Unloaded += (_, _) =>
        {
            _loaded = false;
            WaveMotion.Stop();
            EchoMotion.Stop();
        };
    }

    private static void OnIsActiveChanged(
        DependencyObject sender,
        DependencyPropertyChangedEventArgs args)
    {
        ((PlayingWaveform)sender).UpdateAnimation();
    }

    private void UpdateAnimation()
    {
        Visibility = IsActive
            ? Visibility.Visible
            : Visibility.Collapsed;
        WaveMotion.Stop();
        EchoMotion.Stop();

        if (_loaded && IsActive)
        {
            WaveMotion.Begin();
            EchoMotion.Begin();
        }
    }
}
