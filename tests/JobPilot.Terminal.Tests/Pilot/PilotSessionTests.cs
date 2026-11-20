using System.Text;
using JobPilot.Terminal.Hosting;
using JobPilot.Terminal.Pilot;
using JobPilot.Terminal.Providers;
using JobPilot.Terminal.Sessions;
using Microsoft.Extensions.Logging.Abstractions;
using Pty.Net;
using Xunit;
using static JobPilot.Terminal.Tests.Builders;

namespace JobPilot.Terminal.Tests;

public sealed class PilotSessionTests : IDisposable
{
    private readonly TempDir temp = new();
    private readonly List<FakePtyConnection> spawned = [];
    private readonly TerminalSession terminal;
    private readonly PilotStore store;
    private readonly PilotSession pilot;

    public PilotSessionTests()
    {
        temp.WriteValidPluginTree();
        var paths = new InstallPaths { WorkingDir = temp.Root, PluginDir = Path.Combine(temp.Root, "plugin") };
        terminal = new TerminalSession(
            new HostInstall(paths),
            new ScratchCleaner(new HostInstall(paths: null), NullLogger<ScratchCleaner>.Instance),
            NullLogger<TerminalSession>.Instance,
            Spawn);
        store = new PilotStore(Path.Combine(temp.Root, "pilot.json"), NullLogger<PilotStore>.Instance);
        var api = new PilotApi(new HttpClient(), NullLogger<PilotApi>.Instance);
        pilot = new PilotSession(
            terminal,
            store,
            api,
            new UsageMeter(),
            NullLogger<PilotSession>.Instance,
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
        var settings = Settings() with { Provider = Provider.Devin };
        store.Save(settings);
        pilot.Start(settings);
        var conn = Live;

        var cycle = pilot.SendCycleAsync(settings, "run-1", TestContext.Current.CancellationToken);
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
        var settings = Settings() with { Provider = Provider.Devin };
        store.Save(settings);
        pilot.Start(settings);
        var conn = Live;

        await pilot.SendCycleAsync(settings, "run-2", TestContext.Current.CancellationToken);

        var written = Written(conn);
        Assert.Contains("/bypass", written);
        Assert.Contains("/pilot run-2", written);
    }

    [Fact]
    public async Task SendCycle_DoesNotWaitForAMarker_WhenTheProviderHasNone()
    {
        var settings = Settings() with { Provider = Provider.Claude };
        store.Save(settings);
        pilot.Start(settings);
        var conn = Live;

        await pilot.SendCycleAsync(settings, "run-3", TestContext.Current.CancellationToken);

        Assert.Contains("/jobpilot:pilot run-3", Written(conn));
    }
}
