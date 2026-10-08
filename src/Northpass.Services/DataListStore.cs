using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Northpass.Engine;
using Northpass.Models;
using Northpass.Services.Installation;

namespace Northpass.Services;

public enum DataListKind { Hosts, IpSet }
public sealed record DataListEntry(string Id, string Name, DataListKind Kind);
public sealed class DataListStore(string directory)
{
    public const int MaximumBytes = 1024 * 1024;
    public string DirectoryPath { get; } = Path.GetFullPath(directory);
    public IReadOnlyList<DataListEntry> Load()
    {
        SafeArchive.NoLinks(DirectoryPath); Directory.CreateDirectory(DirectoryPath);
        var result = new List<DataListEntry>();
        foreach (string file in Directory.EnumerateFiles(DirectoryPath, "*.json"))
        {
            SafeArchive.NoLinks(file);
            if (new FileInfo(file).Length > 4096) throw new InvalidDataException("List metadata is too large.");
            var entry = JsonSerializer.Deserialize<DataListEntry>(File.ReadAllText(file)) ?? throw new InvalidDataException("List metadata is empty.");
            ValidateId(entry.Id);
            if (Path.GetFileNameWithoutExtension(file) != entry.Id || !Enum.IsDefined(entry.Kind) || string.IsNullOrWhiteSpace(entry.Name) || entry.Name.Length > 120 || entry.Name.Any(char.IsControl))
                throw new InvalidDataException("List metadata is invalid.");
            result.Add(entry);
        }
        return result.AsReadOnly();
    }
    public DataListEntry Import(string path, DataListKind kind)
    {
        if (!Enum.IsDefined(kind) || !Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Import only data-only .txt hostlists or IP sets. BAT, scripts, Lua, DLLs and executables are not accepted.");
        string normalized = ReadValidated(path, kind);
        string id = "list-" + Guid.NewGuid().ToString("N");
        var name = Path.GetFileName(path);
        if (name.Length > 120 || name.Any(char.IsControl)) throw new InvalidDataException("List name is invalid.");
        var entry = new DataListEntry(id, name, kind);
        SafeArchive.NoLinks(DirectoryPath); Directory.CreateDirectory(DirectoryPath);
        AtomicFile.Write(Path.Combine(DirectoryPath, id + ".txt"), normalized, overwrite: false);
        AtomicFile.Write(Path.Combine(DirectoryPath, id + ".json"), JsonSerializer.Serialize(entry), overwrite: false);
        return entry;
    }
    public string Read(string id, DataListKind expected)
    {
        ValidateId(id);
        var entry = Load().SingleOrDefault(e => e.Id == id) ?? throw new InvalidDataException("Custom list is missing: " + id);
        if (entry.Kind != expected) throw new InvalidDataException("A hostlist cannot be used as an IP set, or vice versa.");
        return ReadValidated(Path.Combine(DirectoryPath, id + ".txt"), expected);
    }
    public static void ValidateId(string id)
    {
        if (id is null || !Regex.IsMatch(id, "^list-[a-f0-9]{32}$", RegexOptions.CultureInvariant)) throw new InvalidDataException("Invalid data-only list identifier.");
    }
    private static string ReadValidated(string path, DataListKind kind)
    {
        SafeArchive.NoLinks(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is < 1 or > MaximumBytes) throw new InvalidDataException("List must contain 1 byte–1 MiB of UTF-8 data.");
        using var reader = new StreamReader(file, new UTF8Encoding(false, true), detectEncodingFromByteOrderMarks: false);
        return Normalize(reader.ReadToEnd().TrimStart('\uFEFF'), kind);
    }
    public static string Normalize(string text, DataListKind kind)
    {
        if (!Enum.IsDefined(kind) || text.Length > MaximumBytes || text.Contains('\0')) throw new InvalidDataException("List is invalid or too large.");
        var result = new List<string>();
        foreach (string raw in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.Length > 253 || line.Any(char.IsControl) || result.Count >= 20000) throw new InvalidDataException("List entry is invalid or exceeds its limit.");
            if (kind == DataListKind.Hosts)
            {
                string domain = line.TrimStart('^');
                if (line.StartsWith("^^", StringComparison.Ordinal) || !domain.Contains('.') || domain.Split('.').Any(label =>
                    label.Length is < 1 or > 63 || label[0] == '-' || label[^1] == '-' || label.Any(c => !(char.IsAsciiLetterOrDigit(c) || c == '-'))))
                    throw new InvalidDataException("Hostlists accept domain names only, optionally prefixed with '^'. No URLs, directives or commands.");
                result.Add(line.ToLowerInvariant());
            }
            else
            {
                var parts = line.Split('/');
                if (parts.Length is < 1 or > 2 || parts[0].Contains('%') || !IPAddress.TryParse(parts[0], out var address) ||
                    address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && (parts[0].Split('.').Length != 4 || parts[0].Any(c => !char.IsAsciiDigit(c) && c != '.')))
                    throw new InvalidDataException("IP sets accept IPv4/IPv6 addresses and CIDRs only.");
                int max = address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128;
                if (parts.Length == 2 && (parts[1].Length == 0 || parts[1].Any(c => !char.IsAsciiDigit(c)) || !int.TryParse(parts[1], out int prefix) || prefix < 0 || prefix > max))
                    throw new InvalidDataException("Invalid CIDR prefix.");
                result.Add(address + (parts.Length == 2 ? "/" + int.Parse(parts[1]) : ""));
            }
        }
        if (result.Count == 0) throw new InvalidDataException("A custom list needs at least one valid entry.");
        return string.Join('\n', result.Distinct(StringComparer.Ordinal)) + "\n";
    }
}

public sealed class ProtectedEngineDataProvider(DataListStore store, string root, IInstallationSecurity security) : IEngineDataProvider
{
    private static readonly IReadOnlyDictionary<string, (string File, DataListKind Kind, string Default)> Slots =
        new Dictionary<string, (string, DataListKind, string)>(StringComparer.Ordinal)
        {
            ["general"] = ("list-general-user.txt", DataListKind.Hosts, "domain.example.abc\n"),
            ["excluded-hosts"] = ("list-exclude-user.txt", DataListKind.Hosts, "domain.example.abc\n"),
            ["excluded-ips"] = ("ipset-exclude-user.txt", DataListKind.IpSet, "203.0.113.113/32\n"),
            ["all-ips"] = ("ipset-all-user.txt", DataListKind.IpSet, "203.0.113.113/32\n")
        };
    private Dictionary<string, string> Resolve(StrategyProfile profile)
    {
        if (profile.ListBindings is null || profile.ListBindings.Count > Slots.Count) throw new InvalidDataException("Invalid custom list bindings.");
        var files = Slots.ToDictionary(pair => pair.Value.File, pair => pair.Value.Default, StringComparer.Ordinal);
        foreach (var binding in profile.ListBindings)
        {
            if (!Slots.TryGetValue(binding.Key, out var slot)) throw new InvalidDataException("Unknown data-only list slot: " + binding.Key);
            files[slot.File] = store.Read(binding.Value, slot.Kind);
        }
        return files;
    }
    public Task<ValidationResult> ValidateAsync(StrategyProfile profile, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        try { Resolve(profile); return Task.FromResult(ValidationResult.Valid); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or ArgumentException or JsonException)
        { return Task.FromResult(new ValidationResult([new("data.list", ex.Message)])); }
    }
    public Task<IEngineDataLease> PrepareAsync(StrategyProfile profile, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var contents = Resolve(ProfileValidation.Copy(profile));
        security.PrepareRoot(root); SafeArchive.NoLinks(root);
        string directory = Path.Combine(root, "session-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); security.ProtectDirectory(directory);
        var files = new List<FileStream>();
        try
        {
            foreach (var item in contents)
            {
                token.ThrowIfCancellationRequested();
                string path = Path.Combine(directory, item.Key); SafeArchive.NoLinks(path);
                using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { byte[] data = Encoding.UTF8.GetBytes(item.Value); output.Write(data); }
                security.ProtectFile(path); security.ValidateFile(path);
                files.Add(new(path, FileMode.Open, FileAccess.Read, FileShare.Read));
            }
            security.ValidateDirectory(directory);
            return Task.FromResult<IEngineDataLease>(new Lease(directory,
                profile.ListBindings.ContainsKey("all-ips") ? Path.Combine(directory, "ipset-all-user.txt") : null, files));
        }
        catch { foreach (var file in files) file.Dispose(); Directory.Delete(directory, true); throw; }
    }
    private sealed class Lease(string directory, string? allIps, List<FileStream> files) : IEngineDataLease
    {
        private bool _disposed;
        public string DirectoryPath => directory;
        public string? AllIpsPath => allIps;
        public ValueTask DisposeAsync()
        {
            if (_disposed) return ValueTask.CompletedTask;
            foreach (var file in files) file.Dispose();
            SafeArchive.NoLinks(directory); Directory.Delete(directory, true); _disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
