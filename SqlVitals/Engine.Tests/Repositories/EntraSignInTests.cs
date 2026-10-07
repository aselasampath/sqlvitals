using Microsoft.Data.SqlClient;

namespace SqlVitals.Engine.Tests.Repositories;

public class EntraSignInTests
{
    [Fact]
    public void EntraMfa_HasASignInProvider()
    {
        // Since SqlClient 7 the Entra sign-in lives in Microsoft.Data.SqlClient.Extensions.Azure (#163).
        // Without that package a connection with Entra MFA fails when it opens, not when it builds.
        Assert.NotNull(SqlAuthenticationProvider.GetProvider(SqlAuthenticationMethod.ActiveDirectoryInteractive));
    }
}
