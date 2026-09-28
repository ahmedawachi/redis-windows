using System.Text;

namespace RedisService.Service;

/// <summary>Thread-safe ring buffer of the most recent output lines, used for crash excerpts when there is no logfile.</summary>
public sealed class LineRingBuffer(int capacity)
{
    private readonly string[] _lines = new string[capacity];
    private readonly Lock _lock = new();
    private int _next;
    private int _count;

    public int Capacity { get; } = capacity;

    public void Add(string line)
    {
        lock (_lock)
        {
            _lines[_next] = line;
            _next = (_next + 1) % Capacity;
            if (_count < Capacity) _count++;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _next = 0;
            _count = 0;
            Array.Clear(_lines);
        }
    }

    public IReadOnlyList<string> Snapshot()
    {
        lock (_lock)
        {
            var result = new List<string>(_count);
            var start = (_next - _count + Capacity) % Capacity;
            for (var i = 0; i < _count; i++) result.Add(_lines[(start + i) % Capacity]);
            return result;
        }
    }
}

/// <summary>Token bucket used to keep a noisy stderr from flooding the Event Log.</summary>
public sealed class TokenBucket(int capacity, TimeSpan refillPeriod, Func<DateTimeOffset>? clock = null)
{
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly Lock _lock = new();
    private double _tokens = capacity;
    private DateTimeOffset _last = (clock ?? (() => DateTimeOffset.UtcNow))();
    private int _suppressed;

    /// <summary>Takes a token. When none is left, counts the event as suppressed and returns false.</summary>
    public bool TryTake()
    {
        lock (_lock)
        {
            Refill();
            if (_tokens >= 1)
            {
                _tokens -= 1;
                return true;
            }
            _suppressed++;
            return false;
        }
    }

    /// <summary>Returns how many events were suppressed since the last call, and resets the count.</summary>
    public int TakeSuppressedCount()
    {
        lock (_lock)
        {
            var n = _suppressed;
            _suppressed = 0;
            return n;
        }
    }

    private void Refill()
    {
        var now = _clock();
        var elapsed = now - _last;
        if (elapsed <= TimeSpan.Zero) return;
        _tokens = Math.Min(capacity, _tokens + elapsed / refillPeriod * capacity);
        _last = now;
    }
}

/// <summary>Builds the Redis output excerpt attached to the crash event.</summary>
public static class CrashExcerpt
{
    public const int MaxLines = 120;
    public const int MaxChars = 12_000;

    /// <summary>
    /// From this run's output lines: the bug report (with the panic lines just before "REDIS BUG REPORT START") when there is one,
    /// otherwise the last 20 lines. Capped to <see cref="MaxLines"/> lines and <see cref="MaxChars"/> characters.
    /// </summary>
    public static string Build(IReadOnlyList<string> lines)
    {
        if (lines.Count == 0) return "";
        // serverPanic prints the OOM line, "Software Failure" and "Guru Meditation" just before
        // "=== REDIS BUG REPORT START", so a few lines of context in front of the marker belong to the report.
        var report = LastIndexOf(lines, "REDIS BUG REPORT START");
        var guru = LastIndexOf(lines, "Guru Meditation");
        var start = report >= 0 ? Math.Max(0, report - 6) : guru >= 0 ? Math.Max(0, guru - 3) : -1;

        IEnumerable<string> selected = start >= 0
            ? lines.Skip(start).Take(MaxLines)
            : lines.Skip(Math.Max(0, lines.Count - 20));

        var sb = new StringBuilder();
        foreach (var line in selected)
        {
            if (sb.Length + line.Length + 1 > MaxChars)
            {
                sb.Append("[excerpt truncated]");
                break;
            }
            sb.Append(line).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    private static int LastIndexOf(IReadOnlyList<string> lines, string marker)
    {
        for (var i = lines.Count - 1; i >= 0; i--)
        {
            if (lines[i].Contains(marker, StringComparison.Ordinal)) return i;
        }
        return -1;
    }

    /// <summary>
    /// Reads what was appended to the logfile since <paramref name="startOffset"/> (the file size when this run started),
    /// limited to the last <paramref name="maxBytes"/> bytes. Returns null when the file cannot be read.
    /// </summary>
    public static IReadOnlyList<string>? ReadLogTail(string path, long startOffset, int maxBytes = 256 * 1024)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = fs.Length;
            if (startOffset > length) startOffset = 0; // Rotated or truncated since the run started.
            var from = Math.Max(startOffset, length - maxBytes);
            fs.Seek(from, SeekOrigin.Begin);
            using var reader = new StreamReader(fs, Encoding.UTF8);
            var text = reader.ReadToEnd();
            var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
            if (from > startOffset && lines.Count > 0) lines.RemoveAt(0); // Partial first line.
            while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            return lines;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static long FileLength(string? path)
    {
        if (path is null) return 0;
        try
        {
            return File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
