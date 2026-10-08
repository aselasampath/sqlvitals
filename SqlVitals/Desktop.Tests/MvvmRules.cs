using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SqlVitals.Desktop.Tests;

/// <summary>One place a rule is broken: the file (relative to <c>Desktop/</c>), the line, and what.</summary>
/// <param name="Name">What the allowlist names: "MainWindow", "IWaitStatsRepository", "async void".</param>
internal sealed record Offence(string File, int Line, string Name, string What)
{
    public override string ToString() => $"{DesktopSource.Where(File, Line)}: {What}";
}

/// <summary>
/// The MVVM rules of #181, as checks over the app's sources (<see cref="DesktopSource"/>). Each
/// returns where it is broken; <see cref="MvvmGuardTests"/> holds today's offenders.
/// </summary>
internal static partial class MvvmRules
{
    // ── A view model stays out of the view ───────────────────────────────

    /// <summary>
    /// Where a file refers to WPF's controls, <c>MessageBox</c>, <c>MainWindow</c>, the
    /// <c>Application</c> (or <c>App</c>) or a <c>Dispatcher</c>: by name, through a member, or
    /// through the type of what it uses.
    /// </summary>
    public static IEnumerable<Offence> ViewReferences(SemanticModel model, string file) =>
        References(model, file, ViewName);

    private static string? ViewName(ISymbol symbol) => symbol switch
    {
        INamespaceSymbol ns when IsIn(ns, "System.Windows.Controls") => "System.Windows.Controls",
        ITypeSymbol type when IsIn(type.ContainingNamespace, "System.Windows.Controls") => "System.Windows.Controls",
        ITypeSymbol type when Is(type, "System.Windows.MessageBox") => "MessageBox",
        ITypeSymbol type when Is(type, "SqlVitals.Desktop.MainWindow") => "MainWindow",
        ITypeSymbol type when Is(type, "System.Windows.Threading.Dispatcher") => "Dispatcher",
        ITypeSymbol type when BaseTypes(type).Any(t => Is(t, "System.Windows.Application")) => "Application",
        _ => null,
    };

    // ── Where a view model lives ─────────────────────────────────────────

    /// <summary>
    /// Where a file under <c>ViewModels/</c> breaks the naming rules: one type, named as the file;
    /// in <c>ViewModels/Pages</c> named for a page (<c>Pages/XPage.xaml</c> ↔ <c>XViewModel.cs</c>, or
    /// <c>X…ViewModel.cs</c> for a type its view model owns); in <c>ViewModels/Settings</c> named for
    /// its XAML; and a page's view model (a concrete <c>PageViewModel</c>) only in <c>ViewModels/Pages</c>.
    /// </summary>
    /// <param name="pages">The pages' names: "Backups" for <c>Pages/BackupsPage.xaml</c>.</param>
    /// <param name="settings">The settings XAML's names: "AlertsCategory" for <c>Pages/Settings/AlertsCategory.xaml</c>.</param>
    public static IEnumerable<Offence> ViewModelLayout(SemanticModel model, string file, IReadOnlySet<string> pages, IReadOnlySet<string> settings)
    {
        const string suffix = "ViewModel";
        var name    = Path.GetFileNameWithoutExtension(file);
        var folder  = Path.GetDirectoryName(file)?.Replace('\\', '/');
        var forPage = name.EndsWith(suffix, StringComparison.Ordinal) ? name[..^suffix.Length] : null;
        var types   = model.SyntaxTree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>()
            .Where(t => t.Parent is BaseNamespaceDeclarationSyntax or CompilationUnitSyntax)
            .ToList();

        if (types is not [var only] || only.Identifier.Text != name)
            yield return new(file, types.Count > 1 ? Line(types[1].Identifier) : 1, "layout",
                             $"one type per file, named as the file ({types.Count} here: {string.Join(", ", types.Select(t => t.Identifier.Text))})");

        if (folder == "ViewModels/Pages" && !(forPage is not null && pages.Any(page => forPage.StartsWith(page, StringComparison.Ordinal))))
            yield return new(file, 1, "layout", "a page's view model is named for its page: Pages/XPage.xaml ↔ ViewModels/Pages/XViewModel.cs");

        if (folder == "ViewModels/Settings" && !(forPage is not null && settings.Contains(forPage)))
            yield return new(file, 1, "layout", "a settings view model is named for its XAML: Pages/Settings/X.xaml ↔ ViewModels/Settings/XViewModel.cs");

        foreach (var type in types)
        {
            if (model.GetDeclaredSymbol(type) is INamedTypeSymbol { IsAbstract: false } symbol
                && BaseTypes(symbol).Skip(1).Any(t => Is(t, "SqlVitals.Desktop.ViewModels.PageViewModel"))
                && !(folder == "ViewModels/Pages" && symbol.Name == $"{forPage}{suffix}" && pages.Contains(forPage!)))
                yield return new(file, Line(type.Identifier), "layout",
                                 $"{symbol.Name} is a page's view model, so it is ViewModels/Pages/XViewModel.cs for Pages/XPage.xaml");
        }
    }

    // ── Page code leaves the repositories to its view model ──────────────

    /// <summary>Where a file refers to an Engine repository: its type, a member of it, or a value of it.</summary>
    public static IEnumerable<Offence> RepositoryReferences(SemanticModel model, string file) =>
        References(model, file, symbol => symbol is ITypeSymbol type
                                          && type.Name.EndsWith("Repository", StringComparison.Ordinal)
                                          && IsIn(type.ContainingNamespace, "SqlVitals.Engine")
            ? type.Name : null);

    // ── Pages report to the shell, not the window ────────────────────────

    /// <summary>Where a file refers to <c>MainWindow</c>: by name, through a member, or through the type of what it uses.</summary>
    public static IEnumerable<Offence> WindowReferences(SemanticModel model, string file) =>
        References(model, file, symbol => symbol is ITypeSymbol type && Is(type, "SqlVitals.Desktop.MainWindow") ? "MainWindow" : null);

    // ── async void only where an event calls it ──────────────────────────

    /// <summary>
    /// The <c>async void</c> methods, local functions and lambdas in a file that aren't event
    /// handlers. A method or local function is a handler when its class subscribes it with
    /// <c>+=</c> (or wraps it in a <c>new …EventHandler(…)</c>), or when an event attribute of its
    /// XAML names it; a lambda when it is subscribed with <c>+=</c>. A lambda counts as
    /// <c>async void</c> when the delegate it becomes returns void.
    /// </summary>
    /// <param name="xaml">The file's XAML, when it is code-behind: the handlers it names.</param>
    public static IEnumerable<Offence> AsyncVoid(SemanticModel model, string file, string? xaml)
    {
        var root = model.SyntaxTree.GetRoot();

        foreach (var node in root.DescendantNodes())
        {
            switch (node)
            {
                case MethodDeclarationSyntax m when IsAsyncVoid(m.Modifiers, m.ReturnType)
                                                  && !IsHandler(model.Compilation, m, m.Identifier.Text, xaml):
                    yield return new(file, Line(m.Identifier), "async void", $"async void {m.Identifier.Text} isn't an event handler");
                    break;

                case LocalFunctionStatementSyntax f when IsAsyncVoid(f.Modifiers, f.ReturnType)
                                                       && !IsHandler(model.Compilation, f, f.Identifier.Text, xaml: null):
                    yield return new(file, Line(f.Identifier), "async void", $"async void local function {f.Identifier.Text} isn't an event handler");
                    break;

                case AnonymousFunctionExpressionSyntax lambda when lambda.AsyncKeyword != default && !IsSubscribed(lambda):
                    if (model.GetTypeInfo(lambda).ConvertedType is not INamedTypeSymbol { DelegateInvokeMethod: { } invoke })
                        yield return new(file, Line(lambda.AsyncKeyword), "async void", "an async lambda whose delegate can't be told");
                    else if (invoke.ReturnsVoid)
                        yield return new(file, Line(lambda.AsyncKeyword), "async void",
                                         $"an async lambda passed as {invoke.ContainingType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)}, which returns void");
                    break;
            }
        }
    }

    private static bool IsAsyncVoid(SyntaxTokenList modifiers, TypeSyntax returns) =>
        modifiers.Any(SyntaxKind.AsyncKeyword) && returns is PredefinedTypeSyntax { Keyword.RawKind: (int)SyntaxKind.VoidKeyword };

    private static bool IsSubscribed(SyntaxNode node) =>
        node.Parent is AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.AddAssignmentExpression } add && add.Right == node;

    private static bool IsHandler(Compilation compilation, SyntaxNode declaration, string name, string? xaml)
    {
        // An event attribute names it: Click="Save_Click", ButtonBase.Click="Save_Click"; not x:Name="Save".
        if (xaml is not null)
        {
            var events = EventNames.GetValue(compilation, AllEventNames);
            foreach (Match attribute in XamlAttribute().Matches(xaml))
            {
                if (attribute.Groups["value"].Value.Trim() == name
                    && events.Contains(attribute.Groups["name"].Value.Split('.', ':')[^1]))
                    return true;
            }
        }

        // Subscribed in its own class, which may be split over partial files.
        var owner = declaration.FirstAncestorOrSelf<TypeDeclarationSyntax>()?.Identifier.Text;
        return Subscribed.GetValue(compilation, Subscriptions).Contains((owner, name));
    }

    [GeneratedRegex(@"(?<name>[\w.:]+)\s*=\s*""(?<value>[^""]*)""")]
    private static partial Regex XamlAttribute();

    private static readonly ConditionalWeakTable<Compilation, HashSet<string>> EventNames = new();

    // Every event a XAML attribute can name: the CLR events and routed events of WPF, LiveCharts and the app.
    private static HashSet<string> AllEventNames(Compilation compilation)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        var assemblies = compilation.SourceModule.ReferencedAssemblySymbols
            .Where(a => a.Name is "PresentationCore" or "PresentationFramework" or "WindowsBase"
                        || a.Name.StartsWith("LiveChartsCore", StringComparison.Ordinal))
            .Append(compilation.Assembly);

        foreach (var assembly in assemblies)
            Collect(assembly.GlobalNamespace);
        return names;

        void Collect(INamespaceOrTypeSymbol container)
        {
            foreach (var member in container.GetMembers())
            {
                switch (member)
                {
                    case INamespaceOrTypeSymbol inner: Collect(inner); break;
                    case IEventSymbol e: names.Add(e.Name); break;
                    case IFieldSymbol { IsStatic: true, Type.Name: "RoutedEvent" } f when f.Name.EndsWith("Event", StringComparison.Ordinal):
                        names.Add(f.Name[..^"Event".Length]);
                        break;
                }
            }
        }
    }

    private static readonly ConditionalWeakTable<Compilation, HashSet<(string?, string)>> Subscribed = new();

    // Each method a class subscribes: `Event += Handler`, `+= this.Handler`, `new XEventHandler(Handler)`.
    private static HashSet<(string?, string)> Subscriptions(Compilation compilation)
    {
        var subscribed = new HashSet<(string?, string)>();
        foreach (var node in compilation.SyntaxTrees.SelectMany(tree => tree.GetRoot().DescendantNodes()))
        {
            var handler = node switch
            {
                AssignmentExpressionSyntax { RawKind: (int)SyntaxKind.AddAssignmentExpression } add => HandlerName(add.Right),
                ObjectCreationExpressionSyntax { Type: var type } creation
                    when type.ToString().EndsWith("EventHandler", StringComparison.Ordinal) => HandlerName(creation),
                _ => null,
            };
            if (handler is not null)
                subscribed.Add((node.FirstAncestorOrSelf<TypeDeclarationSyntax>()?.Identifier.Text, handler));
        }

        return subscribed;
    }

    private static string? HandlerName(ExpressionSyntax expression) => expression switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax } access => access.Name.Identifier.Text,
        ObjectCreationExpressionSyntax { ArgumentList.Arguments: [var only] } => HandlerName(only.Expression),
        _ => null,
    };

    // ── Click= in a page's XAML ──────────────────────────────────────────

    /// <summary>Each <c>Click="…"</c> in a XAML file, not counting comments.</summary>
    public static IEnumerable<Offence> Clicks(string xaml, string file)
    {
        // Blank the comments but keep their lines, so line numbers still match the file.
        var code = Comment().Replace(xaml, c => new string('\n', c.Value.Count(ch => ch == '\n')).PadRight(c.Length));
        foreach (Match click in ClickAttribute().Matches(code))
            yield return new(file, code.Take(click.Index).Count(ch => ch == '\n') + 1, "Click", $"Click=\"{click.Groups[1].Value}\"");
    }

    [GeneratedRegex(@"<!--[\s\S]*?-->")]
    private static partial Regex Comment();

    [GeneratedRegex(@"(?<![\w.])(?:\w+\.)?Click\s*=\s*""([^""]*)""")]
    private static partial Regex ClickAttribute();

    // ── Shared ───────────────────────────────────────────────────────────

    /// <summary>
    /// Each line where a name refers to what <paramref name="classify"/> names: the symbol itself,
    /// the type it is a member of, or the type of the value it gives (a field, property, local,
    /// parameter, method's result or event). Names the compiler can't resolve are left out.
    /// </summary>
    private static IEnumerable<Offence> References(SemanticModel model, string file, Func<ISymbol, string?> classify)
    {
        var seen = new HashSet<(int, string)>();

        foreach (var node in model.SyntaxTree.GetRoot().DescendantNodes().OfType<SimpleNameSyntax>())
        {
            var info = model.GetSymbolInfo(node);
            if ((info.Symbol ?? info.CandidateSymbols.FirstOrDefault()) is not { } symbol)
                continue;

            foreach (var related in Related(symbol))
            {
                if (classify(related) is not { } name)
                    continue;

                var line = Line(node.Identifier);
                if (seen.Add((line, name)))
                    yield return new(file, line, name, $"refers to {name} ({node.Identifier.Text})");
            }
        }
    }

    private static IEnumerable<ISymbol> Related(ISymbol symbol)
    {
        if (symbol is IAliasSymbol alias)
            symbol = alias.Target;

        yield return symbol;
        if (symbol is IFieldSymbol or IPropertySymbol or IMethodSymbol or IEventSymbol or INamedTypeSymbol
            && symbol.ContainingType is { } owner)
            yield return owner;

        var type = symbol switch
        {
            IFieldSymbol f     => f.Type,
            IPropertySymbol p  => p.Type,
            ILocalSymbol l     => l.Type,
            IParameterSymbol p => p.Type,
            IMethodSymbol m    => m.ReturnType,
            IEventSymbol e     => e.Type,
            _                  => null,
        };
        if (type is not null)
            yield return type;
    }

    private static IEnumerable<ITypeSymbol> BaseTypes(ITypeSymbol type)
    {
        for (var t = type; t is not null; t = t.BaseType)
            yield return t;
    }

    private static bool Is(ITypeSymbol type, string fullName) => type.OriginalDefinition.ToDisplayString() == fullName;

    private static bool IsIn(INamespaceSymbol? ns, string name)
    {
        var full = ns?.ToDisplayString() ?? string.Empty;
        return full == name || full.StartsWith(name + ".", StringComparison.Ordinal);
    }

    private static int Line(SyntaxToken token) => token.GetLocation().GetLineSpan().StartLinePosition.Line + 1;
}
