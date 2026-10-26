using System.Buffers.Binary;
using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using IoTClient.Enums;

namespace Yale.SprayProcessSCADASystem.Communication;

/// <summary>
/// Modbus TCP client for zero-based C:n coils and HR:n holding registers.
/// Multi-register values use the selected ABCD/CDAB/BADC/DCBA byte order.
/// Calls share one lock so background reads and operator writes cannot interleave frames.
/// </summary>
public sealed class ModbusTcpPlcClient : IPlcClient
{
    private readonly object sync = new();
    private readonly string host;
    private readonly int port;
    private readonly byte stationId;
    private readonly int timeout;
    private readonly string wordOrder;
    private TcpClient? socket;
    private volatile bool connected;
    private ushort transactionId;

    public ModbusTcpPlcClient(string host, int port = 502, byte stationId = 1,
        int timeout = 3000, string wordOrder = "ABCD")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port));
        if (timeout < 1) throw new ArgumentOutOfRangeException(nameof(timeout));
        if (stationId is < 1 or > 247) throw new ArgumentOutOfRangeException(nameof(stationId), "站号必须为 1 到 247。");
        this.wordOrder = wordOrder.ToUpperInvariant();
        if (this.wordOrder is not ("ABCD" or "CDAB" or "BADC" or "DCBA"))
            throw new ArgumentException("字节序必须为 ABCD、CDAB、BADC 或 DCBA。", nameof(wordOrder));
        this.host = host;
        this.port = port;
        this.stationId = stationId;
        this.timeout = timeout;
    }

    public bool Connected => connected;

    public PlcResult Open()
    {
        lock (sync)
        {
            if (connected) return PlcResult.Success();
            CloseCore();
            try
            {
                socket = new TcpClient { NoDelay = true, ReceiveTimeout = timeout, SendTimeout = timeout };
                using var cancellation = new CancellationTokenSource(timeout);
                socket.ConnectAsync(host, port, cancellation.Token).AsTask().GetAwaiter().GetResult();
                connected = true;
                return PlcResult.Success();
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                CloseCore();
                return PlcResult.Failure($"Modbus TCP 连接失败：{ex.Message}");
            }
        }
    }

    public void Close()
    {
        lock (sync) CloseCore();
    }

    public void Dispose() => Close();

    private void CloseCore()
    {
        connected = false;
        socket?.Dispose();
        socket = null;
    }

    public PlcReadResult BatchRead(Dictionary<string, DataTypeEnum> addresses)
    {
        lock (sync)
        {
            try
            {
                ArgumentNullException.ThrowIfNull(addresses);
                var points = addresses.Select(item => Parse(item.Key, item.Value)).ToArray();
                var values = new Dictionary<string, object>();
                foreach (var group in points.GroupBy(point => point.Coil))
                {
                    var sorted = group.OrderBy(point => point.Offset).ToArray();
                    for (var index = 0; index < sorted.Length;)
                    {
                        var first = index;
                        var start = sorted[index].Offset;
                        var end = start + sorted[index].Width;
                        var maximum = group.Key ? 2000 : 125;
                        index++;
                        while (index < sorted.Length && sorted[index].Offset <= end &&
                               Math.Max(end, sorted[index].Offset + sorted[index].Width) - start <= maximum)
                        {
                            end = Math.Max(end, sorted[index].Offset + sorted[index].Width);
                            index++;
                        }
                        var count = end - start;
                        var request = AddressRequest(group.Key ? (byte)1 : (byte)3, start, count);
                        var response = Exchange(request);
                        var byteCount = group.Key ? (count + 7) / 8 : count * 2;
                        if (response.Length != byteCount + 2 || response[1] != byteCount)
                            throw new IOException("Modbus 读取响应的字节数不匹配。");
                        for (var pointIndex = first; pointIndex < index; pointIndex++)
                        {
                            var point = sorted[pointIndex];
                            var relative = point.Offset - start;
                            values.Add(point.Address, point.Coil
                                ? (response[2 + relative / 8] >> (relative % 8)) & 1
                                : Decode(response.AsSpan(2 + relative * 2, point.Width * 2), point.Type));
                        }
                    }
                }
                return new PlcReadResult { IsSucceed = true, Value = values };
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                if (ex is IOException or SocketException or OperationCanceledException) CloseCore();
                return new PlcReadResult { ErrorMessage = $"Modbus 读取失败：{ex.Message}" };
            }
        }
    }

    public PlcResult Write(string address, object value, DataTypeEnum dataType)
    {
        lock (sync)
        {
            try
            {
                var point = Parse(address, dataType);
                byte[] request;
                if (point.Coil)
                {
                    request = AddressRequest(5, point.Offset, ToBoolean(value) ? 0xff00 : 0);
                }
                else
                {
                    var bytes = Encode(value, dataType);
                    if (point.Width == 1)
                    {
                        request = AddressRequest(6, point.Offset, BinaryPrimitives.ReadUInt16BigEndian(bytes));
                    }
                    else
                    {
                        request = new byte[6 + bytes.Length];
                        request[0] = 16;
                        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(1), (ushort)point.Offset);
                        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(3), (ushort)point.Width);
                        request[5] = (byte)bytes.Length;
                        bytes.CopyTo(request, 6);
                    }
                }
                var response = Exchange(request);
                if (response.Length != 5 || !response.AsSpan().SequenceEqual(request.AsSpan(0, 5)))
                    throw new IOException("Modbus 写入确认与请求不匹配。");
                return PlcResult.Success();
            }
            catch (Exception ex) when (IsExpected(ex))
            {
                if (ex is IOException or SocketException or OperationCanceledException) CloseCore();
                return PlcResult.Failure($"Modbus 写入失败：{ex.Message}");
            }
        }
    }

    private byte[] Exchange(byte[] pdu)
    {
        if (!connected || socket is null) throw new InvalidOperationException("尚未连接 Modbus TCP 服务器。");
        var request = new byte[7 + pdu.Length];
        transactionId = unchecked((ushort)(transactionId + 1));
        BinaryPrimitives.WriteUInt16BigEndian(request, transactionId);
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(4), (ushort)(pdu.Length + 1));
        request[6] = stationId;
        pdu.CopyTo(request, 7);
        var stream = socket.GetStream();
        var stopwatch = Stopwatch.StartNew();
        stream.WriteTimeout = timeout;
        stream.Write(request);
        var header = new byte[7];
        ReadFully(stream, header, stopwatch);
        var length = BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(4));
        if (BinaryPrimitives.ReadUInt16BigEndian(header) != transactionId ||
            BinaryPrimitives.ReadUInt16BigEndian(header.AsSpan(2)) != 0 || header[6] != stationId ||
            length is < 2 or > 254)
            throw new IOException("Modbus MBAP 响应头的事务号、协议号、站号或长度无效。");
        var response = new byte[length - 1];
        ReadFully(stream, response, stopwatch);
        if (response[0] == (pdu[0] | 0x80))
        {
            if (response.Length != 2) throw new IOException("Modbus 异常响应长度无效。");
            throw new IOException($"服务器返回 Modbus 异常码 0x{response[1]:X2}（功能码 {pdu[0]}）。");
        }
        if (response[0] != pdu[0]) throw new IOException("Modbus 响应功能码不匹配。");
        return response;
    }

    private void ReadFully(NetworkStream stream, byte[] buffer, Stopwatch stopwatch)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var remaining = timeout - stopwatch.ElapsedMilliseconds;
            if (remaining <= 0) throw new IOException("等待 Modbus TCP 响应超时。");
            stream.ReadTimeout = (int)remaining;
            var count = stream.Read(buffer, offset, buffer.Length - offset);
            if (count == 0) throw new IOException("Modbus TCP 服务器已断开连接。");
            offset += count;
        }
    }

    private static byte[] AddressRequest(byte function, int offset, int countOrValue)
    {
        var request = new byte[5];
        request[0] = function;
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(1), checked((ushort)offset));
        BinaryPrimitives.WriteUInt16BigEndian(request.AsSpan(3), checked((ushort)countOrValue));
        return request;
    }

    private sealed record Point(string Address, bool Coil, int Offset, int Width, DataTypeEnum Type);

    private static Point Parse(string address, DataTypeEnum type)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        var parts = address.Split(':', StringSplitOptions.TrimEntries);
        if (parts.Length != 2 || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var offset) ||
            offset is < 0 or > 65535)
            throw new ArgumentException($"地址 {address} 无效，应为从零开始的 C:n 或 HR:n。");
        var coil = parts[0].Equals("C", StringComparison.OrdinalIgnoreCase);
        var holding = parts[0].Equals("HR", StringComparison.OrdinalIgnoreCase);
        if ((!coil && !holding) || (coil && type != DataTypeEnum.Bool) || (holding && type == DataTypeEnum.Bool))
            throw new ArgumentException($"地址 {address} 与类型 {type} 不匹配；Bool 使用 C:n，数值使用 HR:n。");
        var width = type switch
        {
            DataTypeEnum.Bool or DataTypeEnum.Byte or DataTypeEnum.Int16 or DataTypeEnum.UInt16 => 1,
            DataTypeEnum.Int32 or DataTypeEnum.UInt32 or DataTypeEnum.Float => 2,
            DataTypeEnum.Int64 or DataTypeEnum.UInt64 or DataTypeEnum.Double => 4,
            _ => throw new ArgumentException($"Modbus 暂不支持变量类型 {type}。")
        };
        if (offset + width > 65536) throw new ArgumentOutOfRangeException(nameof(address), "变量超出 Modbus 地址范围。");
        return new Point(address, coil, offset, width, type);
    }

    private object Decode(ReadOnlySpan<byte> source, DataTypeEnum type)
    {
        var bytes = source.ToArray();
        Reorder(bytes);
        return type switch
        {
            DataTypeEnum.Byte => (object)checked((byte)BinaryPrimitives.ReadUInt16BigEndian(bytes)),
            DataTypeEnum.Int16 => (object)BinaryPrimitives.ReadInt16BigEndian(bytes),
            DataTypeEnum.UInt16 => (object)BinaryPrimitives.ReadUInt16BigEndian(bytes),
            DataTypeEnum.Int32 => (object)BinaryPrimitives.ReadInt32BigEndian(bytes),
            DataTypeEnum.UInt32 => (object)BinaryPrimitives.ReadUInt32BigEndian(bytes),
            DataTypeEnum.Int64 => (object)BinaryPrimitives.ReadInt64BigEndian(bytes),
            DataTypeEnum.UInt64 => (object)BinaryPrimitives.ReadUInt64BigEndian(bytes),
            DataTypeEnum.Float => (object)BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(bytes)),
            DataTypeEnum.Double => (object)BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(bytes)),
            _ => throw new ArgumentException($"不支持变量类型 {type}。")
        };
    }

    private byte[] Encode(object value, DataTypeEnum type)
    {
        ArgumentNullException.ThrowIfNull(value);
        var bytes = new byte[type is DataTypeEnum.Int64 or DataTypeEnum.UInt64 or DataTypeEnum.Double ? 8
            : type is DataTypeEnum.Int32 or DataTypeEnum.UInt32 or DataTypeEnum.Float ? 4 : 2];
        switch (type)
        {
            case DataTypeEnum.Byte: BinaryPrimitives.WriteUInt16BigEndian(bytes, Convert.ToByte(value, CultureInfo.InvariantCulture)); break;
            case DataTypeEnum.Int16: BinaryPrimitives.WriteInt16BigEndian(bytes, Convert.ToInt16(value, CultureInfo.InvariantCulture)); break;
            case DataTypeEnum.UInt16: BinaryPrimitives.WriteUInt16BigEndian(bytes, Convert.ToUInt16(value, CultureInfo.InvariantCulture)); break;
            case DataTypeEnum.Int32: BinaryPrimitives.WriteInt32BigEndian(bytes, Convert.ToInt32(value, CultureInfo.InvariantCulture)); break;
            case DataTypeEnum.UInt32: BinaryPrimitives.WriteUInt32BigEndian(bytes, Convert.ToUInt32(value, CultureInfo.InvariantCulture)); break;
            case DataTypeEnum.Int64: BinaryPrimitives.WriteInt64BigEndian(bytes, Convert.ToInt64(value, CultureInfo.InvariantCulture)); break;
            case DataTypeEnum.UInt64: BinaryPrimitives.WriteUInt64BigEndian(bytes, Convert.ToUInt64(value, CultureInfo.InvariantCulture)); break;
            case DataTypeEnum.Float: BinaryPrimitives.WriteInt32BigEndian(bytes, BitConverter.SingleToInt32Bits(Convert.ToSingle(value, CultureInfo.InvariantCulture))); break;
            case DataTypeEnum.Double: BinaryPrimitives.WriteInt64BigEndian(bytes, BitConverter.DoubleToInt64Bits(Convert.ToDouble(value, CultureInfo.InvariantCulture))); break;
            default: throw new ArgumentException($"不支持变量类型 {type}。");
        }
        Reorder(bytes);
        return bytes;
    }

    // Each transform is its own inverse, so reads and writes use the same operation.
    // A single 16-bit register always retains the Modbus big-endian byte encoding.
    private void Reorder(byte[] bytes)
    {
        if (bytes.Length <= 2) return;
        if (wordOrder is "BADC" or "DCBA")
            for (var index = 0; index < bytes.Length; index += 2)
                (bytes[index], bytes[index + 1]) = (bytes[index + 1], bytes[index]);
        if (wordOrder is "CDAB" or "DCBA")
            for (var index = 0; index < bytes.Length / 2; index += 2)
            {
                var other = bytes.Length - 2 - index;
                (bytes[index], bytes[other]) = (bytes[other], bytes[index]);
                (bytes[index + 1], bytes[other + 1]) = (bytes[other + 1], bytes[index + 1]);
            }
    }

    private static bool ToBoolean(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value is bool boolean) return boolean;
        if (value is string text)
        {
            if (bool.TryParse(text, out var parsed)) return parsed;
            if (text == "0") return false;
            if (text == "1") return true;
            throw new FormatException("线圈值必须为 true、false、0 或 1。");
        }
        var number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
        return number switch { 0 => false, 1 => true, _ => throw new FormatException("线圈值必须为 0 或 1。") };
    }

    private static bool IsExpected(Exception ex) => ex is ArgumentException or FormatException or OverflowException or InvalidCastException
        or IOException or SocketException or InvalidOperationException or OperationCanceledException;
}
