using System.Security.Cryptography;
using System.Text;

namespace Nexora.Shared.Security;

/// <summary>
/// The shared secret that pairs a client with a receiver. Shown to the user as 20 Crockford base32
/// characters in groups of four (100 bits), e.g. "7KQ2-M9XD-4TPA-ZR3W-HN6C".
/// It is never sent over the network; both sides derive authentication and session keys from it.
/// </summary>
public sealed class PairingKey : IEquatable<PairingKey>
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford base32
    public const int CharCount = 20;
    private readonly byte[] _secret;

    private PairingKey(byte[] secret) => _secret = secret;

    /// <summary>Raw key material: SHA-256 of the canonical code, so every representation maps to 32 bytes.</summary>
    public ReadOnlySpan<byte> Secret => _secret;

    public string Code { get; private init; } = "";

    public static PairingKey Generate()
    {
        Span<char> chars = stackalloc char[CharCount];
        for (int i = 0; i < CharCount; i++)
            chars[i] = Alphabet[RandomNumberGenerator.GetInt32(Alphabet.Length)];
        return FromCanonical(new string(chars));
    }

    public static bool TryParse(string? text, out PairingKey? key)
    {
        key = null;
        if (string.IsNullOrWhiteSpace(text)) return false;
        var sb = new StringBuilder(CharCount);
        foreach (char raw in text)
        {
            if (raw is '-' or ' ') continue;
            char c = char.ToUpperInvariant(raw);
            c = c switch { 'O' => '0', 'I' or 'L' => '1', _ => c }; // Crockford decoding aliases
            if (Alphabet.IndexOf(c) < 0) return false;
            sb.Append(c);
        }
        if (sb.Length != CharCount) return false;
        key = FromCanonical(sb.ToString());
        return true;
    }

    public static PairingKey Parse(string text) =>
        TryParse(text, out var k) ? k! : throw new FormatException("Pairing code must be 20 characters (A-Z, 0-9), e.g. 7KQ2-M9XD-4TPA-ZR3W-HN6C.");

    private static PairingKey FromCanonical(string canonical) =>
        new(SHA256.HashData(Encoding.ASCII.GetBytes("nexora-pairing-v1:" + canonical))) { Code = canonical };

    /// <summary>User-facing grouped form.</summary>
    public string Display => string.Join("-", Enumerable.Range(0, CharCount / 4).Select(i => Code.Substring(i * 4, 4)));

    /// <summary>Short non-secret fingerprint to let users confirm both sides hold the same key.</summary>
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(_secret))[..8];

    public bool Equals(PairingKey? other) => other is not null && CryptographicOperations.FixedTimeEquals(_secret, other._secret);
    public override bool Equals(object? obj) => obj is PairingKey k && Equals(k);
    public override int GetHashCode() => BitConverter.ToInt32(_secret, 0);
    public override string ToString() => $"PairingKey({Fingerprint})";
}
