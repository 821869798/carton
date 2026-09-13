#nullable disable
namespace Downloader.Serializer;

public interface IBinarySerializer
{
    /// <summary>
    /// Serialize the specified value.
    /// </summary>
    /// <returns>The serialized bytes</returns>
    /// <param name="value">Value to serialize</param>
    byte[] Serialize(PackageInfo value);

    /// <summary>
    /// Deserialize the specified bytes.
    /// </summary>
    /// <returns>The deserialized PackageInfo object</returns>
    /// <param name="bytes">The serialized bytes</param>
    PackageInfo Deserialize(byte[] bytes, int offset = 0, int count = -1);
}
