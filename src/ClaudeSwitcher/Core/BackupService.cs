using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;

namespace ClaudeSwitcher.Core;

/// <summary>Where Claude's data lives on this machine. Tests and --to point it at another folder.</summary>
public sealed record BackupRoots(string ClaudeHome, string ClaudeJson, string SwitcherRoot, string ProfilesRoot, string DefaultDataDir)
{
    public static BackupRoots Current()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new BackupRoots(Path.Combine(home, ".claude"), Path.Combine(home, ".claude.json"),
            ProfileStore.Root, ProfileStore.ProfilesRoot, ClaudeInstall.FindDefaultDataDir());
    }

    /// <summary>
    /// The roots to restore into. A fresh machine has no signed-in data folder yet, so the MSIX folder the backup
    /// came from is used when FindDefaultDataDir could only fall back to the legacy location.
    /// </summary>
    public static BackupRoots CurrentFor(BackupManifest manifest)
    {
        var roots = Current();
        if (File.Exists(Path.Combine(roots.DefaultDataDir, "Local State"))) return roots;
        if (manifest.DefaultDataDirName is not { } name || !Regex.IsMatch(name, @"^claude_\w+$")) return roots;
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return roots with { DefaultDataDir = Path.Combine(local, "Claude-Data", name) };
    }

    public static BackupRoots Under(string dir) => new(Path.Combine(dir, ".claude"), Path.Combine(dir, ".claude.json"),
        Path.Combine(dir, ".claude-switcher"), Path.Combine(dir, ".claude-switcher", "profiles"), Path.Combine(dir, "default-profile"));
}

public sealed class BackupFile
{
    public string Path { get; set; } = "";
    public long Length { get; set; }
    public string Sha256 { get; set; } = "";
}

/// <summary>The first thing a restore reads: what the zip holds and where it came from.</summary>
public sealed class BackupManifest
{
    public int Format { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public string MachineName { get; set; } = "";
    public string Home { get; set; } = "";
    public string? DefaultDataDirName { get; set; }
    public List<BackupFile> Files { get; set; } = new();
    public List<string> Skipped { get; set; } = new();
}

public enum BackupStage { Writing, Verifying, Restoring }

public readonly record struct BackupProgress(BackupStage Stage, long Done, long Total, string Current)
{
    public int Percent => Total <= 0 ? 0 : (int)Math.Min(100, Done * 100 / Total);
}

public sealed record BackupResult(string ZipPath, int Files, long SourceBytes, long ZipBytes, IReadOnlyList<string> Skipped);

public sealed record RestoreResult(int Restored, int Unchanged, IReadOnlyList<string> Failed, string? UndoDir);

/// <summary>
/// Saves Claude's settings, sessions and MCP servers into one zip and puts them back later. The zip holds one
/// folder per data root plus manifest.json, so it can also be unpacked by hand. Sign-in data is left out on
/// purpose: the zip carries no password, and Desktop's tokens would not decrypt under another Windows user anyway.
/// </summary>
public static class BackupService
{
    const int CurrentFormat = 1;
    const string ManifestEntry = "manifest.json";
    const string ClaudeHomeRoot = "claude-home";
    const string HomeRoot = "home";
    const string ClaudeJsonName = ".claude.json";
    const string SwitcherRoot = "switcher";
    const string ProfileRoot = "profile";
    const string DefaultProfileRoot = "default-profile";
    const string ConfigName = "config.json";
    const string SettingsEntry = SwitcherRoot + "/settings.json";
    const string CredentialsName = ".credentials.json";

    static readonly StringComparer PathCompare = StringComparer.OrdinalIgnoreCase;

    /// <summary>Top-level names in ~\.claude: rebuilt on demand, or holding live tokens (ide, sessions, daemon, shell-snapshots).</summary>
    static readonly HashSet<string> ClaudeHomeExcluded = new(PathCompare)
    {
        "backups", "cache", "daemon", "downloads", "ide", "session-env", "sessions", "shell-snapshots", "telemetry",
        ".last-cleanup", "mcp-needs-auth-cache.json",
    };
    static readonly string[] ClaudeHomeExcludedPrefixes = { "policy-limits", "remote-settings" };

    /// <summary>Top-level names in a Desktop data folder: Chromium state carries the sign-in, the rest is rebuilt on demand.</summary>
    static readonly HashSet<string> DataDirExcluded = new(PathCompare)
    {
        // Sign-in and device identity
        "Local State", "Network", "Local Storage", "Session Storage", "IndexedDB", "WebStorage", "Partitions", "Preferences",
        "ant-did", "ant-device-registry.json", "ssh-remote-server-state.json", "remote-control-state.json",
        // Caches, downloads and diagnostics
        "Cache", "Code Cache", "GPUCache", "GPUPersistentCache", "GrShaderCache", "ShaderCache", "DawnGraphiteCache", "DawnWebGPUCache",
        "Shared Dictionary", "VideoDecodeStats", "blob_storage", "fcache", "shared_proto_db", "claude-code",
        "Crashpad", "logs", "sentry", "DIPS", "DIPS-wal", "lockfile",
        "declarative_performance_observer.db", "declarative_performance_observer.db-journal",
    };

    /// <summary>JSON files that hold absolute paths, rewritten when the backup came from another Windows user.</summary>
    static readonly HashSet<string> HomeRewriteNames = new(PathCompare)
    {
        ClaudeJsonName, "settings.json", "claude_desktop_config.json", "known_marketplaces.json", "installed_plugins.json",
    };

    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    sealed record Source(string FullPath, string EntryPath, long Length);

    // ---------- Create ----------

    public static BackupResult Create(string zipPath, BackupRoots roots, IProgress<BackupProgress>? progress = null, CancellationToken ct = default)
    {
        zipPath = Path.GetFullPath(zipPath);
        var partial = zipPath + ".partial";
        var skipped = new List<string>();
        var sources = CollectSources(roots, skipped)
            .Where(s => !PathCompare.Equals(s.FullPath, zipPath) && !PathCompare.Equals(s.FullPath, partial))
            .ToList();
        var total = sources.Sum(s => s.Length);
        var manifest = new BackupManifest
        {
            Format = CurrentFormat,
            CreatedAt = DateTimeOffset.UtcNow,
            MachineName = Environment.MachineName,
            Home = Path.GetDirectoryName(Path.GetFullPath(roots.ClaudeJson))!,
            DefaultDataDirName = Path.GetFileName(roots.DefaultDataDir),
        };

        try
        {
            ct.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(zipPath)!);
            using (var output = new FileStream(partial, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var zip = new ZipArchive(output, ZipArchiveMode.Create))
            {
                long done = 0;
                foreach (var source in sources)
                {
                    ct.ThrowIfCancellationRequested();
                    progress?.Report(new BackupProgress(BackupStage.Writing, done, total, source.EntryPath));
                    var file = WriteEntry(zip, source, skipped, ct);
                    if (file != null) manifest.Files.Add(file);
                    done += source.Length;
                }
                manifest.Skipped = skipped;
                using var writer = new StreamWriter(zip.CreateEntry(ManifestEntry).Open(), new UTF8Encoding(false));
                writer.Write(JsonSerializer.Serialize(manifest, Indented));
            }

            Verify(partial, manifest, progress, ct);
            File.Move(partial, zipPath, true);
        }
        catch
        {
            TryDelete(partial);
            throw;
        }
        return new BackupResult(zipPath, manifest.Files.Count, manifest.Files.Sum(f => f.Length), new FileInfo(zipPath).Length, skipped);
    }

    static List<Source> CollectSources(BackupRoots roots, List<string> skipped)
    {
        var sources = new List<Source>();
        Walk(roots.ClaudeHome, ClaudeHomeRoot, IsClaudeHomeExcluded, sources, skipped);
        if (File.Exists(roots.ClaudeJson))
            sources.Add(new Source(roots.ClaudeJson, $"{HomeRoot}/{ClaudeJsonName}", new FileInfo(roots.ClaudeJson).Length));
        Walk(roots.SwitcherRoot, SwitcherRoot, IsSwitcherExcluded, sources, skipped);
        Walk(roots.DefaultDataDir, DefaultProfileRoot, IsDataDirExcluded, sources, skipped);
        foreach (var profile in ReadSettings(roots.SwitcherRoot)?.Profiles ?? new())
            if (profile.DataDir is { } dir)
                Walk(dir, $"{ProfileRoot}/{profile.Id}", IsDataDirExcluded, sources, skipped);
        return sources;
    }

    static string TopLevel(string rel) => rel.Split('/')[0];

    static bool IsClaudeHomeExcluded(string rel)
    {
        var top = TopLevel(rel);
        return ClaudeHomeExcluded.Contains(top) || ClaudeHomeExcludedPrefixes.Any(p => top.StartsWith(p, StringComparison.OrdinalIgnoreCase));
    }

    static bool IsDataDirExcluded(string rel) => DataDirExcluded.Contains(TopLevel(rel));

    /// <summary>Profiles are saved by their own walk. Files set aside by a restore may still hold the old sign-in.</summary>
    static bool IsSwitcherExcluded(string rel) =>
        PathCompare.Equals(TopLevel(rel), "profiles") || PathCompare.Equals(rel, "links.log") ||
        rel.StartsWith("backups/restore-", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Lists files under <paramref name="dir"/>. Junctions are followed so the zip holds what they point to
    /// (~\.claude\skills is made of them); a link back into a folder being walked is reported instead of looping.
    /// </summary>
    static void Walk(string dir, string prefix, Func<string, bool> excluded, List<Source> into, List<string> skipped)
    {
        if (Directory.Exists(dir)) Walk(dir, Path.GetFullPath(dir), "", prefix, excluded, into, skipped, new List<string>());
    }

    static void Walk(string dir, string realDir, string relDir, string prefix, Func<string, bool> excluded,
        List<Source> into, List<string> skipped, List<string> visiting)
    {
        visiting.Add(realDir);
        try
        {
            var options = new EnumerationOptions { AttributesToSkip = 0, IgnoreInaccessible = false, RecurseSubdirectories = false };
            foreach (var item in new DirectoryInfo(dir).EnumerateFileSystemInfos("*", options))
            {
                var rel = relDir.Length == 0 ? item.Name : $"{relDir}/{item.Name}";
                var entryPath = $"{prefix}/{rel}";
                if (excluded(rel) || PathCompare.Equals(item.Name, CredentialsName)) continue;
                if (item is FileInfo file)
                {
                    into.Add(new Source(file.FullName, entryPath, file.Length));
                    continue;
                }
                var real = item.FullName;
                if (item.LinkTarget != null)
                {
                    var target = item.ResolveLinkTarget(true);
                    if (target is not { Exists: true } || visiting.Any(v => IsSameOrInside(v, target.FullName)))
                    {
                        skipped.Add(entryPath);
                        continue;
                    }
                    real = target.FullName;
                }
                Walk(item.FullName, real, rel, prefix, excluded, into, skipped, visiting);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            skipped.Add($"{prefix}/{relDir}");
        }
        visiting.RemoveAt(visiting.Count - 1);
    }

    static bool IsSameOrInside(string path, string folder)
    {
        path = path.TrimEnd(Path.DirectorySeparatorChar);
        folder = folder.TrimEnd(Path.DirectorySeparatorChar);
        return PathCompare.Equals(path, folder) || path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Stores one file. Only read failures are skipped: a write failure (disk full) must abort the backup.</summary>
    static BackupFile? WriteEntry(ZipArchive zip, Source source, List<string> skipped, CancellationToken ct)
    {
        Stream input;
        DateTime modified;
        try
        {
            modified = File.GetLastWriteTime(source.FullPath);
            input = IsSignInConfig(source.EntryPath)
                ? new MemoryStream(Encoding.UTF8.GetBytes(StripSignIn(source.EntryPath, DesktopSecrets.ReadShared(source.FullPath))))
                : new FileStream(source.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            skipped.Add(source.EntryPath);
            return null;
        }

        using (input)
        {
            var entry = zip.CreateEntry(source.EntryPath, CompressionLevel.Optimal);
            if (modified.Year is >= 1980 and <= 2107) entry.LastWriteTime = modified;
            using var output = entry.Open();
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long length = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                int read;
                try { read = input.Read(buffer, 0, buffer.Length); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // The partial entry stays in the zip but is not in the manifest, so restore ignores it.
                    skipped.Add(source.EntryPath);
                    return null;
                }
                if (read == 0) break;
                hash.AppendData(buffer, 0, read);
                output.Write(buffer, 0, read);
                length += read;
            }
            return new BackupFile { Path = source.EntryPath, Length = length, Sha256 = Convert.ToHexString(hash.GetHashAndReset()) };
        }
    }

    /// <summary>Reads every file back: ZipArchive does not check CRCs, so a bad write would otherwise go unnoticed until restore.</summary>
    static void Verify(string zipPath, BackupManifest manifest, IProgress<BackupProgress>? progress, CancellationToken ct)
    {
        var total = manifest.Files.Sum(f => f.Length);
        long done = 0;
        using var zip = ZipFile.OpenRead(zipPath);
        foreach (var file in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new BackupProgress(BackupStage.Verifying, done, total, file.Path));
            using var stream = zip.GetEntry(file.Path)?.Open() ?? throw new InvalidDataException(Loc.T("BackupVerifyFailed", file.Path));
            if (HashOf(stream, Stream.Null, ct) != file.Sha256) throw new InvalidDataException(Loc.T("BackupVerifyFailed", file.Path));
            done += file.Length;
        }
    }

    static string HashOf(Stream input, Stream copyTo, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int read;
        while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            hash.AppendData(buffer, 0, read);
            copyTo.Write(buffer, 0, read);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // ---------- Sign-in values inside config files ----------

    static bool IsSignInConfig(string entryPath) =>
        entryPath is $"{HomeRoot}/{ClaudeJsonName}" or $"{ClaudeHomeRoot}/{ClaudeJsonName}" or $"{DefaultProfileRoot}/{ConfigName}" ||
        Regex.IsMatch(entryPath, $"^{ProfileRoot}/[^/]+/{Regex.Escape(ConfigName)}$");

    static bool IsSignInKey(string entryPath, string key) => entryPath.EndsWith(ClaudeJsonName, StringComparison.Ordinal)
        ? key is "oauthAccount" or "primaryApiKey" or "customApiKeyResponses"
        : key.StartsWith("oauth:", StringComparison.Ordinal);

    /// <summary>Throws JsonException when the file is unreadable, so an unparsed file is skipped rather than stored with its secrets.</summary>
    static string StripSignIn(string entryPath, string json)
    {
        var root = JsonNode.Parse(json) as JsonObject ?? throw new JsonException();
        foreach (var key in root.Select(kv => kv.Key).Where(k => IsSignInKey(entryPath, k)).ToList())
            root.Remove(key);
        return root.ToJsonString(Indented);
    }

    /// <summary>Restoring settings must not sign out a Claude that was already signed in again after the reinstall.</summary>
    static string KeepCurrentSignIn(string entryPath, string restoredJson, string dest)
    {
        if (!File.Exists(dest)) return restoredJson;
        try
        {
            var restored = JsonNode.Parse(restoredJson) as JsonObject;
            var current = JsonNode.Parse(DesktopSecrets.ReadShared(dest)) as JsonObject;
            if (restored == null || current == null) return restoredJson;
            foreach (var (key, value) in current.Where(kv => IsSignInKey(entryPath, kv.Key)).ToList())
                restored[key] = value?.DeepClone();
            return restored.ToJsonString(Indented);
        }
        catch (JsonException) { return restoredJson; }
    }

    // ---------- Restore ----------

    public static BackupManifest Inspect(string zipPath)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipPath);
            return ReadManifest(zip);
        }
        catch (Exception e) when (e is InvalidDataException or JsonException)
        {
            throw new InvalidDataException(Loc.T("BackupBadFile"), e);
        }
    }

    static BackupManifest ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry(ManifestEntry) ?? throw new InvalidDataException(Loc.T("BackupBadFile"));
        using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
        var manifest = JsonSerializer.Deserialize<BackupManifest>(reader.ReadToEnd());
        if (manifest == null || manifest.Format != CurrentFormat) throw new InvalidDataException(Loc.T("BackupBadFile"));
        return manifest;
    }

    public static RestoreResult Restore(string zipPath, BackupRoots roots, IProgress<BackupProgress>? progress = null, CancellationToken ct = default)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        var manifest = ReadManifest(zip);
        // Every path is checked first, so a bad file changes nothing at all.
        var targets = manifest.Files.Select(f => (File: f, Dest: MapEntry(f.Path, roots))).ToList();

        var undoDir = Path.Combine(roots.SwitcherRoot, "backups", $"restore-{DateTime.Now:yyyyMMdd-HHmmss}");
        var failed = new List<string>();
        int restored = 0, unchanged = 0;
        var usedUndo = false;
        var total = manifest.Files.Sum(f => f.Length);
        long done = 0;
        var newHome = Path.GetDirectoryName(Path.GetFullPath(roots.ClaudeJson))!;

        foreach (var (file, dest) in targets)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Report(new BackupProgress(BackupStage.Restoring, done, total, file.Path));
            done += file.Length;
            var entry = zip.GetEntry(file.Path);
            if (entry == null) { failed.Add(file.Path); continue; }
            var transform = NeedsTransform(file.Path, manifest.Home, newHome);
            if (!transform && IsUnchanged(entry, dest)) { unchanged++; continue; }

            var tmp = dest + ".restoring";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                string hash;
                using (var input = entry.Open())
                using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    hash = HashOf(input, output, ct);
                if (hash != file.Sha256)
                {
                    failed.Add(file.Path);
                    TryDelete(tmp);
                    continue;
                }
                if (transform) File.WriteAllText(tmp, Transform(file.Path, File.ReadAllText(tmp), dest, manifest.Home, newHome, roots), new UTF8Encoding(false));

                if (File.Exists(dest))
                {
                    var undo = Path.Combine(undoDir, file.Path.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(undo)!);
                    File.Move(dest, undo, true);
                    usedUndo = true;
                    try { File.Move(tmp, dest); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        File.Move(undo, dest);
                        throw;
                    }
                }
                else File.Move(tmp, dest);
                File.SetLastWriteTime(dest, entry.LastWriteTime.LocalDateTime);
                restored++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
            {
                failed.Add($"{file.Path}: {e.Message}");
                TryDelete(tmp);
            }
            catch
            {
                TryDelete(tmp);
                throw;
            }
        }
        return new RestoreResult(restored, unchanged, failed, usedUndo ? undoDir : null);
    }

    static bool IsUnchanged(ZipArchiveEntry entry, string dest)
    {
        var existing = new FileInfo(dest);
        // Zip timestamps have a two-second resolution.
        return existing.Exists && existing.Length == entry.Length &&
               Math.Abs((existing.LastWriteTime - entry.LastWriteTime.LocalDateTime).TotalSeconds) <= 2;
    }

    static bool NeedsTransform(string entryPath, string oldHome, string newHome) =>
        IsSignInConfig(entryPath) || entryPath == SettingsEntry ||
        (!PathCompare.Equals(oldHome, newHome) && HomeRewriteNames.Contains(Path.GetFileName(entryPath)));

    static string Transform(string entryPath, string text, string dest, string oldHome, string newHome, BackupRoots roots)
    {
        if (IsSignInConfig(entryPath)) text = KeepCurrentSignIn(entryPath, text, dest);
        if (entryPath == SettingsEntry) text = MergeSettings(text, roots);
        else if (HomeRewriteNames.Contains(Path.GetFileName(entryPath))) text = RewriteHome(text, oldHome, newHome);
        return text;
    }

    /// <summary>Profiles point at their restored folder; profiles created since the backup stay in the list.</summary>
    static string MergeSettings(string text, BackupRoots roots)
    {
        var restored = JsonSerializer.Deserialize<AppSettings>(text) ?? new AppSettings();
        foreach (var profile in restored.Profiles.Where(p => p.DataDir != null))
            profile.DataDir = Path.Combine(roots.ProfilesRoot, profile.Id);
        foreach (var current in ReadSettings(roots.SwitcherRoot)?.Profiles ?? new())
            if (current.DataDir != null && restored.Profiles.All(p => p.Id != current.Id))
                restored.Profiles.Add(current);
        return JsonSerializer.Serialize(restored, Indented);
    }

    /// <summary>Replaces the old home in the two spellings Claude writes: JSON-escaped backslashes and forward slashes.</summary>
    static string RewriteHome(string json, string oldHome, string newHome)
    {
        foreach (var (from, to) in new[]
                 {
                     (oldHome.Replace("\\", "\\\\"), newHome.Replace("\\", "\\\\")),
                     (oldHome.Replace('\\', '/'), newHome.Replace('\\', '/')),
                 })
            json = Regex.Replace(json, Regex.Escape(from) + @"(?![\w.-])", _ => to, RegexOptions.IgnoreCase);
        return json;
    }

    /// <summary>Maps a zip path to its destination, refusing anything that would land outside the data folders.</summary>
    static string MapEntry(string entryPath, BackupRoots roots)
    {
        var parts = entryPath.Split('/');
        if (parts.Length < 2 || parts.Any(p => p.Length == 0 || p == "." || p == ".." || p.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0))
            throw new InvalidDataException(Loc.T("BackupBadPath", entryPath));
        string root;
        switch (parts[0])
        {
            case ClaudeHomeRoot: root = roots.ClaudeHome; break;
            case SwitcherRoot: root = roots.SwitcherRoot; break;
            case DefaultProfileRoot: root = roots.DefaultDataDir; break;
            case ProfileRoot: root = roots.ProfilesRoot; break;
            case HomeRoot when parts.Length == 2 && parts[1] == ClaudeJsonName: return Path.GetFullPath(roots.ClaudeJson);
            default: throw new InvalidDataException(Loc.T("BackupBadPath", entryPath));
        }
        var fullRoot = Path.GetFullPath(root);
        var dest = Path.GetFullPath(Path.Combine(new[] { fullRoot }.Concat(parts.Skip(1)).ToArray()));
        if (!IsSameOrInside(dest, fullRoot)) throw new InvalidDataException(Loc.T("BackupBadPath", entryPath));
        return dest;
    }

    // ---------- Helpers ----------

    /// <summary>True when a zip saved at <paramref name="path"/> would be deleted along with Claude's data.</summary>
    public static bool IsInsideDataFolders(string path, BackupRoots roots)
    {
        var full = Path.GetFullPath(path);
        return new[] { roots.ClaudeHome, roots.SwitcherRoot, roots.DefaultDataDir }.Any(r => IsSameOrInside(full, Path.GetFullPath(r)));
    }

    static AppSettings? ReadSettings(string switcherRoot)
    {
        var path = Path.Combine(switcherRoot, "settings.json");
        if (!File.Exists(path)) return null;
        try { return JsonSerializer.Deserialize<AppSettings>(DesktopSecrets.ReadShared(path)); }
        catch (JsonException) { return null; }
    }

    static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
