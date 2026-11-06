# Claude Switcher

[English](README.md) | **Tiếng Việt**

Ứng dụng Windows để chạy nhiều cửa sổ **Claude Desktop** cùng lúc, mỗi cửa sổ đăng nhập một tài khoản riêng.

- **Profile:** mỗi profile có thư mục dữ liệu riêng, nên đăng nhập, cài đặt và session đều tách biệt. Profile "Mặc định" là Claude Desktop bạn đang dùng, dữ liệu gốc giữ nguyên.
- **Usage thời gian thực:** hiển thị usage 5 giờ, tuần (mọi model) và tuần theo từng model, kèm giờ reset của từng tài khoản. Số liệu lấy giống hệt cách Claude Desktop lấy: gọi `api.anthropic.com/api/oauth/usage` bằng token của chính profile đó, không đọc file lịch sử. Khi usage 5 giờ vượt ngưỡng, app hiện cảnh báo và gợi ý profile còn nhiều usage nhất.
- **Chuyển session Code** sang profile khác để làm tiếp khi hết usage. Lịch sử hội thoại được giữ nguyên.
- **MCP dùng chung:** chọn MCP server nào dùng cho profile nào. Connector từ xa có thể thêm bằng URL.
- **Sao lưu và khôi phục:** lưu cài đặt Claude, session Code, MCP, skills, plugins và các profile vào một file `.zip`, rồi khôi phục sau khi cài lại Claude.
- **Khởi động cùng Windows:** app chạy ở khay hệ thống và tự mở các profile đã chọn.
- **Giao diện sáng/tối:** chọn Sáng, Tối hoặc Theo Windows trong tab Cài đặt.
- **Claude Remote (điện thoại):** dùng Claude Code trên PC này từ điện thoại qua Tailscale, bằng tài khoản của bất kỳ profile nào: web app hiển thị session như tab Code của Desktop, có usage, duyệt quyền, đính kèm và chọn model. Bật ở **Settings → Claude Remote**; cách cài trong [remote/README.md](remote/README.md).
- **Tiếng Anh / tiếng Việt:** app mặc định dùng tiếng Anh. Đổi sang tiếng Việt ở **Settings → Language → Tiếng Việt**; giao diện đổi ngay, không cần khởi động lại.

## Cài đặt

Yêu cầu: Windows 10/11 và Claude Desktop đã cài sẵn.

1. Tải `ClaudeSwitcher.exe` từ trang [Releases](https://github.com/Colb98/claude-switcher/releases/latest), hoặc tự build ra `dist\ClaudeSwitcher.exe` (xem [Build](#build)).
2. Chép file exe vào một thư mục cố định (ví dụ `C:\Tools\ClaudeSwitcher\`) rồi chạy. Không cần cài .NET.

> Nên chọn thư mục cố định **trước** khi bật "Khởi động cùng Windows", vì Windows lưu đường dẫn exe lúc bật.

## Bắt đầu nhanh

1. Mở `ClaudeSwitcher.exe`. Lần đầu app hiện tiếng Anh: vào tab **Settings**, chọn **Language → Tiếng Việt**. Tab **Profile** đã có sẵn profile "Mặc định", chính là Claude Desktop bạn đang dùng.
2. Bấm **+ Profile mới**, đặt tên (ví dụ "Công việc") và chọn màu, rồi bấm **Tạo**.
3. Khi app hỏi, bấm **Yes** để mở profile mới. Một cửa sổ Claude Desktop riêng hiện ra; đăng nhập tài khoản thứ hai trong cửa sổ đó.
   - Nếu đăng nhập bằng link trong email, bật trước tuỳ chọn "Mở link đăng nhập đúng profile" (bước 5 trong [Cách dùng chi tiết](#cách-dùng-chi-tiết)).
4. Sau vài giây, thẻ profile hiện email, gói và thanh usage 5 giờ / tuần của tài khoản đó.
5. Từ giờ, mở profile nhanh nhất là **click trái icon Claude Switcher ở khay hệ thống** (góc phải taskbar; nếu không thấy, bấm mũi tên ^ cạnh đồng hồ). Một bảng nhỏ hiện danh sách profile kèm % usage 5 giờ; bấm một lần vào profile để mở nó, hoặc đưa cửa sổ của nó lên trước nếu đang chạy.
6. Muốn app luôn sẵn sàng: vào tab **Cài đặt**, bật "Khởi động cùng Windows".

## Cách dùng chi tiết

1. **+ Profile mới:** đặt tên và chọn màu, sau đó bấm **Mở** rồi đăng nhập tài khoản Claude trong cửa sổ vừa hiện ra.
2. Mỗi profile là một cửa sổ Claude Desktop riêng. Có thể mở nhiều profile cùng lúc. Nút **⋯** trên thẻ profile có thêm: mở thư mục dữ liệu, tạo shortcut trên Desktop, xoá profile.
3. **Chuyển session:**
   - Chọn profile nguồn và tick các session cần chuyển.
   - Chọn profile đích (đã đăng nhập ít nhất một lần), rồi bấm **Chuyển**.
   - "Chuyển hẳn" (mặc định bật) gỡ session khỏi profile nguồn, để không mở cùng một session ở hai cửa sổ (hai tiến trình sẽ cùng ghi vào một lịch sử). Bản gốc được giữ trong `backups\`.
   - Claude Desktop đang mở sẽ ghi đè thay đổi, nên app hỏi trước khi tắt các profile liên quan, rồi đề nghị mở profile đích khi chép xong.
4. **MCP dùng chung:** tick profile cho từng server rồi bấm **Áp dụng**. Chỉ profile có thay đổi mới bị đụng tới. Nếu profile đó đang chạy, app tắt nó, ghi cấu hình rồi mở lại, vì Claude Desktop đang mở sẽ ghi đè file cấu hình. MCP khai báo trong `~\.claude.json` (dùng cho session Code) vốn đã dùng chung cho mọi profile.
5. **Cài đặt:** bật "Khởi động cùng Windows". Muốn một profile tự mở khi bật máy, bấm **Sửa** trên profile đó và tick "Tự mở khi khởi động".
   - **Đưa đúng profile lên trước khi bấm notification** (bật sẵn): mọi profile dùng chung một danh tính ứng dụng trên Windows, nên khi bấm notification của Claude, profile Mặc định được focus còn profile sở hữu notification mở chat ở phía sau. Bật tuỳ chọn này thì Claude Switcher đưa profile sở hữu lên trước. Tính năng nhận ra profile sở hữu qua phản ứng của nó với cú bấm (nút taskbar chớp hoặc một dòng trong log) và chỉ có tác dụng với notification đang bật lên; mở lại từ Notification Center vẫn vào profile Mặc định. Sau khi bấm notification, Claude không hiện nội dung mới cho đến khi cửa sổ được click, nên Claude Switcher gửi thêm một cú click vào lề trống bên trái khung chat mà không di chuyển con trỏ chuột. Vì vậy ô nhập có thể mất con trỏ.
   - **Mở link đăng nhập đúng profile:** Windows luôn mở link `claude://` (ví dụ link đăng nhập trong email) bằng profile Mặc định. Khi đăng nhập cho profile khác, cửa sổ Mặc định sẽ bị kẹt ở màn hình "Use verification code". Bật tuỳ chọn này thì Claude Switcher nhận link và chuyển link đăng nhập tới profile đang mở mà chưa đăng nhập; các link khác vẫn mở ở profile Mặc định. Windows không cho ứng dụng tự đặt mình làm trình mở link mặc định, nên khi bật, app mở sẵn trang Claude Switcher trong Settings → Default apps; bấm dòng CLAUDE và chọn Claude Switcher.
6. **Khay hệ thống:** đóng cửa sổ chỉ thu app xuống khay.
   - **Click trái** icon: bảng chọn nhanh, bấm một lần để mở profile. Nút **Mở Claude Switcher** ở cuối bảng mở cửa sổ chính. Bấm ra ngoài hoặc nhấn Esc để đóng bảng.
   - **Chuột phải** icon: menu đầy đủ (mở/tắt từng profile, làm mới usage, **Thoát** để tắt hẳn app).
7. **Sao lưu:** xem [Sao lưu và khôi phục](#sao-lưu-và-khôi-phục).

### Sao lưu và khôi phục

Trong tab **Sao lưu**, **Tạo bản sao lưu...** lưu một file `.zip` rồi đọc lại để kiểm tra. Hãy để file này ngoài các thư mục Claude dùng và giữ kèm file exe. Để khôi phục: cài Claude, mở một lần rồi đóng, sau đó bấm **Khôi phục...**. Các file bị thay thế được chuyển vào `backups\restore-<thời gian>\`, và Claude đang đăng nhập vẫn giữ nguyên đăng nhập.

- **Được lưu:** `~\.claude` (lịch sử session, memory, skills, plugins, cài đặt), MCP trong `~\.claude.json`, và với mỗi profile là `claude_desktop_config.json`, file session Code và Cowork cùng cài đặt app. Skill nối bằng junction được lưu thành thư mục thật.
- **Cố ý không lưu:** thông tin đăng nhập (`.credentials.json`, token trong `config.json`, cookie và dữ liệu trình duyệt). Sau khi khôi phục, hãy đăng nhập lại từng profile và các MCP dùng OAuth. Session Code được liệt kê theo tài khoản, nên hãy dùng đúng tài khoản cũ để thấy chúng trong Desktop.
- **Không lưu vì Claude tự tạo lại:** cache, log và runtime `claude-code` đã tải.
- **Ngoài phạm vi:** chat thường do server claude.ai giữ nên không có trong zip. Các chương trình mà MCP chạy (`npx`, `uvx`, những gì cài ở nơi khác) và thư mục `.claude` trong project của bạn phải tự cài lại hoặc tự giữ.
- **Giữ file zip riêng tư:** file không có mật khẩu, và chứa key ghi trong cài đặt MCP (`env`) cùng mọi nội dung trong session.
- **User Windows khác:** đường dẫn trong các file cấu hình chính được đổi theo thư mục home mới. Lịch sử session của project nằm trong home cũ vẫn giữ đường dẫn cũ. Chỉ khôi phục bản sao lưu do chính bạn tạo, vì bản sao lưu có thể đặt hook và lệnh MCP mà Claude sẽ chạy.

### Dòng lệnh

| Lệnh | Tác dụng |
|---|---|
| `ClaudeSwitcher.exe --launch <id>` | Mở một profile (dùng cho shortcut; nút "⋯ → Tạo shortcut" tạo sẵn) |
| `ClaudeSwitcher.exe --stop <id>` | Tắt một profile |
| `ClaudeSwitcher.exe --selftest report.txt` | Kiểm tra: tìm Claude, đọc token, usage, session, MCP (không ghi token ra file) |
| `ClaudeSwitcher.exe --open-url <link>` | Mở một link `claude://` ở đúng profile (Windows tự gọi khi bật "Mở link đăng nhập đúng profile") |
| `ClaudeSwitcher.exe --backup <file.zip>` | Tạo bản sao lưu; kết quả ghi vào `<file.zip>.report.txt`. Hãy đóng Claude trước, vì lệnh này không tự đóng profile |
| `ClaudeSwitcher.exe --restore <file.zip> [--to <thư mục>]` | Khôi phục bản sao lưu, từ chối khi còn profile đang mở; `--to` giải nén vào thư mục thử thay vì dữ liệu thật của Claude |
| `ClaudeSwitcher.exe --startup` | Chế độ khởi động cùng Windows |
| `ClaudeSwitcher.exe --screenshot ui.png [--theme dark] [--lang vi]` | Render các tab ra ảnh PNG để kiểm tra giao diện, không chiếm focus |
| `ClaudeSwitcher.exe --api profiles|usage [id]|token <id>|launch <id>|stop <id>` | In JSON ra stdout cho Claude Remote (chỉ có stdout khi tiến trình gọi chuyển hướng nó). `token` in token đăng nhập của profile: chỉ cùng user Windows mới gọi được, giống file token gốc |

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
