using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ClaudeSwitcher.Core;

/// <summary>
/// Last good usage report per profile, plus a "leave the server alone until" time after a 429. Shared on disk so the
/// tray app and `--api usage` (called by Claude Remote) do not each poll the usage endpoint with the same token.
/// No tokens are stored here.
/// </summary>
public static class UsageCache
{
    public sealed class Entry
    {
        public UsageReport? Report { get; set; }
        public DateTimeOffset? BackoffUntil { get; set; }
    }

    static readonly object Lock = new();
    static string FilePath => Path.Combine(ProfileStore.Root, "usage-cache.json");

    static string Key(string dataDir) => InstanceManager.Normalize(dataDir).ToLowerInvariant();

    static Dictionary<string, Entry> Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(FilePath)) ?? new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new();
    }

    public static Entry? Get(string dataDir)
    {
        lock (Lock) return Load().TryGetValue(Key(dataDir), out var e) ? e : null;
    }

    public static void Put(string dataDir, UsageReport? report, DateTimeOffset? backoffUntil)
    {
        lock (Lock)
        {
            try
            {
                var all = Load();
                all[Key(dataDir)] = new Entry { Report = report is null ? null : report with { Stale = false }, BackoffUntil = backoffUntil };
                Directory.CreateDirectory(ProfileStore.Root);
                var tmp = FilePath + "." + Environment.ProcessId + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(all));
                File.Move(tmp, FilePath, true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* another process wrote it */ }
        }
    }
}
