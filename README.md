# Claude Switcher

**English** | [Tiếng Việt](README.vi.md)

A Windows app for running several **Claude Desktop** windows at once, each signed in to a different account.

- **Profiles:** each profile has its own data folder, so sign-in, settings and sessions stay separate. The "Default" profile is the Claude Desktop you already use, and its original data is left untouched.
- **Live usage:** shows 5-hour, weekly (all models) and per-model weekly usage, with reset times, for every account. The numbers come from the same place Claude Desktop gets them: a call to `api.anthropic.com/api/oauth/usage` with that profile's own token, not from history files. When 5-hour usage crosses your threshold, the app warns you and suggests the profile with the most usage left.
- **Move Code sessions** to another profile to keep working when you run out of usage. The conversation history is kept.
- **Shared MCP:** choose which MCP servers each profile uses. Remote connectors can be added by URL.
- **Start with Windows:** the app runs in the system tray and opens the profiles you picked.
- **Light/dark theme:** choose Light, Dark or Match Windows in the Settings tab.
- **English / Vietnamese:** the app starts in English. Switch with **Settings → Language**; the UI changes right away, no restart needed.

## Install

Requirements: Windows 10/11 with Claude Desktop installed.

1. Download `ClaudeSwitcher.exe` from the [Releases](https://github.com/Colb98/claude-switcher/releases/latest) page, or build `dist\ClaudeSwitcher.exe` yourself (see [Build](#build)).
2. Copy the exe to a permanent folder (for example `C:\Tools\ClaudeSwitcher\`) and run it. No .NET install needed.

> Pick the permanent folder **before** turning on "Start with Windows", because Windows saves the exe path at that moment.

> The exe is not code-signed, so Windows SmartScreen may warn on first run: click **More info → Run anyway**.

## Quick start

1. Open `ClaudeSwitcher.exe`. The **Profiles** tab already has a "Default" profile, which is the Claude Desktop you already use.
2. Click **+ New profile**, enter a name (for example "Work"), pick a color, and click **Create**.
3. When asked, click **Yes** to open the new profile. A separate Claude Desktop window appears; sign in to your second account there.
   - If you sign in with an email link, first turn on "Open sign-in links in the right profile" (step 5 in [Detailed usage](#detailed-usage)).
4. After a few seconds, the profile card shows that account's email, plan, and 5-hour / weekly usage bars.
5. From now on, the fastest way to open a profile is to **left-click the Claude Switcher icon in the system tray** (bottom-right of the taskbar; if you don't see it, click the ^ arrow next to the clock). A small panel lists your profiles with their 5-hour usage; click a profile once to open it, or to bring its window to the front if it is already running.
6. To keep the app always ready: in the **Settings** tab, turn on "Start with Windows".

## Detailed usage

1. **+ New profile:** enter a name and pick a color, then click **Open** and sign in to a Claude account in the window that appears.
2. Each profile is its own Claude Desktop window, and several can be open at once. The **⋯** button on a profile card also lets you open its data folder, create a desktop shortcut, or delete the profile.
3. **Move sessions:**
   - Pick the source profile and tick the sessions to move.
   - Pick the target profile (it must have signed in at least once), then click **Move selected sessions**.
   - "Move" (on by default) removes the sessions from the source profile, so the same session is never open in two windows (two processes would write to one history). The original is kept in `backups\`.
   - An open Claude Desktop would overwrite the change, so the app asks before closing the profiles involved, then offers to open the target profile when the copy is done.
4. **Shared MCP:** tick the profiles for each server, then click **Apply**. Only profiles with changes are touched. If such a profile is running, the app closes it, writes the config and reopens it, because an open Claude Desktop would overwrite its config file. MCP servers declared in `~\.claude.json` (used by Code sessions) are already shared by every profile.
5. **Settings:** turn on "Start with Windows". To have a profile open when you sign in to Windows, click **Edit** on that profile and tick "Open at startup with Windows".
   - **Open sign-in links in the right profile:** Windows always opens `claude://` links (such as the sign-in link in an email) in the Default profile. When you sign in to another profile, the Default window gets stuck on the "Use verification code" screen. With this option on, Claude Switcher receives the link and sends sign-in links to the open profile that has not signed in yet; other links still open in the Default profile. Windows doesn't let an app make itself the default link handler, so when you turn this on the app opens Claude Switcher's page in Settings → Default apps; click the CLAUDE row and choose Claude Switcher.
   - **Language:** English or Tiếng Việt.
6. **System tray:** closing the window only hides the app to the tray.
   - **Left-click** the icon: a quick picker; click a profile once to open it. The **Open Claude Switcher** button at the bottom opens the main window. Click outside or press Esc to close the picker.
   - **Right-click** the icon: the full menu (open/stop each profile, refresh usage, **Exit** to quit the app).

### Command line

| Command | What it does |
|---|---|
| `ClaudeSwitcher.exe --launch <id>` | Open one profile (used by shortcuts; "⋯ → Create desktop shortcut" makes one) |
| `ClaudeSwitcher.exe --stop <id>` | Close one profile |
| `ClaudeSwitcher.exe --selftest report.txt` | Check: find Claude, read tokens, usage, sessions, MCP (tokens are never written to the file) |
| `ClaudeSwitcher.exe --open-url <link>` | Open a `claude://` link in the right profile (Windows calls this when "Open sign-in links in the right profile" is on) |
| `ClaudeSwitcher.exe --startup` | Start-with-Windows mode |
| `ClaudeSwitcher.exe --screenshot ui.png [--theme dark] [--lang vi]` | Render every tab to PNG to review the UI, without taking focus |

## Data

- App and new profiles: `%USERPROFILE%\.claude-switcher\` (`settings.json`, `profiles\`, `backups\`).
  This folder lives outside AppData because the MSIX build of Claude Desktop virtualizes file writes under AppData.
- Every write to Claude's data (MCP, overwriting sessions) is backed up to `backups\` first.

## Limitations

- Only **Code sessions** can be moved. Regular chats and Cowork are stored per account on the server.
- claude.ai connectors belong to an account. To share one, add it by URL (through `mcp-remote`).
- Claude Desktop refreshes a profile's token while that profile is open. If a profile stays closed too long, its card shows "Token expired"; open that profile once and the numbers update again.
- The app relies on Claude Desktop's internal data format (studied on version 4.4212). Later updates may change it; if something breaks, run `--selftest` to check.

## Build

```powershell
tools\publish.ps1        # produces dist\ClaudeSwitcher.exe
dotnet test tests\ClaudeSwitcher.Tests
```

Plan and technical findings (Vietnamese): [PLAN.md](PLAN.md).
