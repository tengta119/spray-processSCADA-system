using IoTClient.Clients.PLC;
using IoTClient.Common.Enums;
using IoTClient.Enums;

namespace Yale.SprayProcessSCADASystem.Communication;

/// <summary>Preserves the original Siemens S7 connection behind the common PLC interface.</summary>
public sealed class SiemensPlcClient : IPlcClient
{
    private readonly SiemensClient client;

    public SiemensPlcClient(SiemensVersion cpuType, string host, int port, byte slot, byte rack, int timeout)
    {
        client = new SiemensClient(cpuType, host, port, slot, rack, timeout);
    }

    public bool Connected => client.Connected;

    public PlcResult Open()
    {
        var result = client.Open();
        return new PlcResult { IsSucceed = result.IsSucceed, ErrorMessage = result.Err ?? string.Empty };
    }

    public void Close() => client.Close();
    public void Dispose() => Close();

    public PlcReadResult BatchRead(Dictionary<string, DataTypeEnum> addresses)
    {
        var result = client.BatchRead(addresses);
        return new PlcReadResult
        {
            IsSucceed = result.IsSucceed,
            ErrorMessage = result.Err ?? string.Empty,
            Value = result.Value ?? new Dictionary<string, object>()
        };
    }

    public PlcResult Write(string address, object value, DataTypeEnum dataType)
    {
        var result = client.Write(address, value, dataType);
        return new PlcResult { IsSucceed = result.IsSucceed, ErrorMessage = result.Err ?? string.Empty };
    }
}
