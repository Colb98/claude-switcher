using System;
using System.IO;
using System.Linq;
using System.Text;
using ClaudeSwitcher.Core;

namespace ClaudeSwitcher;

/// <summary>
/// Headless backup and restore: `--backup file.zip`, `--restore file.zip [--to folder]`.
/// This is a windowed exe with no console, so the outcome goes to file.zip.report.txt and the exit code.
/// </summary>
public static class BackupCli
{
    public static int Backup(string zipPath) => Run(zipPath, sb =>
    {
        var result = BackupService.Create(zipPath, BackupRoots.Current());
        sb.AppendLine($"files: {result.Files}");
        sb.AppendLine($"source bytes: {result.SourceBytes}");
        sb.AppendLine($"zip bytes: {result.ZipBytes}");
        AppendList(sb, "skipped", result.Skipped);
        return 0;
    });

    public static int Restore(string zipPath, string? toFolder) => Run(zipPath, sb =>
    {
        BackupRoots roots;
        if (toFolder != null) roots = BackupRoots.Under(toFolder);
        else
        {
            // An open Claude Desktop would overwrite what is restored when it next saves.
            var running = ProfileStore.Load().Profiles.Where(p => InstanceManager.FindMainProcess(p) != null).Select(p => p.Name).ToList();
            if (running.Count > 0)
            {
                sb.AppendLine($"refused, profiles still open: {string.Join(", ", running)}");
                return 2;
            }
            roots = BackupRoots.CurrentFor(BackupService.Inspect(zipPath));
        }
        var result = BackupService.Restore(zipPath, roots);
        sb.AppendLine($"restored: {result.Restored}");
        sb.AppendLine($"unchanged: {result.Unchanged}");
        sb.AppendLine($"undo folder: {result.UndoDir ?? "(none)"}");
        AppendList(sb, "failed", result.Failed);
        return result.Failed.Count == 0 ? 0 : 1;
    });

    static int Run(string zipPath, Func<StringBuilder, int> action)
    {
        var sb = new StringBuilder();
        int code;
        try { code = action(sb); }
        catch (Exception e)
        {
            sb.AppendLine($"error: {e.Message}");
            code = 1;
        }
        sb.AppendLine($"exit code: {code}");
        File.WriteAllText(Path.GetFullPath(zipPath) + ".report.txt", sb.ToString());
        return code;
    }

    static void AppendList(StringBuilder sb, string label, System.Collections.Generic.IReadOnlyList<string> items)
    {
        sb.AppendLine($"{label}: {items.Count}");
        foreach (var item in items) sb.AppendLine($"  {item}");
    }
}
