using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace MusicLoom.App;

/// <summary>
/// A wider drag hit target with a real WinUI horizontal-resize pointer.
/// ProtectedCursor must be set by a UIElement subclass after Loaded.
/// </summary>
public sealed class LibraryResizeThumb : Thumb
{
    public LibraryResizeThumb()
    {
        Loaded += SetResizeCursorOnLoaded;
    }

    private void SetResizeCursorOnLoaded(object sender, RoutedEventArgs args)
    {
        ProtectedCursor = InputSystemCursor.Create(
            InputSystemCursorShape.SizeWestEast);
    }
}
