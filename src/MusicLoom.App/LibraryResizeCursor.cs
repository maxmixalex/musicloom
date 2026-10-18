using System.Reflection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;

namespace MusicLoom.App;

/// <summary>
/// WinUI 3 Thumb is sealed and UIElement.ProtectedCursor is protected.
/// Set it on the existing Thumb via the protected property once it has
/// loaded; keep the frame-throttled Thumb drag logic intact.
/// </summary>
internal static class LibraryResizeCursor
{
    private static readonly PropertyInfo? ProtectedCursorProperty =
        typeof(UIElement).GetProperty(
            "ProtectedCursor",
            BindingFlags.Instance | BindingFlags.NonPublic);

    public static void Apply(UIElement thumb)
    {
        if (ProtectedCursorProperty is null)
        {
            AppDiagnostics.Log("Native resize cursor property unavailable.");
            return;
        }

        try
        {
            ProtectedCursorProperty.SetValue(thumb,
                InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast));
        }
        catch (Exception error)
        {
            AppDiagnostics.LogException(
                "Could not set horizontal resize cursor.", error);
        }
    }
}
