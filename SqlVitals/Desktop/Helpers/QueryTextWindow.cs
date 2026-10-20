using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using SqlVitals.Desktop.Services;
using SqlVitals.Engine.Scripting;

namespace SqlVitals.Desktop.Helpers;

/// <summary>
/// A query's full text in its own window (#232): as the server holds it, line breaks and all, in a
/// monospace font that scrolls both ways, with Copy and Save as .sql. A grid's View button binds
/// <see cref="Show"/> with the row's text as the parameter; the details drawer calls <see cref="Open"/>.
/// The window is not modal, so it can sit beside the grid while another row is read.
/// </summary>
public static class QueryTextWindow
{
    /// <summary>Opens the text that is the command's parameter; nothing without one.</summary>
    public static ICommand Show { get; } = new RelayCommand<string?>(text => Open(text), text => !string.IsNullOrWhiteSpace(text));

    /// <param name="heading">"Query text", or the drawer's own heading: "To fix it".</param>
    public static void Open(string? text, string heading = "Query text")
    {
        if (string.IsNullOrWhiteSpace(text))
            return;
        CompositionRoot.Get<IDialogService>().ShowSqlScript(Script(text, heading), modal: false);
    }

    /// <summary>The window's content: the heading names the text by its first words.</summary>
    internal static SqlScript Script(string text, string heading = "Query text") =>
        new($"{heading} · {SqlPreview.Line(text, 60)}", string.Empty, text, "query.sql");
}
