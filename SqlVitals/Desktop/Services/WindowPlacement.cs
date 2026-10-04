using System.Windows;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// Where and how the window is (#148): its state, frame and bounds, so focus mode can make it full
/// screen and put it back exactly as it was.
/// </summary>
/// <param name="Bounds">Its size and place when not maximized; empty when not known (a window never shown).</param>
public sealed record WindowPlacement(WindowState State, WindowStyle Style, ResizeMode ResizeMode, Rect Bounds)
{
    /// <summary>The window as it is now; a minimized one as it was before.</summary>
    public static WindowPlacement Capture(Window window) => new(
        window.WindowState == WindowState.Minimized ? WindowState.Normal : window.WindowState,
        window.WindowStyle,
        window.ResizeMode,
        window.WindowState == WindowState.Normal ? new Rect(window.Left, window.Top, window.Width, window.Height) : window.RestoreBounds);

    /// <summary>Full screen: no frame, maximized over the taskbar; it keeps the bounds to come back to.</summary>
    public WindowPlacement FullScreen() => this with { State = WindowState.Maximized, Style = WindowStyle.None, ResizeMode = ResizeMode.NoResize };

    public bool IsFullScreen => State == WindowState.Maximized && Style == WindowStyle.None;

    /// <summary>Puts the window there.</summary>
    public void ApplyTo(Window window)
    {
        // Restored first: Windows only re-lays out a maximized window's frame when it is maximized
        // again, and a frameless one then covers the taskbar.
        window.WindowState = WindowState.Normal;
        window.WindowStyle = Style;
        window.ResizeMode  = ResizeMode;
        if (!Bounds.IsEmpty && double.IsFinite(Bounds.Width) && double.IsFinite(Bounds.Height))
        {
            window.Left   = Bounds.Left;
            window.Top    = Bounds.Top;
            window.Width  = Bounds.Width;
            window.Height = Bounds.Height;
        }
        window.WindowState = State;
    }
}
