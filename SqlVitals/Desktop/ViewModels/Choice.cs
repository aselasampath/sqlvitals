namespace SqlVitals.Desktop.ViewModels;

/// <summary>
/// One choice of a drop-down: the value a view model keeps, and what the box shows for it. A box
/// binds <c>ItemsSource</c> to the choices, <c>DisplayMemberPath="Text"</c>,
/// <c>SelectedValuePath="Value"</c> and <c>SelectedValue</c> to the view model's value.
/// </summary>
public sealed record Choice<T>(T Value, string Text)
{
    public override string ToString() => Text;
}
