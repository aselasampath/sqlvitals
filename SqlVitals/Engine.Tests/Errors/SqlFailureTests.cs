using SqlVitals.Engine.Errors;

namespace SqlVitals.Engine.Tests.Errors;

/// <summary>
/// A failed query classified by its error number (#167): what permission is missing and the GRANT
/// that adds it, a database without a user, a lock timeout, a timeout. Messages as SQL Server words them.
/// </summary>
public sealed class SqlFailureTests
{
    [Fact]
    public void ViewServerState_Denied_IsAServerPermission_GrantedInMaster()
    {
        var failure = SqlFailure.Classify(300, "VIEW SERVER STATE permission was denied on object 'server', database 'master'.");

        Assert.Equal(SqlFailureKind.MissingPermission, failure.Kind);
        Assert.Equal(new PermissionNeeded("VIEW SERVER STATE", PermissionScope.Server), failure.Permission);
        Assert.True(failure.IsPermission);
        Assert.Equal("The login lacks VIEW SERVER STATE.", failure.Explanation);
        Assert.Equal("USE [master];\nGRANT VIEW SERVER STATE TO [CONTOSO\\svc_vitals];", failure.FixScript(@"CONTOSO\svc_vitals"));
    }

    [Fact]
    public void ViewServerPerformanceState_On2022_IsNamedAsTheServerSaysIt()
    {
        var failure = SqlFailure.Classify(300, "VIEW SERVER PERFORMANCE STATE permission was denied on object 'server', database 'master'.");

        Assert.Equal("VIEW SERVER PERFORMANCE STATE", failure.Permission!.Name);
        Assert.Equal("USE [master];\nGRANT VIEW SERVER PERFORMANCE STATE TO [vitals];", failure.FixScript("vitals"));
    }

    [Fact]
    public void ADatabasePermission_IsGrantedInThatDatabase()
    {
        var onPrem = SqlFailure.Classify(300, "VIEW DATABASE STATE permission was denied on object 'Sales', database 'Sales'.");
        var azure  = SqlFailure.Classify(262, "VIEW DATABASE STATE permission denied in database 'Sales'.");

        foreach (var failure in new[] { onPrem, azure })
        {
            Assert.Equal(new PermissionNeeded("VIEW DATABASE STATE", PermissionScope.Database, "Sales"), failure.Permission);
            Assert.Equal("The login lacks VIEW DATABASE STATE in Sales.", failure.Explanation);
            Assert.Equal("USE [Sales];\nGRANT VIEW DATABASE STATE TO [vitals];", failure.FixScript("vitals"));
        }
    }

    [Fact]
    public void ASelectDenied_OnAnMsdbTable_IsGrantedOnThatTable()
    {
        var failure = SqlFailure.Classify(229, "The SELECT permission was denied on the object 'sysjobs', database 'msdb', schema 'dbo'.");

        Assert.Equal(new PermissionNeeded("SELECT", PermissionScope.OnObject, "msdb", "dbo", "sysjobs"), failure.Permission);
        Assert.Equal("The login lacks SELECT on msdb.dbo.sysjobs.", failure.Explanation);
        Assert.Equal("USE [msdb];\nGRANT SELECT ON [dbo].[sysjobs] TO [vitals];", failure.FixScript("vitals"));
    }

    [Fact]
    public void NoUserInTheDatabase_IsFixedWithAUser()
    {
        var failure = SqlFailure.Classify(916,
            "The server principal \"vitals\" is not able to access the database \"msdb\" under the current security context.");

        Assert.Equal(SqlFailureKind.NoDatabaseAccess, failure.Kind);
        Assert.True(failure.IsPermission);
        Assert.Equal("msdb", failure.Database);
        Assert.Equal("The login has no user in msdb.", failure.Explanation);
        Assert.Equal("USE [msdb];\nCREATE USER [vitals] FOR LOGIN [vitals];", failure.FixScript("vitals"));
    }

    [Fact]
    public void Error297_DoesntNameThePermission_SoTheLikelyOneIsSaidToBeLikely()
    {
        var failure = SqlFailure.Classify(297, "The user does not have permission to perform this action.");

        Assert.Equal(new PermissionNeeded("VIEW SERVER STATE", PermissionScope.Server, Inferred: true), failure.Permission);
        Assert.Equal("The login lacks VIEW SERVER STATE (most likely: the server didn't say which).", failure.Explanation);
        Assert.NotNull(failure.FixScript("vitals"));
    }

    [Fact]
    public void APermissionMessage_WordedOtherwise_IsStillAPermission_WithoutAScript()
    {
        var failure = SqlFailure.Classify(300, "Some future wording.");

        Assert.Equal(SqlFailureKind.MissingPermission, failure.Kind);
        Assert.Null(failure.Permission);
        Assert.Equal("The login lacks a permission this needs.", failure.Explanation);
        Assert.Null(failure.FixScript("vitals"));
    }

    [Theory]
    [InlineData(1222, SqlFailureKind.LockTimeout)]
    [InlineData(-2, SqlFailureKind.Timeout)]
    [InlineData(208, SqlFailureKind.Other)]
    [InlineData(18456, SqlFailureKind.Other)]
    public void OtherErrors_AreToldApartByNumber(int number, SqlFailureKind kind)
    {
        var failure = SqlFailure.Classify(number, "whatever the server said");

        Assert.Equal(kind, failure.Kind);
        Assert.False(failure.IsPermission);
        Assert.Null(failure.FixScript("vitals"));
    }

    [Fact]
    public void TheScript_NamesAnUnknownLogin_AsAPlaceholder_AndEscapesBrackets()
    {
        var failure = SqlFailure.Classify(300, "VIEW SERVER STATE permission was denied on object 'server', database 'master'.");

        Assert.Equal("USE [master];\nGRANT VIEW SERVER STATE TO [<SqlVitals login>];", failure.FixScript(null));
        Assert.Equal("USE [master];\nGRANT VIEW SERVER STATE TO [odd]]name];", failure.FixScript("odd]name"));
    }

    [Fact]
    public void AWaitStatsException_CarriesItsFailure_AndTheScriptForItsLogin()
    {
        var failure = SqlFailure.Classify(300, "VIEW SERVER STATE permission was denied on object 'server', database 'master'.");
        var ex = new WaitStatsException("WaitsRepository", "GetTopWaitTypesAsync", new InvalidOperationException("denied"), failure, "vitals");

        Assert.Same(failure, ex.Failure);
        Assert.Equal(300, ex.SqlErrorNumber);
        Assert.Equal("USE [master];\nGRANT VIEW SERVER STATE TO [vitals];", ex.FixScript);
    }

    [Fact]
    public void ANonSqlError_IsNotClassified() =>
        Assert.Same(SqlFailure.None, new WaitStatsException("WaitsRepository", "GetTopWaitTypesAsync", new InvalidOperationException("boom")).Failure);
}
