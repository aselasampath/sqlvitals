namespace SqlVitals.Desktop.ViewModels;

/// <summary>
/// A grid's bound rows, whatever their type (#184): the view asks whether the page has read them
/// yet. A <see cref="GridRows{T}"/> is there, empty, from the start, so its count can't say.
/// </summary>
public interface IGridRows
{
    /// <summary>The view model has filled them at least once, even with none.</summary>
    bool IsFilled { get; }
}
