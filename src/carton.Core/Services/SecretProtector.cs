using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace carton.Core.Services;

/// <summary>
/// Protects the persisted native API secret at rest. On Windows the secret can fully
/// control the proxy (SetClashMode / SelectOutbound / CloseAllConnections) and read all
/// connection metadata, so it is encrypted with DPAPI (CurrentUser scope) before
/// landing in the preferences JSON; other platforms keep the previous plain
/// behaviour (the API is loopback-bound and the file inherits OS user isolation - see
/// docs/SINGBOX_API_MIGRATION.md threat model note).
/// </summary>
public static class SecretProtector
{
    private const string Prefix = "dpapi:";

    /// <summary>Encrypts the secret for storage (Windows: DPAPI; otherwise passthrough).</summary>
    public static string Protect(string? secret)
    {
        if (string.IsNullOrWhiteSpace(secret))
        {
            return string.Empty;
        }

        if (!OperatingSystem.IsWindows())
        {
            return secret;
        }

        try
        {
            var plainBytes = Encoding.UTF8.GetBytes(secret);
            var cipherBytes = DpapiProtect(plainBytes);
            return Prefix + Convert.ToBase64String(cipherBytes);
        }
        catch
        {
            return secret;
        }
    }

    /// <summary>
    /// True when the stored value is already in protected (dpapi-prefixed) form.
    /// Callers gate re-encryption on this: DPAPI encryption is NON-DETERMINISTIC, so
    /// comparing a freshly protected value against the stored one would always differ
    /// and trigger pointless (and non-atomic) file rewrites on every startup.
    /// </summary>
    public static bool IsProtected(string? stored)
        => !string.IsNullOrWhiteSpace(stored) && stored.StartsWith(Prefix, StringComparison.Ordinal);

    /// <summary>Decrypts a stored secret; returns null when absent or unreadable.</summary>
    public static string? Unprotect(string? stored)
    {
        if (string.IsNullOrWhiteSpace(stored))
        {
            return null;
        }

        if (stored.StartsWith(Prefix, StringComparison.Ordinal))
        {
            // DPAPI-prefixed values are only decryptable on the machine+user that wrote
            // them: a preferences file copied from Windows to another platform (or from
            // another machine) cannot yield a usable secret. Returning the raw ciphertext
            // would make it the literal API secret and fail auth with no hint at the
            // cause - degrade to "no stored secret" like any unreadable blob instead.
            if (!OperatingSystem.IsWindows())
            {
                return null;
            }

            try
            {
                var cipherBytes = Convert.FromBase64String(stored[Prefix.Length..]);
                var plainBytes = DpapiUnprotect(cipherBytes);
                return plainBytes != null ? Encoding.UTF8.GetString(plainBytes) : null;
            }
            catch
            {
                // DPAPI blobs are machine+user bound: unreadable means "no stored secret"
                // (e.g. profile copied from another machine) - the kernel's own config
                // still carries the plain secret for this session.
                return null;
            }
        }

        // No prefix: legacy plain value (pre-DPAPI or non-Windows writer) - use as-is.
        return stored;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(
        ref DATA_BLOB pDataIn,
        string? szDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DATA_BLOB pDataOut);

    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(
        ref DATA_BLOB pDataIn,
        IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy,
        IntPtr pvReserved,
        IntPtr pPromptStruct,
        int dwFlags,
        ref DATA_BLOB pDataOut);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr LocalFree(IntPtr hMem);

    private static byte[] DpapiProtect(byte[] data)
    {
        unsafe
        {
            fixed (byte* pData = data)
            {
                var inBlob = new DATA_BLOB
                {
                    cbData = data.Length,
                    pbData = (IntPtr)pData
                };
                var outBlob = default(DATA_BLOB);
                try
                {
                    if (!CryptProtectData(ref inBlob, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error());
                    }

                    var result = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                    return result;
                }
                finally
                {
                    if (outBlob.pbData != IntPtr.Zero)
                    {
                        LocalFree(outBlob.pbData);
                    }
                }
            }
        }
    }

    private static byte[]? DpapiUnprotect(byte[] data)
    {
        unsafe
        {
            fixed (byte* pData = data)
            {
                var inBlob = new DATA_BLOB
                {
                    cbData = data.Length,
                    pbData = (IntPtr)pData
                };
                var outBlob = default(DATA_BLOB);
                try
                {
                    if (!CryptUnprotectData(ref inBlob, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, CRYPTPROTECT_UI_FORBIDDEN, ref outBlob))
                    {
                        return null;
                    }

                    var result = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                    return result;
                }
                finally
                {
                    if (outBlob.pbData != IntPtr.Zero)
                    {
                        LocalFree(outBlob.pbData);
                    }
                }
            }
        }
    }
}
