using System.Security.Cryptography;
using System.Text;
using VoxType.Core.Settings;

namespace VoxType.Infrastructure.Settings;

public sealed class DpapiSecretProtector : ISecretProtector
{
    public string Protect(string plaintext)
    {
        if (plaintext.Length == 0) return "";
        byte[] bytes = Encoding.UTF8.GetBytes(plaintext);
        try { return Convert.ToBase64String(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    public string Unprotect(string ciphertext)
    {
        if (ciphertext.Length == 0) return "";
        byte[] plain = ProtectedData.Unprotect(Convert.FromBase64String(ciphertext), null, DataProtectionScope.CurrentUser);
        try { return Encoding.UTF8.GetString(plain); }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }
}
