using System.Text.Json;
using JobPilot.Terminal.Providers;
using JobPilot.Terminal.Tests.Support;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace JobPilot.Terminal.Tests.Providers;

public sealed class DevinProviderTests : IDisposable
{
    private readonly TempDir temp = new();

    public void Dispose() => temp.Dispose();

    [Fact]
    public void BuildArgs_SkipsTrustPrompt_AndRunsSmart()
    {
        Assert.Equal(
            ["--permission-mode", "smart", "--respect-workspace-trust", "false"],
            Provider.Devin.BuildArgs(temp.Root, NullLogger.Instance));
    }

    [Fact]
    public void BuildArgs_Unattended_RunsBypass()
    {
        Assert.Equal(
            ["--permission-mode", "bypass", "--respect-workspace-trust", "false"],
            Provider.Devin.BuildArgs(temp.Root, NullLogger.Instance, unattended: true));
    }

    [Fact]
    public void UnattendedCommand_IsBypass() => Assert.Equal("/bypass", Provider.Devin.UnattendedCommand);

    [Fact]
    public void InputReadyPattern_IsTheDevinIdleMarker()
    {
        Assert.Equal("\x1b]7501;state=idle"u8.ToArray(), Provider.Devin.InputReadyPattern);
        Assert.Null(Provider.Claude.InputReadyPattern);
        Assert.Null(Provider.Codex.InputReadyPattern);
    }

    [Fact]
    public void PrepareWorkspace_CopiesSkills_KeepingSetup()
    {
        temp.File(Path.Combine("plugin", "skills", "pilot", "SKILL.md"), "# pilot");
        temp.File(Path.Combine("plugin", "skills", "setup", "SKILL.md"), "# setup");
        temp.File(Path.Combine("plugin", "skills", "_shared", "setup.md"), "# shared");

        Provider.Devin.PrepareWorkspace(temp.Paths);

        Assert.True(File.Exists(Path.Combine(temp.Root, ".agents", "skills", "pilot", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(temp.Root, ".agents", "skills", "setup", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(temp.Root, ".agents", "skills", "_shared", "setup.md")));
    }

    [Fact]
    public void PrepareWorkspace_WritesProjectMcpConfig()
    {
        temp.File(Path.Combine("plugin", "skills", "pilot", "SKILL.md"), "# pilot");
        temp.File(
            Path.Combine("plugin", ".mcp.json"),
            """
            {"mcpServers":{
              "playwright":{"command":"npx","args":["@playwright/mcp@latest","--user-data-dir",".playwright-mcp"]},
              "upwork":{"type":"http","url":"https://mcp.upwork.com/mcp"}
            }}
            """);

        Provider.Devin.PrepareWorkspace(temp.Paths);

        var config = Path.Combine(temp.Root, ".devin", "mcp_config.local.json");
        Assert.True(File.Exists(config));
        using var doc = JsonDocument.Parse(File.ReadAllText(config));
        var servers = doc.RootElement.GetProperty("mcpServers");

        var playwright = servers.GetProperty("playwright");
        Assert.Equal("npx", playwright.GetProperty("command").GetString());
        Assert.Equal("@playwright/mcp@latest", playwright.GetProperty("args")[0].GetString());

        var upwork = servers.GetProperty("upwork");
        Assert.Equal("https://mcp.upwork.com/mcp", upwork.GetProperty("url").GetString());
        Assert.Equal("http", upwork.GetProperty("transport").GetString());
        Assert.False(upwork.TryGetProperty("type", out _));
    }

    [Fact]
    public void PrepareWorkspace_SkipsMcp_WhenPluginManifestMissing()
    {
        temp.File(Path.Combine("plugin", "skills", "pilot", "SKILL.md"), "# pilot");

        Provider.Devin.PrepareWorkspace(temp.Paths);

        Assert.False(File.Exists(Path.Combine(temp.Root, ".devin", "mcp_config.local.json")));
    }
}
