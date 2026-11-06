using System.Text;
using JobPilot.Terminal.Install;
using JobPilot.Terminal.Providers;
using JobPilot.Terminal.Sessions.Pty;
using Pty.Net;

namespace JobPilot.Terminal.Sessions;

/// <summary>How a session ended. A null exit code means it was stopped on request.</summary>
public readonly record struct SessionExit(string ProviderDisplayName, int? ExitCode)
{
    public bool Requested => ExitCode is null;
}

public enum SendResult
{
    Sent,
    NotRunning,
    ProviderMismatch,
}

/// <summary>The one live provider session. Replacing or stopping it disposes its process, so stale exits never land.</summary>
public sealed class TerminalSession : IDisposable
{
    private static readonly byte[] EnterKey = "\r"u8.ToArray();

    // Sending Enter separately keeps provider TUIs from treating it as pasted text.
    private static readonly TimeSpan SubmitKeyDelay = TimeSpan.FromMilliseconds(75);

    private static readonly TimeSpan InputModePoll = TimeSpan.FromMilliseconds(100);
    private static readonly TimeSpan DefaultInputModeTimeout = TimeSpan.FromSeconds(30);

    private readonly HostInstall install;
    private readonly ScratchCleaner scratch;
    private readonly Func<PtyOptions, IPtyConnection> spawn;
    private readonly Func<IPtyConnection, bool>? canonicalProbe;
    private readonly TimeSpan inputModeTimeout;
    private readonly ILogger<TerminalSession> logger;
    private readonly Lock sync = new();

    private volatile PtyProcess? current;
    private volatile Provider activeProvider = Provider.Claude;
    private bool activeUnattended;

    public TerminalSession(HostInstall install, ScratchCleaner scratch, ILogger<TerminalSession> logger)
        : this(install, scratch, logger, PtyProcess.SpawnWithPtyNet)
    {
    }

    internal TerminalSession(
        HostInstall install,
        ScratchCleaner scratch,
        ILogger<TerminalSession> logger,
        Func<PtyOptions, IPtyConnection> spawn,
        Func<IPtyConnection, bool>? canonicalProbe = null,
        TimeSpan? inputModeTimeout = null)
    {
        this.install = install;
        this.scratch = scratch;
        this.logger = logger;
        this.spawn = spawn;
        this.canonicalProbe = canonicalProbe;
        this.inputModeTimeout = inputModeTimeout ?? DefaultInputModeTimeout;
    }

    public event Action<byte[]>? Output;

    /// <summary>Raised just before a new process spawns, so replay buffers reset ahead of its output.</summary>
    public event Action? Starting;

    public event Action<SessionExit>? Exited;

    public bool IsRunning => current is not null;

    /// <summary>The running or last started provider.</summary>
    public Provider ActiveProvider => activeProvider;

    /// <summary>Starts a provider unless it is already running, replacing any other one.</summary>
    /// <returns>True when a new process spawned (fresh start or respawn), false on a no-op.</returns>
    /// <exception cref="PtyStartException">The process could not be spawned.</exception>
    public bool Start(Provider provider, int cols, int rows, ApiConnection api, bool unattended = false)
    {
        lock (sync)
        {
            var paths = install.RequirePaths();
            // An unattended request upgrades an interactive session; a normal start never downgrades one.
            if (current is not null && provider == activeProvider && (!unattended || activeUnattended))
            {
                logger.LogInformation("{Provider} session already running; Start is a no-op.", provider.Id);
                return false;
            }

            // Before the outgoing session stops, so a failed preparation leaves it alive.
            provider.PrepareWorkspace(paths);

            if (current is not null)
            {
                logger.LogInformation(
                    "Replacing {Previous} session with {Next} (unattended {PrevMode} -> {NextMode}).",
                    activeProvider.Id,
                    provider.Id,
                    activeUnattended,
                    unattended);
                current.Dispose();
                current = null;
            }

            scratch.CleanForNewSession(paths);
            var args = provider.BuildArgs(paths.PluginDir, logger, unattended);
            logger.LogInformation(
                "Starting {Provider}: cwd={Cwd} executable={Executable} args={Args} cols={Cols} rows={Rows}",
                provider.DisplayName,
                paths.WorkingDir,
                provider.Executable,
                string.Join(" ", args),
                cols,
                rows);

            var options = new PtyOptions
            {
                App = provider.Executable,
                CommandLine = args,
                Cwd = paths.WorkingDir,
                Cols = cols,
                Rows = rows,
                ForceWinPty = false,
                Environment = SessionEnvironment.Build(paths, api),
            };

            Starting?.Invoke();

            var process = new PtyProcess(options, spawn, logger, canonicalProbe);
            process.Output += data => Output?.Invoke(data);
            process.Exited += exitCode => OnExited(process, exitCode);
            try
            {
                process.Start();
            }
            catch (PtyStartException ex)
            {
                logger.LogError(ex, "Failed to start {Provider} PTY.", provider.Id);
                throw;
            }

            current = process;
            activeProvider = provider;
            activeUnattended = unattended;
            return true;
        }
    }

    /// <summary>Types a command into the session and submits it.</summary>
    public async Task<SendResult> SendCommandAsync(string command, Provider? expectedProvider = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(command);

        PtyProcess process;
        int submitKeyPresses;
        lock (sync)
        {
            if (current is null)
            {
                return SendResult.NotRunning;
            }

            if (expectedProvider is not null && expectedProvider != activeProvider)
            {
                logger.LogWarning("Rejected a command for {Expected}: {Actual} is active.", expectedProvider.Id, activeProvider.Id);
                return SendResult.ProviderMismatch;
            }

            process = current;
            submitKeyPresses = activeProvider.SubmitKeyPresses(command);
        }

        await WaitForRawInputAsync(process, ct);
        lock (sync)
        {
            // A session replaced while waiting must not receive a command it is not running for.
            if (current != process)
            {
                logger.LogWarning("Dropped a command: the session ended or was replaced while waiting for input mode.");
                return SendResult.NotRunning;
            }

            process.Write(Encoding.UTF8.GetBytes(command));
        }

        for (var press = 0; press < submitKeyPresses; press++)
        {
            await Task.Delay(SubmitKeyDelay, ct);
            await WaitForRawInputAsync(process, ct);
            lock (sync)
            {
                // A session replaced during the delay must not receive Enter for a command it never saw.
                if (current != process)
                {
                    logger.LogWarning("Dropped the submit key: the session ended or was replaced mid-command.");
                    return SendResult.NotRunning;
                }

                process.Write(EnterKey);
            }
        }

        return SendResult.Sent;
    }

    /// <summary>
    /// Holds sends while the tty is in canonical mode: written bytes echo and line-buffer, so the
    /// TUI receives them later as pasted text and Enter never submits (devin drops back to
    /// canonical while it respawns session internals after /clear). Fails open with a warning.
    /// </summary>
    private async Task WaitForRawInputAsync(PtyProcess process, CancellationToken ct)
    {
        var deadline = DateTime.UtcNow + inputModeTimeout;
        while (process.InputIsCanonical)
        {
            if (DateTime.UtcNow >= deadline)
            {
                logger.LogWarning("PTY input stayed canonical for {Seconds}s; sending anyway.", inputModeTimeout.TotalSeconds);
                return;
            }

            await Task.Delay(InputModePoll, ct);
        }
    }

    public void Write(byte[] data) => current?.Write(data);

    /// <summary>When the user last typed into the terminal; the pilot holds new runs for a while after.</summary>
    public DateTimeOffset? LastUserInputAt { get; private set; }

    public void MarkUserInput() => LastUserInputAt = DateTimeOffset.UtcNow;

    public void ResetLastUserInput() => LastUserInputAt = null;

    public void Resize(int cols, int rows) => current?.Resize(cols, rows);

    public void Stop()
    {
        string providerName;
        lock (sync)
        {
            if (current is null)
            {
                return;
            }

            logger.LogInformation("Stopping {Provider} session.", activeProvider.Id);
            current.Dispose();
            current = null;
            providerName = activeProvider.DisplayName;
        }

        // Raised here because a disposed process never reports its own exit; Pilot waiters still need to hear it.
        Exited?.Invoke(new SessionExit(providerName, ExitCode: null));
    }

    public void Dispose() => Stop();

    private void OnExited(PtyProcess process, int exitCode)
    {
        string providerName;
        lock (sync)
        {
            if (current != process)
            {
                return;
            }

            current = null;
            providerName = activeProvider.DisplayName;
        }

        Exited?.Invoke(new SessionExit(providerName, exitCode));
    }
}
