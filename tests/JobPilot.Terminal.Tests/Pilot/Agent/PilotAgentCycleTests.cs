using System.Text;
using JobPilot.Terminal.Install;
using JobPilot.Terminal.Pilot.Agent;
using JobPilot.Terminal.Pilot.Pairing;
using JobPilot.Terminal.Pilot.Telemetry;
using JobPilot.Terminal.Providers;
using JobPilot.Terminal.Sessions;
using JobPilot.Terminal.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Pty.Net;
using Xunit;
using static JobPilot.Terminal.Tests.Support.Builders;

namespace JobPilot.Terminal.Tests.Pilot.Agent;

public sealed class PilotAgentCycleTests : IDisposable
{
    private readonly TempDir temp = new();
    private readonly List<FakePtyConnection> spawned = [];
    private readonly TerminalSession terminal;
    private readonly PairingStore store;
    private readonly PilotAgent pilot;

    public PilotAgentCycleTests()
    {
        temp.WriteValidPluginTree();
        var install = new HostInstall(temp.Paths);
        terminal = new TerminalSession(
            install,
            new ScratchCleaner(new HostInstall(paths: null), NullLogger<ScratchCleaner>.Instance),
            NullLogger<TerminalSession>.Instance,
            Spawn);
        store = new PairingStore(install, NullLogger<PairingStore>.Instance);
        var api = new FakePilot();
        pilot = new PilotAgent(
            terminal,
            store,
            api,
            new UsageMeter(),
            new StepRecorder(api),
            NullLogger<PilotAgent>.Instance,
            inputReadyTimeout: TimeSpan.FromMilliseconds(400));
    }

    public void Dispose()
    {
        pilot.Dispose();
        terminal.Dispose();
        temp.Dispose();
    }

    private FakePtyConnection Live => spawned[^1];

    private IPtyConnection Spawn(PtyOptions options)
    {
        var connection = new FakePtyConnection();
        connection.EnablePushReader();
        spawned.Add(connection);
        return connection;
    }

    private static string Written(FakePtyConnection connection) =>
        string.Concat(connection.Writes.Select(Encoding.UTF8.GetString));

    [Fact]
    public async Task SendCycle_WaitsForTheDevinIdleMarker_AfterClear()
    {
        var pairing = NewPairing() with { Provider = Provider.Devin };
        store.Save(pairing);
        pilot.Start(pairing);
        var conn = Live;

        var cycle = pilot.SendCycleAsync(pairing.Provider, "run-1", TestContext.Current.CancellationToken);
        await TestWait.Until(() => Written(conn).Contains("/clear"));
        await Task.Delay(300, TestContext.Current.CancellationToken);
        Assert.DoesNotContain("/bypass", Written(conn));

        // The marker straddling two output chunks must still count.
        conn.PushOutput("\x1b]7501;state=id"u8.ToArray());
        conn.PushOutput("le:app=devin\x1b\\"u8.ToArray());

        await cycle;
        var written = Written(conn);
        Assert.Contains("/bypass", written);
        Assert.Contains("/pilot run-1", written);
    }

    [Fact]
    public async Task SendCycle_SendsAnyway_WhenTheIdleMarkerNeverComes()
    {
        var pairing = NewPairing() with { Provider = Provider.Devin };
        store.Save(pairing);
        pilot.Start(pairing);
        var conn = Live;

        await pilot.SendCycleAsync(pairing.Provider, "run-2", TestContext.Current.CancellationToken);

        var written = Written(conn);
        Assert.Contains("/bypass", written);
        Assert.Contains("/pilot run-2", written);
    }

    [Fact]
    public async Task SendCycle_DoesNotWaitForAMarker_WhenTheProviderHasNone()
    {
        var pairing = NewPairing() with { Provider = Provider.Claude };
        store.Save(pairing);
        pilot.Start(pairing);
        var conn = Live;

        await pilot.SendCycleAsync(pairing.Provider, "run-3", TestContext.Current.CancellationToken);

        Assert.Contains("/jobpilot:pilot run-3", Written(conn));
    }
}
