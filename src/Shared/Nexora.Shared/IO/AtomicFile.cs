using System.Security.Cryptography;

namespace Nexora.Shared.IO;

public static class AtomicFile
{
    /// <summary>Writes to a temp file in the same directory, flushes to disk, then renames over the target.</summary>
    public static void WriteAllBytes(string path, ReadOnlySpan<byte> data)
    {
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        Directory.CreateDirectory(dir);
        var tmp = Path.Combine(dir, $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var fs = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                fs.Write(data);
                fs.Flush(flushToDisk: true);
            }
            File.Move(tmp, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tmp)) File.Delete(tmp);
        }
    }

    public static void WriteAllText(string path, string text) =>
        WriteAllBytes(path, System.Text.Encoding.UTF8.GetBytes(text));
}

public static class Hashing
{
    public static string Sha256Hex(Stream stream)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(stream)).ToLowerInvariant();
    }

    public static string Sha256File(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        return Sha256Hex(fs);
    }

    public static string Sha256Hex(ReadOnlySpan<byte> data) => Convert.ToHexString(SHA256.HashData(data)).ToLowerInvariant();

    public static bool IsSha256Hex(string? s) =>
        s is { Length: 64 } && s.All(Uri.IsHexDigit);
}
