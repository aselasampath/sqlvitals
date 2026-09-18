namespace SqlVitals.Desktop.Pages;

/// <summary>All pages implement this so MainWindow can trigger refresh.</summary>
public interface IRefreshable
{
    System.Threading.Tasks.Task RefreshAsync();
}
