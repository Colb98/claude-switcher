# Claude Switcher: kế hoạch chi tiết

Ứng dụng Windows độc lập (một file `.exe`) để chạy nhiều cửa sổ Claude Desktop cùng lúc, mỗi cửa sổ đăng nhập một tài khoản riêng. App hiển thị usage 5 giờ và hàng tuần của từng tài khoản, chuyển session Code giữa các profile, dùng chung MCP và khởi động cùng Windows.

Ngày lập: 2026-10-31. Bản Claude Desktop dùng để khảo sát: MSIX `Claude_4.4212.0.0_x64__pzs8sxrjxfjjc` (Electron 44).

## 1. Kết quả khảo sát (đã kiểm chứng trên máy)

| Câu hỏi | Kết quả | Bằng chứng |
|---|---|---|
| Chạy nhiều instance được không? | **Được.** `claude-desktop.exe --user-data-dir="<thư mục>"` mở một instance độc lập. Khoá single-instance gắn với thư mục dữ liệu. | Spike 2026-10-31: 7 tiến trình riêng cho profile thử, thư mục được tạo đầy đủ `Local State`, `config.json`… Mã khởi động: `chosen && !movedOut → stay("chosen_folder")`. |
| Biến `CLAUDE_USER_DATA_DIR`? | **Không dùng được.** Bản đã ký xoá biến này. | Log: "CLAUDE_USER_DATA_DIR is set and signed builds ignore it". |
| Thư mục dữ liệu mặc định | `%LOCALAPPDATA%\Claude-Data\claude_pzs8sxrjxfjjc` | README trong thư mục. |
| App lấy usage thế nào? | `GET https://api.anthropic.com/api/oauth/usage`, header `Authorization: Bearer <OAuth token Claude Code>` và `anthropic-beta: oauth-2025-04-20`. | Hàm `[ccdPlanUsage]` trong `index.chunk-*.js`. Gọi thử: HTTP 200, `five_hour` và `seven_day` khớp với số trong app. |
| Token nằm ở đâu? | `config.json` → `oauth:tokenCacheV2`, được mã hoá bằng Electron `safeStorage`: tiền tố `v10` + AES-256-GCM, khoá AES nằm trong `Local State` → `os_crypt.encrypted_key`, được bảo vệ bằng DPAPI (CurrentUser). | Đã giải mã thành công. Khoá entry có dạng `acct:<accountUuid>\|<clientId>:<orgUuid>:<apiHost>:<scopes>`. |
| Email / tên tài khoản | `GET /api/oauth/profile` với cùng token | HTTP 200, có `account.email`, `organization.name`. |
| Session Code tab | `claude-code-sessions\<accountUuid>\<orgUuid>\local_<id>.json`. Transcript nằm ở `~\.claude\projects\…\<cliSessionId>.jsonl`, **dùng chung** cho mọi profile. | Đọc cấu trúc file. Mặc định `CLAUDE_CONFIG_DIR` không bị đặt, nên dùng `~\.claude`. |
| MCP local | `claude_desktop_config.json` → `mcpServers` trong từng thư mục profile. | Đã đọc. |
| Connector (claude.ai) | Lưu trên server theo tài khoản. **Không chép được** sang tài khoản khác. | Thiết kế của dịch vụ. |

### Lưu ý về độ chính xác của usage
App **không đọc** `plan-usage-history.json`. Mỗi lần làm mới, app gọi đúng endpoint mà Claude Desktop dùng, nên con số là số thực từ server. Token được Claude Desktop tự gia hạn khi profile đang mở. Nếu một profile tắt quá lâu và token hết hạn, app báo "token hết hạn, mở profile để làm mới" thay vì tự refresh. Lý do: tự refresh sẽ xoay vòng (rotate) refresh token và làm hỏng phiên đăng nhập của Claude Desktop.

## 2. Kiến trúc

- **Công nghệ:** C# WPF trên .NET 8. Publish dạng self-contained single-file `ClaudeSwitcher.exe`, không cần cài .NET.
- **Dữ liệu của app:** `%USERPROFILE%\.claude-switcher\`
  - `settings.json`: danh sách profile và cài đặt.
  - `profiles\<id>\`: thư mục dữ liệu Claude Desktop của từng profile mới.
  - `backups\<thời gian>\`: bản sao trước mọi thao tác ghi vào dữ liệu Claude.
  - **Phải nằm ngoài AppData.** Manifest MSIX của Claude không tắt ảo hoá file system. Kết quả thử 2026-10-31: một profile đặt trong `%LOCALAPPDATA%` có `config.json` bị ghi vào `Packages\Claude_pzs8sxrjxfjjc\LocalCache\Local\…`, nên app chạy ngoài package sẽ không đọc được token.
- **Profile "Mặc định"** trỏ tới thư mục dữ liệu gốc và chạy không kèm `--user-data-dir`. Dữ liệu gốc không bị di chuyển.

```
src/ClaudeSwitcher/
  Core/      ClaudeInstall, DesktopSecrets (DPAPI + AES-GCM), ProfileStore,
             InstanceManager, UsageService, SessionTransfer, McpSync, StartupManager
  UI/        MainWindow (4 tab), ProfileDialog, Tray icon
```

## 3. Các bước thực hiện

1. **Core: tìm Claude Desktop.** Ưu tiên alias `%LOCALAPPDATA%\Microsoft\WindowsApps\claude-desktop.exe` (bản MSIX). Dự phòng `%LOCALAPPDATA%\AnthropicClaude\claude.exe` (bản cài cũ). Tìm thư mục dữ liệu mặc định.
2. **Core: quản lý profile.** Thêm, sửa (tên, màu, tự mở khi khởi động), xoá (tuỳ chọn xoá luôn dữ liệu), mở thư mục, tạo shortcut trên Desktop.
3. **Core: chạy và tắt instance.** Mở bằng `--user-data-dir`. Nhận diện tiến trình bằng `--user-data-dir` trong command line (WMI). Instance mặc định là tiến trình chính có các tiến trình con trỏ vào thư mục gốc. Tắt bằng cách kết thúc cả cây tiến trình, có hộp thoại xác nhận.
4. **Core: usage.** Giải mã `oauth:tokenCacheV2` (ưu tiên token có scope `user:inference`, dự phòng `oauth:tokenCache`). Gọi `/api/oauth/usage` và `/api/oauth/profile`. Làm mới mỗi 2 phút (tuỳ chỉnh được) và khi bấm nút. Hiển thị 5 giờ, tuần (mọi model), các giới hạn tuần theo model, giờ reset, gói (plan) và email.
5. **Cảnh báo usage.** Khi 5 giờ của một profile đang chạy ≥ ngưỡng (mặc định 90%), hiện thông báo khay hệ thống và gợi ý profile còn nhiều usage nhất.
6. **Chuyển session Code.** Liệt kê session của profile nguồn (tiêu đề, thư mục, lần hoạt động cuối). Chép file JSON sang `claude-code-sessions\<account>\<org>` của profile đích. Account và org đích lấy từ token của profile đích, nên profile đích phải đăng nhập trước. Trường `remoteMcpServersConfig` (connector gắn với tài khoản nguồn) được làm rỗng. Profile đích phải đang tắt. Transcript dùng chung nên session chạy tiếp được với lịch sử đầy đủ.
7. **MCP dùng chung.** Bảng gồm mọi MCP server có trong các profile. Tick chọn profile nào dùng server nào, rồi bấm Áp dụng: app gộp vào `mcpServers` của từng `claude_desktop_config.json` (backup trước, giữ nguyên các khoá khác). Có nút thêm connector từ xa bằng URL (`npx -y mcp-remote <url>`), để dùng chung cho mọi profile. Profile cần khởi động lại để nhận cấu hình mới.
8. **Khởi động cùng Windows.** Ghi `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\ClaudeSwitcher = "<exe>" --startup`. Khi chạy với `--startup`, app thu nhỏ xuống khay hệ thống và tự mở các profile có bật "Tự mở". Profile đang chạy sẵn (ví dụ do Claude Desktop tự khởi động) được bỏ qua.
9. **Khay hệ thống.** Icon ở khay, menu mở hoặc tắt từng profile, tooltip hiện usage. Đóng cửa sổ sẽ thu xuống khay. Chỉ cho phép một Claude Switcher chạy cùng lúc.
10. **Kiểm tra.** Chạy `ClaudeSwitcher.exe --selftest` để in kết quả tìm Claude, giải mã token, usage và số session cho mọi profile (không in token). Chạy `--screenshot <png>` để render giao diện ra ảnh, dùng khi kiểm tra UI.
11. **Publish.** `dotnet publish -c Release -r win-x64 --self-contained -p:PublishSingleFile=true` ra `dist\ClaudeSwitcher.exe`.

## 4. Kết quả kiểm tra (2026-10-31)

| Kiểm tra | Kết quả |
|---|---|
| Unit test (`tests/ClaudeSwitcher.Tests`): giải mã safeStorage, chọn token, liệt kê/chép session, gộp MCP | 9/9 pass |
| `--selftest` ngoài package (qua WMI, giống khi mở từ Explorer) | PASS: tìm thấy launcher; đọc token; usage thật (5 giờ 81%, tuần 14%, tuần Fable 0%); email; gói "Team, Max 5x"; 74 session; MCP `rea` |
| Lỗi tìm được nhờ selftest | Kho `oauth:tokenCache` (v1) chứa token hạn tới 2027 nhưng server trả 401. Đã sửa: ưu tiên kho v2 và tự thử token kế tiếp khi gặp 401. |
| Mở profile mới bằng `--launch` ngoài package | Instance thứ hai chạy song song; dữ liệu ghi vào đúng `%USERPROFILE%\.claude-switcher\profiles\…`; nhận diện được 2 instance |
| `--stop` | Tắt đúng cây tiến trình của profile thử; profile mặc định không bị ảnh hưởng |
| Giao diện (`--screenshot`, render ngoài màn hình, không chiếm focus) | Đã xem 4 tab với 1 và 2 profile |
| **Chưa kiểm chứng** | Chuyển session sang một tài khoản **khác** đã đăng nhập rồi chạy tiếp trong Claude Desktop (cần 2 tài khoản thật); bật "Khởi động cùng Windows" và tự mở profile sau khi khởi động lại máy; cảnh báo ngưỡng usage trên khay. |

## 5. Giới hạn (không làm được hoặc chưa kiểm chứng)

- **Chat thường trên claude.ai và Cowork** được lưu trên server hoặc trong VM theo từng tài khoản, nên không chuyển được giữa các tài khoản. Chỉ session **Code** chuyển được.
- **Connector của claude.ai** không chép được giữa các tài khoản (chỉ dùng chung được qua `mcp-remote` như bước 7).
- Định dạng dữ liệu bên trong Claude Desktop không có tài liệu chính thức và có thể đổi theo bản cập nhật. Mọi thao tác ghi đều có backup.
- Việc chuyển session sang tài khoản khác cần được bạn thử thật với 2 tài khoản đã đăng nhập. Môi trường build chỉ có một tài khoản nên chưa thử được.
- Nên đối chiếu việc dùng nhiều tài khoản với điều khoản của gói Claude bạn đang dùng.
