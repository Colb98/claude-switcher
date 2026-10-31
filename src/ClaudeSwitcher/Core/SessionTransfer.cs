using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ClaudeSwitcher.Core;

public sealed class CodeSession
{
    public required string FilePath { get; init; }
    public required string SessionId { get; init; }
    public string Title { get; init; } = "";
    public string Cwd { get; init; } = "";
    public DateTimeOffset LastActivity { get; init; }
    public bool IsArchived { get; init; }
    public bool Selected { get; set; }
    public string LastActivityText => LastActivity.LocalDateTime.ToString("dd/MM HH:mm");
}

/// <summary>
/// Moves Code tab sessions between profiles. Desktop keeps one JSON per session under
/// claude-code-sessions\&lt;account&gt;\&lt;org&gt;; transcripts live in the shared ~\.claude, so a copied
/// session resumes with its full history under the target account.
/// </summary>
public static class SessionTransfer
{
    const string SessionsFolder = "claude-code-sessions";

    public static List<CodeSession> List(string dataDir)
    {
        var root = Path.Combine(dataDir, SessionsFolder);
        var sessions = new List<CodeSession>();
        if (!Directory.Exists(root)) return sessions;
        foreach (var file in Directory.EnumerateFiles(root, "local_*.json", SearchOption.AllDirectories))
        {
            try
            {
                var j = JsonNode.Parse(DesktopSecrets.ReadShared(file)) as JsonObject;
                if (j == null) continue;
                var last = j["lastActivityAt"]?.GetValue<double>() ?? j["createdAt"]?.GetValue<double>() ?? 0;
                sessions.Add(new CodeSession
                {
                    FilePath = file,
                    SessionId = (string?)j["sessionId"] ?? Path.GetFileNameWithoutExtension(file),
                    Title = (string?)j["title"] ?? Loc.T("Untitled"),
                    Cwd = (string?)j["cwd"] ?? "",
                    LastActivity = DateTimeOffset.FromUnixTimeMilliseconds((long)last),
                    IsArchived = j["isArchived"]?.GetValue<bool>() ?? false,
                });
            }
            catch (Exception e) when (e is JsonException or IOException or InvalidOperationException) { }
        }
        return sessions.OrderByDescending(s => s.LastActivity).ToList();
    }

    /// <summary>Copies sessions into the target profile. The target must be signed in and closed.</summary>
    public static int CopyTo(IEnumerable<CodeSession> sessions, string targetDataDir, bool overwrite)
    {
        var cred = DesktopSecrets.ReadCredential(targetDataDir)
            ?? throw new InvalidOperationException(Loc.T("TargetSignedOut"));
        var targetDir = Path.Combine(targetDataDir, SessionsFolder, cred.AccountUuid, cred.OrgUuid);
        Directory.CreateDirectory(targetDir);

        var copied = 0;
        foreach (var s in sessions)
        {
            var dest = Path.Combine(targetDir, Path.GetFileName(s.FilePath));
            if (File.Exists(dest) && !overwrite) continue;
            if (File.Exists(dest)) ProfileStore.Backup("session-overwrite", new[] { dest });

            var j = (JsonObject)JsonNode.Parse(DesktopSecrets.ReadShared(s.FilePath))!;
            // Connector settings belong to the source account; the target account re-derives its own.
            if (j.ContainsKey("remoteMcpServersConfig")) j["remoteMcpServersConfig"] = new JsonArray();
            j["isArchived"] = false;
            var tmp = dest + ".tmp";
            File.WriteAllText(tmp, j.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
            File.Move(tmp, dest, true);
            copied++;
        }
        return copied;
    }

    /// <summary>
    /// Takes moved sessions out of the source profile so the same transcript is never resumed by two
    /// instances at once. Files go to the backup folder rather than being deleted. The source must be closed,
    /// or its running Desktop would write them back.
    /// </summary>
    public static string RemoveFromSource(IReadOnlyCollection<CodeSession> sessions)
    {
        var backup = ProfileStore.Backup("moved-sessions", sessions.Select(s => s.FilePath));
        foreach (var s in sessions)
            if (File.Exists(s.FilePath)) File.Delete(s.FilePath);
        return backup;
    }
}
