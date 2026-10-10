namespace SqlVitals.Collector;

/// <summary>
/// The collector's command line (#172): <c>SqlVitals.Collector &lt;command&gt; [value] [--option value] [--flag]</c>.
/// Small on purpose: a handful of commands an admin types once, so no parsing library.
/// </summary>
internal sealed class CommandLine
{
    // Options that take no value.
    private static readonly HashSet<string> Flags = new(StringComparer.OrdinalIgnoreCase)
    {
        "trust-server-certificate", "password-stdin", "stdin", "critical-only", "no-ends", "ping",
        "start", "delete-data", "help",
    };

    private readonly Dictionary<string, string> _options = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string>            _used    = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The command, lower case; null when none was given (run).</summary>
    public string? Command { get; private init; }

    /// <summary>The words after the command that aren't options: a server's or a channel's name.</summary>
    public IReadOnlyList<string> Values { get; private init; } = [];

    /// <summary>The first of <see cref="Values"/>; null when there's none.</summary>
    public string? Value => Values.Count > 0 ? Values[0] : null;

    /// <summary>Why the line can't be read; null when it can.</summary>
    public string? Error { get; private init; }

    public static CommandLine Parse(IReadOnlyList<string> args)
    {
        string? command = null;
        string? error   = null;
        var values  = new List<string>();
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.Length > 2)
            {
                var name  = arg[2..];
                var split = name.IndexOf('=', StringComparison.Ordinal);
                string value;
                if (split > 0)
                {
                    value = name[(split + 1)..];
                    name  = name[..split];
                }
                else if (Flags.Contains(name))
                {
                    value = "true";
                }
                else if (i + 1 < args.Count)
                {
                    value = args[++i];
                }
                else
                {
                    error ??= $"--{name} needs a value.";
                    continue;
                }
                if (!options.TryAdd(name, value))
                    error ??= $"--{name} is given twice.";
            }
            else if (arg is "-h" or "/?")
            {
                options["help"] = "true";
            }
            else if (command is null)
            {
                command = arg.ToLowerInvariant();
            }
            else
            {
                values.Add(arg);
            }
        }

        var line = new CommandLine { Command = command, Values = values, Error = error };
        foreach (var (name, value) in options)
            line._options[name] = value;
        return line;
    }

    /// <summary>An option's value; null when it wasn't given.</summary>
    public string? Option(string name)
    {
        _used.Add(name);
        return _options.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>Whether a flag was given.</summary>
    public bool Flag(string name) => Option(name) is { } value && !string.Equals(value, "false", StringComparison.OrdinalIgnoreCase);

    /// <summary>A whole-number option; null when it wasn't given. Throws <see cref="UsageException"/> when it isn't one.</summary>
    public int? Number(string name) =>
        Option(name) is not { } text ? null
        : int.TryParse(text, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n
        : throw new UsageException($"--{name} must be a whole number, not \"{text}\".");

    /// <summary>The options given that the command never asked for: typos, mostly.</summary>
    public IReadOnlyList<string> Unused() =>
        _options.Keys.Where(k => !_used.Contains(k) && !string.Equals(k, "data-dir", StringComparison.OrdinalIgnoreCase)).ToList();
}

/// <summary>A command typed wrongly: said on the error stream, with exit code 2.</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>Where a command reads and writes: the console, or a test's buffers.</summary>
internal interface ICommandConsole
{
    TextWriter Out   { get; }
    TextWriter Error { get; }

    /// <summary>A line of standard input, for --password-stdin and --stdin; null at its end.</summary>
    string? ReadLine();

    /// <summary>A secret typed at the console without showing it; null when there's no console to type at.</summary>
    string? ReadSecret(string prompt);
}

internal sealed class SystemConsole : ICommandConsole
{
    public TextWriter Out   => Console.Out;
    public TextWriter Error => Console.Error;

    public string? ReadLine() => Console.In.ReadLine();

    public string? ReadSecret(string prompt)
    {
        if (Console.IsInputRedirected)
            return null;
        Console.Out.Write(prompt);
        var text = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
                break;
            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0)
                    text.Length--;
            }
            else if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }
        Console.Out.WriteLine();
        return text.ToString();
    }
}
