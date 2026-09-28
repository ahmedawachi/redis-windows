using System.Diagnostics;
using RedisService.Service;

namespace RedisService.Tests;

/// <summary>The real executable in foreground mode (dotnet RedisService.dll), end to end on this OS.</summary>
[Collection("real-redis")]
public class ProgramTests
{
    private static string WrapperDll => Path.Combine(AppContext.BaseDirectory, "RedisService.dll");

    // PATH may not contain dotnet; use the host that runs these tests.
    private static string DotnetHost =>
        Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host && File.Exists(host)
            ? host
            : Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", "..",
                OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"));

    private static Process StartWrapper(params string[] args)
    {
        var psi = new ProcessStartInfo(DotnetHost) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        psi.ArgumentList.Add(WrapperDll);
        foreach (var a in args) psi.ArgumentList.Add(a);
        var p = Process.Start(psi)!;
        return p;
    }

    private static async Task<(int Code, string Output)> RunWrapperAsync(params string[] args)
    {
        using var p = StartWrapper(args);
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
        return (p.ExitCode, await stdout + await stderr);
    }

    [Theory]
    [InlineData("run", "--no-such-flag")]
    [InlineData("run", "--port", "abc")]
    [InlineData("frobnicate")]
    public async Task InvalidCommandLine_Exits2(params string[] args)
    {
        Assert.SkipUnless(File.Exists(WrapperDll), "RedisService.dll not found next to the tests");
        var (code, output) = await RunWrapperAsync(args);
        Assert.Equal(2, code);
        Assert.StartsWith("Error: ", output.Trim());
    }

    [Fact]
    public async Task Help_Exits0()
    {
        var (code, output) = await RunWrapperAsync("--help");
        Assert.Equal(0, code);
        Assert.Contains("--restart-policy", output);
    }

    [Fact]
    public async Task MissingConfig_Exits1()
    {
        Assert.SkipWhen(RedisTestInstance.ServerPath is null, "REDIS_SERVER_PATH not set");
        var (code, output) = await RunWrapperAsync("run", "-f", "--redis-server", RedisTestInstance.ServerPath!, "-c", "/nonexistent/redis.conf");
        Assert.Equal(1, code);
        Assert.Contains("does not exist", output);
    }

    [Fact]
    public async Task Foreground_SigtermStopsGracefully_AndExits0()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "SIGTERM path is Unix-only; Windows uses ci/wrapper-integration.ps1");
        await using var redis = RedisTestInstance.Create("save 3600 1");
        using var p = StartWrapper("run", "--foreground", "--redis-server", RedisTestInstance.ServerPath!, "-c", redis.ConfPath);
        var output = new System.Text.StringBuilder();
        p.OutputDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { lock (output) output.AppendLine(e.Data); };
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();
        try
        {
            await redis.WaitForPongAsync(TimeSpan.FromSeconds(30));
            await redis.CommandAsync("SET", "k", "v");
            Assert.Equal(0, UnixKill(p.Id, 15));
            await p.WaitForExitAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            Assert.Equal(0, p.ExitCode);
            Assert.Contains("User requested shutdown", redis.Log);
            Assert.Contains("DB saved on disk", redis.Log);
            lock (output)
            {
                var text = output.ToString();
                TestContext.Current.TestOutputHelper?.WriteLine(text);
                Assert.Contains("Started redis-server", text);
                Assert.Contains("Stopping redis-server", text);
                Assert.Contains("exiting with code 0", text);
            }
        }
        finally
        {
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "kill")]
    private static extern int UnixKill(int pid, int sig);
}
