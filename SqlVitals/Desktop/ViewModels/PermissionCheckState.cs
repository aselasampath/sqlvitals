namespace SqlVitals.Desktop.ViewModels;

/// <summary>Where a permission check is.</summary>
public enum PermissionCheckState
{
    NotRun,
    Checking,

    /// <summary>It signed in and read the permissions: <see cref="PermissionCheckViewModel.Report"/> holds them.</summary>
    Done,

    /// <summary>It couldn't sign in, or the server didn't answer: <see cref="PermissionCheckViewModel.Status"/> says why.</summary>
    Failed,
}
