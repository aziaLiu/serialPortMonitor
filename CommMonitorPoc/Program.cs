using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

var profilePath = Path.Combine(AppContext.BaseDirectory, "monitor-profile.json");
var profile = MonitorProfile.Load(profilePath);
var initOnly = args.Any(arg => arg.Equals("--init-only", StringComparison.OrdinalIgnoreCase));
var readOnce = args.Any(arg => arg.Equals("--read-once", StringComparison.OrdinalIgnoreCase));
var replayMode = args.Any(arg => arg.Equals("--replay", StringComparison.OrdinalIgnoreCase));
var captureDurationMs = TryGetIntArg(args, "--capture-ms") ?? profile.CaptureDurationMs;
var replayPath = TryGetStringArg(args, "--replay")
    ?? Path.Combine(AppContext.BaseDirectory, "captures");

if (args.Length > 0 && args[0].Equals("--init", StringComparison.OrdinalIgnoreCase))
{
    profile.Save(profilePath);
    Console.WriteLine($"Wrote default profile to {profilePath}");
    return;
}

if (args.Length > 0 && args[0].Equals("--ports", StringComparison.OrdinalIgnoreCase))
{
    var ports = PortDiscovery.GetPortNames();
    Console.WriteLine(ports.Length == 0 ? "No serial ports found." : string.Join(", ", ports));
    return;
}

if (replayMode)
{
    PacketReplay.Run(profile, replayPath);
    return;
}

if (string.IsNullOrWhiteSpace(profile.PortNames))
{
    var ports = PortDiscovery.GetPortNames();
    profile.PortNames = string.Join(",", ports);
}

Console.WriteLine($"DevicePath: {profile.DevicePath}");
Console.WriteLine($"Ports: {profile.PortNames}");
Console.WriteLine($"BlockWrite: {profile.BlockWrite}");
Console.WriteLine($"ReadBufferSize: 0x{profile.ReadBufferSize:X}");

DriverBootstrapper.EnsureReady(profile, Console.WriteLine);

using var monitor = new DriverMonitor(profile);
monitor.Open();
monitor.Initialize();

Console.WriteLine("Driver initialized. Press Ctrl+C to stop.");

if (initOnly)
{
    Console.WriteLine("Initialization completed. Exiting because --init-only was specified.");
    return;
}

using var cts = new CancellationTokenSource();
if (readOnce)
{
    cts.CancelAfter(profile.ReadOnceTimeoutMs);
}
else if (captureDurationMs > 0)
{
    cts.CancelAfter(captureDurationMs);
}

Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    cts.Cancel();
};

try
{
    await monitor.PollAsync(cts.Token, readOnce ? 1 : null);
}
catch (OperationCanceledException) when (readOnce)
{
    Console.WriteLine($"Read-once window expired after {profile.ReadOnceTimeoutMs} ms without a non-zero packet.");
}
catch (OperationCanceledException)
{
    Console.WriteLine($"Capture window expired after {captureDurationMs} ms.");
}

static int? TryGetIntArg(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(args[i + 1], out var value))
        {
            return value;
        }
    }

    return null;
}

static string? TryGetStringArg(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }

    return null;
}

public sealed class DriverMonitor : IDisposable
{
    private readonly MonitorProfile _profile;
    private SafeFileHandle? _deviceHandle;
    private int _seq;
    private readonly DiagnosticLogger _diagnosticLogger;

    public DriverMonitor(MonitorProfile profile)
    {
        _profile = profile;
        _diagnosticLogger = new DiagnosticLogger(Path.Combine(AppContext.BaseDirectory, "capture-diagnostic.log"));
    }

    public void Open()
    {
        var handle = NativeMethods.CreateFile(
            _profile.DevicePath,
            NativeMethods.GENERIC_READ | NativeMethods.GENERIC_WRITE,
            0,
            IntPtr.Zero,
            NativeMethods.OPEN_EXISTING,
            0,
            IntPtr.Zero);

        if (handle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"CreateFile failed for {_profile.DevicePath}");
        }

        _deviceHandle = handle;
    }

    public void Initialize()
    {
        EnsureOpen();
        _diagnosticLogger.Log($"Initialize start device={_profile.DevicePath} ports={_profile.PortNames} readIoctl=0x{_profile.ReadIoctl:X8}");

        var startReply = DeviceIoControl(_profile.StartIoctl, null, _profile.StartOutSize);
        Console.WriteLine($"Start IOCTL returned {BitConverter.ToString(startReply)}");
        _diagnosticLogger.Log($"StartIoctl reply={BitConverter.ToString(startReply)}");

        var preFilterReply = DeviceIoControl(_profile.CtrlCodeFilterIoctl, BuildCtrlCodeFilterPayload(_profile.PreCtrlCodeFilterPayloadHex), _profile.CtrlCodeFilterOutSize);
        Console.WriteLine($"CtrlCodeFilter pre returned {BitConverter.ToString(preFilterReply)}");
        _diagnosticLogger.Log($"CtrlCodeFilter pre reply={BitConverter.ToString(preFilterReply)}");

        var openPortPayloads = BuildOpenPortPayloads();
        foreach (var payload in openPortPayloads)
        {
            var openReply = DeviceIoControl(_profile.OpenPortIoctl, payload.Payload, _profile.OpenPortOutSize);
            Console.WriteLine($"OpenPort IOCTL {payload.Description} returned {BitConverter.ToString(openReply)}");
            _diagnosticLogger.Log($"OpenPort payload={payload.Description} reply={BitConverter.ToString(openReply)}");
        }

        var postFilterReply = DeviceIoControl(_profile.CtrlCodeFilterIoctl, BuildCtrlCodeFilterPayload(_profile.PostCtrlCodeFilterPayloadHex), _profile.CtrlCodeFilterOutSize);
        Console.WriteLine($"CtrlCodeFilter post returned {BitConverter.ToString(postFilterReply)}");
        _diagnosticLogger.Log($"CtrlCodeFilter post reply={BitConverter.ToString(postFilterReply)}");
    }

    public async Task PollAsync(CancellationToken cancellationToken, int? maxPackets)
    {
        await PollPacketsAsync(cancellationToken, null, maxPackets, saveArtifacts: true, logToConsole: true);
    }

    public async Task PollPacketsAsync(
        CancellationToken cancellationToken,
        Func<CaptureRecord, Task>? onPacket,
        int? maxPackets = null,
        bool saveArtifacts = true,
        bool logToConsole = false)
    {
        var outputDir = Path.Combine(AppContext.BaseDirectory, "captures");
        Directory.CreateDirectory(outputDir);
        var selectedPorts = _profile.GetSelectedPorts();
        string? lastMatchedPort = selectedPorts.Count == 1 ? selectedPorts.First() : null;
        var zeroReadStreak = 0;
        var filteredPacketCount = 0;
        _diagnosticLogger.Log($"Poll start selectedPorts={string.Join(",", selectedPorts.OrderBy(x => x, StringComparer.OrdinalIgnoreCase))} saveArtifacts={saveArtifacts}");

        while (!cancellationToken.IsCancellationRequested)
        {
            var buffer = DeviceIoControl(_profile.ReadIoctl, null, _profile.ReadBufferSize);
            if (!IsAllZero(buffer))
            {
                if (zeroReadStreak > 0)
                {
                    _diagnosticLogger.Log($"Read recovered after {zeroReadStreak} consecutive zero reads.");
                    zeroReadStreak = 0;
                }

                var decoded = DecodedPacket.Decode(buffer, _profile);
                var effectivePort = ResolveEffectivePort(decoded.PortName, selectedPorts, ref lastMatchedPort);
                if (effectivePort is null)
                {
                    filteredPacketCount++;
                    if (filteredPacketCount <= 5 || filteredPacketCount % 50 == 0)
                    {
                        _diagnosticLogger.Log(
                            $"Filtered packet #{filteredPacketCount}: rawPort='{decoded.PortName}' kind={decoded.Kind} typeCode={decoded.TypeCode} declared={decoded.DeclaredLength} lastMatched='{lastMatchedPort ?? "<null>"}'");
                    }

                    await Task.Delay(_profile.PollDelayMs, cancellationToken);
                    continue;
                }

                if (filteredPacketCount > 0)
                {
                    _diagnosticLogger.Log($"Packet stream resumed after filtering {filteredPacketCount} packets.");
                    filteredPacketCount = 0;
                }

                if (!string.IsNullOrWhiteSpace(effectivePort)
                    && !string.Equals(decoded.PortName, effectivePort, StringComparison.OrdinalIgnoreCase))
                {
                    decoded = decoded.WithPortName(effectivePort);
                }

                _seq++;
                var capturedAt = DateTimeOffset.Now;
                var timestamp = capturedAt.ToString("yyyyMMdd_HHmmss_fff");
                var filePath = Path.Combine(outputDir, $"{timestamp}_{_seq:D5}.bin");
                var payloadPath = Path.Combine(outputDir, $"{timestamp}_{_seq:D5}.payload.bin");
                var metaPath = Path.Combine(outputDir, $"{timestamp}_{_seq:D5}.meta.json");

                if (saveArtifacts)
                {
                    await File.WriteAllBytesAsync(filePath, buffer, cancellationToken);

                    if (decoded.Payload.Length > 0)
                    {
                        await File.WriteAllBytesAsync(payloadPath, decoded.Payload, cancellationToken);
                    }

                    await File.WriteAllTextAsync(
                        metaPath,
                        JsonSerializer.Serialize(decoded.ToMetadata(), new JsonSerializerOptions { WriteIndented = true }),
                        cancellationToken);
                }

                var record = new CaptureRecord(
                    _seq,
                    capturedAt,
                    buffer.Length,
                    filePath,
                    saveArtifacts && decoded.Payload.Length > 0 ? payloadPath : null,
                    saveArtifacts ? metaPath : null,
                    decoded);

                if (logToConsole)
                {
                    PacketPresenter.WriteSummary(record, _profile.ConsolePreviewBytes);
                }

                if (onPacket is not null)
                {
                    await onPacket(record);
                }

                if (_seq <= 5 || _seq % 50 == 0)
                {
                    _diagnosticLogger.Log(
                        $"Accepted packet seq={_seq} port={decoded.PortName} kind={decoded.Kind} dir={decoded.Direction} len={decoded.Payload.Length} typeCode={decoded.TypeCode}");
                }

                if (maxPackets.HasValue && _seq >= maxPackets.Value)
                {
                    _diagnosticLogger.Log($"Poll stop because maxPackets={maxPackets.Value} reached.");
                    return;
                }
            }
            else
            {
                zeroReadStreak++;
                if (zeroReadStreak <= 5 || zeroReadStreak == 10 || zeroReadStreak == 25 || zeroReadStreak % 100 == 0)
                {
                    _diagnosticLogger.Log($"Read returned all-zero buffer. streak={zeroReadStreak}");
                }
            }

            await Task.Delay(_profile.PollDelayMs, cancellationToken);
        }

        _diagnosticLogger.Log("Poll stop because cancellation token was triggered.");
    }

    private static string? ResolveEffectivePort(
        string? packetPortName,
        HashSet<string> selectedPorts,
        ref string? lastMatchedPort)
    {
        var portName = NormalizePortName(packetPortName);
        if (selectedPorts.Count == 0)
        {
            return IsValidComPortName(portName) ? portName : null;
        }

        if (IsValidComPortName(portName))
        {
            if (!selectedPorts.Contains(portName))
            {
                return null;
            }

            lastMatchedPort = portName;
            return portName;
        }

        if (selectedPorts.Count == 1)
        {
            return selectedPorts.First();
        }

        return lastMatchedPort;
    }

    private static string NormalizePortName(string? portName)
    {
        return string.IsNullOrWhiteSpace(portName)
            ? string.Empty
            : portName.Trim().ToUpperInvariant();
    }

    private static bool IsValidComPortName(string portName)
    {
        return !string.IsNullOrEmpty(portName)
            && portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            && portName.Length > 3
            && portName[3..].All(char.IsDigit);
    }

    private byte[] BuildCtrlCodeFilterPayload(string? hex)
    {
        if (!string.IsNullOrWhiteSpace(hex))
        {
            return HexCodec.Parse(hex);
        }

        return new byte[_profile.CtrlCodeFilterPayloadSize];
    }

    private List<FilterPayload> BuildOpenPortPayloads()
    {
        if (!string.IsNullOrWhiteSpace(_profile.OpenPortPayloadHex))
        {
            return [new FilterPayload("custom", HexCodec.Parse(_profile.OpenPortPayloadHex))];
        }

        var ports = (_profile.PortNames ?? string.Empty)
            .Split([',', ';', '|', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var payloads = new List<FilterPayload>();
        foreach (var port in ports)
        {
            var deviceName = NativeMethods.QueryDosDevice(port);
            if (string.IsNullOrWhiteSpace(deviceName))
            {
                Console.WriteLine($"Skipping filter payload for {port}: QueryDosDevice returned no target.");
                continue;
            }

            var payload = new byte[_profile.OpenPortPayloadSize];
            payload[0] = 0x01;

            WriteUtf16Le(payload, 3, deviceName);
            WriteUtf16Le(payload, 0x83, port);

            payloads.Add(new FilterPayload($"{port} -> {deviceName}", payload));
        }

        if (payloads.Count == 0)
        {
            payloads.Add(new FilterPayload("zero-fill fallback", new byte[_profile.OpenPortPayloadSize]));
        }

        return payloads;
    }

    private static void WriteUtf16Le(byte[] buffer, int startOffset, string value)
    {
        if (startOffset < 0 || startOffset >= buffer.Length)
        {
            return;
        }

        var bytes = Encoding.Unicode.GetBytes(value);
        var maxLength = Math.Min(bytes.Length, buffer.Length - startOffset - 2);
        if (maxLength > 0)
        {
            Array.Copy(bytes, 0, buffer, startOffset, maxLength);
        }
    }

    private byte[] DeviceIoControl(uint code, byte[]? input, int outputSize)
    {
        EnsureOpen();

        var output = new byte[outputSize];
        var inSize = input?.Length ?? 0;

        unsafe
        {
            fixed (byte* inPtr = input)
            fixed (byte* outPtr = output)
            {
                if (!NativeMethods.DeviceIoControl(
                        _deviceHandle!,
                        code,
                        inSize == 0 ? IntPtr.Zero : (IntPtr)inPtr,
                        inSize,
                        outputSize == 0 ? IntPtr.Zero : (IntPtr)outPtr,
                        outputSize,
                        out var bytesReturned,
                        IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"DeviceIoControl failed. code=0x{code:X8}");
                }

                if (bytesReturned >= 0 && bytesReturned < output.Length)
                {
                    Array.Resize(ref output, bytesReturned);
                }
            }
        }

        return output;
    }

    private void EnsureOpen()
    {
        if (_deviceHandle is null || _deviceHandle.IsInvalid)
        {
            throw new InvalidOperationException("Driver handle is not open.");
        }
    }

    private static bool IsAllZero(byte[] buffer)
    {
        foreach (var b in buffer)
        {
            if (b != 0)
            {
                return false;
            }
        }

        return true;
    }

    private static void PrintHexPreview(byte[] buffer, int previewBytes)
    {
        var size = Math.Min(buffer.Length, previewBytes);
        if (size == 0)
        {
            return;
        }

        Console.WriteLine(HexCodec.Format(buffer.AsSpan(0, size)));
    }

    public void Dispose()
    {
        _diagnosticLogger.Log("DriverMonitor dispose.");
        _deviceHandle?.Dispose();
    }
}

public sealed class MonitorProfile
{
    public string DevicePath { get; set; } = @"\\.\ComDrv11x";
    public bool AutoInstallDriver { get; set; } = true;
    public string DriverServiceName { get; set; } = "ComDrv11x";
    public string DriverDisplayName { get; set; } = "ComDrv11x Driver";
    public string? DriverPath { get; set; }
    public int DriverInstallTimeoutMs { get; set; } = 15000;
    public string PortNames { get; set; } = string.Empty;
    public bool BlockWrite { get; set; }
    public uint StartIoctl { get; set; } = 0x00222200;
    public uint CtrlCodeFilterIoctl { get; set; } = 0x00222218;
    public uint OpenPortIoctl { get; set; } = 0x0022220C;
    public uint ReadIoctl { get; set; } = 0x0022220B;
    public int StartOutSize { get; set; } = 4;
    public int CtrlCodeFilterOutSize { get; set; } = 4;
    public int OpenPortOutSize { get; set; } = 4;
    public int ReadBufferSize { get; set; } = 0x8000;
    public int CtrlCodeFilterPayloadSize { get; set; } = 0x1F;
    public int OpenPortPayloadSize { get; set; } = 0xA3;
    public string? PreCtrlCodeFilterPayloadHex { get; set; } = "01 01 01 00 00 00 00 00 01 01 00 01 01 01 01 01 01 01 00 00 00 00 01 00 01 00 00 00 00 00 00";
    public string? PostCtrlCodeFilterPayloadHex { get; set; } = "01 01 01 00 00 00 00 00 01 01 00 01 01 01 01 00 00 00 00 00 00 00 01 00 01 00 00 00 00 00 00";
    public string? OpenPortPayloadHex { get; set; }
    public int PollDelayMs { get; set; } = 10;
    public int ConsolePreviewBytes { get; set; } = 64;
    public int ReadOnceTimeoutMs { get; set; } = 5000;
    public int CaptureDurationMs { get; set; } = 0;
    public int HeaderSize { get; set; } = 64;
    public byte PayloadXorKey { get; set; } = 0x18;

    public static MonitorProfile Load(string path)
    {
        if (!File.Exists(path))
        {
            var profile = new MonitorProfile();
            profile.Save(path);
            return profile;
        }

        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<MonitorProfile>(json, JsonOptions()) ?? new MonitorProfile();
    }

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, JsonOptions());
        File.WriteAllText(path, json);
    }

    public HashSet<string> GetSelectedPorts()
    {
        return (PortNames ?? string.Empty)
            .Split([',', ';', '|', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(port => port.Trim().ToUpperInvariant())
            .Where(port => !string.IsNullOrWhiteSpace(port))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static JsonSerializerOptions JsonOptions()
    {
        return new JsonSerializerOptions
        {
            WriteIndented = true
        };
    }
}

internal readonly record struct FilterPayload(string Description, byte[] Payload);

public sealed record CaptureRecord(
    int Sequence,
    DateTimeOffset CapturedAt,
    int RawLength,
    string RawPath,
    string? PayloadPath,
    string? MetadataPath,
    DecodedPacket Packet);

public sealed class DecodedPacket
{
    public required byte[] Header { get; init; }
    public required byte[] HeaderDecoded { get; init; }
    public required byte[] Payload { get; init; }
    public required string Direction { get; init; }
    public required string Kind { get; init; }
    public required int SequenceHint { get; init; }
    public required int HeaderXorKey { get; init; }
    public required int PayloadXorKey { get; init; }
    public required int TypeCode { get; init; }
    public required int DeclaredLength { get; init; }
    public required string PortName { get; init; }

    public static DecodedPacket Decode(byte[] raw, MonitorProfile profile)
    {
        var headerSize = Math.Min(profile.HeaderSize, raw.Length);
        var header = raw[..headerSize];
        var headerXorKey = DetectHeaderXorKey(header);
        var headerDecoded = header.Select(b => (byte)(b ^ headerXorKey)).ToArray();
        var typeCode = ReadInt32Le(headerDecoded, 20);
        var declaredLength = ReadInt32Le(headerDecoded, 60);
        var portName = ReadUtf16Le(headerDecoded, 28, 16);
        var payload = DecodePayload(raw, headerSize, declaredLength, headerXorKey, out var xorKey, out var kind);

        var sequenceHint = headerDecoded.Length > 0 ? headerDecoded[0] : 0;
        kind = RefineKind(kind, typeCode, declaredLength);
        var direction = GuessDirection(typeCode, sequenceHint, payload);

        return new DecodedPacket
        {
            Header = header,
            HeaderDecoded = headerDecoded,
            Payload = payload,
            Direction = direction,
            Kind = kind,
            SequenceHint = sequenceHint,
            HeaderXorKey = headerXorKey,
            PayloadXorKey = xorKey,
            TypeCode = typeCode,
            DeclaredLength = declaredLength,
            PortName = portName
        };
    }

    public object ToMetadata()
    {
        return new
        {
            sequenceHint = SequenceHint,
            direction = Direction,
            kind = Kind,
            portName = PortName,
            typeCode = TypeCode,
            declaredLength = DeclaredLength,
            headerXorKey = HeaderXorKey,
            payloadXorKey = PayloadXorKey,
            headerHex = HexCodec.Format(Header),
            headerDecodedHex = HexCodec.Format(HeaderDecoded),
            payloadHex = HexCodec.Format(Payload)
        };
    }

    public DecodedPacket WithPortName(string portName)
    {
        return new DecodedPacket
        {
            Header = Header,
            HeaderDecoded = HeaderDecoded,
            Payload = Payload,
            Direction = Direction,
            Kind = Kind,
            SequenceHint = SequenceHint,
            HeaderXorKey = HeaderXorKey,
            PayloadXorKey = PayloadXorKey,
            TypeCode = TypeCode,
            DeclaredLength = DeclaredLength,
            PortName = portName
        };
    }

    private static byte[] DecodePayload(byte[] raw, int headerSize, int declaredLength, int headerXorKey, out int xorKey, out string kind)
    {
        if (raw.Length <= headerSize)
        {
            xorKey = 0;
            kind = "header-only";
            return [];
        }

        var maxLength = raw.Length - headerSize;
        var effectiveLength = declaredLength > 0 && declaredLength <= maxLength ? declaredLength : maxLength;
        var encodedPayload = raw[headerSize..(headerSize + effectiveLength)];
        xorKey = headerXorKey;
        var payload = encodedPayload.Select(b => (byte)(b ^ headerXorKey)).ToArray();
        kind = GuessKind(payload);
        return payload;
    }

    private static string GuessDirection(int typeCode, int sequenceHint, byte[] payload)
    {
        if (payload.Length == 0)
        {
            return "none";
        }

        return typeCode switch
        {
            4 => "tx",
            3 => "rx",
            0 => "tx",
            9 => "rx",
            _ => (sequenceHint & 1) == 0 ? "tx?" : "rx?"
        };
    }

    private static string GuessKind(byte[] payload)
    {
        if (payload.Length == 0)
        {
            return "header-only";
        }

        return "serial-frame";
    }

    private static string RefineKind(string currentKind, int typeCode, int declaredLength)
    {
        if (currentKind == "serial-frame")
        {
            return currentKind;
        }

        if (declaredLength == 0 && typeCode == 0)
        {
            return "port-open";
        }

        if (declaredLength == 0 && typeCode == 9)
        {
            return "port-close";
        }

        return currentKind;
    }

    private static int DetectHeaderXorKey(byte[] header)
    {
        if (header.Length == 0)
        {
            return 0;
        }

        var candidates = new List<int>();
        if (header.Length > 1) candidates.Add(header[1]);
        if (header.Length > 2) candidates.Add(header[2]);
        if (header.Length > 3) candidates.Add(header[3]);
        candidates.Add(0x18);
        candidates.Add(0x00);

        var bestKey = candidates[0];
        var bestScore = int.MinValue;

        foreach (var candidate in candidates.Distinct())
        {
            var decoded = header.Select(b => (byte)(b ^ candidate)).ToArray();
            var score = ScoreDecodedHeader(decoded);
            if (score > bestScore)
            {
                bestScore = score;
                bestKey = candidate;
            }
        }

        return bestKey;
    }

    private static int ScoreDecodedHeader(byte[] decoded)
    {
        var score = 0;
        var typeCode = ReadInt32Le(decoded, 20);
        var declaredLength = ReadInt32Le(decoded, 60);
        var portName = ReadUtf16Le(decoded, 28, 16);

        if (typeCode is 0 or 3 or 4 or 9)
        {
            score += 8;
        }

        if (declaredLength >= 0 && declaredLength <= 0x8000)
        {
            score += 6;
        }

        if (IsValidComPortName(portName))
        {
            score += 10;
        }
        else if (string.IsNullOrEmpty(portName))
        {
            score += 2;
        }

        var evenUtf16Bytes = 0;
        for (var i = 29; i < Math.Min(decoded.Length, 60); i += 2)
        {
            if (decoded[i] == 0)
            {
                evenUtf16Bytes++;
            }
        }

        score += evenUtf16Bytes;
        return score;
    }

    private static bool IsValidComPortName(string portName)
    {
        return !string.IsNullOrEmpty(portName)
            && portName.StartsWith("COM", StringComparison.OrdinalIgnoreCase)
            && portName.Length > 3
            && portName[3..].All(char.IsDigit);
    }

    private static int ReadInt32Le(byte[] bytes, int offset)
    {
        if (offset < 0 || offset + 4 > bytes.Length)
        {
            return 0;
        }

        return BitConverter.ToInt32(bytes, offset);
    }

    private static string ReadUtf16Le(byte[] bytes, int offset, int maxChars)
    {
        if (offset < 0 || offset >= bytes.Length)
        {
            return string.Empty;
        }

        var count = Math.Min(maxChars * 2, bytes.Length - offset);
        var text = Encoding.Unicode.GetString(bytes, offset, count);
        var nullIndex = text.IndexOf('\0');
        return nullIndex >= 0 ? text[..nullIndex] : text;
    }
}

public static class PacketPresenter
{
    public static void WriteSummary(CaptureRecord record, int previewBytes)
    {
        var decoded = record.Packet;
        Console.WriteLine(
            $"[{record.CapturedAt:HH:mm:ss.fff}] #{record.Sequence:D5} {decoded.Direction,-3} {decoded.PortName,-5} payload={decoded.Payload.Length,3} declared={decoded.DeclaredLength,3} typeCode={decoded.TypeCode,2} hdrKey=0x{decoded.HeaderXorKey:X2} payKey=0x{decoded.PayloadXorKey:X2} kind={decoded.Kind} file={Path.GetFileName(record.RawPath)}");

        if (decoded.Payload.Length > 0)
        {
            Console.WriteLine($"payload: {HexCodec.Format(decoded.Payload.AsSpan(0, Math.Min(decoded.Payload.Length, previewBytes)))}");
            Console.WriteLine($"ascii:   {AsciiCodec.Format(decoded.Payload.AsSpan(0, Math.Min(decoded.Payload.Length, previewBytes)))}");
        }
        else
        {
            Console.WriteLine($"raw:     {HexCodec.Format(decoded.Header.AsSpan(0, Math.Min(record.RawLength, previewBytes)))}");
        }

        Console.WriteLine($"hdr-dec: {HexCodec.Format(decoded.HeaderDecoded.AsSpan(0, Math.Min(decoded.HeaderDecoded.Length, previewBytes)))}");
        Console.WriteLine();
    }
}

public static class PacketReplay
{
    public static void Run(MonitorProfile profile, string directory)
    {
        if (!Directory.Exists(directory))
        {
            Console.WriteLine($"Replay directory not found: {directory}");
            return;
        }

        var files = Directory.GetFiles(directory, "*.bin")
            .Where(path => !path.EndsWith(".payload.bin", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (files.Length == 0)
        {
            Console.WriteLine($"No capture files found in {directory}");
            return;
        }

        var sequence = 0;
        foreach (var file in files)
        {
            sequence++;
            var raw = File.ReadAllBytes(file);
            var decoded = DecodedPacket.Decode(raw, profile);
            var record = new CaptureRecord(sequence, File.GetLastWriteTime(file), raw.Length, file, null, null, decoded);
            PacketPresenter.WriteSummary(record, profile.ConsolePreviewBytes);
        }
    }
}

public static class HexCodec
{
    public static byte[] Parse(string value)
    {
        var chars = value.Where(Uri.IsHexDigit).ToArray();
        if (chars.Length % 2 != 0)
        {
            throw new FormatException("Hex string must contain an even number of hex digits.");
        }

        var bytes = new byte[chars.Length / 2];
        for (var i = 0; i < bytes.Length; i++)
        {
            bytes[i] = Convert.ToByte(new string(chars, i * 2, 2), 16);
        }

        return bytes;
    }

    public static string Format(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length * 3);
        for (var i = 0; i < bytes.Length; i++)
        {
            if (i > 0)
            {
                sb.Append(' ');
            }

            sb.Append(bytes[i].ToString("X2"));
        }

        return sb.ToString();
    }
}

public static class AsciiCodec
{
    public static string Format(ReadOnlySpan<byte> bytes)
    {
        var sb = new StringBuilder(bytes.Length);
        foreach (var b in bytes)
        {
            sb.Append(b >= 0x20 && b <= 0x7E ? (char)b : '.');
        }

        return sb.ToString();
    }
}

internal sealed class DiagnosticLogger
{
    private readonly string _path;
    private readonly object _syncRoot = new();

    public DiagnosticLogger(string path)
    {
        _path = path;
    }

    public void Log(string message)
    {
        var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] {message}{Environment.NewLine}";
        lock (_syncRoot)
        {
            File.AppendAllText(_path, line, Encoding.UTF8);
        }
    }
}

internal static class NativeMethods
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint OPEN_EXISTING = 3;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeviceIoControl(
        SafeFileHandle hDevice,
        uint dwIoControlCode,
        IntPtr lpInBuffer,
        int nInBufferSize,
        IntPtr lpOutBuffer,
        int nOutBufferSize,
        out int lpBytesReturned,
        IntPtr lpOverlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint QueryDosDevice(
        string? lpDeviceName,
        char[] lpTargetPath,
        int ucchMax);

    public static string? QueryDosDevice(string deviceName)
    {
        var buffer = new char[4096];
        var length = QueryDosDevice(deviceName, buffer, buffer.Length);
        if (length == 0)
        {
            return null;
        }

        var raw = new string(buffer, 0, (int)length);
        var zeroIndex = raw.IndexOf('\0');
        return zeroIndex >= 0 ? raw[..zeroIndex] : raw;
    }
}

public static class PortDiscovery
{
    private const string SerialCommKeyPath = @"HARDWARE\DEVICEMAP\SERIALCOMM";

    public static string[] GetPortNames()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        using var key = Registry.LocalMachine.OpenSubKey(SerialCommKeyPath);
        if (key is null)
        {
            return [];
        }

        var ports = new List<string>();
        foreach (var name in key.GetValueNames())
        {
            var value = key.GetValue(name)?.ToString();
            if (!string.IsNullOrWhiteSpace(value))
            {
                ports.Add(value);
            }
        }

        return ports
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
}
