using System.Text.Json;

namespace NTMiner.Modern;

public static class PipeProtocol
{
    public const int MaximumMessageLength = 1024 * 1024;
    public static async Task<T?> ReadAsync<T>(StreamReader reader, CancellationToken token = default)
    {
        var line = await BoundedLines.ReadAsync(reader, MaximumMessageLength, token);
        return line is null ? default : JsonSerializer.Deserialize<T>(line);
    }
    public static async Task WriteAsync<T>(StreamWriter writer, T value, CancellationToken token = default)
    {
        var json = JsonSerializer.Serialize(value);
        if (json.Length > MaximumMessageLength) throw new InvalidDataException("消息过大。");
        await writer.WriteLineAsync(json.AsMemory(), token);
        await writer.FlushAsync(token);
    }
}
