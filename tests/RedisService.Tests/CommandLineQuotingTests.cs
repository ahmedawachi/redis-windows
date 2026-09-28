using System.Text;
using RedisService.Service;

namespace RedisService.Tests;

public class CommandLineQuotingTests
{
    public static TheoryData<string> Tricky =>
    [
        "plain",
        "",
        "with space",
        @"C:\Program Files\Redis\",
        @"D:\data\",
        "quote\"inside",
        @"back\\slash\\""mix",
        @"trailing\",
        "O'Brien",
        "@notafile",
        "star*and?[glob]",
        "~tilde",
        "tab\tchar",
    ];

    [Theory]
    [MemberData(nameof(Tricky))]
    public void Msvc_RoundTrips(string arg)
    {
        var line = WindowsCommandLine.QuoteMsvc(arg);
        Assert.Equal([arg], ParseMsvc(line));
    }

    [Fact]
    public void Msvc_TrailingBackslashDoesNotEatTheNextArgument()
    {
        // The upstream quoting bug: --dir "D:\data\" swallowed everything after it.
        var line = string.Join(' ', new[] { "--dir", @"D:\data\", "--port", "6380" }.Select(WindowsCommandLine.QuoteMsvc));
        Assert.Equal(["--dir", @"D:\data\", "--port", "6380"], ParseMsvc(line));
    }

    [Fact]
    public void Msvc_PlainArgumentsAreNotQuoted() => Assert.Equal("--port", WindowsCommandLine.QuoteMsvc("--port"));

    [Theory]
    [MemberData(nameof(Tricky))]
    public void Cygwin_RoundTrips(string arg)
    {
        var quoted = WindowsCommandLine.QuoteCygwin(arg);
        Assert.StartsWith("\"", quoted);
        Assert.Equal(arg, ParseCygwinQuotedWord(quoted));
    }

    [Fact]
    public void BuildCygwin_QuotesProgramAndEveryArgument() =>
        Assert.Equal("\"C:\\Redis\\redis-server.exe\" \"C:/Redis/redis.conf\" \"--port\" \"6379\"",
            WindowsCommandLine.BuildCygwin(@"C:\Redis\redis-server.exe", ["C:/Redis/redis.conf", "--port", "6379"]));

    /// <summary>Reference implementation of the Microsoft C runtime argument rules.</summary>
    private static List<string> ParseMsvc(string line)
    {
        var args = new List<string>();
        var i = 0;
        while (i < line.Length)
        {
            while (i < line.Length && line[i] is ' ' or '\t') i++;
            if (i >= line.Length) break;
            var sb = new StringBuilder();
            var inQuotes = false;
            while (i < line.Length && (inQuotes || line[i] is not (' ' or '\t')))
            {
                var backslashes = 0;
                while (i < line.Length && line[i] == '\\') { backslashes++; i++; }
                if (i < line.Length && line[i] == '"')
                {
                    sb.Append('\\', backslashes / 2);
                    if (backslashes % 2 == 1) sb.Append('"');
                    else inQuotes = !inQuotes;
                    i++;
                }
                else
                {
                    sb.Append('\\', backslashes);
                    if (i < line.Length && (inQuotes || line[i] is not (' ' or '\t'))) sb.Append(line[i++]);
                }
            }
            args.Add(sb.ToString());
        }
        return args;
    }

    /// <summary>
    /// Reference for a fully double-quoted word as Cygwin's dcrt0.cc parses it for a non-Cygwin parent:
    /// quoted() finds the end (a backslash skips the next character), then globify() takes \\ and \" literally and
    /// every other character as is; with no glob match GLOB_NOCHECK returns the unescaped pattern.
    /// </summary>
    private static string ParseCygwinQuotedWord(string word)
    {
        Assert.Equal('"', word[0]);
        var sb = new StringBuilder();
        var i = 1;
        while (i < word.Length && word[i] != '"')
        {
            if (word[i] == '\\' && i + 1 < word.Length && word[i + 1] is '"' or '\\') i++;
            sb.Append(word[i]);
            i++;
        }
        Assert.Equal(word.Length - 1, i); // The closing quote ends the word.
        return sb.ToString();
    }
}
