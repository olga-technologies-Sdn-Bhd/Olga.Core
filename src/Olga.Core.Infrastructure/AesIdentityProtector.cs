using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using Olga.Core.Application;
using Olga.Core.Domain;

namespace Olga.Core.Infrastructure;

public sealed class AesIdentityProtector(byte[] masterKey) : IIdentityProtector
{
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private readonly byte[] encryptionKey = DeriveKey(masterKey, "olga:identity:encryption:v1");
    private readonly byte[] lookupKey = DeriveKey(masterKey, "olga:identity:lookup:v1");

    public ProtectedIdentity ProtectEmail(string memberId, string value, bool isPrimary)
    {
        var normalized = NormalizeEmail(value);
        var at = normalized.LastIndexOf('@');
        var hint = $"{normalized[0]}***{normalized[at..]}";
        return Protect("EMAIL", memberId, normalized, hint.Length <= 80 ? hint : hint[..80], isPrimary, true);
    }

    public ProtectedIdentity ProtectPhone(string memberId, string value, bool isPrimary)
    {
        var normalized = NormalizePhone(value);
        var visible = Math.Min(4, normalized.Length - 1);
        var hint = new string('*', normalized.Length - visible) + normalized[^visible..];
        return Protect("PHONE", memberId, normalized, hint, isPrimary, false);
    }

    public string EmailLookupHash(string email) => LookupHash("EMAIL", NormalizeEmail(email));

    public string Unprotect(string memberId, string provider, byte[] envelope)
    {
        if (envelope.Length <= 1 + NonceSize + TagSize || envelope[0] != 1) throw new CryptographicException("Unsupported identity ciphertext.");
        var nonce = envelope.AsSpan(1, NonceSize);
        var tag = envelope.AsSpan(1 + NonceSize, TagSize);
        var ciphertext = envelope.AsSpan(1 + NonceSize + TagSize);
        var plaintext = new byte[ciphertext.Length];
        using var aes = new AesGcm(encryptionKey, TagSize);
        aes.Decrypt(nonce, ciphertext, tag, plaintext, AssociatedData(provider, memberId));
        try { return Encoding.UTF8.GetString(plaintext); }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }

    private ProtectedIdentity Protect(string provider, string memberId, string normalized, string hint, bool isPrimary, bool isVerified)
    {
        var plaintext = Encoding.UTF8.GetBytes(normalized);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[plaintext.Length];
        var tag = new byte[TagSize];
        using var aes = new AesGcm(encryptionKey, TagSize);
        aes.Encrypt(nonce, plaintext, ciphertext, tag, AssociatedData(provider, memberId));

        var envelope = new byte[1 + NonceSize + TagSize + ciphertext.Length];
        envelope[0] = 1;
        nonce.CopyTo(envelope, 1);
        tag.CopyTo(envelope, 1 + NonceSize);
        ciphertext.CopyTo(envelope, 1 + NonceSize + TagSize);
        CryptographicOperations.ZeroMemory(plaintext);

        return new ProtectedIdentity(provider, LookupHash(provider, normalized), envelope, hint, isPrimary, isVerified);
    }

    private string LookupHash(string provider, string normalized) =>
        Convert.ToHexString(HMACSHA256.HashData(lookupKey, Encoding.UTF8.GetBytes($"{provider}\n{normalized}"))).ToLowerInvariant();

    private static string NormalizeEmail(string value)
    {
        var candidate = value.Trim().ToLowerInvariant();
        if (candidate.Length is 0 or > 254) throw new DomainException("MEMBER_EMAIL_INVALID");
        try
        {
            var parsed = new MailAddress(candidate);
            if (!string.Equals(parsed.Address, candidate, StringComparison.Ordinal)) throw new DomainException("MEMBER_EMAIL_INVALID");
            return candidate;
        }
        catch (FormatException) { throw new DomainException("MEMBER_EMAIL_INVALID"); }
    }

    private static string NormalizePhone(string value)
    {
        var candidate = value.Trim();
        if (candidate.Length is < 9 or > 16 || candidate[0] != '+' || candidate[1] == '0' || candidate[1..].Any(c => !char.IsAsciiDigit(c)))
            throw new DomainException("MEMBER_PHONE_INVALID");
        return candidate;
    }

    private static byte[] AssociatedData(string provider, string memberId) => Encoding.UTF8.GetBytes($"{provider}\n{memberId}");

    private static byte[] DeriveKey(byte[] masterKey, string purpose)
    {
        if (masterKey.Length != 32) throw new ArgumentException("Identity protection master key must be 32 bytes.", nameof(masterKey));
        return HMACSHA256.HashData(masterKey, Encoding.UTF8.GetBytes(purpose));
    }
}
