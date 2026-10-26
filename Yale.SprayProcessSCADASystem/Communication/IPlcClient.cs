using IoTClient.Enums;

namespace Yale.SprayProcessSCADASystem.Communication;

public interface IPlcClient : IDisposable
{
    bool Connected { get; }
    PlcResult Open();
    void Close();
    PlcReadResult BatchRead(Dictionary<string, DataTypeEnum> addresses);
    PlcResult Write(string address, object value, DataTypeEnum dataType);
}

public class PlcResult
{
    public bool IsSucceed { get; init; }
    public string ErrorMessage { get; init; } = string.Empty;

    public static PlcResult Success() => new() { IsSucceed = true };
    public static PlcResult Failure(string message) => new() { ErrorMessage = message };
}

public sealed class PlcReadResult : PlcResult
{
    public Dictionary<string, object> Value { get; init; } = new();
}
