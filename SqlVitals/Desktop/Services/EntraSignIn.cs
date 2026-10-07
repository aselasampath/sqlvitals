using System.Runtime.InteropServices;
using Microsoft.Data.SqlClient;

namespace SqlVitals.Desktop.Services;

/// <summary>
/// Gives the Entra MFA sign-in a window to belong to. Since SqlClient 7 the sign-in is Windows'
/// account broker (WAM), which refuses to prompt without a parent window ("window_handle_required");
/// left to itself, the provider only looks for a console window, and the app has none.
/// </summary>
public static class EntraSignIn
{
    private static nint _mainWindow;

    /// <summary>The main window's handle, the parent when no window of the app's is in front.</summary>
    public static nint MainWindow
    {
        get => Interlocked.CompareExchange(ref _mainWindow, 0, 0);
        set => Interlocked.Exchange(ref _mainWindow, value);
    }

    /// <summary>Hands the parent window to the provider the app's Entra MFA connections sign in with.</summary>
    /// <returns>False when that provider isn't SqlClient's own, which then prompts as it likes.</returns>
    public static bool Install()
    {
        if (SqlAuthenticationProvider.GetProvider(SqlAuthenticationMethod.ActiveDirectoryInteractive)
            is not ActiveDirectoryAuthenticationProvider provider)
            return false;
        provider.SetParentActivityOrWindowFunc(() => ParentWindow());
        return true;
    }

    /// <summary>
    /// The window in front when it is the app's (a dialog, say), else the main window. MSAL asks from
    /// whichever thread opens the connection, so this reads handles only, never a WPF object.
    /// </summary>
    internal static nint ParentWindow()
    {
        var front = GetForegroundWindow();
        var ours  = front != 0 && GetWindowThreadProcessId(front, out var pid) != 0 && pid == Environment.ProcessId;
        return ChooseParent(front, ours, MainWindow);
    }

    internal static nint ChooseParent(nint front, bool frontIsOurs, nint main) =>
        frontIsOurs ? front : main != 0 ? main : front;

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hWnd, out int processId);
}
