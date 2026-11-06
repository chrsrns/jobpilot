using System.Collections.Concurrent;
using System.Reflection;
using Pty.Net;

namespace JobPilot.Terminal.Tests.Support;

/// <summary>
/// Controllable Pty.Net connection double. Mirrors the Unix implementation's failure modes: Kill and
/// Dispose throw for an already-exited child, and reads fail with EIO instead of returning EOF.
/// </summary>
internal sealed class FakePtyConnection : IPtyConnection
{
    public event EventHandler<PtyExitedEventArgs>? ProcessExited;

    public ScriptedReadStream Reader { get; } = new();

    public Stream Writer { get; set; } = new RecordingStream();

    /// <summary>Each write the process made, when <see cref="Writer"/> is the default recorder.</summary>
    public List<byte[]> Writes => ((RecordingStream)Writer).Writes;

    public bool KillThrows { get; set; }

    public bool ResizeThrows { get; set; }

    public bool DisposeThrows { get; set; }

    public int KillCalls { get; private set; }

    public int DisposeCalls { get; private set; }

    public int ResizeCalls { get; private set; }

    private PushReadStream? pushReader;

    public Stream ReaderStream => pushReader ?? (Stream)Reader;

    public Stream WriterStream => Writer;

    /// <summary>Feeds bytes through the read side like live process output; <see cref="Reader"/> stays scriptable.</summary>
    /// <remarks>Must be called before the process starts: a blocked read never re-evaluates <see cref="ReaderStream"/>.</remarks>
    public void EnablePushReader() => pushReader ??= new PushReadStream();

    public void PushOutput(byte[] data)
    {
        EnablePushReader();
        pushReader!.Push(data);
    }

    public int Pid => 1;

    public int ExitCode { get; set; }

    public bool WaitForExit(int milliseconds) => true;

    /// <summary>Reports a natural exit the way Pty.Net's own event does.</summary>
    public void RaiseExit(int exitCode = 1)
    {
        // Pty.Net keeps the constructor internal.
        var args = (PtyExitedEventArgs)Activator.CreateInstance(
            typeof(PtyExitedEventArgs), BindingFlags.Instance | BindingFlags.NonPublic, null, [exitCode], null)!;
        ProcessExited?.Invoke(this, args);
    }

    public void Kill()
    {
        KillCalls++;
        if (KillThrows)
        {
            throw new InvalidOperationException("Killing terminal failed with error 3");
        }
    }

    public void Dispose()
    {
        DisposeCalls++;
        if (DisposeThrows)
        {
            throw new InvalidOperationException("Killing terminal failed with error 3");
        }
    }

    public void Resize(int cols, int rows)
    {
        ResizeCalls++;
        if (ResizeThrows)
        {
            throw new InvalidOperationException("Resizing terminal failed with error 5");
        }
    }
}

/// <summary>Blocks readers until the test scripts a failure, like a pty master with a live child.</summary>
internal sealed class ScriptedReadStream : Stream
{
    private readonly SemaphoreSlim pending = new(0);
    private Exception? failure;

    public void FailNextRead(Exception ex)
    {
        failure = ex;
        pending.Release();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        pending.Wait();
        throw failure ?? new EndOfStreamException();
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => 0;

    public override long Position
    {
        get => 0;
        set { }
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => 0;

    public override void SetLength(long value)
    {
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
    }
}

/// <summary>Yields pushed chunks to a blocked reader, like live pty output.</summary>
internal sealed class PushReadStream : Stream
{
    private readonly BlockingCollection<byte[]> chunks = new();
    private byte[]? current;
    private int offset;

    public void Push(byte[] data) => chunks.Add(data);

    public override int Read(byte[] buffer, int offset, int count)
    {
        while (current is null || this.offset >= current.Length)
        {
            if (!chunks.TryTake(out current, Timeout.Infinite))
            {
                return 0;
            }

            this.offset = 0;
        }

        var copied = Math.Min(count, current.Length - this.offset);
        Array.Copy(current, this.offset, buffer, offset, copied);
        this.offset += copied;
        return copied;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => 0;

    public override long Position
    {
        get => 0;
        set { }
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => 0;

    public override void SetLength(long value)
    {
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
    }
}

/// <summary>Fails every write with EIO, like a pty master whose child has exited.</summary>
internal sealed class DeadWriteStream : Stream
{
    public override void Write(byte[] buffer, int offset, int count) =>
        throw new IOException("Input/output error");

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => 0;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => 0;

    public override long Position
    {
        get => 0;
        set { }
    }

    public override long Seek(long offset, SeekOrigin origin) => 0;

    public override void SetLength(long value)
    {
    }
}

internal sealed class RecordingStream : Stream
{
    public List<byte[]> Writes { get; } = [];

    public override void Write(byte[] buffer, int offset, int count) => Writes.Add(buffer[offset..(offset + count)]);

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => 0;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => 0;

    public override long Position
    {
        get => 0;
        set { }
    }

    public override long Seek(long offset, SeekOrigin origin) => 0;

    public override void SetLength(long value)
    {
    }
}
