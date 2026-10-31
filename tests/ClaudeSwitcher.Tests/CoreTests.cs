using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using ClaudeSwitcher.Core;
using Xunit;

namespace ClaudeSwitcher.Tests;

/// <summary>
/// Builds fake Claude Desktop data folders encrypted exactly like Electron safeStorage on Windows
/// (DPAPI-wrapped key in Local State, "v10" + AES-256-GCM values), so no real account data is touched.
/// </summary>
public sealed class FakeProfile : IDisposable
{
    // Keep backups written by the code under test out of the user's real ~/.claude-switcher.
    static FakeProfile() => Environment.SetEnvironmentVariable("CLAUDE_SWITCHER_HOME",
        Path.Combine(Path.GetTempPath(), "cs-test-home"));

    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "cs-test-" + Guid.NewGuid().ToString("N")[..8]);
    readonly byte[] _key = RandomNumberGenerator.GetBytes(32);

    public FakeProfile()
    {
        Directory.CreateDirectory(Dir);
        var wrapped = Encoding.ASCII.GetBytes("DPAPI").Concat(ProtectedData.Protect(_key, null, DataProtectionScope.CurrentUser)).ToArray();
        File.WriteAllText(Path.Combine(Dir, "Local State"),
            new JsonObject { ["os_crypt"] = new JsonObject { ["encrypted_key"] = Convert.ToBase64String(wrapped) } }.ToJsonString());
    }

    public string Encrypt(string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(12);
        var data = Encoding.UTF8.GetBytes(plain);
        var cipher = new byte[data.Length];
        var tag = new byte[16];
        using (var gcm = new AesGcm(_key, 16)) gcm.Encrypt(nonce, data, cipher, tag);
        return Convert.ToBase64String(Encoding.ASCII.GetBytes("v10").Concat(nonce).Concat(cipher).Concat(tag).ToArray());
    }

    public void SignIn(string account, string org, DateTimeOffset expires, string token = "tok-v2", string? legacyToken = null)
    {
        var v2 = new JsonObject
        {
            [$"acct:{account}|client-1:{org}:https://api.anthropic.com:user:profile"] = new JsonObject
                { ["token"] = "tok-profile", ["expiresAt"] = expires.AddDays(5).ToUnixTimeMilliseconds() },
            [$"acct:{account}|client-2:{org}:https://api.anthropic.com:user:inference user:profile"] = new JsonObject
                { ["token"] = token, ["expiresAt"] = expires.ToUnixTimeMilliseconds(), ["subscriptionType"] = "max" },
        };
        var config = new JsonObject { ["oauth:tokenCacheV2"] = Encrypt(v2.ToJsonString()) };
        if (legacyToken != null)
            config["oauth:tokenCache"] = Encrypt(new JsonObject
            {
                [$"acct:{account}|client-3:{org}:https://api.anthropic.com:user:inference"] = new JsonObject
                    { ["token"] = legacyToken, ["expiresAt"] = expires.AddYears(1).ToUnixTimeMilliseconds() },
            }.ToJsonString());
        File.WriteAllText(Path.Combine(Dir, "config.json"), config.ToJsonString());
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); } catch (IOException) { }
    }
}

public class DesktopSecretsTests
{
    [Fact]
    public void ReadsCredentialAndParsesAccountOrgHost()
    {
        using var p = new FakeProfile();
        p.SignIn("acc-1", "org-1", DateTimeOffset.UtcNow.AddHours(8));
        var cred = DesktopSecrets.ReadCredential(p.Dir)!;
        Assert.Equal("tok-v2", cred.AccessToken);
        Assert.Equal("acc-1", cred.AccountUuid);
        Assert.Equal("org-1", cred.OrgUuid);
        Assert.Equal("https://api.anthropic.com", cred.ApiHost);
        Assert.Equal("max", cred.SubscriptionType);
    }

    [Fact]
    public void PrefersCurrentCacheOverLongerLivedLegacyToken()
    {
        using var p = new FakeProfile();
        p.SignIn("acc-1", "org-1", DateTimeOffset.UtcNow.AddHours(8), legacyToken: "tok-legacy");
        var creds = DesktopSecrets.ReadCredentials(p.Dir);
        Assert.Equal("tok-v2", creds[0].AccessToken);
        Assert.Contains(creds, c => c.AccessToken == "tok-legacy");
    }

    [Fact]
    public void SignedOutProfileHasNoCredential()
    {
        using var p = new FakeProfile();
        Assert.Null(DesktopSecrets.ReadCredential(p.Dir));
    }
}

public class SessionTransferTests
{
    static string WriteSession(string dataDir, string account, string org, string id, long lastActivity)
    {
        var dir = Path.Combine(dataDir, "claude-code-sessions", account, org);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, $"local_{id}.json");
        File.WriteAllText(path, new JsonObject
        {
            ["sessionId"] = $"local_{id}", ["cliSessionId"] = "cli-" + id, ["cwd"] = @"D:\work",
            ["title"] = "Task " + id, ["lastActivityAt"] = lastActivity, ["isArchived"] = false,
            ["remoteMcpServersConfig"] = new JsonArray(new JsonObject { ["name"] = "source-only-connector" }),
        }.ToJsonString());
        return path;
    }

    [Fact]
    public void ListsNewestFirst()
    {
        using var src = new FakeProfile();
        WriteSession(src.Dir, "a", "o", "old", 1000);
        WriteSession(src.Dir, "a", "o", "new", 2000);
        var list = SessionTransfer.List(src.Dir);
        Assert.Equal(new[] { "local_new", "local_old" }, list.Select(s => s.SessionId));
    }

    [Fact]
    public void CopiesIntoTargetAccountFolderAndDropsSourceConnectors()
    {
        using var src = new FakeProfile();
        using var dst = new FakeProfile();
        dst.SignIn("acc-target", "org-target", DateTimeOffset.UtcNow.AddHours(8));
        WriteSession(src.Dir, "acc-source", "org-source", "s1", 1000);

        var n = SessionTransfer.CopyTo(SessionTransfer.List(src.Dir), dst.Dir, overwrite: false);

        Assert.Equal(1, n);
        var copied = Path.Combine(dst.Dir, "claude-code-sessions", "acc-target", "org-target", "local_s1.json");
        var j = JsonNode.Parse(File.ReadAllText(copied))!;
        Assert.Equal("cli-s1", (string?)j["cliSessionId"]);
        Assert.Empty(j["remoteMcpServersConfig"]!.AsArray());
        Assert.True(File.Exists(Path.Combine(src.Dir, "claude-code-sessions", "acc-source", "org-source", "local_s1.json")));
    }

    [Fact]
    public void MoveRemovesSourceButKeepsBackup()
    {
        using var src = new FakeProfile();
        var path = WriteSession(src.Dir, "s", "o", "s1", 1000);
        var backup = SessionTransfer.RemoveFromSource(SessionTransfer.List(src.Dir));
        Assert.False(File.Exists(path));
        Assert.Empty(SessionTransfer.List(src.Dir));
        Assert.Single(Directory.GetFiles(backup));
    }

    [Fact]
    public void SkipsExistingUnlessOverwrite()
    {
        using var src = new FakeProfile();
        using var dst = new FakeProfile();
        dst.SignIn("t", "o", DateTimeOffset.UtcNow.AddHours(8));
        WriteSession(src.Dir, "s", "o", "s1", 1000);
        var sessions = SessionTransfer.List(src.Dir);
        Assert.Equal(1, SessionTransfer.CopyTo(sessions, dst.Dir, overwrite: false));
        Assert.Equal(0, SessionTransfer.CopyTo(sessions, dst.Dir, overwrite: false));
        Assert.Equal(1, SessionTransfer.CopyTo(sessions, dst.Dir, overwrite: true));
    }

    [Fact]
    public void RefusesSignedOutTarget()
    {
        using var src = new FakeProfile();
        using var dst = new FakeProfile();
        WriteSession(src.Dir, "s", "o", "s1", 1000);
        Assert.Throws<InvalidOperationException>(() => SessionTransfer.CopyTo(SessionTransfer.List(src.Dir), dst.Dir, false));
    }
}

public class McpSyncTests
{
    [Fact]
    public void MergesSelectionAndPreservesOtherSettings()
    {
        using var a = new FakeProfile();
        using var b = new FakeProfile();
        File.WriteAllText(Path.Combine(a.Dir, "claude_desktop_config.json"), """
            {"mcpServers":{"unity":{"command":"relay.exe","args":["--mcp"]},"local-only":{"command":"x"}},"preferences":{"theme":"dark"}}
            """);
        File.WriteAllText(Path.Combine(b.Dir, "claude_desktop_config.json"), """{"globalShortcut":"Ctrl+Space"}""");
        var profiles = new List<ProfileConfig>
        {
            new() { Id = "a", Name = "A", DataDir = a.Dir },
            new() { Id = "b", Name = "B", DataDir = b.Dir },
        };

        var servers = McpSync.Collect(profiles);
        var unity = servers.Single(s => s.Name == "unity");
        Assert.True(unity.EnabledFor["a"]);
        Assert.False(unity.EnabledFor["b"]);

        Assert.Empty(McpSync.ProfilesNeedingChange(profiles, servers));
        unity.EnabledFor["b"] = true;
        Assert.Equal(new[] { "b" }, McpSync.ProfilesNeedingChange(profiles, servers).Select(p => p.Id));
        servers.Single(s => s.Name == "local-only").EnabledFor["a"] = false;
        McpSync.Apply(profiles, servers);
        Assert.Empty(McpSync.ProfilesNeedingChange(profiles, servers));

        var ja = JsonNode.Parse(File.ReadAllText(Path.Combine(a.Dir, "claude_desktop_config.json")))!;
        var jb = JsonNode.Parse(File.ReadAllText(Path.Combine(b.Dir, "claude_desktop_config.json")))!;
        Assert.Equal("dark", (string?)ja["preferences"]!["theme"]);
        Assert.Null(ja["mcpServers"]!["local-only"]);
        Assert.Equal("relay.exe", (string?)jb["mcpServers"]!["unity"]!["command"]);
        Assert.Equal("Ctrl+Space", (string?)jb["globalShortcut"]);
    }

    [Fact]
    public void RemoteConnectorUsesMcpRemote()
    {
        var def = McpSync.RemoteConnector("https://mcp.example.com/sse");
        Assert.Equal("npx", (string?)def["command"]);
        Assert.Equal("https://mcp.example.com/sse", (string?)def["args"]![2]);
    }
}

public class LinkRouterTests
{
    static readonly ProfileConfig Default = new() { Id = "default", Name = "Mặc định" };
    static readonly ProfileConfig Work = new() { Id = "work", Name = "Work", DataDir = @"C:\p\work" };
    static readonly ProfileConfig Fresh = new() { Id = "fresh", Name = "Fresh", DataDir = @"C:\p\fresh" };
    static readonly ProfileConfig[] All = { Default, Work, Fresh };

    [Theory]
    [InlineData("claude://magic-link#abc:def", true)]
    [InlineData("claude://sso-callback?code=1", true)]
    [InlineData("claude://claude.ai/magic-link#abc:def", true)]
    [InlineData("claude://new?q=hello", false)]
    [InlineData("https://claude.ai/magic-link#a:b", false)]
    public void Recognises_sign_in_links(string url, bool expected) =>
        Assert.Equal(expected, LinkRouter.IsSignInLink(url));

    [Fact]
    public void Log_line_drops_sign_in_secrets()
    {
        Assert.Equal("claude://magic-link", LinkRouter.Describe("claude://magic-link#secret:token"));
        Assert.Equal("claude://sso-callback", LinkRouter.Describe("claude://sso-callback?code=secret"));
    }

    [Fact]
    public void Sign_in_link_goes_to_running_signed_out_profile()
    {
        var target = LinkRouter.PickTarget("claude://magic-link#a:b", All,
            p => p == Fresh || p == Default ? DateTime.Now : null,
            p => p != Fresh);
        Assert.Same(Fresh, target);
    }

    [Fact]
    public void Picks_the_most_recently_started_signed_out_profile()
    {
        var target = LinkRouter.PickTarget("claude://magic-link#a:b", All,
            p => p == Work ? DateTime.Now : p == Fresh ? DateTime.Now.AddMinutes(-5) : null,
            _ => false);
        Assert.Same(Work, target);
    }

    [Fact]
    public void Falls_back_to_default_profile()
    {
        // Signed-out but not running, and non sign-in links, both keep Windows' usual behaviour.
        Assert.Same(Default, LinkRouter.PickTarget("claude://magic-link#a:b", All, p => p == Default ? DateTime.Now : null, p => p != Fresh));
        Assert.Same(Default, LinkRouter.PickTarget("claude://new?q=hi", All, _ => DateTime.Now, _ => false));
    }
}
