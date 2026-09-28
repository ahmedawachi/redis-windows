using RedisService.Service;

namespace RedisService.Tests;

public class DiagnosticsTests
{
    [Fact]
    public void RingBuffer_KeepsTheLastLinesInOrder()
    {
        var ring = new LineRingBuffer(3);
        foreach (var l in new[] { "a", "b", "c", "d", "e" }) ring.Add(l);
        Assert.Equal(["c", "d", "e"], ring.Snapshot());
        ring.Clear();
        Assert.Empty(ring.Snapshot());
    }

    [Fact]
    public void TokenBucket_LimitsAndRefills()
    {
        var now = DateTimeOffset.UnixEpoch;
        var bucket = new TokenBucket(20, TimeSpan.FromMinutes(1), () => now);
        var taken = Enumerable.Range(0, 25).Count(_ => bucket.TryTake());
        Assert.Equal(20, taken);
        Assert.Equal(5, bucket.TakeSuppressedCount());
        Assert.Equal(0, bucket.TakeSuppressedCount());
        now = now.AddSeconds(30);
        Assert.Equal(10, Enumerable.Range(0, 25).Count(_ => bucket.TryTake()));
    }

    [Fact]
    public void Excerpt_StartsJustBeforeTheBugReport()
    {
        var lines = new List<string>();
        for (var i = 0; i < 50; i++) lines.Add($"normal {i}");
        // The order serverPanic writes (debug.c): OOM line, panic banner, Guru Meditation, then the bug report.
        lines.Add("4242:M 01 Jan 2026 12:00:00.000 # Out Of Memory allocating 2097176 bytes!");
        lines.Add("4242:M 01 Jan 2026 12:00:00.000 # ------------------------------------------------");
        lines.Add("4242:M 01 Jan 2026 12:00:00.000 # !!! Software Failure. Press left mouse button to continue");
        lines.Add("4242:M 01 Jan 2026 12:00:00.000 # Guru Meditation: Redis aborting for OUT OF MEMORY. Allocating 2097176 bytes! #server.c:7823");
        lines.Add("");
        lines.Add("=== REDIS BUG REPORT START: Cut & paste starting from here ===");
        lines.Add("4242:M 01 Jan 2026 12:00:00.006 # Redis version=8.10.2");
        var excerpt = CrashExcerpt.Build(lines);
        Assert.StartsWith("normal 49", excerpt);
        Assert.Contains("Out Of Memory allocating 2097176 bytes", excerpt);
        Assert.Contains("Guru Meditation", excerpt);
        Assert.DoesNotContain("normal 48", excerpt);
    }

    [Fact]
    public void Excerpt_WithoutReport_IsTheLast20Lines()
    {
        var lines = Enumerable.Range(0, 100).Select(i => $"line {i}").ToList();
        var excerpt = CrashExcerpt.Build(lines).Split('\n');
        Assert.Equal(20, excerpt.Length);
        Assert.Equal("line 80", excerpt[0]);
    }

    [Fact]
    public void Excerpt_IsCapped()
    {
        var lines = new List<string> { "REDIS BUG REPORT START" };
        lines.AddRange(Enumerable.Range(0, 1000).Select(i => new string('x', 200)));
        var excerpt = CrashExcerpt.Build(lines);
        Assert.True(excerpt.Length <= CrashExcerpt.MaxChars + 40);
    }

    [Fact]
    public void LogTail_ReadsOnlyThisRun()
    {
        using var tmp = new TempDir();
        var log = tmp.File("redis.log", "old run line 1\nold run line 2\n");
        var offset = CrashExcerpt.FileLength(log);
        File.AppendAllText(log, "new line A\nnew line B\n");
        Assert.Equal(["new line A", "new line B"], CrashExcerpt.ReadLogTail(log, offset));
        Assert.Null(CrashExcerpt.ReadLogTail(Path.Combine(tmp.Path, "missing.log"), 0));
    }

    [Fact]
    public void LogTail_FileTruncatedSinceStart_ReadsFromTheBeginning()
    {
        using var tmp = new TempDir();
        var log = tmp.File("redis.log", "a\n");
        Assert.Equal(["a"], CrashExcerpt.ReadLogTail(log, 10_000));
    }
}
