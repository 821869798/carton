using System.Text.Encodings.Web;

namespace carton.Core.Utilities;

/// <summary>
/// 仅转义 JSON 规范强制要求的字符（U+0000–U+001F 控制字符、双引号、反斜杠）。
/// 所有其他码点（包括中文、emoji 等补充平面字符）均作为 UTF-8 字节直接写入，
/// 不生成 \uXXXX 转义序列。
/// </summary>
public sealed class UnicodeJsonEncoder : JavaScriptEncoder
{
    public static readonly UnicodeJsonEncoder Instance = new();

    private UnicodeJsonEncoder() { }

    // 最坏情况：代理对被转义为 \uXXXX\uXXXX = 12 个字符
    public override int MaxOutputCharactersPerInputCharacter => 12;

    /// <summary>
    /// 返回第一个需要转义的字符位置，-1 表示整段可直接复制（含代理对，
    /// Utf8JsonWriter 的快速路径会将 UTF-16 代理对正确转码为 4 字节 UTF-8）。
    /// </summary>
    public override unsafe int FindFirstCharacterToEncode(char* text, int textLength)
    {
        for (int i = 0; i < textLength; i++)
        {
            char c = text[i];
            if (c < '\x20' || c == '"' || c == '\\')
                return i;
        }
        return -1;
    }

    /// <summary>
    /// 只有控制字符、双引号、反斜杠才需要转义。
    /// </summary>
    public override bool WillEncode(int unicodeScalar)
        => (uint)unicodeScalar < 0x20u || unicodeScalar == '"' || unicodeScalar == '\\';

    /// <summary>
    /// 对于确实需要转义的码点，委托给默认编码器生成转义序列。
    /// </summary>
    public override unsafe bool TryEncodeUnicodeScalar(
        int unicodeScalar, char* buffer, int bufferLength, out int numberOfCharactersWritten)
        => JavaScriptEncoder.Default.TryEncodeUnicodeScalar(
            unicodeScalar, buffer, bufferLength, out numberOfCharactersWritten);
}
