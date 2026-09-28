using System.Text;

namespace RedisService.Service;

/// <summary>Where relative paths in a config file resolve, and how POSIX paths map to Windows.</summary>
public sealed record ConfPathContext(string WorkingDirectory, string InstallDirectory, RuntimeFlavor Flavor)
{
    public string ToWindows(string confPath, string currentDirectory) =>
        RuntimeLayout.ConfPathToWindows(confPath, currentDirectory, InstallDirectory, Flavor);
}

/// <summary>
/// A minimal redis.conf reader for the handful of settings the wrapper needs (port, bind, TLS, auth, dir, logfile).
/// It follows Redis's own rules: sdssplitargs tokenising, case-insensitive names, last value wins,
/// <c>include</c> (with globs) resolved against the current directory, and <c>dir</c> changing that directory
/// mid-file exactly like Redis's chdir during config load. Command-line overrides are applied last.
/// </summary>
public sealed class RedisConfFile
{
    private const int MaxIncludeDepth = 16;
    private readonly Dictionary<string, string[]> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _warnings = [];

    private RedisConfFile() { }

    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>The Windows directory Redis ends up in after processing every <c>dir</c> directive and override.</summary>
    public string EffectiveDirectory { get; private set; } = "";

    /// <summary>Whether any <c>dir</c> value that took effect was relative.</summary>
    public bool DirectoryWasRelative { get; private set; }

    public string? Get(string name) => _values.TryGetValue(name, out var v) ? (v.Length > 0 ? v[0] : "") : null;

    public string[]? GetArgs(string name) => _values.TryGetValue(name, out var v) ? v : null;

    public bool Contains(string name) => _values.ContainsKey(name);

    public static RedisConfFile Parse(string text, ConfPathContext context, IEnumerable<KeyValuePair<string, string>>? overrides = null)
    {
        var conf = new RedisConfFile { EffectiveDirectory = context.WorkingDirectory };
        var cwd = context.WorkingDirectory;
        conf.ParseText(text, "(inline)", context, ref cwd, 0);
        conf.ApplyOverrides(overrides, context, ref cwd);
        conf.EffectiveDirectory = cwd;
        return conf;
    }

    public static RedisConfFile Load(string? path, ConfPathContext context, IEnumerable<KeyValuePair<string, string>>? overrides = null)
    {
        var conf = new RedisConfFile { EffectiveDirectory = context.WorkingDirectory };
        var cwd = context.WorkingDirectory;
        if (path is not null)
        {
            try
            {
                conf.ParseText(File.ReadAllText(path), path, context, ref cwd, 0);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                conf._warnings.Add($"Cannot read config file '{path}': {ex.Message}");
            }
        }
        conf.ApplyOverrides(overrides, context, ref cwd);
        conf.EffectiveDirectory = cwd;
        return conf;
    }

    private void ApplyOverrides(IEnumerable<KeyValuePair<string, string>>? overrides, ConfPathContext context, ref string cwd)
    {
        if (overrides is null) return;
        foreach (var (name, value) in overrides)
            Set(name, [value], context, ref cwd);
    }

    private void Set(string name, string[] args, ConfPathContext context, ref string cwd)
    {
        _values[name] = args;
        if (name.Equals("dir", StringComparison.OrdinalIgnoreCase) && args.Length > 0 && args[0].Length > 0)
        {
            DirectoryWasRelative = RuntimeLayout.IsConfPathRelative(args[0]);
            cwd = context.ToWindows(args[0], cwd);
        }
    }

    private void ParseText(string text, string source, ConfPathContext context, ref string cwd, int depth)
    {
        var lineNo = 0;
        foreach (var rawLine in text.Split('\n'))
        {
            lineNo++;
            var line = rawLine.Trim(' ', '\t', '\r');
            if (line.Length == 0 || line[0] == '#') continue;

            var tokens = SplitArgs(line);
            if (tokens is null)
            {
                _warnings.Add($"{source}:{lineNo}: unbalanced quotes, line ignored");
                continue;
            }
            if (tokens.Count == 0) continue;

            var name = tokens[0];
            var args = tokens.Skip(1).ToArray();
            if (name.Equals("include", StringComparison.OrdinalIgnoreCase))
            {
                if (args.Length != 1) { _warnings.Add($"{source}:{lineNo}: include expects one argument"); continue; }
                Include(args[0], context, ref cwd, depth, $"{source}:{lineNo}");
                continue;
            }
            Set(name, args, context, ref cwd);
        }
    }

    private void Include(string pattern, ConfPathContext context, ref string cwd, int depth, string where)
    {
        if (depth >= MaxIncludeDepth)
        {
            _warnings.Add($"{where}: include nesting deeper than {MaxIncludeDepth}, ignored");
            return;
        }

        var resolved = context.ToWindows(pattern, cwd);
        var files = new List<string>();
        var fileName = Path.GetFileName(resolved.Replace('\\', Path.DirectorySeparatorChar));
        if (fileName.IndexOfAny(['*', '?', '[']) >= 0)
        {
            var directory = resolved[..^fileName.Length].TrimEnd('\\', '/');
            try
            {
                files.AddRange(Directory.EnumerateFiles(ToHostPath(directory), fileName).Order(StringComparer.Ordinal));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                _warnings.Add($"{where}: cannot expand include '{pattern}': {ex.Message}");
            }
        }
        else
        {
            files.Add(ToHostPath(resolved));
        }

        foreach (var file in files)
        {
            try
            {
                ParseText(File.ReadAllText(file), file, context, ref cwd, depth + 1);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _warnings.Add($"{where}: cannot read include '{file}': {ex.Message}");
            }
        }
    }

    // On Windows the normalised path is usable as is; on other hosts (tests) it already is a host path.
    private static string ToHostPath(string path) => path;

    /// <summary>
    /// Tokenises one line the way Redis's sdssplitargs does: whitespace-separated, "double quotes" with
    /// \n \r \t \b \a \\ \" and \xHH escapes, 'single quotes' with \' escape. A closing quote must be followed
    /// by whitespace or the end of the line. Returns null for unbalanced quotes.
    /// </summary>
    public static List<string>? SplitArgs(string line)
    {
        var result = new List<string>();
        var i = 0;
        while (true)
        {
            while (i < line.Length && char.IsWhiteSpace(line[i])) i++;
            if (i >= line.Length) return result;

            var current = new StringBuilder();
            bool inDq = false, inSq = false, done = false;
            while (!done)
            {
                if (inDq)
                {
                    if (i >= line.Length) return null;
                    var c = line[i];
                    if (c == '\\' && i + 3 < line.Length && line[i + 1] == 'x' && IsHex(line[i + 2]) && IsHex(line[i + 3]))
                    {
                        current.Append((char)Convert.ToByte(line.Substring(i + 2, 2), 16));
                        i += 3;
                    }
                    else if (c == '\\' && i + 1 < line.Length)
                    {
                        i++;
                        current.Append(line[i] switch { 'n' => '\n', 'r' => '\r', 't' => '\t', 'b' => '\b', 'a' => '\a', var o => o });
                    }
                    else if (c == '"')
                    {
                        if (i + 1 < line.Length && !char.IsWhiteSpace(line[i + 1])) return null;
                        done = true;
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
                else if (inSq)
                {
                    if (i >= line.Length) return null;
                    var c = line[i];
                    if (c == '\\' && i + 1 < line.Length && line[i + 1] == '\'')
                    {
                        i++;
                        current.Append('\'');
                    }
                    else if (c == '\'')
                    {
                        if (i + 1 < line.Length && !char.IsWhiteSpace(line[i + 1])) return null;
                        done = true;
                    }
                    else
                    {
                        current.Append(c);
                    }
                }
                else
                {
                    if (i >= line.Length) { done = true; break; }
                    var c = line[i];
                    switch (c)
                    {
                        case ' ' or '\n' or '\r' or '\t' or '\0': done = true; break;
                        case '"': inDq = true; break;
                        case '\'': inSq = true; break;
                        default: current.Append(c); break;
                    }
                }
                if (i < line.Length) i++;
            }
            result.Add(current.ToString());
        }
    }

    private static bool IsHex(char c) => char.IsAsciiHexDigit(c);
}
