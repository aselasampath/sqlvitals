using System.IO;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace SqlVitals.Desktop.Tests;

/// <summary>
/// The app's own sources, for the MVVM guard tests (#181): every <c>.cs</c> under <c>Desktop/</c>
/// compiled with Roslyn against the assemblies this test runs with, so a rule can ask what a name
/// refers to and say on which line. The XAML compiler's and the MVVM Toolkit's generated code is
/// not in it; names that only those define stay unresolved, which no rule depends on.
/// </summary>
internal static class DesktopSource
{
    /// <summary>The app project's folder, <c>SqlVitals/Desktop</c>, found above the test's output.</summary>
    public static string Dir => LazyDir.Value;

    /// <summary>Every C# file of the app, with its symbols.</summary>
    public static CSharpCompilation Compilation => LazyCompilation.Value;

    private static readonly Lazy<string> LazyDir = new(() =>
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var desktop = Path.Combine(dir.FullName, "Desktop");
            if (File.Exists(Path.Combine(desktop, "SqlVitals.Desktop.csproj")))
                return desktop;
        }

        throw new DirectoryNotFoundException($"No Desktop/SqlVitals.Desktop.csproj above {AppContext.BaseDirectory}.");
    });

    private static readonly Lazy<CSharpCompilation> LazyCompilation = new(() =>
        Compile(Files("*.cs").Select(path => Parse(File.ReadAllText(path), path))));

    /// <summary>The app's files matching <paramref name="pattern"/>, under <paramref name="folder"/> of <see cref="Dir"/>; not bin or obj.</summary>
    public static IEnumerable<string> Files(string pattern, string folder = "")
    {
        var root = Path.Combine(Dir, folder);
        if (!Directory.Exists(root))
            return [];

        return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(path => !Relative(path).Split('/').Any(part => part is "bin" or "obj"))
            .Order(StringComparer.Ordinal);
    }

    /// <summary>A path as the allowlists write it: relative to <c>Desktop/</c>, with forward slashes.</summary>
    public static string Relative(string path) => Path.GetRelativePath(Dir, path).Replace('\\', '/');

    /// <summary>Where a failure points: <c>Desktop/Pages/BackupsPage.xaml.cs:42</c>.</summary>
    public static string Where(string relative, int line) => $"Desktop/{relative}:{line}";

    public static SyntaxTree Parse(string code, string path = "") =>
        CSharpSyntaxTree.ParseText(code, new CSharpParseOptions(LanguageVersion.CSharp12), path);

    /// <summary>Compiles sources the way the app is compiled: its implicit usings, nullable on.</summary>
    public static CSharpCompilation Compile(IEnumerable<SyntaxTree> trees) =>
        CSharpCompilation.Create(
            "SqlVitals.Desktop.Guard",
            trees.Prepend(ImplicitUsings),
            References.Value,
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

    // What the SDK puts in the app's GlobalUsings.g.cs: <ImplicitUsings> for WPF, less WinForms.
    private static readonly SyntaxTree ImplicitUsings = Parse("""
        global using System;
        global using System.Collections.Generic;
        global using System.Linq;
        global using System.Threading;
        global using System.Threading.Tasks;
        """);

    // Everything this test runs with (WPF, Engine, the MVVM Toolkit, LiveCharts) except the app
    // itself, whose types the compiled sources define.
    private static readonly Lazy<MetadataReference[]> References = new(() =>
        ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Where(path => Path.GetFileNameWithoutExtension(path) is not ("SqlVitals.Desktop" or "SqlVitals.Desktop.Tests"))
            .Select(path => (MetadataReference)MetadataReference.CreateFromFile(path))
            .ToArray());
}
