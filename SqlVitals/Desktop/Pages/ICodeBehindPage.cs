namespace SqlVitals.Desktop.Pages;

/// <summary>
/// A page whose code-behind still reads (#183). Its view model, a
/// <see cref="ViewModels.CodeBehindViewModel"/>, calls <see cref="RefreshAsync"/> to load it. A page
/// leaves this list when its wave of #180 moves the reading to its view model;
/// <c>MvvmGuardTests</c> holds the list.
/// </summary>
public interface ICodeBehindPage
{
    Task RefreshAsync();
}
