using System.IO.Compression;
using System.Security.Cryptography;

namespace Northpass.Services.Installation;

public static class SafeArchive
{
    public const long MaximumArchiveSize = 64 * 1024 * 1024;
    public const long MaximumComponentSize = 16 * 1024 * 1024;

    public static void RelativePath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\\') || path.StartsWith('/') || path.Contains(':') || path.Length > 240 ||
            path.Split('/').Any(p => p.Length == 0 || p is "." or ".." || p.EndsWith('.') || p.EndsWith(' ') ||
                p.Any(c => c < 32 || "<>\"|?*".Contains(c)) || IsDevice(p)))
            throw new InvalidDataException("Unsafe archive path: " + path);
    }
    private static bool IsDevice(string part) => new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9", "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9" }
        .Contains(part.Split('.')[0], StringComparer.OrdinalIgnoreCase);

    public static void NoLinks(string path)
    {
        for (string? current = System.IO.Path.GetFullPath(path); current is not null; current = System.IO.Path.GetDirectoryName(current))
        {
            FileAttributes attributes;
            try { attributes = File.GetAttributes(current); }
            catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException) { continue; }
            if ((attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Engine paths cannot contain symbolic links or junctions: " + current);
        }
    }

    public static async Task VerifyFileAsync(string path, long size, string hash, CancellationToken token)
    {
        NoLinks(path);
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length != size || !Convert.ToHexString(await SHA256.HashDataAsync(file, token)).Equals(hash, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Engine integrity check failed: " + System.IO.Path.GetFileName(path));
    }

    public static async Task ExtractAsync(string archivePath, string destination, EngineManifest manifest, bool offline, CancellationToken token)
    {
        NoLinks(destination);
        using var archive = ZipFile.OpenRead(archivePath);
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            // Check even non-selected entries: never silently accept ambiguous or malicious archives.
            string name = entry.FullName.TrimEnd('/');
            RelativePath(name);
            int kind = (entry.ExternalAttributes >> 16) & 0xf000;
            if (kind is 0xa000 or 0x6000 or 0x2000 || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Archive contains a link or special file.");
            if (!entries.TryAdd(name, entry)) throw new InvalidDataException("Archive contains duplicate paths.");
            expanded = checked(expanded + entry.Length);
            if (expanded > 256L * 1024 * 1024 || entries.Count > 10000) throw new InvalidDataException("Archive exceeds extraction limits.");
        }
        foreach (var component in manifest.Components)
        {
            token.ThrowIfCancellationRequested();
            string source = (offline ? "" : manifest.ArchivePrefix) + component.SourcePath;
            if (!entries.TryGetValue(source, out var entry) || entry.Length != component.Size || entry.FullName.EndsWith('/'))
                throw new InvalidDataException("Required engine component is missing or has the wrong size: " + component.Path);
            string target = System.IO.Path.Combine(destination, component.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
            NoLinks(target);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(target)!);
            await using (var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            await using (var input = entry.Open())
            {
                var buffer = new byte[65536]; long total = 0; int count;
                while ((count = await input.ReadAsync(buffer, token)) > 0)
                {
                    total += count;
                    if (total > component.Size) throw new InvalidDataException("Expanded component exceeds its trusted size.");
                    await output.WriteAsync(buffer.AsMemory(0, count), token);
                }
            }
            await VerifyFileAsync(target, component.Size, component.Sha256, token);
        }
    }
}
