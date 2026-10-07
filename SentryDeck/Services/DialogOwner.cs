using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;

namespace SentryDeck;

/// <summary>
/// Opens the app's dialogs over the main window.
/// Without an owner, Windows gives a dialog to whichever window is active, or to WPF's hidden window at the top left of the primary monitor when there is none.
/// A dialog opened from the clip list's context menu, or while another app was in front, then opened on the primary monitor, away from Sentry Deck on another screen, and took the focus there.
/// </summary>
internal static class DialogOwner
{
    public static bool? ShowDialog(CommonDialog dialog) =>
        MainWindow() is { } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog();

    public static MessageBoxResult ShowMessageBox(
        string messageBoxText,
        string caption,
        MessageBoxButton button,
        MessageBoxImage icon,
        MessageBoxResult defaultResult) =>
        MainWindow() is { } owner
            ? MessageBox.Show(owner, messageBoxText, caption, button, icon, defaultResult)
            : MessageBox.Show(messageBoxText, caption, button, icon, defaultResult);

    /// <summary>
    /// The window, or null when it can't own a dialog yet.
    /// A file dialog throws when its owner has no window handle, so a window that hasn't opened (or has closed) falls back to no owner rather than failing to ask.
    /// </summary>
    internal static Window Usable(Window window) =>
        window is not null && new WindowInteropHelper(window).Handle != IntPtr.Zero ? window : null;

    private static Window MainWindow() => Usable(Application.Current?.MainWindow);
}
