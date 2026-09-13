#nullable disable
using System;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Downloader.Serializer;

[JsonSourceGenerationOptions(WriteIndented = false)]
[JsonSerializable(typeof(PackageInfo))]
internal partial class JsonBinarySerializerContext : JsonSerializerContext { }

public class JsonBinarySerializer : IBinarySerializer
{
    public byte[] Serialize(PackageInfo value)
    {
        if (value == null)
            throw new ArgumentNullException(nameof(value));

        string json = JsonSerializer.Serialize(value, JsonBinarySerializerContext.Default.PackageInfo);
        return Encoding.UTF8.GetBytes(json);
    }

    public PackageInfo Deserialize(byte[] bytes, int offset = 0, int count = -1)
    {
        ArgumentNullException.ThrowIfNull(bytes);

        if (bytes.Length == 0)
            return default;

        if (count == -1)
            count = bytes.Length - offset;

        string json = Encoding.UTF8.GetString(bytes, offset, count);
        return JsonSerializer.Deserialize(json, JsonBinarySerializerContext.Default.PackageInfo);
    }
}