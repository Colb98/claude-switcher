using System;
using System.Collections.Generic;

namespace ClaudeSwitcher.Core;

/// <summary>
/// UI strings in English and Vietnamese. Code reads them with <see cref="T"/>; XAML reads them as
/// DynamicResource "T.&lt;key&gt;", which UI/Theme.ApplyLanguage copies into the application resources.
/// </summary>
public static class Loc
{
    public const string English = "en";
    public const string Vietnamese = "vi";

    public static string Language { get; private set; } = English;

    public static void SetLanguage(string? language) =>
        Language = string.Equals(language, Vietnamese, StringComparison.OrdinalIgnoreCase) ? Vietnamese : English;

    /// <summary>The string for <paramref name="key"/> in the current language, formatted with <paramref name="args"/>.</summary>
    public static string T(string key, params object?[] args)
    {
        if (!Table.TryGetValue(key, out var pair)) return key;
        var text = Language == Vietnamese ? pair.Vi : pair.En;
        return args.Length == 0 ? text : string.Format(text, args);
    }

    public static IEnumerable<KeyValuePair<string, (string En, string Vi)>> All => Table;

    static readonly Dictionary<string, (string En, string Vi)> Table = new()
    {
        // Main window: header and profile cards
        ["Tagline"] = ("Run several Claude Desktop accounts side by side", "Chạy nhiều tài khoản Claude Desktop cùng lúc"),
        ["RefreshUsage"] = ("Refresh usage", "Làm mới usage"),
        ["NewProfile"] = ("+ New profile", "+ Profile mới"),
        ["Open"] = ("Open", "Mở"),
        ["Stop"] = ("Stop", "Tắt"),
        ["Edit"] = ("Edit", "Sửa"),
        ["Running"] = ("Running", "Đang chạy"),
        ["Stopped"] = ("Stopped", "Đã tắt"),
        ["SignedOut"] = ("Not signed in", "Chưa đăng nhập"),
        ["SignInHint"] = ("Click Open, then sign in to the Claude account for this profile.", "Bấm Mở rồi đăng nhập tài khoản Claude cho profile này."),
        ["Updated"] = ("updated {0}", "cập nhật {0}"),
        ["TrayLine"] = ("{0}: 5h {1}, week {2}", "{0}: 5h {1}, tuần {2}"),
        ["ResetAt"] = ("resets {0}", "reset {0}"),
        ["ResetSoon"] = ("soon", "sắp"),
        ["ResetMinutes"] = ("in {0} min", "sau {0} phút"),
        ["ResetHours"] = ("in {0} h {1} min", "sau {0} giờ {1} phút"),
        ["ResetDays"] = ("in {0} days", "sau {0} ngày"),
        ["DefaultProfile"] = ("Default", "Mặc định"),

        // Tabs
        ["TabProfiles"] = ("Profiles", "Profile"),
        ["TabTransfer"] = ("Move sessions", "Chuyển session"),
        ["TabMcp"] = ("Shared MCP", "MCP dùng chung"),
        ["TabSettings"] = ("Settings", "Cài đặt"),

        // Session transfer tab
        ["TransferIntro"] = (
            "Copy Code sessions from one profile to another to keep working when you run out of usage. The conversation history lives in the shared ~\\.claude folder, so a session opened in the target profile keeps its full history. The target profile must have signed in at least once and be closed. Regular chats and Cowork are stored per account on the server, so they cannot be moved.",
            "Chép session Code từ profile này sang profile khác để làm tiếp khi hết usage. Lịch sử hội thoại nằm trong ~\\.claude dùng chung, nên session mở ở profile đích vẫn còn đầy đủ. Profile đích phải đã đăng nhập và đang tắt. Chat thường và Cowork lưu theo tài khoản trên server nên không chuyển được."),
        ["FromProfile"] = ("From profile", "Từ profile"),
        ["ToProfile"] = ("To profile", "Sang profile"),
        ["ShowArchived"] = ("Include archived sessions", "Hiện cả session đã lưu trữ"),
        ["ColTitle"] = ("Title", "Tiêu đề"),
        ["ColFolder"] = ("Folder", "Thư mục"),
        ["ColActivity"] = ("Last active", "Hoạt động"),
        ["MoveSessions"] = ("Move (remove from source profile)", "Chuyển hẳn (gỡ khỏi profile nguồn)"),
        ["MoveSessionsTip"] = (
            "Avoids opening the same session in two windows, where two processes would write to one history. The original is kept in the backup folder.",
            "Tránh mở cùng một session ở hai cửa sổ: hai tiến trình sẽ cùng ghi vào một lịch sử. Bản gốc được giữ trong thư mục backup."),
        ["Overwrite"] = ("Overwrite if it exists", "Ghi đè nếu đã có"),
        ["TransferSelected"] = ("Move selected sessions", "Chuyển session đã chọn"),
        ["Untitled"] = ("(untitled)", "(không có tiêu đề)"),

        // MCP tab
        ["McpIntro"] = (
            "Tick which profiles use which MCP server, then click Apply. The app merges them into each profile's claude_desktop_config.json (with a backup) and leaves other settings alone. Running profiles are restarted, because an open Claude Desktop would overwrite the config. claude.ai connectors belong to an account; to share one, add it by URL (it runs through mcp-remote). MCP servers in ~\\.claude.json (used by Code sessions) are already shared by every profile.",
            "Tick để chọn profile nào dùng MCP server nào, rồi bấm Áp dụng. App gộp vào claude_desktop_config.json của từng profile (có backup), các cài đặt khác giữ nguyên. Profile đang chạy sẽ được khởi động lại, vì Claude Desktop đang mở sẽ ghi đè cấu hình. Connector của claude.ai gắn với tài khoản; muốn dùng chung, thêm nó bằng URL (chạy qua mcp-remote). MCP trong ~\\.claude.json (dùng cho session Code) vốn đã dùng chung cho mọi profile."),
        ["McpRemoveAll"] = ("Remove from all profiles", "Xoá khỏi mọi profile"),
        ["McpAllOn"] = ("Enable for all", "Bật cho tất cả"),
        ["McpAddJson"] = ("Add MCP (JSON)", "Thêm MCP (JSON)"),
        ["McpAddUrl"] = ("Add connector by URL", "Thêm connector bằng URL"),
        ["Reload"] = ("Reload", "Tải lại"),
        ["Apply"] = ("Apply", "Áp dụng"),

        // Settings tab
        ["Language"] = ("Language", "Ngôn ngữ"),
        ["Appearance"] = ("Appearance", "Giao diện"),
        ["ThemeSystem"] = ("Match Windows", "Theo Windows"),
        ["ThemeLight"] = ("Light", "Sáng"),
        ["ThemeDark"] = ("Dark", "Tối"),
        ["StartWithWindows"] = ("Start with Windows", "Khởi động cùng Windows"),
        ["StartWithWindowsHint"] = (
            "When you sign in to Windows, Claude Switcher runs in the system tray and opens the profiles that have \"Open at startup\" turned on (set it with each profile's Edit button).",
            "Khi bật máy, Claude Switcher chạy ở khay hệ thống và tự mở các profile có bật \"Tự mở khi khởi động\" (chỉnh trong nút Sửa của từng profile)."),
        ["StartMinimized"] = ("Start minimized to the tray when starting with Windows", "Thu nhỏ xuống khay khi khởi động cùng Windows"),
        ["RouteLinks"] = ("Open sign-in links in the right profile", "Mở link đăng nhập đúng profile"),
        ["RouteLinksHint"] = (
            "Sign-in links from email (claude://) go to the open profile that has not signed in yet, instead of always opening the Default profile. Other links still open in the Default profile. If Windows asks which app should open the link, choose Claude Switcher.",
            "Link đăng nhập trong email (claude://) được gửi tới profile đang mở mà chưa đăng nhập, thay vì luôn mở profile Mặc định. Các link khác vẫn mở ở profile Mặc định. Nếu Windows hỏi mở link bằng ứng dụng nào, chọn Claude Switcher."),
        ["SetDefaultApp"] = ("Choose Claude Switcher in Windows Default apps", "Chọn Claude Switcher trong Default apps của Windows"),
        ["RefreshEvery"] = ("Refresh usage every", "Làm mới usage mỗi"),
        ["WarnAt"] = ("Warn when 5-hour usage ≥", "Cảnh báo khi usage 5 giờ ≥"),
        ["OpenAppData"] = ("Open app data folder", "Mở thư mục dữ liệu app"),
        ["OpenBackups"] = ("Open backup folder", "Mở thư mục backup"),
        ["Seconds"] = ("{0} seconds", "{0} giây"),
        ["Minutes"] = ("{0} minutes", "{0} phút"),
        ["LauncherInfo"] = ("Claude Desktop: {0}\nDefault profile data: {1}\nApp data: {2}", "Claude Desktop: {0}\nDữ liệu profile mặc định: {1}\nDữ liệu app: {2}"),
        ["NotFound"] = ("not found", "không tìm thấy"),

        // Status bar and messages
        ["OpenFailed"] = ("Could not open {0}: {1}", "Không mở được {0}: {1}"),
        ["Focusing"] = ("Bringing the {0} window to the front…", "Đang đưa cửa sổ {0} lên trước…"),
        ["Opening"] = ("Opening {0}…", "Đang mở {0}…"),
        ["NotRunning"] = ("{0} is not running.", "{0} không chạy."),
        ["StopConfirm"] = ("Close Claude Desktop for the \"{0}\" profile?\nWork running in that window will stop.", "Tắt Claude Desktop của profile \"{0}\"?\nCông việc đang chạy trong cửa sổ đó sẽ dừng."),
        ["StopTitle"] = ("Stop profile", "Tắt profile"),
        ["StoppedProfile"] = ("Stopped {0}.", "Đã tắt {0}."),
        ["CreatedOpenNow"] = ("Created the \"{0}\" profile. Open it now to sign in?", "Đã tạo profile \"{0}\". Mở ngay để đăng nhập tài khoản?"),
        ["NewProfileTitle"] = ("New profile", "Profile mới"),
        ["MenuOpenData"] = ("Open data folder", "Mở thư mục dữ liệu"),
        ["MenuShortcut"] = ("Create desktop shortcut", "Tạo shortcut trên Desktop"),
        ["ShortcutCreated"] = ("Created {0}", "Đã tạo {0}"),
        ["MenuTransferFrom"] = ("Move sessions from this profile…", "Chuyển session từ profile này…"),
        ["MenuDelete"] = ("Delete profile…", "Xoá profile…"),
        ["DeleteConfirm"] = (
            "Delete the \"{0}\" profile?\n\nYes: delete the profile and all of its sign-in data.\nNo: only remove it from the list and keep its data folder.",
            "Xoá profile \"{0}\"?\n\nYes: xoá profile và toàn bộ dữ liệu đăng nhập của nó.\nNo: chỉ gỡ khỏi danh sách, giữ thư mục dữ liệu."),
        ["DeleteTitle"] = ("Delete profile", "Xoá profile"),
        ["DeleteFolderFailed"] = ("Could not delete the data folder yet (a process is still using it): {0}", "Chưa xoá được thư mục dữ liệu (còn tiến trình đang dùng): {0}"),
        ["FetchingUsage"] = ("Fetching usage…", "Đang lấy usage…"),
        ["UsageUpdated"] = ("Usage updated at {0}.", "Usage cập nhật lúc {0}."),
        ["WarnSuggest"] = ("Tip: move your session to \"{0}\" (5 hours: {1:0}%).", "Gợi ý: chuyển session sang \"{0}\" (5 giờ: {1:0}%)."),
        ["WarnNoBetter"] = ("No other profile has more usage left.", "Chưa có profile nào còn nhiều usage hơn."),
        ["WarnTitle"] = ("{0} has used {1:0}% of its 5-hour usage", "{0} đã dùng {1:0}% usage 5 giờ"),
        ["SameProfile"] = ("The source and target profiles must be different.", "Profile nguồn và đích phải khác nhau."),
        ["PickSession"] = ("Tick at least one session.", "Hãy tick ít nhất một session."),
        ["TransferMustClose"] = (
            "{0} must be closed to move sessions (an open Claude Desktop would overwrite the change).\nOther sessions running in that window will stop. Close now?",
            "Cần tắt {0} để chuyển session (Claude Desktop đang mở sẽ ghi đè thay đổi).\nCác session khác đang chạy trong cửa sổ đó sẽ dừng. Tắt ngay?"),
        ["TransferTitle"] = ("Move sessions", "Chuyển session"),
        ["Copied"] = ("Copied {0} session(s) to \"{1}\".", "Đã chép {0} session sang \"{1}\"."),
        ["Skipped"] = (" Skipped {0} session(s) that already exist.", " Bỏ qua {0} session đã có sẵn."),
        ["RemovedFromSource"] = (" Removed from \"{0}\" (the original is in the backup folder).", " Đã gỡ khỏi \"{0}\" (bản gốc nằm trong thư mục backup)."),
        ["OpenTargetNow"] = ("\n\nOpen the target profile now to continue?", "\n\nMở profile đích ngay để làm tiếp?"),
        ["McpReloaded"] = ("Reloaded the MCP configuration.", "Đã đọc lại cấu hình MCP."),
        ["McpAddJsonTitle"] = ("Add MCP server", "Thêm MCP server"),
        ["McpAddJsonPrompt"] = ("Paste JSON like {\"name\": {\"command\": \"...\", \"args\": [...]}} or a whole \"mcpServers\" block:", "Dán JSON dạng {\"tên\": {\"command\": \"...\", \"args\": [...]}} hoặc cả khối \"mcpServers\":"),
        ["NeedJsonObject"] = ("Expected a JSON object.", "Cần một object JSON."),
        ["BadJson"] = ("Invalid JSON: {0}", "JSON không hợp lệ: {0}"),
        ["McpAddUrlPrompt"] = ("URL of the remote MCP server (for example https://mcp.example.com/sse):", "URL của MCP server từ xa (ví dụ https://mcp.example.com/sse):"),
        ["ConnectorNameTitle"] = ("Connector name", "Tên connector"),
        ["ConnectorNamePrompt"] = ("Name the connector:", "Đặt tên cho connector:"),
        ["McpAdded"] = ("Added \"{0}\" for every profile. Click Apply to save.", "Đã thêm \"{0}\" cho mọi profile. Bấm Áp dụng để ghi."),
        ["McpNoChange"] = ("The MCP configuration has no changes.", "Cấu hình MCP không có gì thay đổi."),
        ["McpRestart"] = (
            "{0} must restart to apply MCP (an open Claude Desktop would overwrite the config).\nSessions running in that window will stop. Continue?",
            "Cần khởi động lại {0} để áp dụng MCP (Claude Desktop đang mở sẽ ghi đè cấu hình).\nCác session đang chạy trong cửa sổ đó sẽ dừng. Tiếp tục?"),
        ["McpApplied"] = ("Applied MCP to {0}. Backup: {1}", "Đã áp dụng MCP cho {0}. Backup: {1}"),
        ["StartupSaved"] = ("Saved the startup setting.", "Đã lưu cài đặt khởi động."),
        ["LinksSaved"] = ("Saved the link setting.", "Đã lưu cài đặt mở link."),
        ["LinksDefaultApp"] = (
            "Windows only lets you choose which app opens claude:// links yourself.\n\nClick OK to open Claude Switcher's page in Settings → Default apps, then click the CLAUDE row and choose Claude Switcher.",
            "Windows chỉ cho bạn tự chọn ứng dụng mở link claude://.\n\nBấm OK để mở trang Claude Switcher trong Settings → Default apps, rồi bấm vào dòng CLAUDE và chọn Claude Switcher."),

        // Tray
        ["TrayOpenApp"] = ("Open Claude Switcher", "Mở Claude Switcher"),
        ["TrayExit"] = ("Exit", "Thoát"),
        ["TrayStillRunning"] = ("Claude Switcher is still running", "Claude Switcher vẫn chạy"),
        ["TrayReopen"] = ("Open it again from the system tray icon.", "Mở lại từ biểu tượng ở khay hệ thống."),
        ["PopupTitle"] = ("Open a profile", "Mở profile"),
        ["PopupUsageTip"] = ("5-hour usage", "Usage 5 giờ"),
        ["PopupResetTip"] = ("5-hour usage, {0}", "Usage 5 giờ, {0}"),
        ["PopupWeek"] = ("Week {0}", "Tuần {0}"),
        ["PopupWeekTip"] = ("Weekly limit reached, {0}", "Đã hết hạn mức tuần, {0}"),
        ["PopupFocus"] = ("Bring the {0} window to the front", "Đưa cửa sổ {0} lên trước"),
        ["PopupOpen"] = ("Open {0}", "Mở {0}"),

        // Dialogs
        ["EditProfileTitle"] = ("Edit profile", "Sửa profile"),
        ["ProfileName"] = ("Profile name", "Tên profile"),
        ["Color"] = ("Color", "Màu"),
        ["AutoStart"] = ("Open at startup with Windows", "Tự mở khi khởi động cùng Windows"),
        ["NewProfileHint"] = (
            "A new profile has its own data folder. The first time you open it, sign in to the Claude account you want to use for this profile.",
            "Profile mới có thư mục dữ liệu riêng. Lần đầu mở, hãy đăng nhập tài khoản Claude muốn dùng cho profile này."),
        ["Create"] = ("Create", "Tạo"),
        ["Save"] = ("Save", "Lưu"),
        ["Cancel"] = ("Cancel", "Huỷ"),
        ["EnterName"] = ("Enter a profile name.", "Hãy nhập tên profile."),

        // Core
        ["ClaudeNotFound"] = ("Claude Desktop was not found. Install Claude Desktop first.", "Không tìm thấy Claude Desktop. Hãy cài Claude Desktop trước."),
        ["TargetSignedOut"] = ("The target profile has not signed in. Open it and sign in once first.", "Profile đích chưa đăng nhập. Hãy mở và đăng nhập profile đích một lần trước."),
        ["LinkDescription"] = ("Opens claude:// links in the right Claude Desktop profile", "Mở link claude:// đúng profile Claude Desktop"),
        ["BadLink"] = ("(invalid link)", "(link không hợp lệ)"),
        ["ShortcutDescription"] = ("Open Claude Desktop with the {0} profile", "Mở Claude Desktop với profile {0}"),
        ["TokenReadFailed"] = ("Could not read the token: {0}", "Không đọc được token: {0}"),
        ["TokenExpired"] = ("Token expired, open the profile to refresh it", "Token hết hạn, mở profile để làm mới"),
        ["TokenRejected"] = ("Token rejected, open the profile to refresh it", "Token bị từ chối, mở profile để làm mới"),
        ["NetworkError"] = ("Network error: {0}", "Lỗi mạng: {0}"),
        ["FiveHour"] = ("5 hours", "5 giờ"),
        ["WeekAll"] = ("Week (all models)", "Tuần (mọi model)"),
        ["WeekModel"] = ("Week ({0})", "Tuần ({0})"),
    };
}
