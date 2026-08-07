using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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

    public Task<JsonElement> GetConfigJsonAsync(CancellationToken cancellationToken = default) =>
        SendRequestAsync<JsonElement>("GetConfig", null, cancellationToken);

    public Task<bool> UpdateConfigAsync(
        JsonElement config,
        CancellationToken cancellationToken = default) =>
        SendRequestAsync<bool>("UpdateConfig", config, cancellationToken);

    public Task<DeviceStatusSnapshot> GetDeviceStatusAsync(CancellationToken cancellationToken = default) =>
        SendRequestAsync<DeviceStatusSnapshot>("GetDeviceStatus", null, cancellationToken);

    public Task<List<FanCurvePoint>> GetFanCurveAsync(CancellationToken cancellationToken = default) =>
        SendRequestAsync<List<FanCurvePoint>>("GetFanCurve", null, cancellationToken);

    public Task<FanCurveProfilesSnapshot> GetFanCurveProfilesAsync(CancellationToken cancellationToken = default) =>
        SendRequestAsync<FanCurveProfilesSnapshot>("GetFanCurveProfiles", null, cancellationToken);

    public Task<TemperatureHistorySnapshot> GetTemperatureHistoryAsync(CancellationToken cancellationToken = default) =>
        SendRequestAsync<TemperatureHistorySnapshot>("GetTemperatureHistory", null, cancellationToken);

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

    public Task<bool> SetManualGearAsync(string gear, string level, CancellationToken cancellationToken = default) =>
        SendRequestAsync<bool>("SetManualGear", new { gear, level }, cancellationToken);

    public Task<bool> SetCustomSpeedAsync(bool enabled, int rpm, CancellationToken cancellationToken = default) =>
        SendRequestAsync<bool>("SetCustomSpeed", new { enabled, rpm }, cancellationToken);

    public Task<bool> SetGearLightAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SendRequestAsync<bool>("SetGearLight", new { enabled }, cancellationToken);

    public Task<bool> SetLightStripAsync(
        LightStripSnapshot config,
        CancellationToken cancellationToken = default) =>
        SendRequestAsync<bool>("SetLightStrip", new { config }, cancellationToken);

    public Task<bool> CheckWindowsAutoStartAsync(CancellationToken cancellationToken = default) =>
        SendRequestAsync<bool>("CheckWindowsAutoStart", null, cancellationToken);

    public Task<string> GetAutoStartMethodAsync(CancellationToken cancellationToken = default) =>
        SendRequestAsync<string>("GetAutoStartMethod", null, cancellationToken);

    public Task<bool> IsRunningAsAdminAsync(CancellationToken cancellationToken = default) =>
        SendRequestAsync<bool>("IsRunningAsAdmin", null, cancellationToken);

    public Task<bool> SetAutoStartWithMethodAsync(
        bool enable,
        string method,
        CancellationToken cancellationToken = default) =>
        SendRequestAsync<bool>("SetAutoStartWithMethod", new { enable, method }, cancellationToken);

    public Task<bool> SetPowerOnStartAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SendRequestAsync<bool>("SetPowerOnStart", new { enabled }, cancellationToken);

    public Task<bool> SetSmartStartStopAsync(string value, CancellationToken cancellationToken = default) =>
        SendRequestAsync<bool>("SetSmartStartStop", new { value }, cancellationToken);

    public Task<bool> SetTemperatureHistoryEnabledAsync(bool enabled, CancellationToken cancellationToken = default) =>
        SendRequestAsync<bool>("SetTemperatureHistoryEnabled", new { enabled }, cancellationToken);

    public Task<bool> SetTemperatureHistoryRetentionHoursAsync(int value, CancellationToken cancellationToken = default) =>
        SendRequestAsync<bool>("SetTemperatureHistoryRetentionHours", new { value }, cancellationToken);

    public Task<bool> SetFanCurveAsync(IReadOnlyList<FanCurvePoint> curve, CancellationToken cancellationToken = default) =>
        SendRequestAsync<bool>("SetFanCurve", curve, cancellationToken);

    public Task<FanCurveProfileSnapshot> SetActiveFanCurveProfileAsync(string id, CancellationToken cancellationToken = default) =>
        SendRequestAsync<FanCurveProfileSnapshot>("SetActiveFanCurveProfile", new { id }, cancellationToken);

    public Task<FanCurveProfileSnapshot> SaveFanCurveProfileAsync(
        string id,
        string name,
        IReadOnlyList<FanCurvePoint> curve,
        bool setActive,
        CancellationToken cancellationToken = default) =>
        SendRequestAsync<FanCurveProfileSnapshot>("SaveFanCurveProfile", new { id, name, curve, setActive }, cancellationToken);

    public Task<bool> DeleteFanCurveProfileAsync(string id, CancellationToken cancellationToken = default) =>
        SendRequestAsync<bool>("DeleteFanCurveProfile", new { id }, cancellationToken);

    public Task<string> ExportFanCurveProfilesAsync(CancellationToken cancellationToken = default) =>
        SendRequestAsync<string>("ExportFanCurveProfiles", null, cancellationToken);

    public Task<bool> ImportFanCurveProfilesAsync(string code, CancellationToken cancellationToken = default) =>
        SendRequestAsync<bool>("ImportFanCurveProfiles", new { code }, cancellationToken);

    public async Task ResetLearnedOffsetsAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendRequestAsync<JsonElement>("ResetLearnedOffsets", null, cancellationToken).ConfigureAwait(false);
        if (response.ValueKind != JsonValueKind.Object
            || !response.TryGetProperty("ok", out var ok)
            || ok.ValueKind != JsonValueKind.True)
        {
            throw new JsonException("IPC response for ResetLearnedOffsets did not contain ok=true.");
        }
    }

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

internal static class TimeCurveScheduleConfigJson
{
    public static JsonElement ReplaceTimeCurveSchedule(
        JsonElement config,
        TimeCurveScheduleSnapshot schedule)
    {
        if (config.ValueKind != JsonValueKind.Object
            || JsonNode.Parse(config.GetRawText()) is not JsonObject root)
        {
            throw new JsonException("Core configuration must be a JSON object.");
        }

        root["timeCurveSchedule"] = JsonSerializer.SerializeToNode(
            schedule,
            ThrmIpcClient.JsonOptions)
            ?? throw new JsonException("Time curve schedule could not be serialized.");

        using var document = JsonDocument.Parse(root.ToJsonString(ThrmIpcClient.JsonOptions));
        return document.RootElement.Clone();
    }
}

public sealed class ConfigSnapshot
{
    [JsonPropertyName("autoControl")] public bool AutoControl { get; init; }
    [JsonPropertyName("manualGear")] public string? ManualGear { get; init; }
    [JsonPropertyName("manualLevel")] public string? ManualLevel { get; init; }
    [JsonPropertyName("customSpeedEnabled")] public bool CustomSpeedEnabled { get; init; }
    [JsonPropertyName("customSpeedRPM")] public int CustomSpeedRpm { get; init; }
    [JsonPropertyName("gearLight")] public bool GearLight { get; init; }
    [JsonPropertyName("powerOnStart")] public bool PowerOnStart { get; init; }
    [JsonPropertyName("smartStartStop")] public string? SmartStartStop { get; init; }
    [JsonPropertyName("smartControl")] public SmartControlSnapshot? SmartControl { get; init; }
    [JsonPropertyName("timeCurveSchedule")] public TimeCurveScheduleSnapshot? TimeCurveSchedule { get; init; }
    [JsonPropertyName("lightStrip")] public LightStripSnapshot? LightStrip { get; init; }
}

public sealed class LightStripSnapshot
{
    [JsonPropertyName("mode")] public string Mode { get; init; } = "smart_temp";
    [JsonPropertyName("speed")] public string Speed { get; init; } = "medium";
    [JsonPropertyName("brightness")] public int Brightness { get; init; } = 100;
    [JsonPropertyName("colors")] public List<LightRgbSnapshot> Colors { get; init; } = [];
}

public sealed class LightRgbSnapshot
{
    [JsonPropertyName("r")] public byte R { get; init; }
    [JsonPropertyName("g")] public byte G { get; init; }
    [JsonPropertyName("b")] public byte B { get; init; }
}

public sealed class SmartControlSnapshot
{
    [JsonPropertyName("learning")] public bool Learning { get; init; }
    [JsonPropertyName("learningBias")] public string? LearningBias { get; init; }
    [JsonPropertyName("learnedOffsets")] public List<int> LearnedOffsets { get; init; } = [];
}

public sealed class TimeCurveScheduleSnapshot
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("rules")] public List<TimeCurveScheduleRuleSnapshot> Rules { get; init; } = [];
}

public sealed class TimeCurveScheduleRuleSnapshot
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("weekdays")] public List<int> Weekdays { get; init; } = [];
    [JsonPropertyName("startTime")] public string StartTime { get; init; } = "00:00";
    [JsonPropertyName("endTime")] public string EndTime { get; init; } = "23:59";
    [JsonPropertyName("curveProfileId")] public string CurveProfileId { get; init; } = string.Empty;
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
    [JsonPropertyName("gearSettings")] public int? GearSettings { get; init; }
    [JsonPropertyName("currentRpm")] public int CurrentRpm { get; init; }
    [JsonPropertyName("targetRpm")] public int TargetRpm { get; init; }
    [JsonPropertyName("maxGear")] public string? MaxGear { get; init; }
    [JsonPropertyName("workMode")] public string? WorkMode { get; init; }
}

public sealed class FanCurvePoint
{
    [JsonPropertyName("temperature")] public int Temperature { get; init; }
    [JsonPropertyName("rpm")] public int Rpm { get; init; }
}

public sealed class FanCurveProfileSnapshot
{
    [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;
    [JsonPropertyName("curve")] public List<FanCurvePoint> Curve { get; init; } = [];

    public override string ToString() => Name;
}

public sealed class FanCurveProfilesSnapshot
{
    [JsonPropertyName("profiles")] public List<FanCurveProfileSnapshot> Profiles { get; init; } = [];
    [JsonPropertyName("activeId")] public string ActiveId { get; init; } = string.Empty;
}

public sealed class TemperatureSnapshot
{
    [JsonPropertyName("cpuTemp")] public int CpuTemp { get; init; }
    [JsonPropertyName("gpuTemp")] public int GpuTemp { get; init; }
    [JsonPropertyName("maxTemp")] public int MaxTemp { get; init; }
    [JsonPropertyName("bridgeOk")] public bool BridgeOk { get; init; }
    [JsonPropertyName("bridgeMessage")] public string? BridgeMessage { get; init; }
}

public sealed class TemperatureHistorySnapshot
{
    [JsonPropertyName("enabled")] public bool Enabled { get; init; }
    [JsonPropertyName("sampleIntervalSeconds")] public int SampleIntervalSeconds { get; init; }
    [JsonPropertyName("retentionHours")] public int RetentionHours { get; init; }
    [JsonPropertyName("points")] public List<TemperatureHistoryPointSnapshot> Points { get; init; } = [];
    [JsonPropertyName("events")] public List<TimelineEventSnapshot> Events { get; init; } = [];
}

public sealed class TemperatureHistoryPointSnapshot
{
    [JsonPropertyName("timestamp")] public long Timestamp { get; init; }
    [JsonPropertyName("cpuTemp")] public int CpuTemp { get; init; }
    [JsonPropertyName("gpuTemp")] public int GpuTemp { get; init; }
    [JsonPropertyName("cpuPower")] public double CpuPower { get; init; }
    [JsonPropertyName("gpuPower")] public double GpuPower { get; init; }
    [JsonPropertyName("fanRpm")] public int FanRpm { get; init; }
    [JsonPropertyName("cpuFanRpm")] public int CpuFanRpm { get; init; }
    [JsonPropertyName("gpuFanRpm")] public int GpuFanRpm { get; init; }
}

public sealed class TimelineEventSnapshot
{
    [JsonPropertyName("timestamp")] public long Timestamp { get; init; }
    [JsonPropertyName("type")] public string Type { get; init; } = string.Empty;
    [JsonPropertyName("labelKey")] public string LabelKey { get; init; } = string.Empty;
}

internal static class IpcProtocolSelfCheck
{
    public static void Run()
    {
        LightStripLogic.SelfCheck();

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

        using var manualGearRequest = JsonDocument.Parse(
            ThrmIpcClient.BuildRequestLine("SetManualGear", new { gear = "标准", level = "中" }, "self-check-manual"));
        var manualGearData = manualGearRequest.RootElement.GetProperty("data");
        if (manualGearRequest.RootElement.GetProperty("type").GetString() != "SetManualGear"
            || manualGearData.ValueKind != JsonValueKind.Object
            || manualGearData.EnumerateObject().Count() != 2
            || !manualGearData.TryGetProperty("gear", out var gear)
            || gear.ValueKind != JsonValueKind.String
            || gear.GetString() != "标准"
            || !manualGearData.TryGetProperty("level", out var level)
            || level.ValueKind != JsonValueKind.String
            || level.GetString() != "中")
        {
            throw new InvalidOperationException("SetManualGear request check failed.");
        }

        var curve = new[]
        {
            new FanCurvePoint { Temperature = 30, Rpm = 1000 },
            new FanCurvePoint { Temperature = 40, Rpm = 1600 },
        };
        using var curveRequest = JsonDocument.Parse(
            ThrmIpcClient.BuildRequestLine("SetFanCurve", curve, "self-check-curve"));
        var curveData = curveRequest.RootElement.GetProperty("data");
        if (curveRequest.RootElement.GetProperty("type").GetString() != "SetFanCurve"
            || curveData.ValueKind != JsonValueKind.Array
            || curveData.GetArrayLength() != 2
            || curveData[0].GetProperty("temperature").GetInt32() != 30
            || curveData[1].GetProperty("rpm").GetInt32() != 1600)
        {
            throw new InvalidOperationException("SetFanCurve request check failed.");
        }

        using var customSpeedRequest = JsonDocument.Parse(
            ThrmIpcClient.BuildRequestLine("SetCustomSpeed", new { enabled = true, rpm = 2200 }, "self-check-custom-speed"));
        var customSpeedData = customSpeedRequest.RootElement.GetProperty("data");
        if (customSpeedRequest.RootElement.GetProperty("type").GetString() != "SetCustomSpeed"
            || customSpeedData.GetProperty("enabled").ValueKind != JsonValueKind.True
            || customSpeedData.GetProperty("rpm").GetInt32() != 2200)
        {
            throw new InvalidOperationException("SetCustomSpeed request check failed.");
        }

        using var smartStartStopRequest = JsonDocument.Parse(
            ThrmIpcClient.BuildRequestLine("SetSmartStartStop", new { value = "delayed" }, "self-check-smart-start-stop"));
        var smartStartStopData = smartStartStopRequest.RootElement.GetProperty("data");
        if (smartStartStopRequest.RootElement.GetProperty("type").GetString() != "SetSmartStartStop"
            || smartStartStopData.GetProperty("value").GetString() != "delayed")
        {
            throw new InvalidOperationException("SetSmartStartStop request check failed.");
        }

        var lightStrip = new LightStripSnapshot
        {
            Mode = "static_multi",
            Speed = "fast",
            Brightness = 72,
            Colors =
            [
                new LightRgbSnapshot { R = 255, G = 0, B = 128 },
                new LightRgbSnapshot { R = 0, G = 255, B = 255 },
                new LightRgbSnapshot { R = 128, G = 0, B = 255 },
            ],
        };
        using var lightStripRequest = JsonDocument.Parse(
            ThrmIpcClient.BuildRequestLine("SetLightStrip", new { config = lightStrip }, "self-check-light-strip"));
        var lightStripData = lightStripRequest.RootElement.GetProperty("data");
        var decodedLightStrip = lightStripData.GetProperty("config").Deserialize<LightStripSnapshot>(ThrmIpcClient.JsonOptions);
        if (lightStripRequest.RootElement.GetProperty("type").GetString() != "SetLightStrip"
            || decodedLightStrip is not { Mode: "static_multi", Speed: "fast", Brightness: 72 }
            || decodedLightStrip.Colors.Count != 3
            || decodedLightStrip.Colors[0].R != 255
            || decodedLightStrip.Colors[1].G != 255
            || decodedLightStrip.Colors[2].B != 255)
        {
            throw new InvalidOperationException("SetLightStrip request/decoding check failed.");
        }

        using var checkWindowsAutoStartRequest = JsonDocument.Parse(
            ThrmIpcClient.BuildRequestLine("CheckWindowsAutoStart", null, "self-check-check-autostart"));
        using var getAutoStartMethodRequest = JsonDocument.Parse(
            ThrmIpcClient.BuildRequestLine("GetAutoStartMethod", null, "self-check-get-autostart-method"));
        using var isRunningAsAdminRequest = JsonDocument.Parse(
            ThrmIpcClient.BuildRequestLine("IsRunningAsAdmin", null, "self-check-is-admin"));
        var setAutoStartWithMethodPayload = new { enable = true, method = "registry" };
        using var setAutoStartWithMethodRequest = JsonDocument.Parse(
            ThrmIpcClient.BuildRequestLine(
                "SetAutoStartWithMethod",
                setAutoStartWithMethodPayload,
                "self-check-set-autostart-method"));
        var setAutoStartWithMethodData = setAutoStartWithMethodRequest.RootElement.GetProperty("data");
        if (checkWindowsAutoStartRequest.RootElement.GetProperty("type").GetString() != "CheckWindowsAutoStart"
            || getAutoStartMethodRequest.RootElement.GetProperty("type").GetString() != "GetAutoStartMethod"
            || isRunningAsAdminRequest.RootElement.GetProperty("type").GetString() != "IsRunningAsAdmin"
            || setAutoStartWithMethodRequest.RootElement.GetProperty("type").GetString() != "SetAutoStartWithMethod"
            || setAutoStartWithMethodData.ValueKind != JsonValueKind.Object
            || setAutoStartWithMethodData.EnumerateObject().Count() != 2
            || setAutoStartWithMethodData.GetProperty("enable").ValueKind != JsonValueKind.True
            || setAutoStartWithMethodData.GetProperty("method").GetString() != "registry")
        {
            throw new InvalidOperationException("Auto-start request envelope check failed.");
        }

        using var saveProfileRequest = JsonDocument.Parse(
            ThrmIpcClient.BuildRequestLine(
                "SaveFanCurveProfile",
                new { id = string.Empty, name = "Quiet", curve, setActive = true },
                "self-check-save-profile"));
        var saveProfileData = saveProfileRequest.RootElement.GetProperty("data");
        if (saveProfileRequest.RootElement.GetProperty("type").GetString() != "SaveFanCurveProfile"
            || saveProfileData.GetProperty("name").GetString() != "Quiet"
            || !saveProfileData.GetProperty("setActive").GetBoolean()
            || saveProfileData.GetProperty("curve").GetArrayLength() != 2)
        {
            throw new InvalidOperationException("SaveFanCurveProfile request check failed.");
        }

        using var deleteProfileRequest = JsonDocument.Parse(
            ThrmIpcClient.BuildRequestLine("DeleteFanCurveProfile", new { id = "quiet" }, "self-check-delete-profile"));
        var deleteProfileData = deleteProfileRequest.RootElement.GetProperty("data");
        if (deleteProfileRequest.RootElement.GetProperty("type").GetString() != "DeleteFanCurveProfile"
            || deleteProfileData.ValueKind != JsonValueKind.Object
            || deleteProfileData.EnumerateObject().Count() != 1
            || !deleteProfileData.TryGetProperty("id", out var profileId)
            || profileId.ValueKind != JsonValueKind.String
            || profileId.GetString() != "quiet")
        {
            throw new InvalidOperationException("DeleteFanCurveProfile request check failed.");
        }

        using var exportProfilesRequest = JsonDocument.Parse(
            ThrmIpcClient.BuildRequestLine("ExportFanCurveProfiles", null, "self-check-export-profiles"));
        if (exportProfilesRequest.RootElement.GetProperty("type").GetString() != "ExportFanCurveProfiles"
            || exportProfilesRequest.RootElement.TryGetProperty("data", out var exportProfilesData)
                && exportProfilesData.ValueKind != JsonValueKind.Null)
        {
            throw new InvalidOperationException("ExportFanCurveProfiles request check failed.");
        }

        using var importProfilesRequest = JsonDocument.Parse(
            ThrmIpcClient.BuildRequestLine("ImportFanCurveProfiles", new { code = "THRM-PROFILES" }, "self-check-import-profiles"));
        var importProfilesData = importProfilesRequest.RootElement.GetProperty("data");
        if (importProfilesRequest.RootElement.GetProperty("type").GetString() != "ImportFanCurveProfiles"
            || importProfilesData.ValueKind != JsonValueKind.Object
            || importProfilesData.EnumerateObject().Count() != 1
            || !importProfilesData.TryGetProperty("code", out var profileCode)
            || profileCode.ValueKind != JsonValueKind.String
            || profileCode.GetString() != "THRM-PROFILES")
        {
            throw new InvalidOperationException("ImportFanCurveProfiles request check failed.");
        }

        using var resetLearnedOffsetsRequest = JsonDocument.Parse(
            ThrmIpcClient.BuildRequestLine("ResetLearnedOffsets", null, "self-check-reset-learned-offsets"));
        if (resetLearnedOffsetsRequest.RootElement.GetProperty("type").GetString() != "ResetLearnedOffsets"
            || resetLearnedOffsetsRequest.RootElement.TryGetProperty("data", out var resetLearnedOffsetsData)
                && resetLearnedOffsetsData.ValueKind != JsonValueKind.Null)
        {
            throw new InvalidOperationException("ResetLearnedOffsets request check failed.");
        }

        using var temperatureHistoryRequest = JsonDocument.Parse(
            ThrmIpcClient.BuildRequestLine("GetTemperatureHistory", null, "self-check-temperature-history"));
        if (temperatureHistoryRequest.RootElement.GetProperty("type").GetString() != "GetTemperatureHistory"
            || temperatureHistoryRequest.RootElement.TryGetProperty("data", out var temperatureHistoryData)
                && temperatureHistoryData.ValueKind != JsonValueKind.Null)
        {
            throw new InvalidOperationException("GetTemperatureHistory request check failed.");
        }

        using var temperatureHistoryEnabledRequest = JsonDocument.Parse(
            ThrmIpcClient.BuildRequestLine("SetTemperatureHistoryEnabled", new { enabled = true }, "self-check-temperature-history-enabled"));
        var temperatureHistoryEnabledData = temperatureHistoryEnabledRequest.RootElement.GetProperty("data");
        if (temperatureHistoryEnabledRequest.RootElement.GetProperty("type").GetString() != "SetTemperatureHistoryEnabled"
            || temperatureHistoryEnabledData.GetProperty("enabled").ValueKind != JsonValueKind.True)
        {
            throw new InvalidOperationException("SetTemperatureHistoryEnabled request check failed.");
        }

        using var temperatureHistoryRetentionRequest = JsonDocument.Parse(
            ThrmIpcClient.BuildRequestLine("SetTemperatureHistoryRetentionHours", new { value = 12 }, "self-check-temperature-history-retention"));
        var temperatureHistoryRetentionData = temperatureHistoryRetentionRequest.RootElement.GetProperty("data");
        if (temperatureHistoryRetentionRequest.RootElement.GetProperty("type").GetString() != "SetTemperatureHistoryRetentionHours"
            || temperatureHistoryRetentionData.GetProperty("value").GetInt32() != 12)
        {
            throw new InvalidOperationException("SetTemperatureHistoryRetentionHours request check failed.");
        }

        var schedule = new TimeCurveScheduleSnapshot
        {
            Enabled = true,
            Rules =
            [
                new TimeCurveScheduleRuleSnapshot
                {
                    Id = "schedule-1",
                    Name = "Quiet hours",
                    Enabled = true,
                    Weekdays = [1, 2, 3, 4, 5],
                    StartTime = "22:00",
                    EndTime = "06:00",
                    CurveProfileId = "quiet",
                },
            ],
        };
        using var originalConfig = JsonDocument.Parse(
            "{\"futureField\":{\"keep\":true},\"timeCurveSchedule\":{\"enabled\":false,\"rules\":[]}}" );
        var patchedConfig = TimeCurveScheduleConfigJson.ReplaceTimeCurveSchedule(
            originalConfig.RootElement,
            schedule);
        using var updateConfigRequest = JsonDocument.Parse(
            ThrmIpcClient.BuildRequestLine("UpdateConfig", patchedConfig, "self-check-time-curve-update"));
        var updateConfigData = updateConfigRequest.RootElement.GetProperty("data");
        if (updateConfigRequest.RootElement.GetProperty("type").GetString() != "UpdateConfig"
            || updateConfigData.GetProperty("futureField").GetProperty("keep").ValueKind != JsonValueKind.True
            || updateConfigData.GetProperty("timeCurveSchedule").GetProperty("enabled").ValueKind != JsonValueKind.True
            || updateConfigData.GetProperty("timeCurveSchedule").GetProperty("rules").GetArrayLength() != 1)
        {
            throw new InvalidOperationException("Time curve schedule config patch check failed.");
        }

        var response = JsonSerializer.Deserialize<IpcMessage>(
            "{\"protocolVersion\":\"3.0\",\"requestId\":\"self-check\",\"isResponse\":true,\"success\":true,\"data\":\"pong\"}",
            ThrmIpcClient.JsonOptions);
        if (response is null || !response.IsResponse || !response.Success || response.Data?.GetString() != "pong")
        {
            throw new InvalidOperationException("Response parsing check failed.");
        }

        var config = JsonSerializer.Deserialize<ConfigSnapshot>(
            "{\"autoControl\":true,\"smartControl\":{\"learning\":true,\"learningBias\":\"cooling\",\"learnedOffsets\":[0,150,-50]}}",
            ThrmIpcClient.JsonOptions);
        if (config?.SmartControl is not { Learning: true, LearningBias: "cooling" } smartControl
            || !smartControl.LearnedOffsets.SequenceEqual(new[] { 0, 150, -50 }))
        {
            throw new InvalidOperationException("Smart control config decoding check failed.");
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
