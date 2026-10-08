namespace SqlVitals.Desktop.ViewModels;

/// <summary>What the top bar's Refresh and F5 call on the page on screen: its view model (#183).</summary>
public interface IRefreshable
{
    Task RefreshAsync();
}
