using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeSwitcher.Core;

/// <summary>One local MCP server and which profiles should have it.</summary>
public sealed class SharedMcpServer
{
    public required string Name { get; init; }
    public required JsonObject Definition { get; set; }
    public Dictionary<string, bool> EnabledFor { get; } = new();
    public string Summary
    {
        get
        {
            if (Definition["url"] is JsonValue url) return url.ToString();
            var cmd = (string?)Definition["command"] ?? "";
            var args = Definition["args"] is JsonArray a ? string.Join(" ", a.Select(x => x?.ToString())) : "";
            return $"{cmd} {args}".Trim();
        }
    }
}

/// <summary>Shares local MCP servers by merging them into each profile's claude_desktop_config.json.</summary>
public static class McpSync
{
    const string ConfigFile = "claude_desktop_config.json";
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    public static List<SharedMcpServer> Collect(IEnumerable<ProfileConfig> profiles)
    {
        var servers = new Dictionary<string, SharedMcpServer>(StringComparer.Ordinal);
        var list = profiles.ToList();
        foreach (var p in list)
        {
            foreach (var (name, def) in ReadServers(p.ResolveDataDir()))
            {
                if (!servers.TryGetValue(name, out var s))
                    servers[name] = s = new SharedMcpServer { Name = name, Definition = (JsonObject)def.DeepClone() };
                s.EnabledFor[p.Id] = true;
            }
        }
        foreach (var s in servers.Values)
            foreach (var p in list)
                s.EnabledFor.TryAdd(p.Id, false);
        return servers.Values.OrderBy(s => s.Name).ToList();
    }

    /// <summary>
    /// Writes the selection into every profile. Only mcpServers entries named in <paramref name="servers"/>
    /// are touched; any other server or setting in the file is preserved. Returns the backup folder.
    /// </summary>
    public static string Apply(IEnumerable<ProfileConfig> profiles, IReadOnlyCollection<SharedMcpServer> servers)
    {
        var list = profiles.ToList();
        var backup = ProfileStore.Backup("mcp", list.Select(p => Path.Combine(p.ResolveDataDir(), ConfigFile)));
        foreach (var p in list)
        {
            var dir = p.ResolveDataDir();
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, ConfigFile);
            var root = File.Exists(path) ? JsonNode.Parse(DesktopSecrets.ReadShared(path)) as JsonObject ?? new JsonObject() : new JsonObject();
            if (root["mcpServers"] is not JsonObject mcp) root["mcpServers"] = mcp = new JsonObject();
            foreach (var s in servers)
            {
                var want = s.EnabledFor.TryGetValue(p.Id, out var on) && on;
                if (want) mcp[s.Name] = s.Definition.DeepClone();
                else mcp.Remove(s.Name);
            }
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(Indented));
            File.Move(tmp, path, true);
        }
        return backup;
    }

    /// <summary>Profiles whose current mcpServers differ from the selection, so untouched profiles are never restarted.</summary>
    public static List<ProfileConfig> ProfilesNeedingChange(IEnumerable<ProfileConfig> profiles, IReadOnlyCollection<SharedMcpServer> servers)
    {
        var result = new List<ProfileConfig>();
        foreach (var p in profiles)
        {
            var current = ReadServers(p.ResolveDataDir()).ToDictionary(s => s.Name, s => s.Def.ToJsonString());
            var differs = servers.Any(s =>
            {
                var want = s.EnabledFor.TryGetValue(p.Id, out var on) && on;
                var has = current.TryGetValue(s.Name, out var json);
                return want ? !has || json != s.Definition.ToJsonString() : has;
            });
            if (differs) result.Add(p);
        }
        return result;
    }

    /// <summary>A remote connector URL wrapped with mcp-remote so every profile can use it as a local server.</summary>
    public static JsonObject RemoteConnector(string url) => new()
    {
        ["command"] = "npx",
        ["args"] = new JsonArray("-y", "mcp-remote", url),
    };

    static IEnumerable<(string Name, JsonObject Def)> ReadServers(string dataDir)
    {
        var path = Path.Combine(dataDir, ConfigFile);
        if (!File.Exists(path)) yield break;
        JsonObject? root;
        try { root = JsonNode.Parse(DesktopSecrets.ReadShared(path)) as JsonObject; }
        catch (JsonException) { yield break; }
        if (root?["mcpServers"] is not JsonObject mcp) yield break;
        foreach (var (name, def) in mcp)
            if (def is JsonObject o) yield return (name, o);
    }
}
