using System.Buffers.Binary;
using System.IO.Pipes;
using System.Text.Json;
using System.Text.Json.Nodes;
using UniversalPresence.Infrastructure;

namespace UniversalPresence.Discord;

/// <summary>
/// Minimal, dependency-free Discord RPC client speaking the IPC protocol directly:
/// 8 byte frame header (opcode + length, little endian) followed by UTF-8 JSON.
///
/// Ops: 0 handshake, 1 frame, 2 close, 3 ping, 4 pong.
/// </summary>
public sealed class DiscordIpcClient : IAsyncDisposable
{
    private const int OpHandshake = 0;
    private const int OpFrame = 1;
    private const int OpClose = 2;
    private const int OpPing = 3;
    private const int OpPong = 4;
    private const int MaxFrameBytes = 16 * 1024 * 1024;

    private readonly NamedPipeClientStream _pipe;
    private readonly Log _log;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private Task? _reader;
    private int _closedRaised;
    private volatile bool _connected;
    private int _nonce;
    private bool _disposed;

    private DiscordIpcClient(NamedPipeClientStream pipe, Log log)
    {
        _pipe = pipe;
        _log = log;
    }

    public bool IsConnected => _connected;

    /// <summary>Raised once when the pipe breaks (Discord closed / restarted).</summary>
    public event Action<DiscordIpcClient>? Closed;

    /// <summary>
    /// Connects to the first available Discord IPC pipe (discord-ipc-0 .. discord-ipc-9).
    /// UNIVERSAL_PRESENCE_IPC_PIPE forces a single pipe name; it exists so the protocol
    /// can be exercised against a stub server without a Discord application id.
    /// </summary>
    public static async Task<DiscordIpcClient?> ConnectAsync(
        string clientId,
        TimeSpan readyTimeout,
        Log log,
        CancellationToken cancellationToken)
    {
        var forcedPipe = Environment.GetEnvironmentVariable("UNIVERSAL_PRESENCE_IPC_PIPE");

        if (!string.IsNullOrWhiteSpace(forcedPipe))
        {
            log.Debug($"Usando pipe IPC forzado: {forcedPipe.Trim()}");
            return await TryConnectAsync(forcedPipe.Trim(), clientId, readyTimeout, log, cancellationToken)
                .ConfigureAwait(false);
        }

        for (var index = 0; index < 10; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var client = await TryConnectAsync($"discord-ipc-{index}", clientId, readyTimeout, log, cancellationToken)
                .ConfigureAwait(false);

            if (client is not null)
            {
                return client;
            }
        }

        return null;
    }

    private static async Task<DiscordIpcClient?> TryConnectAsync(
        string pipeName,
        string clientId,
        TimeSpan readyTimeout,
        Log log,
        CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);

        try
        {
            await pipe.ConnectAsync(250, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            pipe.Dispose();
            return null;
        }

        var client = new DiscordIpcClient(pipe, log);
        client._reader = Task.Run(client.ReaderLoopAsync, CancellationToken.None);

        try
        {
            await client.SendFrameAsync(
                OpHandshake,
                new JsonObject { ["v"] = 1, ["client_id"] = clientId },
                cancellationToken).ConfigureAwait(false);

            var ready = client._ready.Task;
            var completed = await Task.WhenAny(ready, Task.Delay(readyTimeout, cancellationToken))
                .ConfigureAwait(false);

            if (completed == ready && ready.Result)
            {
                log.Debug($"Discord IPC conectado en {pipeName}.");
                return client;
            }

            log.Debug($"Discord did not accept the handshake on {pipeName}.");
        }
        catch (Exception ex)
        {
            log.Debug($"Fallo el handshake de Discord en {pipeName}: {ex.Message}");
        }

        await client.DisposeAsync().ConfigureAwait(false);
        return null;
    }

    public async Task SetActivityAsync(DiscordActivity? activity, CancellationToken cancellationToken)
    {
        var args = new JsonObject
        {
            ["pid"] = Environment.ProcessId,
        };

        if (activity is not null)
        {
            args["activity"] = JsonSerializer.SerializeToNode(activity, DiscordJson.Options);
        }

        var frame = new JsonObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["args"] = args,
            ["nonce"] = $"up-{Interlocked.Increment(ref _nonce)}",
        };

        await SendFrameAsync(OpFrame, frame, cancellationToken).ConfigureAwait(false);
    }

    private async Task ReaderLoopAsync()
    {
        var header = new byte[8];

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                await ReadExactlyAsync(header, _cts.Token).ConfigureAwait(false);

                var op = BinaryPrimitives.ReadInt32LittleEndian(header);
                var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));

                if (length < 0 || length > MaxFrameBytes)
                {
                    throw new InvalidDataException($"Invalid Discord IPC frame ({length} bytes).");
                }

                var payload = length == 0 ? Array.Empty<byte>() : new byte[length];
                if (length > 0)
                {
                    await ReadExactlyAsync(payload, _cts.Token).ConfigureAwait(false);
                }

                await HandleFrameAsync(op, payload).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // normal shutdown
        }
        catch (Exception ex)
        {
            if (!_cts.IsCancellationRequested)
            {
                _log.Debug($"IPC connection with Discord ended: {ex.Message}");
            }
        }
        finally
        {
            _connected = false;
            _ready.TrySetResult(false);

            if (!_cts.IsCancellationRequested && Interlocked.Exchange(ref _closedRaised, 1) == 0)
            {
                Closed?.Invoke(this);
            }
        }
    }

    private async Task HandleFrameAsync(int op, byte[] payload)
    {
        switch (op)
        {
            case OpPing:
                await SendFrameAsync(OpPong, payload, _cts.Token).ConfigureAwait(false);
                return;

            case OpClose:
                throw new IOException("Discord closed the IPC connection.");

            case OpFrame:
                break;

            default:
                return;
        }

        if (payload.Length == 0)
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;

            var command = root.TryGetProperty("cmd", out var cmd) && cmd.ValueKind == JsonValueKind.String
                ? cmd.GetString()
                : null;

            if (!root.TryGetProperty("evt", out var evt)
                || evt.ValueKind != JsonValueKind.String
                || evt.GetString() is null)
            {
                // Discord acknowledges accepted commands without an event name.
                if (string.Equals(command, "SET_ACTIVITY", StringComparison.Ordinal))
                {
                    _log.Debug("Discord confirmed SET_ACTIVITY (activity accepted).");
                }

                return;
            }

            switch (evt.GetString())
            {
                case "READY":
                    _connected = true;
                    _ready.TrySetResult(true);
                    break;

                case "ERROR":
                    _log.Warn($"Discord returned an error on handshake: {root.GetRawText()}");
                    _ready.TrySetResult(false);
                    break;
            }
        }
        catch (JsonException ex)
        {
            _log.Debug($"Respuesta IPC de Discord no interpretable: {ex.Message}");
        }
    }

    private async Task SendFrameAsync(int op, JsonNode payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(payload, DiscordJson.Options);
        await SendFrameAsync(op, json, cancellationToken).ConfigureAwait(false);
    }

    private async Task SendFrameAsync(int op, ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        var header = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(header, op);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), payload.Length);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _pipe.WriteAsync(header, cancellationToken).ConfigureAwait(false);

            if (payload.Length > 0)
            {
                await _pipe.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            }

            await _pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task ReadExactlyAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        var offset = 0;

        while (offset < buffer.Length)
        {
            var read = await _pipe.ReadAsync(buffer[offset..], cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException("Discord closed the IPC pipe.");
            }

            offset += read;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            _cts.Cancel();
        }
        catch
        {
            // ignore
        }

        try
        {
            _pipe.Dispose();
        }
        catch
        {
            // ignore
        }

        if (_reader is not null)
        {
            try
            {
                await _reader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        _cts.Dispose();
        _writeLock.Dispose();
    }
}
