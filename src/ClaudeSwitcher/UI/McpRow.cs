using System.Collections.Generic;
using System.Linq;
using ClaudeSwitcher.Core;

namespace ClaudeSwitcher.UI;

public sealed class McpToggle
{
    readonly SharedMcpServer _server;
    public McpToggle(SharedMcpServer server, ProfileConfig profile)
    {
        _server = server;
        ProfileId = profile.Id;
        ProfileName = profile.Name;
    }

    public string ProfileId { get; }
    public string ProfileName { get; }
    public bool Enabled
    {
        get => _server.EnabledFor.TryGetValue(ProfileId, out var on) && on;
        set => _server.EnabledFor[ProfileId] = value;
    }
}

public sealed class McpRow : Observable
{
    public McpRow(SharedMcpServer server, IEnumerable<ProfileConfig> profiles)
    {
        Server = server;
        Toggles = profiles.Select(p => new McpToggle(server, p)).ToList();
    }

    public SharedMcpServer Server { get; }
    public string Name => Server.Name;
    public string Summary => Server.Summary;
    public IReadOnlyList<McpToggle> Toggles { get; private set; }

    public void SetAll(bool on)
    {
        foreach (var t in Toggles) t.Enabled = on;
        Toggles = Toggles.ToList();
        Raise(nameof(Toggles));
    }
}
