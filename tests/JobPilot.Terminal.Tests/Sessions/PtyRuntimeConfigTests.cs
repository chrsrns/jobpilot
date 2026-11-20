using System.Text.Json;
using Xunit;

namespace JobPilot.Terminal.Tests.Sessions;

public class PtyRuntimeConfigTests
{
    // Pty.Net's forkpty path segfaults under CoreCLR W^X on Linux; the env var in Program.cs
    // only satisfies Pty.Net's own check. The runtime reads the knob from runtimeconfig at boot.
    [Fact]
    public void RuntimeConfig_DisablesWriteXorExecute()
    {
        var repo = new DirectoryInfo(AppContext.BaseDirectory);
        while (!File.Exists(Path.Combine(repo.FullName, "JobPilot.slnx")))
        {
            repo = repo.Parent!;
        }

        var template = Path.Combine(repo.FullName, "apps", "terminal", "runtimeconfig.template.json");
        using var doc = JsonDocument.Parse(File.ReadAllText(template));
        var value = doc.RootElement
            .GetProperty("configProperties")
            .GetProperty("System.Runtime.EnableWriteXorExecute");

        Assert.Equal(0, value.GetInt32());
    }
}
