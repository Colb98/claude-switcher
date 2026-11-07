using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using ClaudeSwitcher.Core;

namespace ClaudeSwitcher;

/// <summary>
/// Machine-readable commands for Claude Remote (the phone bridge in remote/): `--api &lt;command&gt; [profileId]`.
/// The reply is one JSON document on stdout. This is a windowed exe, so stdout only exists when the caller
/// redirects it (as a child process with pipes does); `--out &lt;file&gt;` writes there instead.
/// Nothing listens on a port: the caller is a local process of the same Windows user, which could read the
/// same DPAPI-protected tokens itself.
/// </summary>
public static class ApiCli
{
    static readonly JsonSerializerOptions Json = new() { WriteIndented = false };

    public static async Task<int> Run(string command, string? id, string? outFile)
    {
        JsonNode reply;
        int code = 0;
        try
        {
            reply = command switch
            {
                "profiles" => Profiles(),
                "usage" => await Usage(id),
                "token" => Token(Require(id)),
                "launch" => Launch(Require(id)),
                "stop" => new JsonObject { ["ok"] = InstanceManager.Stop(Require(id)) },
                _ => throw new ArgumentException($"unknown command '{command}'"),
            };
        }
        catch (Exception e)
        {
            reply = new JsonObject { ["error"] = e.Message };
            code = 1;
        }
        var text = reply.ToJsonString(Json);
        if (outFile != null) File.WriteAllText(outFile, text, new UTF8Encoding(false));
        else
        {
            using var stdout = Console.OpenStandardOutput();
            var bytes = new UTF8Encoding(false).GetBytes(text + "\n");
            stdout.Write(bytes, 0, bytes.Length);
        }
        return code;
    }

    static ProfileConfig Require(string? id) =>
        ProfileStore.Load().Profiles.FirstOrDefault(p => p.Id == id)
        ?? throw new ArgumentException($"no profile '{id}'");

    static JsonNode Profiles()
    {
        var running = InstanceManager.RunningInstances();
        var arr = new JsonArray();
        foreach (var p in ProfileStore.Load().Profiles)
        {
            var dir = p.ResolveDataDir();
            arr.Add(new JsonObject
            {
                ["id"] = p.Id,
                ["name"] = p.Name,
                ["color"] = p.Color,
                ["isDefault"] = p.IsDefault,
                ["dataDir"] = dir,
                ["running"] = running.ContainsKey(InstanceManager.Normalize(dir)),
            });
        }
        return arr;
    }

    static async Task<JsonNode> Usage(string? id)
    {
        var service = new UsageService();
        var profiles = ProfileStore.Load().Profiles.Where(p => id == null || p.Id == id).ToList();
        // The tray app refreshes every couple of minutes; reuse its numbers instead of polling the same token again.
        var tasks = profiles.Select(async p => (p, r: await service.FetchAsync(p.ResolveDataDir(), TimeSpan.FromSeconds(150)))).ToList();
        var arr = new JsonArray();
        foreach (var (p, r) in await Task.WhenAll(tasks))
        {
            arr.Add(new JsonObject
            {
                ["id"] = p.Id,
                ["status"] = r.Status.ToString(),
                ["message"] = r.Message,
                ["email"] = r.Email,
                ["plan"] = r.PlanName,
                ["fiveHour"] = Window(r.FiveHour),
                ["weekly"] = Window(r.Weekly),
                ["scoped"] = new JsonArray(r.ScopedWeekly.Select(w => (JsonNode?)Window(w)).ToArray()),
                ["fetchedAt"] = r.FetchedAt.ToUnixTimeMilliseconds(),
                ["stale"] = r.Stale,
            });
        }
        return arr;
    }

    static JsonObject? Window(UsageWindow? w) => w == null ? null : new JsonObject
    {
        ["label"] = w.Label,
        ["percent"] = w.Percent,
        ["resetsAt"] = w.ResetsAt?.ToUnixTimeMilliseconds(),
    };

    static JsonNode Token(ProfileConfig p)
    {
        var cred = DesktopSecrets.ReadCredential(p.ResolveDataDir())
                   ?? throw new InvalidOperationException("signed out");
        return new JsonObject
        {
            ["token"] = cred.AccessToken,
            ["expiresAt"] = cred.ExpiresAt.ToUnixTimeMilliseconds(),
            ["expired"] = cred.IsExpired,
            ["accountUuid"] = cred.AccountUuid,
            ["orgUuid"] = cred.OrgUuid,
            ["apiHost"] = cred.ApiHost,
            ["subscriptionType"] = cred.SubscriptionType,
        };
    }

    static JsonNode Launch(ProfileConfig p)
    {
        if (InstanceManager.FindMainProcess(p) != null) return new JsonObject { ["ok"] = true, ["alreadyRunning"] = true };
        InstanceManager.Launch(p);
        return new JsonObject { ["ok"] = true, ["alreadyRunning"] = false };
    }
}
