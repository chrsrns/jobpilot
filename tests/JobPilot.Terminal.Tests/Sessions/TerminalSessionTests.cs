using JobPilot.Terminal.Install;
using JobPilot.Terminal.Providers;
using JobPilot.Terminal.Sessions;
using JobPilot.Terminal.Sessions.Pty;
using JobPilot.Terminal.Sessions.Relay;
using JobPilot.Terminal.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Pty.Net;
using Xunit;

namespace JobPilot.Terminal.Tests.Sessions;

public sealed class TerminalSessionTests : IDisposable
{
    private static readonly byte[] Enter = "\r"u8.ToArray();

    private readonly TempDir temp = new();
    private readonly List<FakePtyConnection> spawned = [];
    private readonly TerminalSession session;
    private PtyOptions? lastOptions;
    private Exception? spawnFailure;

    public TerminalSessionTests()
    {
        temp.WriteValidPluginTree();
        var paths = temp.Paths;
        session = Create(new HostInstall(paths));
    }

    public void Dispose()
    {
        session.Dispose();
        temp.Dispose();
    }

    private FakePtyConnection Live => spawned[^1];

    private TerminalSession Create(
        HostInstall install,
        Func<IPtyConnection, bool>? canonicalProbe = null,
        TimeSpan? inputModeTimeout = null) => new(
        install,
        new ScratchCleaner(new HostInstall(paths: null), NullLogger<ScratchCleaner>.Instance),
        NullLogger<TerminalSession>.Instance,
        Spawn,
        canonicalProbe,
        inputModeTimeout);

    private IPtyConnection Spawn(PtyOptions options)
    {
        if (spawnFailure is not null)
        {
            throw spawnFailure;
        }

        lastOptions = options;
        var connection = new FakePtyConnection();
        spawned.Add(connection);
        return connection;
    }

    private void Start(Provider? provider = null) => session.Start(provider ?? Provider.Claude, 80, 24, Builders.Api);

    [Fact]
    public void Start_RunsTheRequestedProvider_AndPreparesItsWorkspace()
    {
        Start(Provider.Codex);

        Assert.True(session.IsRunning);
        Assert.Same(Provider.Codex, session.ActiveProvider);
        Assert.Equal("codex", lastOptions!.App);
        Assert.True(File.Exists(Path.Combine(temp.Root, ".agents", "skills", "auto-apply", "SKILL.md")));
    }

    [Fact]
    public void Start_IsANoOp_WhenTheSameProviderIsAlreadyRunning()
    {
        Start(Provider.Claude);
        Start(Provider.Claude);

        Assert.Single(spawned);
    }

    [Fact]
    public void Start_UnattendedRespawnsAnInteractiveSession_WithoutDowngradingBack()
    {
        Start(Provider.Devin);
        var interactive = Live;

        session.Start(Provider.Devin, 80, 24, unattended: true);

        Assert.Equal(2, spawned.Count);
        Assert.Equal(1, interactive.KillCalls);
        Assert.Contains("bypass", lastOptions!.CommandLine);

        // A plain start on the unattended session is a no-op, not a downgrade.
        session.Start(Provider.Devin, 80, 24);
        Assert.Equal(2, spawned.Count);
    }

    [Fact]
    public void Start_ReplacesAnotherProvider_AndKillsItsProcess()
    {
        Start(Provider.Claude);
        var replaced = Live;

        Start(Provider.Codex);

        Assert.Equal(2, spawned.Count);
        Assert.Equal(1, replaced.KillCalls);
        Assert.Same(Provider.Codex, session.ActiveProvider);
    }

    [Fact]
    public void Start_GivesTheAgentItsEnvironment()
    {
        session.Start(Provider.Claude, 80, 24, Builders.Api);

        var env = lastOptions!.Environment;
        Assert.Equal(Path.Combine(temp.Root, "plugin", "skills"), env["JOBPILOT_SKILLS_ROOT"]);
        Assert.Equal(temp.Root, env["JOBPILOT_WORKSPACE_ROOT"]);
        Assert.Equal(Path.Combine(temp.Root, ".temp"), env["JOBPILOT_TEMP"]);
        Assert.True(Directory.Exists(env["JOBPILOT_TEMP"]));
        Assert.Equal("tok", env["JOBPILOT_API_TOKEN"]);
        Assert.Equal("https://api", env["JOBPILOT_API"]);
        Assert.Equal("https://web", env["JOBPILOT_WEB"]);
        Assert.StartsWith(Path.Combine(temp.Root, "plugin", "bin") + Path.PathSeparator, env["PATH"]);
    }

    [Fact]
    public void Start_LeavesTheSessionStopped_WhenTheProviderCannotSpawn()
    {
        spawnFailure = new FileNotFoundException("claude not on PATH");
        Assert.Throws<PtyStartException>(() => Start());
        Assert.False(session.IsRunning);
    }

    [Fact]
    public void Start_Throws_WhenTheInstallIsIncomplete()
    {
        using var broken = Create(new HostInstall(paths: null, pathsError: "no plugin tree"));

        var ex = Assert.Throws<InvalidOperationException>(() => broken.Start(Provider.Claude, 80, 24, Builders.Api));
        Assert.Contains("no plugin tree", ex.Message);
    }

    [Fact]
    public async Task NaturalExit_StopsTheSession_AndShowsTheCrashBanner()
    {
        Start();
        SessionExit? seen = null;
        session.Exited += exit => seen = exit;

        Live.RaiseExit(7);

        await TestWait.Until(() => seen is not null);
        Assert.False(session.IsRunning);
        Assert.False(seen!.Value.Requested);
        Assert.Contains("exited with code 7", TerminalRelay.ExitBanner(seen.Value));
    }

    [Fact]
    public void ExitOfAReplacedProcess_IsIgnored()
    {
        Start(Provider.Claude);
        var replaced = Live;
        Start(Provider.Codex);

        replaced.RaiseExit();

        Assert.True(session.IsRunning);
        Assert.Same(Provider.Codex, session.ActiveProvider);
    }

    [Fact]
    public void Stop_ReportsARequestedExitOnce_WithNoBanner()
    {
        Start();
        var stopped = Live;
        var exits = new List<SessionExit>();
        session.Exited += exits.Add;

        session.Stop();
        stopped.RaiseExit(); // the kill's own exit event must not report again
        session.Stop();

        var exit = Assert.Single(exits);
        Assert.True(exit.Requested);
        Assert.Null(TerminalRelay.ExitBanner(exit));
    }

    [Fact]
    public async Task SendCommand_WaitsForRawMode_BeforeWriting()
    {
        // Canonical-mode ttys echo and line-buffer input; the TUI then receives "cmd\n" as pasted
        // text where Enter never submits — so sends must wait for ICANON to clear (B73).
        var canonical = true;
        var paths = new InstallPaths { WorkingDir = temp.Root, PluginDir = Path.Combine(temp.Root, "plugin") };
        using var gated = Create(new HostInstall(paths), canonicalProbe: _ => canonical);
        gated.Start(Provider.Claude, 80, 24);

        var send = gated.SendCommandAsync("/bypass", ct: TestContext.Current.CancellationToken);
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.Empty(Live.Writes);

        canonical = false;
        Assert.Equal(SendResult.Sent, await send);
        Assert.Equal(["/bypass"u8.ToArray(), Enter], Live.Writes);
    }

    [Fact]
    public async Task SendCommand_RechecksRawMode_BeforePressingEnter()
    {
        // The TUI drops back to canonical mid-command (devin respawns session internals on /clear):
        // hold the window open exactly while the written command awaits its Enter.
        var hold = true;
        var paths = new InstallPaths { WorkingDir = temp.Root, PluginDir = Path.Combine(temp.Root, "plugin") };
        using var gated = Create(new HostInstall(paths), canonicalProbe: _ => hold && Live.Writes.Count == 1);
        gated.Start(Provider.Claude, 80, 24);

        var send = gated.SendCommandAsync("/clear", ct: TestContext.Current.CancellationToken);
        await TestWait.Until(() => Live.Writes.Count == 1);
        await Task.Delay(200, TestContext.Current.CancellationToken);
        Assert.Single(Live.Writes);

        hold = false;
        Assert.Equal(SendResult.Sent, await send);
        Assert.Equal(["/clear"u8.ToArray(), Enter], Live.Writes);
    }

    [Fact]
    public async Task SendCommand_SendsAnyway_WhenRawModeNeverComes()
    {
        var paths = new InstallPaths { WorkingDir = temp.Root, PluginDir = Path.Combine(temp.Root, "plugin") };
        using var gated = Create(
            new HostInstall(paths),
            canonicalProbe: _ => true,
            inputModeTimeout: TimeSpan.FromMilliseconds(250));
        gated.Start(Provider.Claude, 80, 24);

        var result = await gated.SendCommandAsync("/pilot x", ct: TestContext.Current.CancellationToken);

        Assert.Equal(SendResult.Sent, result);
        Assert.Equal(["/pilot x"u8.ToArray(), Enter], Live.Writes);
    }

    [Fact]
    public async Task SendCommand_WritesTheCommandThenASeparateEnter()
    {
        Start();

        var result = await session.SendCommandAsync("/jobpilot:setup", ct: TestContext.Current.CancellationToken);

        Assert.Equal(SendResult.Sent, result);
        Assert.Equal(["/jobpilot:setup"u8.ToArray(), Enter], Live.Writes);
    }

    [Fact]
    public async Task SendCommand_PressesEnterTwice_ForACodexSkill()
    {
        Start(Provider.Codex);

        await session.SendCommandAsync("$pilot", ct: TestContext.Current.CancellationToken);

        Assert.Equal(["$pilot"u8.ToArray(), Enter, Enter], Live.Writes);
    }

    [Fact]
    public async Task SendCommand_IsRejected_WhenStoppedOrForAnotherProvider()
    {
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal(SendResult.NotRunning, await session.SendCommandAsync("x", ct: ct));

        Start(Provider.Claude);
        Assert.Equal(SendResult.ProviderMismatch, await session.SendCommandAsync("x", Provider.Codex, ct));
        Assert.Empty(Live.Writes);
    }

    [Fact]
    public async Task SendCommand_DoesNotPressEnter_WhenTheSessionIsReplacedDuringTheDelay()
    {
        Start(Provider.Claude);
        var original = Live;

        var send = session.SendCommandAsync("rm -rf /", ct: TestContext.Current.CancellationToken);
        await Task.Delay(15, TestContext.Current.CancellationToken);
        Start(Provider.Codex);

        Assert.Equal(SendResult.NotRunning, await send);
        Assert.DoesNotContain(original.Writes, w => w.AsSpan().SequenceEqual(Enter));
        Assert.Empty(Live.Writes);
    }

    [Fact]
    public async Task SendCommand_DoesNotPressEnter_WhenTheCallerCancelsDuringTheDelay()
    {
        Start();
        using var cts = new CancellationTokenSource();

        var send = session.SendCommandAsync("hello", ct: cts.Token);
        await Task.Delay(15, TestContext.Current.CancellationToken);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => send);
        Assert.Single(Live.Writes);
    }
}
