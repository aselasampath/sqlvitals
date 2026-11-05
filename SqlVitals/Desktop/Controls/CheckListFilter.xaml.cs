using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace SqlVitals.Desktop.Controls;

/// <summary>
/// A multi-select filter box (#237), bound to a <see cref="ViewModels.MultiFilterChoices"/> as its
/// DataContext: a box showing what's ticked that opens a list of check boxes. The list is a popup
/// apart from the values, so refilling them while it's open leaves it open; Esc closes it.
/// </summary>
public partial class CheckListFilter : UserControl
{
    public CheckListFilter() => InitializeComponent();

    /// <summary>The list is open.</summary>
    public bool IsOpen
    {
        get => DropList.IsOpen;
        set => DropList.IsOpen = value;
    }

    // Opened: the keyboard goes into the list, so Tab and Space tick from there.
    private void DropList_Opened(object? sender, EventArgs e) => ChkAll.Focus();

    // Closed however it was, by Esc or a click elsewhere: the keyboard, if it was in the list, goes
    // back to the box rather than stay on a check box no longer seen.
    private void DropList_Closed(object? sender, EventArgs e)
    {
        if (DropList.Child.IsKeyboardFocusWithin)
            BtnOpen.Focus();
    }

    private void DropList_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Escape)
            return;
        DropList.IsOpen = false;
        e.Handled = true;
    }
}
