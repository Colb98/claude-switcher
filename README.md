# Claude Switcher

Ứng dụng Windows để chạy nhiều cửa sổ **Claude Desktop** cùng lúc, mỗi cửa sổ đăng nhập một tài khoản riêng.

- **Profile:** mỗi profile có thư mục dữ liệu riêng, nên đăng nhập, cài đặt và session đều tách biệt. Profile "Mặc định" là Claude Desktop bạn đang dùng, dữ liệu gốc giữ nguyên.
- **Usage thời gian thực:** hiển thị usage 5 giờ, tuần (mọi model) và tuần theo từng model, kèm giờ reset của từng tài khoản. Số liệu lấy giống hệt cách Claude Desktop lấy: gọi `api.anthropic.com/api/oauth/usage` bằng token của chính profile đó, không đọc file lịch sử. Khi usage 5 giờ vượt ngưỡng, app hiện cảnh báo và gợi ý profile còn nhiều usage nhất.
- **Chuyển session Code** sang profile khác để làm tiếp khi hết usage. Lịch sử hội thoại được giữ nguyên.
- **MCP dùng chung:** chọn MCP server nào dùng cho profile nào. Connector từ xa có thể thêm bằng URL.
- **Khởi động cùng Windows:** app chạy ở khay hệ thống và tự mở các profile đã chọn.
- **Giao diện sáng/tối:** chọn Sáng, Tối hoặc Theo Windows trong tab Cài đặt.

## Cài đặt

Chép `dist\ClaudeSwitcher.exe` vào một thư mục cố định (ví dụ `C:\Tools\ClaudeSwitcher\`) rồi chạy. Không cần cài .NET.

> Nên chọn thư mục cố định **trước** khi bật "Khởi động cùng Windows", vì Windows lưu đường dẫn exe lúc bật.

## Cách dùng

1. **+ Profile mới:** đặt tên và chọn màu, sau đó bấm **Mở** rồi đăng nhập tài khoản Claude trong cửa sổ vừa hiện ra.
2. Mỗi profile là một cửa sổ Claude Desktop riêng. Có thể mở nhiều profile cùng lúc.
3. **Chuyển session:**
   - Chọn profile nguồn và tick các session cần chuyển.
   - Chọn profile đích (đã đăng nhập ít nhất một lần), rồi bấm **Chuyển**.
   - "Chuyển hẳn" (mặc định bật) gỡ session khỏi profile nguồn, để không mở cùng một session ở hai cửa sổ (hai tiến trình sẽ cùng ghi vào một lịch sử). Bản gốc được giữ trong `backups\`.
   - Claude Desktop đang mở sẽ ghi đè thay đổi, nên app hỏi trước khi tắt các profile liên quan, rồi đề nghị mở profile đích khi chép xong.
4. **MCP dùng chung:** tick profile cho từng server rồi bấm **Áp dụng**. Chỉ profile có thay đổi mới bị đụng tới. Nếu profile đó đang chạy, app tắt nó, ghi cấu hình rồi mở lại, vì Claude Desktop đang mở sẽ ghi đè file cấu hình. MCP khai báo trong `~\.claude.json` (dùng cho session Code) vốn đã dùng chung cho mọi profile.
5. **Cài đặt:** bật "Khởi động cùng Windows". Muốn một profile tự mở khi bật máy, bấm **Sửa** trên profile đó và tick "Tự mở khi khởi động".
   - **Mở link đăng nhập đúng profile:** Windows luôn mở link `claude://` (ví dụ link đăng nhập trong email) bằng profile Mặc định. Khi đăng nhập cho profile khác, cửa sổ Mặc định sẽ bị kẹt ở màn hình "Use verification code". Bật tuỳ chọn này thì Claude Switcher nhận link và chuyển link đăng nhập tới profile đang mở mà chưa đăng nhập; các link khác vẫn mở ở profile Mặc định. Windows không cho ứng dụng tự đặt mình làm trình mở link mặc định, nên khi bật, app mở sẵn trang Claude Switcher trong Settings → Default apps; bấm dòng CLAUDE và chọn Claude Switcher.
6. **Khay hệ thống:** đóng cửa sổ chỉ thu app xuống khay. Muốn thoát hẳn, chuột phải icon ở khay và chọn **Thoát**.

### Dòng lệnh

| Lệnh | Tác dụng |
|---|---|
| `ClaudeSwitcher.exe --launch <id>` | Mở một profile (dùng cho shortcut; nút "⋯ → Tạo shortcut" tạo sẵn) |
| `ClaudeSwitcher.exe --stop <id>` | Tắt một profile |
| `ClaudeSwitcher.exe --selftest report.txt` | Kiểm tra: tìm Claude, đọc token, usage, session, MCP (không ghi token ra file) |
| `ClaudeSwitcher.exe --open-url <link>` | Mở một link `claude://` ở đúng profile (Windows tự gọi khi bật "Mở link đăng nhập đúng profile") |
| `ClaudeSwitcher.exe --startup` | Chế độ khởi động cùng Windows |
| `ClaudeSwitcher.exe --screenshot ui.png [--theme dark]` | Render các tab ra ảnh PNG để kiểm tra giao diện, không chiếm focus |

## Dữ liệu

- App và các profile mới: `%USERPROFILE%\.claude-switcher\` (`settings.json`, `profiles\`, `backups\`).
  Thư mục này nằm ngoài AppData vì Claude Desktop bản MSIX ảo hoá việc ghi file vào AppData.
- Mọi thao tác ghi vào dữ liệu Claude (MCP, ghi đè session) đều được backup vào `backups\` trước.

## Giới hạn

- Chỉ **session Code** chuyển được. Chat thường và Cowork được lưu theo tài khoản trên server nên không chuyển được.
- Connector của claude.ai gắn với tài khoản. Muốn dùng chung, thêm connector bằng URL (qua `mcp-remote`).
- Token của một profile được Claude Desktop tự làm mới khi profile đó đang mở. Nếu profile tắt quá lâu, thẻ sẽ báo "Token hết hạn"; mở profile đó một lần là số liệu cập nhật lại.
- App dựa vào định dạng dữ liệu nội bộ của Claude Desktop (khảo sát trên bản 4.4212). Bản cập nhật sau có thể thay đổi định dạng này; khi đó hãy chạy `--selftest` để kiểm tra.

## Build

```powershell
tools\publish.ps1        # ra dist\ClaudeSwitcher.exe
dotnet test tests\ClaudeSwitcher.Tests
```

Kế hoạch và kết quả khảo sát kỹ thuật: [PLAN.md](PLAN.md).
