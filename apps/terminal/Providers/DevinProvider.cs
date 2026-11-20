using System.Text.Json;
using JobPilot.Terminal.Common;
using JobPilot.Terminal.Hosting;
using JobPilot.Terminal.Install;

namespace JobPilot.Terminal.Providers;

internal sealed class DevinProvider : Provider
{
    public override string Id => "devin";

    public override string DisplayName => "Devin";

    public override string Executable => "devin";

    // Trust prompt would intercept the host's first typed command; the workspace is managed, so skipping is safe.
    // Unattended gets bypass: any permission prompt would stall a pilot run with nobody to answer it.
    public override string[] BuildArgs(string pluginDir, ILogger logger, bool unattended = false) =>
        ["--permission-mode", unattended ? "bypass" : "smart", "--respect-workspace-trust", "false"];

    // Devin loads skills unprefixed from .agents/skills, and a single Enter submits typed commands.
    public override string SkillCommand(string skill) => $"/{skill}";

    // Bypass applies at spawn only via args; a shared dock session needs the in-session flip instead.
    public override string UnattendedCommand => "/bypass";

    // Devin detaches stdin while session internals respawn (after /clear); bytes typed into the gap
    // get flushed or batch-read as pasted text. The idle marker lands once the reader is back.
    public override byte[]? InputReadyPattern => "\x1b]7501;state=idle"u8.ToArray();

    /// <summary>Devin reads .agents/skills and .devin/mcp_config.local.json from the session cwd; both are ours to rebuild.</summary>
    public override void PrepareWorkspace(InstallPaths paths)
    {
        var target = Path.Combine(paths.WorkingDir, ".agents", "skills");
        FileTree.DeleteIfExists(target);
        FileTree.Copy(paths.SkillsDir, target);

        var mcpFile = Path.Combine(paths.PluginDir, ".mcp.json");
        if (!File.Exists(mcpFile))
        {
            return;
        }

        var config = Path.Combine(paths.WorkingDir, ".devin", "mcp_config.local.json");
        Directory.CreateDirectory(Path.GetDirectoryName(config)!);
        File.WriteAllText(config, McpConfig(mcpFile));
    }

    /// <summary>Translates plugin/.mcp.json into devin's mcpServers shape: url-only entries gain transport http.</summary>
    internal static string McpConfig(string mcpFile)
    {
        var mcp = JsonSerializer.Deserialize(File.ReadAllText(mcpFile), AppJsonContext.Default.PluginMcpFile);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("mcpServers");
            writer.WriteStartObject();
            foreach (var (name, server) in mcp?.McpServers ?? [])
            {
                writer.WritePropertyName(name);
                writer.WriteStartObject();
                if (!string.IsNullOrWhiteSpace(server.Url))
                {
                    writer.WriteString("url", server.Url);
                    writer.WriteString("transport", "http");
                }
                else if (!string.IsNullOrWhiteSpace(server.Command))
                {
                    writer.WriteString("command", server.Command);
                    if (server.Args is { Length: > 0 } args)
                    {
                        writer.WritePropertyName("args");
                        writer.WriteStartArray();
                        foreach (var arg in args)
                        {
                            writer.WriteStringValue(arg);
                        }

                        writer.WriteEndArray();
                    }
                }

                writer.WriteEndObject();
            }

            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }
}
