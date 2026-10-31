using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ClaudeSwitcher.Core;

namespace ClaudeSwitcher;

/// <summary>Headless diagnostics: `ClaudeSwitcher.exe --selftest report.txt`. Never writes tokens.</summary>
public static class SelfTest
{
    public static async Task<int> Run(string reportPath)
    {
        var sb = new StringBuilder();
        var failures = 0;
        void Line(string s) => sb.AppendLine(s);

        Line($"Claude Switcher self-test {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        var launcher = ClaudeInstall.FindLauncher();
        Line($"launcher: {launcher ?? "NOT FOUND"}");
        if (launcher == null) failures++;
        Line($"default data dir: {ClaudeInstall.FindDefaultDataDir()}");

        var settings = ProfileStore.Load();
        var running = InstanceManager.RunningInstances();
        Line($"running instances: {running.Count}");
        foreach (var (dir, pid) in running) Line($"  pid {pid}: {dir}");

        var usage = new UsageService();
        foreach (var p in settings.Profiles)
        {
            var dir = p.ResolveDataDir();
            Line("");
            Line($"[{p.Name}] id={p.Id} default={p.IsDefault} dir={dir}");
            Line($"  running: {InstanceManager.FindMainProcess(p)?.ToString() ?? "no"}");
            try
            {
                var cred = DesktopSecrets.ReadCredential(dir);
                Line(cred == null
                    ? "  credential: none (signed out)"
                    : $"  credential: account={cred.AccountUuid} org={cred.OrgUuid} host={cred.ApiHost} expires={cred.ExpiresAt.LocalDateTime:g} expired={cred.IsExpired}");
            }
            catch (Exception e) { Line($"  credential: ERROR {e.GetType().Name}: {e.Message}"); failures++; }

            var r = await usage.FetchAsync(dir);
            Line($"  usage: {r.Status} {r.Message}");
            if (r.Status == UsageStatus.Ok)
            {
                Line($"  account: {r.Email} plan: {r.PlanName}");
                foreach (var w in new[] { r.FiveHour, r.Weekly }.Concat(r.ScopedWeekly).Where(w => w != null))
                    Line($"  {w!.Label}: {w.Percent:0}% resets {w.ResetsAt?.LocalDateTime:g}");
            }
            else if (r.Status == UsageStatus.Failed) failures++;

            var sessions = SessionTransfer.List(dir);
            Line($"  code sessions: {sessions.Count} (archived {sessions.Count(s => s.IsArchived)})");
        }

        var servers = McpSync.Collect(settings.Profiles);
        Line("");
        Line($"mcp servers: {string.Join(", ", servers.Select(s => $"{s.Name}[{string.Join("/", s.EnabledFor.Where(kv => kv.Value).Select(kv => kv.Key))}]"))}");
        Line($"start with windows: {StartupManager.IsEnabled}");
        Line($"result: {(failures == 0 ? "PASS" : $"{failures} FAILURE(S)")}");

        await File.WriteAllTextAsync(reportPath, sb.ToString());
        return failures == 0 ? 0 : 1;
    }
}
