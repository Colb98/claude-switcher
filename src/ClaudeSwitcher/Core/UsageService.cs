using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeSwitcher.Core;

public sealed record UsageWindow(string Label, double Percent, DateTimeOffset? ResetsAt);

public enum UsageStatus { Ok, SignedOut, TokenExpired, Failed }

public sealed record UsageReport(
    UsageStatus Status,
    string? Message,
    UsageWindow? FiveHour,
    UsageWindow? Weekly,
    IReadOnlyList<UsageWindow> ScopedWeekly,
    string? Email,
    string? PlanName,
    DateTimeOffset FetchedAt)
{
    public static UsageReport Fail(UsageStatus status, string message) =>
        new(status, message, null, null, Array.Empty<UsageWindow>(), null, null, DateTimeOffset.Now);
}

/// <summary>
/// Fetches plan usage exactly as Claude Desktop's Code tab does: GET {apiHost}/api/oauth/usage with the
/// profile's own OAuth bearer. Values come live from the server, never from plan-usage-history.json.
/// </summary>
public sealed class UsageService
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };
    readonly Dictionary<string, (string Token, string? Email, string? Plan)> _profileCache = new();

    public async Task<UsageReport> FetchAsync(string dataDir, CancellationToken ct = default)
    {
        IReadOnlyList<DesktopCredential> creds;
        try { creds = DesktopSecrets.ReadCredentials(dataDir); }
        catch (Exception e) { return UsageReport.Fail(UsageStatus.Failed, Loc.T("TokenReadFailed", e.Message)); }
        if (creds.Count == 0) return UsageReport.Fail(UsageStatus.SignedOut, Loc.T("SignedOut"));

        try
        {
            DesktopCredential? cred = null;
            JsonNode? usage = null;
            foreach (var candidate in creds.Where(c => !c.IsExpired))
            {
                usage = await GetJson(candidate, "/api/oauth/usage", ct);
                if (usage != null) { cred = candidate; break; }
            }
            if (cred == null || usage == null)
                return UsageReport.Fail(UsageStatus.TokenExpired,
                    creds.All(c => c.IsExpired) ? Loc.T("TokenExpired") : Loc.T("TokenRejected"));
            var (email, plan) = await GetIdentity(dataDir, cred, ct);

            var scoped = new List<UsageWindow>();
            if (usage["limits"] is JsonArray limits)
                foreach (var l in limits)
                {
                    if ((string?)l?["kind"] != "weekly_scoped") continue;
                    var name = (string?)l["scope"]?["model"]?["display_name"]
                               ?? (string?)l["scope"]?["surface"]?["display_name"];
                    if (string.IsNullOrWhiteSpace(name)) continue;
                    scoped.Add(new UsageWindow(Loc.T("WeekModel", name.Trim()),
                        l["percent"]?.GetValue<double>() ?? 0, ParseTime(l["resets_at"])));
                }
            if (scoped.Count == 0)
            {
                AddLegacy(scoped, usage, "seven_day_sonnet", Loc.T("WeekModel", "Sonnet"));
                AddLegacy(scoped, usage, "seven_day_opus", Loc.T("WeekModel", "Opus"));
            }

            return new UsageReport(UsageStatus.Ok, null,
                Window(usage["five_hour"], Loc.T("FiveHour")),
                Window(usage["seven_day"], Loc.T("WeekAll")),
                scoped, email, plan ?? FormatPlan(cred), DateTimeOffset.Now);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return UsageReport.Fail(UsageStatus.Failed, Loc.T("NetworkError", e.Message));
        }
    }

    async Task<(string? Email, string? Plan)> GetIdentity(string dataDir, DesktopCredential cred, CancellationToken ct)
    {
        if (_profileCache.TryGetValue(dataDir, out var cached) && cached.Token == cred.AccessToken)
            return (cached.Email, cached.Plan);
        string? email = null, plan = null;
        try
        {
            var profile = await GetJson(cred, "/api/oauth/profile", ct);
            email = (string?)profile?["account"]?["email"];
            var org = profile?["organization"];
            var orgType = (string?)org?["organization_type"];
            var tier = (string?)org?["rate_limit_tier"];
            plan = DescribePlan(orgType, tier, (string?)org?["name"]);
        }
        catch (HttpRequestException) { }
        _profileCache[dataDir] = (cred.AccessToken, email, plan);
        return (email, plan);
    }

    static async Task<JsonNode?> GetJson(DesktopCredential cred, string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, cred.ApiHost.TrimEnd('/') + path);
        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + cred.AccessToken);
        req.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        req.Headers.TryAddWithoutValidation("User-Agent", "ClaudeSwitcher/1.0");
        using var res = await Http.SendAsync(req, ct);
        if (res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) return null;
        res.EnsureSuccessStatusCode();
        return JsonNode.Parse(await res.Content.ReadAsStringAsync(ct));
    }

    static UsageWindow? Window(JsonNode? node, string label)
    {
        if (node is not JsonObject o || o["utilization"] is null) return null;
        return new UsageWindow(label, o["utilization"]!.GetValue<double>(), ParseTime(o["resets_at"]));
    }

    static void AddLegacy(List<UsageWindow> list, JsonNode usage, string key, string label)
    {
        if (Window(usage[key], label) is { } w) list.Add(w);
    }

    static DateTimeOffset? ParseTime(JsonNode? node) =>
        node is JsonValue v && DateTimeOffset.TryParse(v.GetValue<string>(), out var t) ? t : null;

    static string? DescribePlan(string? orgType, string? tier, string? orgName)
    {
        var plan = tier switch
        {
            { } t when t.Contains("max_20x") => "Max 20x",
            { } t when t.Contains("max_5x") => "Max 5x",
            { } t when t.Contains("pro") => "Pro",
            _ => null
        };
        var kind = orgType switch
        {
            "claude_team" => "Team",
            "claude_enterprise" => "Enterprise",
            "claude_max" => "Max",
            "claude_pro" => "Pro",
            _ => null
        };
        var label = kind != null && plan != null && !plan.StartsWith(kind) ? $"{kind}, {plan}" : plan ?? kind;
        return label ?? orgName;
    }

    static string? FormatPlan(DesktopCredential cred) => cred.SubscriptionType switch
    {
        null => null,
        var s => char.ToUpper(s[0]) + s[1..]
    };
}
