using System.Reflection;
using Microsoft.Data.SqlClient;
using SqlVitals.Desktop.Services;

namespace SqlVitals.Desktop.Tests;

public class EntraSignInTests
{
    [Fact]
    public void Install_GivesTheSignInTheAppsWindow()
    {
        // Without a parent window Windows' account broker refuses to prompt: "window_handle_required".
        var before = EntraSignIn.MainWindow;
        try
        {
            EntraSignIn.MainWindow = 0x1234;
            Assert.True(EntraSignIn.Install());

            var provider = Assert.IsType<ActiveDirectoryAuthenticationProvider>(
                SqlAuthenticationProvider.GetProvider(SqlAuthenticationMethod.ActiveDirectoryInteractive));
            // What MSAL asks the provider for when it opens the prompt.
            var parent = typeof(ActiveDirectoryAuthenticationProvider)
                .GetMethod("GetParentWindow", BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)!
                .Invoke(provider, null);

            // A test host has no window in front of its own, so the main window is the parent.
            Assert.Equal(EntraSignIn.ParentWindow(), (nint)parent!);
            Assert.NotEqual(0, (nint)parent!);
        }
        finally
        {
            EntraSignIn.MainWindow = before;
        }
    }

    [Theory]
    [InlineData(0x10, true,  0x20, 0x10)]   // a dialog of the app's in front: the prompt belongs to it
    [InlineData(0x10, false, 0x20, 0x20)]   // another app in front: the main window
    [InlineData(0x10, false, 0,    0x10)]   // no main window yet: whatever is in front
    public void ChooseParent_PrefersTheAppsWindowInFront(int front, bool frontIsOurs, int main, int expected) =>
        Assert.Equal(expected, EntraSignIn.ChooseParent(front, frontIsOurs, main));
}
