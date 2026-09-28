using System.Text;

namespace RedisService.Service;

/// <summary>Builds Windows command lines for the two different parsers on the other end.</summary>
public static class WindowsCommandLine
{
    /// <summary>
    /// Quotes one argument for a Microsoft C runtime / CommandLineToArgvW parser (used for the service ImagePath,
    /// which .NET parses): backslashes are literal except before a double quote.
    /// </summary>
    public static string QuoteMsvc(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
            return arg;

        var sb = new StringBuilder(arg.Length + 2).Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }
            if (c == '"')
            {
                sb.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                sb.Append('\\', backslashes).Append(c);
            }
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// Quotes one argument for a Cygwin/MSYS2 program started by a non-Cygwin parent (dcrt0.cc build_argv).
    /// That parser differs from the Microsoft one: it also treats single quotes as quotes, glob-expands unquoted
    /// words, expands a leading @file, and inside double quotes turns \\ into \ and \" into ". Always double-quoting and
    /// escaping every backslash and double quote defeats all of that.
    /// </summary>
    public static string QuoteCygwin(string arg)
    {
        var sb = new StringBuilder(arg.Length + 2).Append('"');
        foreach (var c in arg)
        {
            if (c is '\\' or '"') sb.Append('\\');
            sb.Append(c);
        }
        return sb.Append('"').ToString();
    }

    /// <summary>The program name is parsed without escape processing; it only needs quotes.</summary>
    public static string QuoteProgram(string path) => $"\"{path}\"";

    public static string BuildCygwin(string program, IEnumerable<string> args) =>
        string.Join(' ', args.Select(QuoteCygwin).Prepend(QuoteProgram(program)));

    public static string BuildMsvc(string program, IEnumerable<string> args) =>
        string.Join(' ', args.Select(QuoteMsvc).Prepend(QuoteProgram(program)));
}
