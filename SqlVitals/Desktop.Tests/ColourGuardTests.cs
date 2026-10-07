using System.IO;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// "Use brush keys, never hex" (docs/Architecture.md, UI conventions), checked over the app's
/// sources (#164). Colours live in <c>Styles/LightTheme.xaml</c> and <c>DarkTheme.xaml</c>; code
/// names a key (<c>SetResourceReference</c>, <c>ThemeResources</c>, <c>ChartTheme</c>), so both
/// themes can set it. C# has no colour literal left; the pages' XAML has an allowlist of today's
/// counts that may only shrink.
/// </summary>
public sealed class ColourGuardTests
{
    /// <summary>Hex colours each XAML file still has. Lower a count (or remove the file) when its colours move to keys.</summary>
    private static readonly Dictionary<string, int> XamlAllowed = new()
    {
        ["Controls/ExecutionPlanView.xaml"]          = 1,
        ["Controls/ProcessMapControl.xaml"]          = 16,
        ["Controls/QuickSwitcherOverlay.xaml"]       = 1,
        ["Controls/SidebarStyles.xaml"]              = 2,
        ["Pages/ConfigChecksPage.xaml"]              = 2,
        ["Pages/ExportPage.xaml"]                    = 11,
        ["Pages/FileIoPage.xaml"]                    = 3,
        ["Pages/ImplicitConversionsPage.xaml"]       = 3,
        ["Pages/LiveMetricsDashboardPage.xaml"]      = 20,
        ["Pages/MemoryGrantsPage.xaml"]              = 5,
        ["Pages/PlanCacheHealthPage.xaml"]           = 21,
        ["Pages/ProcessesPage.xaml"]                 = 1,
        ["Pages/QueryStorePage.xaml"]                = 2,
        ["Pages/SecurityAuditPage.xaml"]             = 2,
        ["Pages/Settings/ConnectionEditor.xaml"]     = 2,
        ["Pages/Settings/SettingsStyles.xaml"]       = 2,
        ["Pages/SettingsPage.xaml"]                  = 3,
        ["Pages/SpTracePage.xaml"]                   = 2,
        ["Pages/StaleStatisticsPage.xaml"]           = 3,
        ["Pages/WaitStatsTrendPage.xaml"]            = 6,
        ["Windows/QueryExecutionPlanWindow.xaml"]    = 1,
        ["Windows/SqlScriptWindow.xaml"]             = 1,
    };

    private static readonly string[] ThemeFiles = ["Styles/DarkTheme.xaml", "Styles/LightTheme.xaml"];

    // The app's sources as the MVVM guard compiles them (parsed once), less the implicit usings.
    private static IEnumerable<SyntaxTree> AppTrees => DesktopSource.Compilation.SyntaxTrees.Where(t => t.FilePath.Length > 0);

    [Fact]
    public void Code_NamesThemeKeys_NotColours()
    {
        var found = AppTrees
            .SelectMany(tree => ColourRules.InCode(tree, DesktopSource.Relative(tree.FilePath)))
            .Select(o => $"{DesktopSource.Where(o.File, o.Line)}: {o.What}")
            .ToList();

        Assert.True(found.Count == 0,
            "Use a theme key, not a colour (docs/Architecture.md, UI conventions): add the colour to both Styles/*Theme.xaml " +
            "and name its key with SetResourceReference, ThemeResources or ChartTheme.\n" + string.Join('\n', found));
    }

    [Fact]
    public void Xaml_HasNoNewColours()
    {
        var grown = XamlCounts()
            .Where(f => f.Count > XamlAllowed.GetValueOrDefault(f.File))
            .Select(f => $"Desktop/{f.File}: {f.Count} hex colours, {XamlAllowed.GetValueOrDefault(f.File)} allowed")
            .ToList();

        Assert.True(grown.Count == 0,
            "Use a theme brush ({DynamicResource Key}), not a hex colour; add the colour to both Styles/*Theme.xaml.\n" + string.Join('\n', grown));
    }

    [Fact]
    public void Xaml_AllowlistOnlyShrinks()
    {
        var counts = XamlCounts().ToDictionary(f => f.File, f => f.Count);
        var stale  = XamlAllowed
            .Where(a => counts.GetValueOrDefault(a.Key) < a.Value)
            .Select(a => $"{a.Key}: {counts.GetValueOrDefault(a.Key)} now; lower {nameof(XamlAllowed)} to match")
            .ToList();

        Assert.True(stale.Count == 0, "The allowlist only shrinks:\n" + string.Join('\n', stale));
    }

    [Fact]
    public void Themes_DefineTheSameKeys()
    {
        var dark  = ThemeKeys("Styles/DarkTheme.xaml");
        var light = ThemeKeys("Styles/LightTheme.xaml");

        Assert.Empty(dark.Except(light));
        Assert.Empty(light.Except(dark));
    }

    [Fact]
    public void Code_NamesKeysTheXamlHas()
    {
        var keys    = DesktopSource.Files("*.xaml").SelectMany(path => ThemeKeys(DesktopSource.Relative(path))).ToHashSet();
        var missing = AppTrees
            .SelectMany(tree => ColourRules.KeysNamed(DesktopSource.Compilation.GetSemanticModel(tree), DesktopSource.Relative(tree.FilePath)))
            .Where(o => !keys.Contains(o.Name))
            .Select(o => $"{DesktopSource.Where(o.File, o.Line)}: \"{o.Name}\"")
            .ToList();

        Assert.True(missing.Count == 0, "No resource with that key in the app's XAML:\n" + string.Join('\n', missing));
    }

    private static HashSet<string> ThemeKeys(string file) =>
        Regex.Matches(File.ReadAllText(Path.Combine(DesktopSource.Dir, file)), "x:Key=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToHashSet();

    private static IEnumerable<(string File, int Count)> XamlCounts() =>
        DesktopSource.Files("*.xaml")
            .Select(DesktopSource.Relative)
            .Where(file => !ThemeFiles.Contains(file))
            .Select(file => (file, ColourRules.InXaml(File.ReadAllText(Path.Combine(DesktopSource.Dir, file)))))
            .Where(f => f.Item2 > 0);

    // ── The rules themselves ─────────────────────────────────────────────────

    [Fact]
    public void InCode_FindsHexStringsAndLiteralChannels()
    {
        var code = """
            using System.Windows.Media;
            using SkiaSharp;

            class Page
            {
                Brush A = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#22C55E"));  // ✗
                SKColor B = SKColor.Parse("#FF7C3AED");                                            // ✗
                Color C = Color.FromRgb(0xFF, 0x80, 0x00);                                          // ✗
                Color D = Color.FromArgb(0x26, 255, 128, 0);                                        // ✗
                SKColor E = new SKColor(0x7C, 0x3A, 0xED);                                          // ✗
                Brush F = Frozen(0x22, 0xC5, 0x5E);                                                 // ✗
                Color G(Color c) => Color.FromArgb(0x1A, c.R, c.G, c.B);
                Color H = Colors.Gray;
                string I = 1234.5.ToString("#,0.##");
                string J = "#1";
                Brush K(byte r) => Frozen(r, 0, 0);
            }
            """;

        var expected = code.Split('\n').Select((line, i) => (line, i + 1)).Where(l => l.line.Contains("// ✗")).Select(l => l.Item2);
        Assert.Equal(expected, ColourRules.InCode(DesktopSource.Parse(code), "Snippet.cs").Select(o => o.Line));
    }

    [Fact]
    public void KeysNamed_FindsKeyArguments_NotOtherStrings()
    {
        var code = """
            using System.Windows;
            using System.Windows.Controls;

            class Page : UserControl
            {
                void Card(string label, string brushKey) { }
                void State(string state) { }
                void Show(TextBlock text, bool ok)
                {
                    Card("Queries", "Accent");                                        // ✗ Accent
                    Card("State", ok ? "Success" : "Danger");                         // ✗ Success, Danger
                    State("Blocked");
                    text.SetResourceReference(TextBlock.ForegroundProperty, "Info");  // ✗ Info
                    _ = TryFindResource("KpiCard");                                   // ✗ KpiCard
                    Card($"Plans {1}", brushKey: "TextMuted");                        // ✗ TextMuted
                }
            }
            """;
        var tree     = DesktopSource.Parse(code, "Snippet.cs");
        var model    = DesktopSource.Compile([tree]).GetSemanticModel(tree);
        var expected = code.Split('\n')
            .Select((line, i) => (Line: i + 1, Mark: line.Split("// ✗ ") is [_, var keys] ? keys.Trim() : null))
            .Where(l => l.Mark is not null)
            .SelectMany(l => l.Mark!.Split(", ").Select(key => $"{l.Line}: {key}"));

        Assert.Equal(expected, ColourRules.KeysNamed(model, "Snippet.cs").Select(o => $"{o.Line}: {o.Name}"));
    }

    [Fact]
    public void InXaml_CountsAttributesAndContent()
    {
        Assert.Equal(3, ColourRules.InXaml("""
            <Border Background="#1A7C3AED" BorderBrush="{DynamicResource Border}">
                <TextBlock Foreground="#FFF" Text="Item #12" />
                <Border.Resources><Color x:Key="Local">#FF22C55E</Color></Border.Resources>
            </Border>
            """));
    }
}

/// <summary>Where code writes a colour instead of naming a theme key.</summary>
internal static partial class ColourRules
{
    private static readonly string[] ChannelMethods = ["FromRgb", "FromArgb", "FromScRgb"];

    public static IEnumerable<Offence> InCode(SyntaxTree tree, string file)
    {
        foreach (var node in tree.GetRoot().DescendantNodes())
        {
            switch (node)
            {
                case LiteralExpressionSyntax { RawKind: (int)SyntaxKind.StringLiteralExpression } s when HexColour().IsMatch(s.Token.ValueText):
                    yield return Found(s, $"\"{s.Token.ValueText}\"");
                    break;

                // Color.FromRgb(0xFF, 0x80, 0x00), new SKColor(124, 58, 237), and helpers like Frozen(0x22, 0xC5, 0x5E).
                case InvocationExpressionSyntax call when LiteralChannels(call.ArgumentList, Name(call.Expression)):
                    yield return Found(call, call.ToString());
                    break;
                case BaseObjectCreationExpressionSyntax create when create.ArgumentList is { } args
                                                                    && LiteralChannels(args, (create as ObjectCreationExpressionSyntax)?.Type.ToString()):
                    yield return Found(create, create.ToString());
                    break;
            }
        }

        Offence Found(SyntaxNode at, string what) =>
            new(file, at.GetLocation().GetLineSpan().StartLinePosition.Line + 1, what, what);
    }

    // Parameters that take a resource key, by name: ours (brushKey, colorKey) and WPF's (resourceKey).
    private static readonly string[] KeyParameters = ["brushKey", "colorKey", "resourceKey"];

    /// <summary>
    /// Resource keys code names as literals: an argument for a <c>brushKey</c> or <c>colorKey</c>
    /// parameter (<c>AddCard(…, "Success")</c>, <c>ChartTheme.Of("WaitCpuColor")</c>), WPF's
    /// <c>FindResource</c>, and <c>SetResourceReference</c>, which goes by name because its element is
    /// often a XAML field the compilation doesn't have. Both arms of <c>ok ? "Success" : "Danger"</c>
    /// count, and a switch's.
    /// </summary>
    public static IEnumerable<Offence> KeysNamed(SemanticModel model, string file)
    {
        foreach (var call in model.SyntaxTree.GetRoot().DescendantNodes().OfType<InvocationExpressionSyntax>())
        {
            var args = call.ArgumentList.Arguments;
            IEnumerable<ArgumentSyntax> keyArgs = Name(call.Expression) switch
            {
                "SetResourceReference" when args.Count == 2 => [args[1]],
                _ when model.GetSymbolInfo(call).Symbol is IMethodSymbol method
                    => args.Where(a => KeyParameters.Contains(ParameterOf(method, a, args)?.Name)),
                _ => [],
            };

            foreach (var literal in keyArgs.SelectMany(a => Results(a.Expression)))
                yield return new Offence(file, literal.GetLocation().GetLineSpan().StartLinePosition.Line + 1, literal.Token.ValueText, "key");
        }
    }

    // The string literals an expression can evaluate to: itself, either arm of ?:, a switch's arms.
    // Not a condition's strings, and not a key built with + (that one is only known at run time).
    private static IEnumerable<LiteralExpressionSyntax> Results(ExpressionSyntax e) => e switch
    {
        LiteralExpressionSyntax l when l.IsKind(SyntaxKind.StringLiteralExpression) => [l],
        ParenthesizedExpressionSyntax p => Results(p.Expression),
        ConditionalExpressionSyntax c   => Results(c.WhenTrue).Concat(Results(c.WhenFalse)),
        SwitchExpressionSyntax s        => s.Arms.SelectMany(arm => Results(arm.Expression)),
        _                               => [],
    };

    private static IParameterSymbol? ParameterOf(IMethodSymbol method, ArgumentSyntax arg, SeparatedSyntaxList<ArgumentSyntax> args) =>
        arg.NameColon is { } named
            ? method.Parameters.FirstOrDefault(p => p.Name == named.Name.Identifier.ValueText)
            : method.Parameters.ElementAtOrDefault(args.IndexOf(arg));

    /// <summary>Hex colours in a XAML file: attribute values and element content (<c>&lt;Color&gt;#FF22C55E&lt;/Color&gt;</c>).</summary>
    public static int InXaml(string xaml) => XamlColour().Count(xaml);

    // Red, green and blue all written as numbers: by a colour method or type, or as three or more 0x literals.
    private static bool LiteralChannels(ArgumentListSyntax list, string? name)
    {
        var args = list.Arguments.Select(a => a.Expression).ToList();
        if (args.Count < 3)
            return false;
        if (name is not null && (ChannelMethods.Contains(name) || name.EndsWith("SKColor", StringComparison.Ordinal) || name == "Color"))
            return args.TakeLast(3).All(IsNumber);
        return args.All(a => IsNumber(a) && a.ToString().StartsWith("0x", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsNumber(ExpressionSyntax e) => e switch
    {
        LiteralExpressionSyntax l => l.IsKind(SyntaxKind.NumericLiteralExpression),
        CastExpressionSyntax c    => IsNumber(c.Expression),
        _                         => false,
    };

    private static string? Name(ExpressionSyntax e) => e switch
    {
        MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
        IdentifierNameSyntax i         => i.Identifier.ValueText,
        _                              => null,
    };

    [GeneratedRegex("^#([0-9A-Fa-f]{3}|[0-9A-Fa-f]{4}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{8})$")]
    private static partial Regex HexColour();

    [GeneratedRegex("(?<=[\">])#([0-9A-Fa-f]{8}|[0-9A-Fa-f]{6}|[0-9A-Fa-f]{4}|[0-9A-Fa-f]{3})(?=[\"<])")]
    private static partial Regex XamlColour();
}
