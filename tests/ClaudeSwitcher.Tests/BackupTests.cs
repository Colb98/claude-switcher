using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using ClaudeSwitcher.Core;
using Xunit;

namespace ClaudeSwitcher.Tests;

/// <summary>A fake machine under a temp folder, so backups never read or write the user's real data.</summary>
public sealed class FakeMachine : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "cs-backup-" + Guid.NewGuid().ToString("N")[..8]);
    public string Zip => Path.Combine(Dir, "out", "backup.zip");
    public BackupRoots Source { get; }
    public BackupRoots Target { get; }

    public FakeMachine()
    {
        Source = BackupRoots.Under(Path.Combine(Dir, "source"));
        Target = BackupRoots.Under(Path.Combine(Dir, "target"));
        Directory.CreateDirectory(Path.GetDirectoryName(Zip)!);

        Write(Source.ClaudeHome, "settings.json", "{\"model\":\"opus\"}");
        Write(Source.ClaudeHome, "projects/D--Work/session.jsonl", "{\"line\":1}\n");
        Write(Source.ClaudeHome, ".credentials.json", "{\"claudeAiOauth\":\"secret\"}");
        Write(Source.ClaudeHome, "cache/x.bin", "cache");
        Write(Source.ClaudeHome, "sessions/1.key", "peer-key");
        Write(Source.ClaudeHome, "ide/1.lock", "{\"authToken\":\"secret\"}");
        Write(Source.ClaudeHome, "plugins/deep/.credentials.json", "secret");
        File.WriteAllText(Source.ClaudeJson,
            "{\"mcpServers\":{\"a\":{\"command\":\"npx\"}},\"oauthAccount\":{\"emailAddress\":\"old@example.com\"},\"primaryApiKey\":\"secret\"}");

        Write(Source.DefaultDataDir, "claude_desktop_config.json", "{\"mcpServers\":{\"b\":{\"command\":\"uvx\"}}}");
        Write(Source.DefaultDataDir, "config.json", "{\"locale\":\"vi\",\"oauth:tokenCache\":\"enc1\",\"oauth:tokenCacheV2\":\"enc2\"}");
        Write(Source.DefaultDataDir, "claude-code-sessions/acc/org/local_1.json", "{\"sessionId\":\"s1\"}");
        Write(Source.DefaultDataDir, "Local State", "{\"os_crypt\":{}}");
        Write(Source.DefaultDataDir, "Network/Cookies", "cookies");
        Write(Source.DefaultDataDir, "Local Storage/leveldb/1.log", "login");
        Write(Source.DefaultDataDir, "Cache/data", "cache");
        Write(Source.DefaultDataDir, "claude-code/2.0/claude.exe", "binary");
        Write(Source.DefaultDataDir, "ssh-remote-server-state.json", "{\"tokens\":{}}");

        var work = Path.Combine(Source.ProfilesRoot, "work1");
        Write(work, "claude_desktop_config.json", "{\"mcpServers\":{}}");
        Write(work, "config.json", "{\"locale\":\"en\",\"oauth:tokenCacheV2\":\"enc3\"}");
        Write(work, "Code Cache/js", "cache");
        Write(Source.SwitcherRoot, "settings.json", SettingsJson(work, "work1"));
        Write(Source.SwitcherRoot, "links.log", "log");
        Write(Source.SwitcherRoot, "backups/20261101-mcp/00-a.json", "{}");
        Write(Source.SwitcherRoot, "backups/restore-20261102/config.json", "old");
    }

    public static string SettingsJson(string dataDir, string id) => new JsonObject
    {
        ["Theme"] = "Dark",
        ["Profiles"] = new JsonArray(
            new JsonObject { ["Id"] = "default", ["Name"] = "Default", ["Color"] = "#111111", ["DataDir"] = null },
            new JsonObject { ["Id"] = id, ["Name"] = id, ["Color"] = "#222222", ["DataDir"] = dataDir }),
    }.ToJsonString();

    public static void Write(string root, string rel, string content)
    {
        var path = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    public List<string> EntryNames()
    {
        using var zip = ZipFile.OpenRead(Zip);
        return zip.Entries.Select(e => e.FullName).ToList();
    }

    public string ReadEntry(string name)
    {
        using var zip = ZipFile.OpenRead(Zip);
        using var reader = new StreamReader(zip.GetEntry(name)!.Open());
        return reader.ReadToEnd();
    }

    public void Dispose()
    {
        try { Directory.Delete(Dir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}

public class BackupTests
{
    [Fact]
    public void Round_trip_restores_settings_sessions_and_mcp()
    {
        using var m = new FakeMachine();
        var created = BackupService.Create(m.Zip, m.Source);
        Assert.Empty(created.Skipped);
        Assert.True(created.ZipBytes > 0);

        var restored = BackupService.Restore(m.Zip, m.Target);
        Assert.Empty(restored.Failed);
        Assert.Equal(created.Files, restored.Restored);

        Assert.Equal("{\"model\":\"opus\"}", File.ReadAllText(Path.Combine(m.Target.ClaudeHome, "settings.json")));
        Assert.Equal("{\"line\":1}\n", File.ReadAllText(Path.Combine(m.Target.ClaudeHome, "projects/D--Work/session.jsonl")));
        Assert.Equal("{\"sessionId\":\"s1\"}", File.ReadAllText(Path.Combine(m.Target.DefaultDataDir, "claude-code-sessions/acc/org/local_1.json")));
        Assert.Contains("uvx", File.ReadAllText(Path.Combine(m.Target.DefaultDataDir, "claude_desktop_config.json")));
        Assert.True(File.Exists(Path.Combine(m.Target.ProfilesRoot, "work1", "claude_desktop_config.json")));
        Assert.Equal("npx", (string?)JsonNode.Parse(File.ReadAllText(m.Target.ClaudeJson))!["mcpServers"]!["a"]!["command"]);
    }

    [Fact]
    public void Leaves_out_sign_in_data_caches_and_downloads()
    {
        using var m = new FakeMachine();
        BackupService.Create(m.Zip, m.Source);
        var names = m.EntryNames();

        foreach (var banned in new[]
                 {
                     ".credentials.json", "cache/", "sessions/", "ide/", "Local State", "Network/", "Local Storage/",
                     "Cache/", "Code Cache/", "claude-code/", "ssh-remote-server-state.json", "links.log", "restore-20261102",
                 })
            Assert.DoesNotContain(names, n => n.Contains(banned) && !n.Contains("claude-code-sessions"));
        Assert.Contains("switcher/backups/20261101-mcp/00-a.json", names);
    }

    [Fact]
    public void Strips_sign_in_values_from_config_files()
    {
        using var m = new FakeMachine();
        BackupService.Create(m.Zip, m.Source);

        var config = JsonNode.Parse(m.ReadEntry("default-profile/config.json"))!.AsObject();
        Assert.Equal("vi", (string?)config["locale"]);
        Assert.DoesNotContain(config, kv => kv.Key.StartsWith("oauth:"));

        var home = m.ReadEntry("home/.claude.json");
        Assert.DoesNotContain("old@example.com", home);
        Assert.DoesNotContain("primaryApiKey", home);
        Assert.Contains("mcpServers", home);

        Assert.DoesNotContain("enc3", m.ReadEntry("profile/work1/config.json"));
    }

    [Fact]
    public void Restore_keeps_the_sign_in_of_the_new_install()
    {
        using var m = new FakeMachine();
        BackupService.Create(m.Zip, m.Source);
        FakeMachine.Write(m.Target.DefaultDataDir, "config.json", "{\"locale\":\"en\",\"oauth:tokenCacheV2\":\"fresh\"}");
        Directory.CreateDirectory(Path.GetDirectoryName(m.Target.ClaudeJson)!);
        File.WriteAllText(m.Target.ClaudeJson, "{\"oauthAccount\":{\"emailAddress\":\"new@example.com\"}}");

        BackupService.Restore(m.Zip, m.Target);

        var config = JsonNode.Parse(File.ReadAllText(Path.Combine(m.Target.DefaultDataDir, "config.json")))!;
        Assert.Equal("vi", (string?)config["locale"]);
        Assert.Equal("fresh", (string?)config["oauth:tokenCacheV2"]);
        var home = JsonNode.Parse(File.ReadAllText(m.Target.ClaudeJson))!;
        Assert.Equal("new@example.com", (string?)home["oauthAccount"]!["emailAddress"]);
        Assert.NotNull(home["mcpServers"]);
    }

    [Fact]
    public void Overwritten_files_move_to_the_undo_folder_and_unchanged_files_stay()
    {
        using var m = new FakeMachine();
        BackupService.Create(m.Zip, m.Source);
        FakeMachine.Write(m.Target.ClaudeHome, "settings.json", "{\"model\":\"mine\"}");

        var first = BackupService.Restore(m.Zip, m.Target);

        Assert.NotNull(first.UndoDir);
        Assert.Equal("{\"model\":\"mine\"}", File.ReadAllText(Path.Combine(first.UndoDir!, "claude-home", "settings.json")));
        Assert.Equal("{\"model\":\"opus\"}", File.ReadAllText(Path.Combine(m.Target.ClaudeHome, "settings.json")));

        var second = BackupService.Restore(m.Zip, m.Target);
        Assert.True(second.Unchanged > 0);
        Assert.Contains(Path.Combine("backups", "restore-"), first.UndoDir!);
    }

    [Fact]
    public void Locked_file_is_reported_and_the_backup_still_finishes()
    {
        using var m = new FakeMachine();
        var locked = Path.Combine(m.Source.ClaudeHome, "projects/D--Work/locked.jsonl");
        File.WriteAllText(locked, "x");
        using (new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var result = BackupService.Create(m.Zip, m.Source);
            Assert.Contains(result.Skipped, s => s.Contains("locked.jsonl"));
        }
        Assert.DoesNotContain("claude-home/projects/D--Work/locked.jsonl", m.EntryNames());
        Assert.Contains("claude-home/projects/D--Work/session.jsonl", m.EntryNames());
    }

    [Theory]
    [InlineData("claude-home/../../evil.txt")]
    [InlineData("claude-home/C:/evil.txt")]
    [InlineData("unknown-root/evil.txt")]
    [InlineData("home/not-claude-json.txt")]
    public void Paths_leaving_the_data_folders_are_refused_before_anything_is_written(string badPath)
    {
        using var m = new FakeMachine();
        BackupService.Create(m.Zip, m.Source);
        using (var zip = ZipFile.Open(m.Zip, ZipArchiveMode.Update))
        {
            var entry = zip.GetEntry("manifest.json")!;
            string text;
            using (var r = new StreamReader(entry.Open())) text = r.ReadToEnd();
            entry.Delete();
            var manifest = JsonNode.Parse(text)!;
            manifest["Files"]!.AsArray().Add(new JsonObject { ["Path"] = badPath, ["Length"] = 1, ["Sha256"] = "00" });
            using var w = new StreamWriter(zip.CreateEntry("manifest.json").Open());
            w.Write(manifest.ToJsonString());
        }

        Assert.Throws<InvalidDataException>(() => BackupService.Restore(m.Zip, m.Target));
        Assert.False(Directory.Exists(Path.Combine(m.Dir, "target")));
        Assert.False(File.Exists(Path.Combine(m.Dir, "evil.txt")));
    }

    [Fact]
    public void A_zip_that_is_not_a_backup_is_refused()
    {
        using var m = new FakeMachine();
        using (var zip = ZipFile.Open(m.Zip, ZipArchiveMode.Create)) zip.CreateEntry("other.txt");
        Assert.Throws<InvalidDataException>(() => BackupService.Inspect(m.Zip));

        File.WriteAllText(m.Zip, "not a zip");
        Assert.Throws<InvalidDataException>(() => BackupService.Inspect(m.Zip));
    }

    [Fact]
    public void Inspect_describes_the_backup()
    {
        using var m = new FakeMachine();
        var created = BackupService.Create(m.Zip, m.Source);
        var manifest = BackupService.Inspect(m.Zip);
        Assert.Equal(created.Files, manifest.Files.Count);
        Assert.Equal(created.SourceBytes, manifest.Files.Sum(f => f.Length));
        Assert.True(DateTimeOffset.UtcNow - manifest.CreatedAt < TimeSpan.FromMinutes(5));
    }

    [Fact]
    public void Paths_follow_the_new_windows_user()
    {
        using var m = new FakeMachine();
        var oldHome = Path.GetDirectoryName(m.Source.ClaudeJson)!;
        var escaped = oldHome.Replace("\\", "\\\\");
        var slashed = oldHome.Replace('\\', '/');
        File.WriteAllText(m.Source.ClaudeJson,
            $"{{\"projects\":{{\"{slashed}/app\":{{}},\"{slashed}by/app\":{{}}}},\"tool\":\"{escaped}\\\\bin\"}}");
        BackupService.Create(m.Zip, m.Source);

        BackupService.Restore(m.Zip, m.Target);

        var newHome = Path.GetDirectoryName(m.Target.ClaudeJson)!;
        var json = JsonNode.Parse(File.ReadAllText(m.Target.ClaudeJson))!;
        Assert.Equal(newHome + "\\bin", (string?)json["tool"]);
        Assert.NotNull(json["projects"]![newHome.Replace('\\', '/') + "/app"]);
        // A longer folder name that merely starts with the old home belongs to someone else.
        Assert.NotNull(json["projects"]![slashed + "by/app"]);
    }

    [Fact]
    public void Profiles_point_at_their_restored_folder_and_current_profiles_stay()
    {
        using var m = new FakeMachine();
        BackupService.Create(m.Zip, m.Source);
        var mine = Path.Combine(m.Target.ProfilesRoot, "mine");
        FakeMachine.Write(m.Target.SwitcherRoot, "settings.json", FakeMachine.SettingsJson(mine, "mine"));

        BackupService.Restore(m.Zip, m.Target);

        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(m.Target.SwitcherRoot, "settings.json")))!;
        Assert.Equal("Dark", (string?)settings["Theme"]);
        var profiles = settings["Profiles"]!.AsArray().Select(p => p!).ToList();
        Assert.Equal(Path.Combine(m.Target.ProfilesRoot, "work1"), (string?)profiles.Single(p => (string?)p["Id"] == "work1")["DataDir"]);
        Assert.Equal(mine, (string?)profiles.Single(p => (string?)p["Id"] == "mine")["DataDir"]);
        Assert.Single(profiles, p => p["DataDir"] == null);
    }

    [Fact]
    public void Junctions_are_stored_as_real_folders_and_cycles_do_not_hang()
    {
        using var m = new FakeMachine();
        var shared = Path.Combine(m.Dir, "shared-skills", "my-skill");
        FakeMachine.Write(shared, "SKILL.md", "skill");
        Directory.CreateDirectory(Path.Combine(m.Source.ClaudeHome, "skills"));
        Junction(Path.Combine(m.Source.ClaudeHome, "skills", "my-skill"), shared);
        Junction(Path.Combine(shared, "loop"), Path.GetDirectoryName(shared)!);

        var created = BackupService.Create(m.Zip, m.Source);
        BackupService.Restore(m.Zip, m.Target);

        var restored = Path.Combine(m.Target.ClaudeHome, "skills", "my-skill");
        Assert.Equal("skill", File.ReadAllText(Path.Combine(restored, "SKILL.md")));
        Assert.Null(new DirectoryInfo(restored).LinkTarget);
        Assert.Contains(created.Skipped, s => s.Contains("loop"));
        // A recursive delete cannot pass through the cycle, so it goes first.
        Directory.Delete(Path.Combine(shared, "loop"));
    }

    [Fact]
    public void Cancelling_removes_the_partial_file_and_keeps_an_older_backup()
    {
        using var m = new FakeMachine();
        File.WriteAllText(m.Zip, "older backup");
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.ThrowsAny<OperationCanceledException>(() => BackupService.Create(m.Zip, m.Source, ct: cts.Token));

        Assert.Equal("older backup", File.ReadAllText(m.Zip));
        Assert.False(File.Exists(m.Zip + ".partial"));
    }

    [Fact]
    public void Damaged_file_is_reported_and_not_restored()
    {
        using var m = new FakeMachine();
        BackupService.Create(m.Zip, m.Source);
        const string name = "claude-home/settings.json";
        using (var zip = ZipFile.Open(m.Zip, ZipArchiveMode.Update))
        {
            zip.GetEntry(name)!.Delete();
            using var w = new StreamWriter(zip.CreateEntry(name).Open());
            w.Write("{\"model\":\"tampered\"}");
        }

        var result = BackupService.Restore(m.Zip, m.Target);

        Assert.Contains(result.Failed, f => f.Contains(name));
        Assert.False(File.Exists(Path.Combine(m.Target.ClaudeHome, "settings.json")));
    }

    [Fact]
    public void Knows_when_the_output_would_be_removed_with_claude()
    {
        using var m = new FakeMachine();
        Assert.True(BackupService.IsInsideDataFolders(Path.Combine(m.Source.ClaudeHome, "a.zip"), m.Source));
        Assert.True(BackupService.IsInsideDataFolders(Path.Combine(m.Source.DefaultDataDir, "x", "a.zip"), m.Source));
        Assert.True(BackupService.IsInsideDataFolders(Path.Combine(m.Source.SwitcherRoot, "a.zip"), m.Source));
        Assert.False(BackupService.IsInsideDataFolders(m.Zip, m.Source));
        Assert.False(BackupService.IsInsideDataFolders(m.Source.ClaudeHome + "-other\\a.zip", m.Source));
    }

    static void Junction(string link, string target)
    {
        using var p = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
            { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        p.WaitForExit();
        Assert.Equal(0, p.ExitCode);
    }
}
