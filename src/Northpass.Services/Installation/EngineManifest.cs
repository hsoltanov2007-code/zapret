using System.Text.Json;
using System.Text.RegularExpressions;

namespace Northpass.Services.Installation;

public sealed record EngineComponent(string SourcePath, string Path, long Size, string Sha256);
public sealed record EngineManifest(string EngineId, string Revision, string Version, string Executable,
    string ArchiveUrl, string ArchiveSha256, long ArchiveSize, string ArchivePrefix,
    string OfflineSha256, long OfflineSize, IReadOnlyList<EngineComponent> Components, string SourceRevision = "", string AcquisitionKind = "PinnedGit")
{
    public static EngineManifest Parse(Stream stream)
    {
        var manifest = JsonSerializer.Deserialize<EngineManifest>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Empty trusted engine manifest.");
        manifest.Validate();
        return manifest;
    }
    public void Validate()
    {
        if (!Regex.IsMatch(Revision, "^[a-f0-9]{40}$") || !Regex.IsMatch(EngineId, "^[a-z0-9-]+$"))
            throw new InvalidDataException("Invalid trusted engine identifier/revision.");
        SafeArchive.RelativePath(Executable);
        if (SourceRevision.Length > 0 && !Regex.IsMatch(SourceRevision, "^[a-f0-9]{40}$")) throw new InvalidDataException("Invalid engine source revision.");
        if (ArchivePrefix.Length > 0) SafeArchive.RelativePath(ArchivePrefix.TrimEnd('/'));
        if (AcquisitionKind == "OfflineBuild")
        {
            if (EngineId != "native" || SourceRevision.Length != 40 || ArchiveUrl.Length != 0 || ArchivePrefix.Length != 0 ||
                ArchiveSha256 != OfflineSha256 || ArchiveSize != OfflineSize || OfflineSha256.Length != 64 || Revision != OfflineSha256[..40])
                throw new InvalidDataException("Native builds require an immutable offline payload and source revision.");
        }
        else if (AcquisitionKind != "PinnedGit" || !Uri.TryCreate(ArchiveUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "codeload.github.com" ||
            !uri.AbsolutePath.EndsWith("/zip/" + Revision, StringComparison.Ordinal) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.UserInfo))
            throw new InvalidDataException("Engine acquisition must use a pinned HTTPS Git source archive.");
        foreach (string hash in new[] { ArchiveSha256, OfflineSha256 }.Concat(Components.Select(c => c.Sha256)))
            if (!Regex.IsMatch(hash, "^[a-f0-9]{64}$")) throw new InvalidDataException("Invalid trusted SHA-256.");
        if (ArchiveSize <= 0 || ArchiveSize > SafeArchive.MaximumArchiveSize || OfflineSize <= 0 || OfflineSize > SafeArchive.MaximumArchiveSize || Components.Count is < 1 or > 256)
            throw new InvalidDataException("Invalid engine size limits.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sources = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Components)
        {
            SafeArchive.RelativePath(file.Path); SafeArchive.RelativePath(file.SourcePath);
            if (file.Size <= 0 || file.Size > SafeArchive.MaximumComponentSize || !names.Add(file.Path) || !sources.Add(file.SourcePath))
                throw new InvalidDataException("Invalid or duplicate engine component.");
        }
        if (!Components.Any(c => c.Path == Executable)) throw new InvalidDataException("Trusted manifest has no executable.");
    }
}

// Platform policy is mandatory; production supplies Windows ACL enforcement.
public interface IInstallationSecurity
{
    void PrepareRoot(string root);
    void ProtectDirectory(string directory);
    void ValidateDirectory(string directory);
    void ProtectFile(string file);
    void ValidateFile(string file);
}
