using System.Security.Cryptography;
using System.Text;
using Nexora.Shared.Security;

namespace Nexora.Transport;

/// <summary>Derives all NXP/2 keys from the pairing secret with HKDF-SHA256. See docs/SECURITY.md.</summary>
public static class KeySchedule
{
    public const int NonceSize = 32;

    public static byte[] AuthKey(PairingKey key) => Derive(key, [], "nexora nxp2 auth");
    public static byte[] DiscoveryKey(PairingKey key) => Derive(key, [], "nexora nxp2 discovery");

    public static (byte[] ClientToServer, byte[] ServerToClient) SessionKeys(PairingKey key, byte[] clientNonce, byte[] serverNonce)
    {
        byte[] salt = [.. clientNonce, .. serverNonce];
        return (Derive(key, salt, "nexora nxp2 c2s"), Derive(key, salt, "nexora nxp2 s2c"));
    }

    public static byte[] ServerProof(PairingKey key, byte[] clientNonce, byte[] serverNonce, string receiverId) =>
        HMACSHA256.HashData(AuthKey(key), (byte[])[.. "srv"u8, .. clientNonce, .. serverNonce, .. Encoding.UTF8.GetBytes(receiverId)]);

    public static byte[] ClientProof(PairingKey key, byte[] clientNonce, byte[] serverNonce, string clientId) =>
        HMACSHA256.HashData(AuthKey(key), (byte[])[.. "cli"u8, .. serverNonce, .. clientNonce, .. Encoding.UTF8.GetBytes(clientId)]);

    public static byte[] NewNonce() => RandomNumberGenerator.GetBytes(NonceSize);

    private static byte[] Derive(PairingKey key, byte[] salt, string info) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, key.Secret.ToArray(), 32, salt, Encoding.ASCII.GetBytes(info));
}
