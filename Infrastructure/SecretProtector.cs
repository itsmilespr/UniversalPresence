using System.Security.Cryptography;
using System.Text;

namespace UniversalPresence.Infrastructure;

/// <summary>
/// Protects small secrets at rest with Windows DPAPI (current user). Values stored in
/// config-adjacent files are never plaintext; a stolen config copy cannot be reused on
/// another account or machine.
/// </summary>
public static class SecretProtector
{
    private const string Prefix = "dpapi:";
    private static readonly byte[] Entropy = "UniversalPresence"u8.ToArray();

    public static string Protect(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        if (value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return value;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        var protectedBytes = ProtectedData.Protect(bytes, Entropy, DataProtectionScope.CurrentUser);
        return Prefix + Convert.ToBase64String(protectedBytes);
    }

    public static string Unprotect(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        if (!value.StartsWith(Prefix, StringComparison.Ordinal))
        {
            // Legacy plaintext value: callers migrate it on the next save.
            return value;
        }

        try
        {
            var protectedBytes = Convert.FromBase64String(value[Prefix.Length..]);
            var bytes = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }

    public static bool IsProtected(string? value) =>
        !string.IsNullOrEmpty(value) && value.StartsWith(Prefix, StringComparison.Ordinal);
}