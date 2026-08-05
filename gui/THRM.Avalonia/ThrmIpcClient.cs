using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace THRM.Avalonia;

public sealed class ThrmIpcClient : IDisposable
{
    internal const string ProtocolVersion = "3.0";
    private const string PipeName = "THRM-IPC";
    private const string LegacyPipeName = "BS2PRO-Controller-IPC";
    private const string PipeNameEnvironmentVariable = "THRM_IPC_PIPE_NAME";
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan RecoveryInterval = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(8);
    private static readonly Encoding Utf8 = new UTF8Encoding(false);
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<IpcResponse>> _pending = new();
    private Stream? _stream;
    private CancellationTokenSource? _connectionCancellation;
    private bool _disposed;

    public event EventHandler<IpcConnectionChangedEventArgs>? ConnectionChanged;
    public event EventHandler<IpcEvent>? EventReceived;

    public bool IsConnected
    {
        get
        {
            lock (_stateLock)
            {
                return _stream is not null;
            }
        }
    }

    public async Task RunRecoveryLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !IsDisposed())
        {
            if (!IsConnected)
            {
                try
                {
                    await ConnectAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // The Core may start after the GUI. The next pass retries the connection.
                }
            }

            try
            {
                await Task.Delay(RecoveryInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            lock (_stateLock)
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_stream is not null)
                {
                    return;
                }
            }

            var stream = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            var connectionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            lock (_stateLock)
            {
                if (_disposed)
                {
                    connectionCancellation.Dispose();
                    stream.Dispose();
                    return;
                }

                _stream = stream;
                _connectionCancellation = connectionCancellation;
            }

            _ = ReadLoopAsync(stream, connectionCancellation);
            RaiseConnectionChanged(true, null);
        }
        finally
        {
            _connectGate.Release();
        }
    }

    public Task<string> PingAsync(CancellationToken cancellationToken = default) =>
        SendRequestAsync<string>("Ping", null, cancellationToken);

    public Task<ConfigSnapshot> GetConfigAsync(CancellationToken cancellationToken = default) =>
        SendRequestAsync<ConfigSnapshot>("GetConfig", null, cancellationToken);

    public Task<DeviceStatusSnapshot> GetDeviceStatusAsync(CancellationToken cancellationToken = default) =>
        SendRequestAsync<DeviceStatusSnapshot>("GetDeviceStatus", null, cancellationToken);

    public Task<bool> ShowWindowAsync(CancellationToken cancellationToken = default) =>
        SendRequestAsync<bool>("ShowWindow", null, cancellationToken);

    public Task<bool> QuitCoreAsync(CancellationToken cancellationToken = default) =>
        SendRequestAsync<bool>("QuitApp", null, cancellationToken);

    public Task<bool> ConnectDeviceAsync(CancellationToken cancellationToken = default) =>
        SendRequestAsync<bool>("Connect", null, cancellationToken);

    public Task<bool> DisconnectDeviceAsync(CancellationToken cancellationToken = default) =>
        SendRequestAsync<bool>("Disconnect", null, cancellationToken);

    public Task<bool> SetAutoControlAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SendRequestAsync<bool>("SetAutoControl", new { enabled }, cancellationToken);

    private async Task<T> SendRequestAsync<T>(string type, object? data, CancellationToken cancellationToken)
    {
        Stream stream;
        lock (_stateLock)
        {
            stream = _stream ?? throw new IpcDisconnectedException("THRM Core is not connected.");
        }

        var requestId = $"avalonia-{Guid.NewGuid():N}";
        var completion = new TaskCompletionSource<IpcResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(requestId, completion))
        {
            throw new InvalidOperationException("Could not register an IPC request.");
        }

        try
        {
            var payload = BuildRequestLine(type, data, requestId);
            var bytes = Utf8.GetBytes(payload);
            try
            {
                await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    lock (_stateLock)
                    {
                        if (!ReferenceEquals(_stream, stream))
                        {
                            throw new IpcDisconnectedException("THRM Core connection changed before the request was sent.");
                        }
                    }

                    using var writeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    writeTimeout.CancelAfter(RequestTimeout);
                    await stream.WriteAsync(bytes, writeTimeout.Token).ConfigureAwait(false);
                    await stream.FlushAsync(writeTimeout.Token).ConfigureAwait(false);
                }
                finally
                {
                    _writeGate.Release();
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // A write may have reached Core before the transport reported an error.
                // Close the connection and let the caller decide whether a later action is safe.
                HandleDisconnect(stream, ex);
                throw new IpcDisconnectedException($"IPC write failed for {type}: {ex.Message}", ex);
            }

            IpcResponse response;
            try
            {
                response = await completion.Task.WaitAsync(RequestTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException($"IPC response timed out for {type}.", ex);
            }

            if (!response.Success)
            {
                throw new IpcResponseException(response.Error ?? "THRM Core rejected the request.");
            }

            if (response.Data is not { } responseData || responseData.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                throw new JsonException($"IPC response for {type} did not include data.");
            }

            return responseData.Deserialize<T>(JsonOptions)
                ?? throw new JsonException($"IPC response for {type} contained null data.");
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    private async Task<Stream> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        Exception? lastError = null;
        foreach (var name in GetEndpointCandidates())
        {
            try
            {
                return OperatingSystem.IsWindows()
                    ? await OpenNamedPipeAsync(name, cancellationToken).ConfigureAwait(false)
                    : await OpenUnixSocketAsync(name, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new IOException($"Could not connect to THRM Core IPC: {lastError?.Message ?? "no endpoint"}", lastError);
    }

    private static async Task<Stream> OpenNamedPipeAsync(string name, CancellationToken cancellationToken)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            return pipe;
        }
        catch
        {
            pipe.Dispose();
            throw;
        }
    }

    private static async Task<Stream> OpenUnixSocketAsync(string name, CancellationToken cancellationToken)
    {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(ConnectTimeout);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(GetUnixSocketPath(name)), timeout.Token)
                .ConfigureAwait(false);
            return new NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    private async Task ReadLoopAsync(Stream stream, CancellationTokenSource connectionCancellation)
    {
        var cancellationToken = connectionCancellation.Token;
        Exception? disconnectReason = null;
        try
        {
            using var reader = new StreamReader(stream, Utf8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    disconnectReason = new EndOfStreamException("THRM Core closed the IPC connection.");
                    break;
                }

                IpcMessage? message;
                try
                {
                    message = JsonSerializer.Deserialize<IpcMessage>(line, JsonOptions);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (message is null)
                {
                    continue;
                }

                if (message.IsResponse)
                {
                    var response = new IpcResponse
                    {
                        ProtocolVersion = message.ProtocolVersion,
                        RequestId = message.RequestId,
                        Timestamp = message.Timestamp,
                        Success = message.Success,
                        ErrorCode = message.ErrorCode,
                        Error = message.Error,
                        Data = Clone(message.Data),
                    };
                    if (_pending.TryRemove(response.RequestId ?? string.Empty, out var completion))
                    {
                        completion.TrySetResult(response);
                    }
                }
                else if (message.IsEvent && !string.IsNullOrWhiteSpace(message.Type))
                {
                    RaiseEvent(new IpcEvent(message.Type, Clone(message.Data)));
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            disconnectReason = ex;
        }
        finally
        {
            HandleDisconnect(stream, disconnectReason);
            connectionCancellation.Dispose();
        }
    }

    private void HandleDisconnect(Stream stream, Exception? reason)
    {
        CancellationTokenSource? connectionCancellation;
        lock (_stateLock)
        {
            if (!ReferenceEquals(_stream, stream))
            {
                return;
            }

            _stream = null;
            connectionCancellation = _connectionCancellation;
            _connectionCancellation = null;
        }

        connectionCancellation?.Cancel();
        stream.Dispose();
        var error = new IpcDisconnectedException(
            reason is null ? "THRM Core IPC connection closed." : $"THRM Core IPC connection lost: {reason.Message}",
            reason);
        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(error);
        }

        RaiseConnectionChanged(false, reason);
    }

    private void RaiseConnectionChanged(bool connected, Exception? error)
    {
        try
        {
            ConnectionChanged?.Invoke(this, new IpcConnectionChangedEventArgs(connected, error));
        }
        catch
        {
            // An observer must not take down the IPC read loop.
        }
    }

    private void RaiseEvent(IpcEvent @event)
    {
        try
        {
            EventReceived?.Invoke(this, @event);
        }
        catch
        {
            // An observer must not take down the IPC read loop.
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        Stream? stream;
        lock (_stateLock)
        {
            stream = _stream;
        }

        if (stream is not null)
        {
            HandleDisconnect(stream, new ObjectDisposedException(nameof(ThrmIpcClient)));
        }

        foreach (var pending in _pending.Values)
        {
            pending.TrySetException(new ObjectDisposedException(nameof(ThrmIpcClient)));
        }

    }

    private bool IsDisposed()
    {
        lock (_stateLock)
        {
            return _disposed;
        }
    }

    internal static string BuildRequestLine(string type, object? data, string requestId) =>
        JsonSerializer.Serialize(new IpcRequest
        {
            ProtocolVersion = ProtocolVersion,
            RequestId = requestId,
            Timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            Type = type,
            Data = data,
        }, JsonOptions) + "\n";

    internal static IReadOnlyList<string> GetEndpointCandidates()
    {
        var configured = Environment.GetEnvironmentVariable(PipeNameEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return new[] { configured.Trim() };
        }

        return new[] { PipeName, LegacyPipeName };
    }

    internal static string GetUnixSocketPath(string name) =>
        Path.Combine(Path.GetTempPath(), $"{name}.sock");

    private static JsonElement? Clone(JsonElement? element) => element?.Clone();
}

public sealed class IpcConnectionChangedEventArgs(bool connected, Exception? error) : EventArgs
{
    public bool Connected { get; } = connected;
    public Exception? Error { get; } = error;
}

public sealed class IpcEvent(string type, JsonElement? data)
{
    public string Type { get; } = type;
    public JsonElement? Data { get; } = data;

    public bool TryGetData<T>(out T? value)
    {
        if (Data is not { } data || data.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
        {
            value = default;
            return false;
        }

        try
        {
            value = data.Deserialize<T>(ThrmIpcClient.JsonOptions);
            return value is not null;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }
}

public sealed class IpcDisconnectedException(string message, Exception? inner = null) : IOException(message, inner);

public sealed class IpcResponseException(string message) : IOException(message);

internal sealed class IpcRequest
{
    [JsonPropertyName("protocolVersion")] public string? ProtocolVersion { get; init; }
    [JsonPropertyName("requestId")] public string? RequestId { get; init; }
    [JsonPropertyName("timestamp")] public long Timestamp { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("data")] public object? Data { get; init; }
}

internal sealed class IpcMessage
{
    [JsonPropertyName("protocolVersion")] public string? ProtocolVersion { get; init; }
    [JsonPropertyName("requestId")] public string? RequestId { get; init; }
    [JsonPropertyName("timestamp")] public long Timestamp { get; init; }
    [JsonPropertyName("isResponse")] public bool IsResponse { get; init; }
    [JsonPropertyName("isEvent")] public bool IsEvent { get; init; }
    [JsonPropertyName("success")] public bool Success { get; init; }
    [JsonPropertyName("errorCode")] public string? ErrorCode { get; init; }
    [JsonPropertyName("error")] public string? Error { get; init; }
    [JsonPropertyName("type")] public string? Type { get; init; }
    [JsonPropertyName("data")] public JsonElement? Data { get; init; }
}

internal sealed class IpcResponse
{
    public string? ProtocolVersion { get; init; }
    public string? RequestId { get; init; }
    public long Timestamp { get; init; }
    public bool Success { get; init; }
    public string? ErrorCode { get; init; }
    public string? Error { get; init; }
    public JsonElement? Data { get; init; }
}

public sealed class ConfigSnapshot
{
    [JsonPropertyName("autoControl")] public bool AutoControl { get; init; }
}

public sealed class DeviceStatusSnapshot
{
    [JsonPropertyName("connected")] public bool Connected { get; init; }
    [JsonPropertyName("monitoring")] public bool Monitoring { get; init; }
    [JsonPropertyName("currentData")] public FanDataSnapshot? CurrentData { get; init; }
    [JsonPropertyName("temperature")] public TemperatureSnapshot? Temperature { get; init; }
    [JsonPropertyName("productId")] public string? ProductId { get; init; }
    [JsonPropertyName("model")] public string? Model { get; init; }
}

public sealed class FanDataSnapshot
{
    [JsonPropertyName("currentRpm")] public int CurrentRpm { get; init; }
    [JsonPropertyName("targetRpm")] public int TargetRpm { get; init; }
    [JsonPropertyName("workMode")] public string? WorkMode { get; init; }
}

public sealed class TemperatureSnapshot
{
    [JsonPropertyName("cpuTemp")] public int CpuTemp { get; init; }
    [JsonPropertyName("gpuTemp")] public int GpuTemp { get; init; }
    [JsonPropertyName("maxTemp")] public int MaxTemp { get; init; }
    [JsonPropertyName("bridgeOk")] public bool BridgeOk { get; init; }
    [JsonPropertyName("bridgeMessage")] public string? BridgeMessage { get; init; }
}

internal static class IpcProtocolSelfCheck
{
    public static void Run()
    {
        var line = ThrmIpcClient.BuildRequestLine("Ping", null, "self-check");
        if (!line.EndsWith('\n') || !line.Contains("\"type\":\"Ping\"", StringComparison.Ordinal))
        {
            throw new InvalidOperationException("Request framing check failed.");
        }

        using var connectRequest = JsonDocument.Parse(
            ThrmIpcClient.BuildRequestLine("Connect", null, "self-check-connect"));
        if (connectRequest.RootElement.GetProperty("type").GetString() != "Connect")
        {
            throw new InvalidOperationException("Connect request check failed.");
        }

        using var disconnectRequest = JsonDocument.Parse(
            ThrmIpcClient.BuildRequestLine("Disconnect", null, "self-check-disconnect"));
        if (disconnectRequest.RootElement.GetProperty("type").GetString() != "Disconnect")
        {
            throw new InvalidOperationException("Disconnect request check failed.");
        }

        using var autoControlRequest = JsonDocument.Parse(
            ThrmIpcClient.BuildRequestLine("SetAutoControl", new { enabled = true }, "self-check-auto"));
        var autoControlData = autoControlRequest.RootElement.GetProperty("data");
        if (autoControlRequest.RootElement.GetProperty("type").GetString() != "SetAutoControl"
            || autoControlData.ValueKind != JsonValueKind.Object
            || autoControlData.EnumerateObject().Count() != 1
            || !autoControlData.TryGetProperty("enabled", out var enabled)
            || enabled.ValueKind != JsonValueKind.True)
        {
            throw new InvalidOperationException("SetAutoControl request check failed.");
        }

        var response = JsonSerializer.Deserialize<IpcMessage>(
            "{\"protocolVersion\":\"3.0\",\"requestId\":\"self-check\",\"isResponse\":true,\"success\":true,\"data\":\"pong\"}",
            ThrmIpcClient.JsonOptions);
        if (response is null || !response.IsResponse || !response.Success || response.Data?.GetString() != "pong")
        {
            throw new InvalidOperationException("Response parsing check failed.");
        }

        var @event = JsonSerializer.Deserialize<IpcMessage>(
            "{\"isEvent\":true,\"type\":\"temperature-update\",\"data\":{\"cpuTemp\":42}}",
            ThrmIpcClient.JsonOptions);
        if (@event is null || !@event.IsEvent || @event.Type != "temperature-update")
        {
            throw new InvalidOperationException("Event parsing check failed.");
        }

        var endpoints = ThrmIpcClient.GetEndpointCandidates();
        if (endpoints.Count == 0)
        {
            throw new InvalidOperationException("Endpoint candidate check failed.");
        }

        if (CoreProcessLauncher.GetCandidates().Count == 0)
        {
            throw new InvalidOperationException("Core launch candidate check failed.");
        }

        Console.WriteLine("THRM Avalonia IPC self-check passed.");
    }
}
