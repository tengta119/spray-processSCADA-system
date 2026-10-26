using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using IoTClient.Enums;
using Yale.SprayProcessSCADASystem.Communication;

var checks = new List<(string Name, Func<Task> Run)>
{
    ("Coils, numeric decoding, batching, and FC05/06/16 writes", BasicReadWrite),
    ("ABCD/CDAB/BADC/DCBA read and write against known wire bytes", ByteOrders),
    ("Fragmented TCP responses", Fragmented),
    ("Malformed headers, payloads, device exceptions, and disconnects", BadResponses),
    ("Address/type/range validation before sending", InvalidAddresses),
    ("Read timeout and reconnect", TimeoutAndReconnect),
    ("Concurrent polling and operator writes", ConcurrentCalls),
    ("Maximum block lengths and boundary addresses", BlockLimits)
};
foreach (var check in checks)
{
    await check.Run();
    Console.WriteLine($"PASS {check.Name}");
}
Console.WriteLine($"{checks.Count} protocol check groups passed.");

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static ModbusTcpPlcClient Connect(Slave server, string order = "ABCD", int timeout = 1500)
{
    var client = new ModbusTcpPlcClient("127.0.0.1", server.Port, 1, timeout, order);
    Assert(client.Open().IsSucceed, "Open failed");
    return client;
}

static async Task BasicReadWrite()
{
    await using var server = new Slave();
    server.Coils[7] = true;
    server.Coils[8] = false;
    server.Coils[9] = true;
    server.Registers[0] = 0x41a4; // 20.5f, independent IEEE754 bytes
    server.Registers[1] = 0;
    server.Registers[2] = 0x075b; // 123456789
    server.Registers[3] = 0xcd15;
    server.Registers[4] = 0xff85; // -123
    server.Registers[5] = 60000;
    using var client = Connect(server);
    var result = client.BatchRead(new()
    {
        ["C:7"] = DataTypeEnum.Bool, ["C:8"] = DataTypeEnum.Bool, ["C:9"] = DataTypeEnum.Bool,
        ["HR:0"] = DataTypeEnum.Float, ["HR:2"] = DataTypeEnum.Int32,
        ["HR:4"] = DataTypeEnum.Int16, ["HR:5"] = DataTypeEnum.UInt16
    });
    Assert(result.IsSucceed, result.ErrorMessage);
    Assert(result.Value["C:7"] is int one && one == 1 && (int)result.Value["C:8"] == 0 && (int)result.Value["C:9"] == 1, "Coil packing");
    Assert(result.Value["HR:0"] is float f && f == 20.5f, "Float decode/type");
    Assert(result.Value["HR:2"] is int i && i == 123456789, "Int32 decode/type");
    Assert(result.Value["HR:4"] is short s && s == -123, "Int16 decode/type");
    Assert(result.Value["HR:5"] is ushort u && u == 60000, "UInt16 decode/type");
    Assert(server.Requests.Count == 2, "Contiguous values must be batched into two frames");
    Assert(client.Write("C:8", true, DataTypeEnum.Bool).IsSucceed && server.Coils[8], "FC05 true");
    Assert(client.Write("C:8", "0", DataTypeEnum.Bool).IsSucceed && !server.Coils[8], "FC05 false");
    Assert(client.Write("HR:4", "-321", DataTypeEnum.Int16).IsSucceed && server.Registers[4] == unchecked((ushort)-321), "FC06");
    Assert(client.Write("HR:0", "20.5", DataTypeEnum.Float).IsSucceed && server.Registers[0] == 0x41a4 && server.Registers[1] == 0, "FC16 float");
    Assert(client.Write("HR:2", -123456789, DataTypeEnum.Int32).IsSucceed && server.Registers[2] == 0xf8a4 && server.Registers[3] == 0x32eb, "FC16 signed Int32");
    Assert(server.Requests.Any(p => p[0] == 5) && server.Requests.Any(p => p[0] == 6) && server.Requests.Any(p => p[0] == 16), "Write functions");
}

static async Task ByteOrders()
{
    var orders = new[]
    {
        ("ABCD", new byte[] { 0x41, 0xa4, 0, 0 }, new byte[] { 0x12, 0x34, 0x56, 0x78 }),
        ("CDAB", new byte[] { 0, 0, 0x41, 0xa4 }, new byte[] { 0x56, 0x78, 0x12, 0x34 }),
        ("BADC", new byte[] { 0xa4, 0x41, 0, 0 }, new byte[] { 0x34, 0x12, 0x78, 0x56 }),
        ("DCBA", new byte[] { 0, 0, 0xa4, 0x41 }, new byte[] { 0x78, 0x56, 0x34, 0x12 })
    };
    foreach (var (order, floatBytes, intBytes) in orders)
    {
        await using var server = new Slave();
        server.SetBytes(0, floatBytes);
        server.SetBytes(2, intBytes);
        using var client = Connect(server, order);
        var result = client.BatchRead(new() { ["HR:0"] = DataTypeEnum.Float, ["HR:2"] = DataTypeEnum.Int32 });
        Assert(result.IsSucceed && (float)result.Value["HR:0"] == 20.5f && (int)result.Value["HR:2"] == 0x12345678, $"Read {order}");
        Assert(client.Write("HR:4", 20.5f, DataTypeEnum.Float).IsSucceed && server.GetBytes(4, 2).SequenceEqual(floatBytes), $"Write float {order}");
        Assert(client.Write("HR:6", 0x12345678, DataTypeEnum.Int32).IsSucceed && server.GetBytes(6, 2).SequenceEqual(intBytes), $"Write Int32 {order}");
    }
}

static async Task Fragmented()
{
    await using var server = new Slave { Fragment = true };
    server.Registers[0] = 37;
    using var client = Connect(server);
    var result = client.BatchRead(new() { ["HR:0"] = DataTypeEnum.UInt16 });
    Assert(result.IsSucceed && (ushort)result.Value["HR:0"] == 37, "Complete fragmented frame");
}

static async Task BadResponses()
{
    var corruptions = new Action<byte[]>[]
    {
        bytes => bytes[1] ^= 1, // transaction
        bytes => bytes[3] = 1, // protocol
        bytes => bytes[6] = 2, // station
        bytes => { bytes[4] = 1; bytes[5] = 0; }, // oversized length
        bytes => bytes[8] = 99, // wrong byte count
        bytes => bytes[7] = 4 // wrong function
    };
    foreach (var corrupt in corruptions)
    {
        await using var server = new Slave { Mutate = corrupt };
        using var client = Connect(server);
        var result = client.BatchRead(new() { ["HR:0"] = DataTypeEnum.UInt16 });
        Assert(!result.IsSucceed && !client.Connected, "Reject malformed frame and close socket");
    }
    await using (var server = new Slave { ExceptionResponse = true })
    {
        using var client = Connect(server);
        var result = client.BatchRead(new() { ["HR:0"] = DataTypeEnum.UInt16 });
        Assert(!result.IsSucceed && result.ErrorMessage.Contains("0x02") && !client.Connected, "Modbus exception code");
    }
    await using (var server = new Slave { TruncateAndClose = true })
    {
        using var client = Connect(server);
        Assert(!client.BatchRead(new() { ["HR:0"] = DataTypeEnum.UInt16 }).IsSucceed && !client.Connected, "Truncated response");
    }
    await using (var server = new Slave { Mutate = bytes => bytes[10] ^= 1 })
    {
        using var client = Connect(server);
        Assert(!client.Write("C:0", true, DataTypeEnum.Bool).IsSucceed && !client.Connected, "Write echo validation");
    }
}

static async Task InvalidAddresses()
{
    foreach (var stationId in new byte[] { 0, 248, 255 })
    {
        var rejected = false;
        try { using var invalid = new ModbusTcpPlcClient("127.0.0.1", stationId: stationId); }
        catch (ArgumentOutOfRangeException) { rejected = true; }
        Assert(rejected, $"Reject invalid station ID {stationId}");
    }
    using (var lastStation = new ModbusTcpPlcClient("127.0.0.1", stationId: 247))
        Assert(!lastStation.Connected, "Constructor must accept station 247 without connecting");
    await using var server = new Slave();
    using var client = Connect(server);
    foreach (var (address, type) in new[]
    {
        ("DB1.0", DataTypeEnum.Float), ("HR:65535", DataTypeEnum.Int32), ("C:1", DataTypeEnum.Float),
        ("HR:1", DataTypeEnum.Bool), ("HR:-1", DataTypeEnum.Int16), ("HR:65536", DataTypeEnum.Int16),
        ("HR:0", DataTypeEnum.String)
    })
        Assert(!client.BatchRead(new() { [address] = type }).IsSucceed, $"Reject {address}/{type}");
    Assert(!client.Write("C:0", "2", DataTypeEnum.Bool).IsSucceed, "Reject non-boolean coil");
    Assert(!client.Write("HR:0", 99999, DataTypeEnum.UInt16).IsSucceed, "Reject numeric overflow");
    Assert(server.Requests.IsEmpty && client.Connected, "Local validation must send nothing and preserve connection");
}

static async Task TimeoutAndReconnect()
{
    await using var server = new Slave { DelayResponse = true };
    using var client = Connect(server, timeout: 100);
    var start = System.Diagnostics.Stopwatch.StartNew();
    Assert(!client.BatchRead(new() { ["HR:0"] = DataTypeEnum.UInt16 }).IsSucceed && !client.Connected, "Read timeout");
    Assert(start.ElapsedMilliseconds < 1500, "Timeout must be bounded");
    server.DelayResponse = false;
    Assert(client.Open().IsSucceed, "Reconnect after timeout");
    Assert(client.BatchRead(new() { ["HR:0"] = DataTypeEnum.UInt16 }).IsSucceed, "Read after reconnect");
}

static async Task ConcurrentCalls()
{
    await using var server = new Slave { Fragment = true };
    using var client = Connect(server);
    var calls = Enumerable.Range(0, 12).Select(index => Task.Run(() => index % 2 == 0
        ? client.BatchRead(new() { ["HR:0"] = DataTypeEnum.UInt16 }).IsSucceed
        : client.Write("C:0", true, DataTypeEnum.Bool).IsSucceed));
    Assert((await Task.WhenAll(calls)).All(success => success), "Concurrent operations must serialize whole frames");
}

static async Task BlockLimits()
{
    await using var server = new Slave();
    using var client = Connect(server);
    var points = Enumerable.Range(0, 126).ToDictionary(index => $"HR:{index}", _ => DataTypeEnum.UInt16);
    var result = client.BatchRead(points);
    Assert(result.IsSucceed && result.Value.Count == 126 && server.Requests.Count == 2, "125 register block split");
    var registers = server.Requests.ToArray();
    Assert(BinaryPrimitives.ReadUInt16BigEndian(registers[0].AsSpan(3)) == 125 && BinaryPrimitives.ReadUInt16BigEndian(registers[1].AsSpan(3)) == 1, "Read quantity limits");
    server.Requests.Clear();
    result = client.BatchRead(Enumerable.Range(0, 2001).ToDictionary(index => $"C:{index}", _ => DataTypeEnum.Bool));
    Assert(result.IsSucceed && result.Value.Count == 2001 && server.Requests.Count == 2, "2000 coil block split");
    result = client.BatchRead(new() { ["HR:65535"] = DataTypeEnum.UInt16, ["C:65535"] = DataTypeEnum.Bool });
    Assert(result.IsSucceed, "Last legal address");
}

sealed class Slave : IAsyncDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource cancellation = new();
    private readonly ConcurrentBag<TcpClient> clients = new();
    private readonly ConcurrentBag<Task> sessions = new();
    private readonly Task acceptLoop;
    public bool[] Coils { get; } = new bool[65536];
    public ushort[] Registers { get; } = new ushort[65536];
    public ConcurrentQueue<byte[]> Requests { get; } = new();
    public bool Fragment { get; init; }
    public bool ExceptionResponse { get; init; }
    public bool TruncateAndClose { get; init; }
    public bool DelayResponse { get; set; }
    public Action<byte[]>? Mutate { get; init; }
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;

    public Slave()
    {
        listener.Start();
        acceptLoop = Accept();
    }

    public void SetBytes(int offset, byte[] bytes)
    {
        for (var index = 0; index < bytes.Length / 2; index++)
            Registers[offset + index] = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(index * 2));
    }

    public byte[] GetBytes(int offset, int count)
    {
        var bytes = new byte[count * 2];
        for (var index = 0; index < count; index++)
            BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(index * 2), Registers[offset + index]);
        return bytes;
    }

    private async Task Accept()
    {
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(cancellation.Token);
                clients.Add(client);
                sessions.Add(Serve(client));
            }
        }
        catch (Exception exception) when (exception is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }

    private async Task Serve(TcpClient client)
    {
        try
        {
            var stream = client.GetStream();
            while (!cancellation.IsCancellationRequested)
            {
                var header = new byte[7];
                await stream.ReadExactlyAsync(header, cancellation.Token);
                var pdu = new byte[BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4)) - 1];
                await stream.ReadExactlyAsync(pdu, cancellation.Token);
                Requests.Enqueue(pdu);
                if (DelayResponse) await Task.Delay(500, cancellation.Token);
                var response = ExceptionResponse ? new byte[] { (byte)(pdu[0] | 0x80), 2 } : Process(pdu);
                var frame = new byte[7 + response.Length];
                header.CopyTo(frame, 0);
                BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(4), (ushort)(response.Length + 1));
                response.CopyTo(frame, 7);
                Mutate?.Invoke(frame);
                if (TruncateAndClose)
                {
                    await stream.WriteAsync(frame.AsMemory(0, 8), cancellation.Token);
                    client.Dispose();
                    return;
                }
                if (Fragment)
                {
                    foreach (var octet in frame)
                    {
                        await stream.WriteAsync(new[] { octet }, cancellation.Token);
                        await Task.Delay(1, cancellation.Token);
                    }
                }
                else await stream.WriteAsync(frame, cancellation.Token);
            }
        }
        catch (Exception exception) when (exception is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
    }

    private byte[] Process(byte[] pdu)
    {
        var start = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(1));
        var count = BinaryPrimitives.ReadUInt16BigEndian(pdu.AsSpan(3));
        switch (pdu[0])
        {
            case 1:
                var coilResponse = new byte[2 + (count + 7) / 8];
                coilResponse[0] = 1;
                coilResponse[1] = (byte)(coilResponse.Length - 2);
                for (var index = 0; index < count; index++)
                    if (Coils[start + index]) coilResponse[2 + index / 8] |= (byte)(1 << (index % 8));
                return coilResponse;
            case 3:
                var registerResponse = new byte[2 + count * 2];
                registerResponse[0] = 3;
                registerResponse[1] = (byte)(count * 2);
                GetBytes(start, count).CopyTo(registerResponse, 2);
                return registerResponse;
            case 5: Coils[start] = count == 0xff00; return pdu;
            case 6: Registers[start] = count; return pdu;
            case 16:
                SetBytes(start, pdu.AsSpan(6).ToArray());
                return pdu.AsSpan(0, 5).ToArray();
            default: throw new Exception($"Unexpected function {pdu[0]}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        await cancellation.CancelAsync();
        listener.Stop();
        foreach (var client in clients) client.Dispose();
        await acceptLoop;
        await Task.WhenAll(sessions);
        cancellation.Dispose();
    }
}
