using System.Security.Cryptography;
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
/// <remarks>
/// DPAPI goes through the <c>System.Security.Cryptography.ProtectedData</c> package on
/// purpose. A hand-written <c>crypt32</c> P/Invoke briefly replaced it, which bought no
/// measurable memory (the assembly is tens of KB of metadata) while adding
/// <c>AllowUnsafeBlocks</c>, a manual <c>LocalFree</c> and hand-rolled buffer hygiene.
/// Both call <c>CryptProtectData</c> with the same flags and no optional entropy, so
/// <c>dpapi:</c> blobs written by either version stay readable - no migration needed.
/// </remarks>
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
            try
            {
                var cipherBytes = ProtectedData.Protect(
                    plainBytes,
                    optionalEntropy: null,
                    scope: DataProtectionScope.CurrentUser);
                return Prefix + Convert.ToBase64String(cipherBytes);
            }
            finally
            {
                // The plaintext secret must not linger in a pooled/collectable buffer.
                Array.Clear(plainBytes);
            }
        }
        catch (Exception ex)
        {
            // Silently returning the raw secret here would write it to preferences.json in
            // PLAINTEXT, and IsProtected() would then report false forever - a silent,
            // permanent downgrade of the at-rest protection. Fail loudly instead: the
            // caller decides whether to keep the old ciphertext or surface the error.
            throw new InvalidOperationException(
                "Failed to protect the sing-box API secret with DPAPI. Refusing to persist it in plaintext.",
                ex);
        }
    }

    /// <summary>
    /// Non-throwing variant of <see cref="Protect"/> for persistence paths that must not
    /// fail the surrounding operation. Returns <see langword="false"/> when the secret
    /// could not be encrypted, in which case <paramref name="protectedValue"/> is empty and
    /// the caller must leave the previously stored value untouched rather than writing
    /// plaintext.
    /// </summary>
    public static bool TryProtect(string? secret, out string protectedValue)
    {
        try
        {
            protectedValue = Protect(secret);
            return true;
        }
        catch
        {
            protectedValue = string.Empty;
            return false;
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
                var plainBytes = ProtectedData.Unprotect(
                    cipherBytes,
                    optionalEntropy: null,
                    scope: DataProtectionScope.CurrentUser);
                try
                {
                    return Encoding.UTF8.GetString(plainBytes);
                }
                finally
                {
                    // Same discipline as Protect: the decrypted bytes must not linger in the
                    // heap waiting for a collection.
                    Array.Clear(plainBytes);
                }
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
}
