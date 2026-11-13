# ACOSE Automation Platform

Ứng dụng Windows giúp quản lý phiên Coursera và tự động điều hướng các hoạt động trong khóa học. Repo cũng có một ứng dụng WPF riêng để quản lý profile/worker và trang web Order Entry phục vụ tạo đơn.

> Đây là phần mềm tự động hóa không chính thức, không liên kết hay được Coursera chứng thực. Hãy chỉ sử dụng với tài khoản của bạn và tuân thủ điều khoản, quy định của khóa học cũng như pháp luật áp dụng. Người dùng chịu trách nhiệm kiểm tra nội dung và kết quả trước khi gửi.

## Thành phần

| Thành phần | Thư mục | Mục đích |
| --- | --- | --- |
| Automation Worker | `src/AutomationPlatform.Presentation` | Ứng dụng WPF chứa WebView2, đăng nhập/duyệt Coursera và xử lý luồng tự động hóa. Có thể chạy độc lập hoặc khởi chạy ở chế độ worker có điều phối. |
| Account Manager | `src/AutomationPlatform.AccountManager` | Ứng dụng WPF quản lý profile trên máy, theo dõi worker, xếp hàng và giới hạn số worker chạy đồng thời. Đây là project độc lập, hiện chưa được thêm vào `src/AutomationPlatform.sln`. |
| Customer Manager đang cài | `distribution/customer-manager/src/InstalledManager` | Mã nguồn tương ứng ứng dụng `D:\Acose\CustomerManager\Acose.CustomerManager.exe`. Có nút **Bỏ qua dạng bài** trên thanh công cụ; lựa chọn được lưu để dùng cho các khóa mở tiếp theo. |
| Core projects | `src/AutomationPlatform.Domain`, `Application`, `Infrastructure` | Mô hình nghiệp vụ, luồng ứng dụng, lưu trữ và tích hợp trình duyệt. |
| Order Entry | `tools/OrderSite` | Trang Fly độc lập để tạo/theo dõi đơn qua ACOSE Control Server. Xem [hướng dẫn riêng](tools/OrderSite/README.md). |

## Tính năng chính

- Duyệt nội dung khóa học và điều phối tiến độ qua giao diện Coursera.
- Hỗ trợ các luồng quiz, phản hồi quiz, thảo luận và hoạt động peer review/PDF có trong mã nguồn.
- Có cơ chế lưu hàng đợi, checkpoint và khôi phục cho một số luồng tự động hóa.
- Dùng AgentRouter làm nhà cung cấp AI trong các luồng cần AI; model có thể cấu hình qua biến môi trường.
- Chạy nhiều profile Coursera trên máy qua Account Manager; profile tách biệt theo thư mục.
- Chọn bỏ qua bài nộp PDF/Peer-graded và đánh giá chéo độc lập trên thanh **Bỏ qua dạng bài**, rồi bấm **Áp dụng bỏ qua**. Tùy chọn áp dụng cho cửa sổ hiện tại; nếu đang ở dạng đã chọn, ứng dụng quay về danh sách để tìm bài khác. Bỏ chọn và áp dụng lại để xét các bài đó trong lần quét tiếp theo. Bài bỏ qua vẫn chưa hoàn thành trên Coursera.
- Worker có thể nhận job từ dịch vụ điều phối qua HTTP/HTTPS. Dịch vụ điều phối không được cung cấp trong repo này.

Tính năng và khả năng tương thích có thể thay đổi khi Coursera cập nhật giao diện. Tự động hóa có thể dừng để yêu cầu người dùng xử lý bước cần phán đoán hoặc xác nhận.

## Yêu cầu

- Windows 10/11 x64.
- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) để build từ mã nguồn.
- Microsoft Edge WebView2 Runtime để chạy ứng dụng WPF.
- Kết nối Internet và tài khoản Coursera hợp lệ.
- Với chức năng AI: khóa AgentRouter, model được cấp quyền và Codex executable tương thích với cấu hình ứng dụng.

Account Manager cũng tham chiếu `System.Management` để đọc thông tin tiến trình trên Windows.

## Bắt đầu nhanh

### Build và chạy Automation Worker

Mở PowerShell tại thư mục repo:

```powershell
dotnet restore .\src\AutomationPlatform.Presentation\AutomationPlatform.Presentation.csproj
dotnet run --project .\src\AutomationPlatform.Presentation\AutomationPlatform.Presentation.csproj
```

Ứng dụng mở cửa sổ có trình duyệt nhúng. Đăng nhập vào tài khoản Coursera của bạn, điều hướng đến khóa học cần dùng và bắt đầu từ giao diện. Chỉ bật luồng tự động khi đã cấu hình đầy đủ các dịch vụ mà luồng đó cần.

### Build và chạy Account Manager

```powershell
dotnet restore .\src\AutomationPlatform.AccountManager\AutomationPlatform.AccountManager.csproj
dotnet run --project .\src\AutomationPlatform.AccountManager\AutomationPlatform.AccountManager.csproj
```

Trong Thiết lập, chọn file `AutomationPlatform.Presentation.exe` làm Worker executable, đặt URL dịch vụ điều phối và thư mục profile phù hợp. Mặc định ứng dụng tìm thư mục profile trong `%LOCALAPPDATA%\Acose\WorkerProfiles`; có thể đặt `ACOSE_WORKER_PROFILE_ROOT`, `ACOSE_WORKER_EXECUTABLE`, `ACOSE_WORKER_SERVER_URL` và `ACOSE_WORKER_MAX_COURSE_WORKERS` trước khi mở ứng dụng.

> Chế độ worker cần dịch vụ điều phối tương thích và biến môi trường `ACOSE_WORKER_KEY`. Khi khởi chạy qua Account Manager, các tham số worker do ứng dụng truyền vào. Không dùng URL HTTP công khai cho thông tin đăng nhập; direct-login chỉ cho phép HTTPS, ngoại trừ loopback.

### Cấu hình AI

Automation Worker đọc cấu hình từ biến môi trường của tiến trình, tài khoản Windows hoặc máy:

| Biến | Ý nghĩa | Mặc định |
| --- | --- | --- |
| `AGENTROUTER_API_KEY` | Khóa truy cập AgentRouter; bắt buộc với luồng gọi AI. | Không có |
| `AGENTROUTER_MODEL` | Model được gửi qua AgentRouter. | `deepseek-v4-flash` |
| `AGENTROUTER_CODEX_PATH` | Đường dẫn Codex executable nếu không dùng vị trí mặc định được ứng dụng dò tìm. | Tự dò tìm |

Ví dụ đặt biến cho cửa sổ PowerShell hiện tại:

```powershell
$env:AGENTROUTER_API_KEY = "<your-api-key>"
$env:AGENTROUTER_MODEL = "deepseek-v4-flash"
dotnet run --project .\src\AutomationPlatform.Presentation\AutomationPlatform.Presentation.csproj
```

Không commit API key, cookie, hồ sơ trình duyệt hay dữ liệu tài khoản vào Git. Nội dung được gửi đến AgentRouter/model khi dùng tính năng AI; hãy xem xét dữ liệu trước khi gửi.

## Cấu hình và dữ liệu cục bộ

Với **Customer Manager đang cài**, bấm **Bỏ qua dạng bài** → chọn **PDF/Peer-graded** và/hoặc **đánh giá chéo** → **Lưu lựa chọn**. Thiết lập nằm trong `Data/activity-skips.json` bên cạnh chương trình. Dừng rồi chạy lại các khóa đang mở để dùng thiết lập mới. Hai biến `ACOSE_SKIP_PEER_PDF` và `ACOSE_SKIP_PEER_REVIEW` được Manager đặt riêng cho tiến trình Engine (`1` là bỏ qua, `0` là xử lý).

- Thiết lập WebView và database mặc định nằm trong `src/AutomationPlatform.Presentation/appsettings.json`; database mặc định là `automation.db` theo working directory.
- Account Manager ghi thiết lập trong `%LOCALAPPDATA%\Acose\AccountManager\settings.json`.
- Account Manager đặt các profile do mình quản lý trong thư mục profile đã cấu hình, tách thành `_ManagedAccounts` và `_CourseJobs`.
- Thư mục profile có thể chứa cookie đăng nhập và dữ liệu nhạy cảm. Không chia sẻ, đồng bộ công khai hoặc xóa khi worker còn chạy.

## Cấu trúc mã nguồn

```text
src/
  AutomationPlatform.sln
  AutomationPlatform.Domain/
  AutomationPlatform.Application/
  AutomationPlatform.Infrastructure/
  AutomationPlatform.Presentation/
  AutomationPlatform.AccountManager/   # project độc lập
tools/
  OrderSite/                           # ứng dụng web độc lập
Directory.Build.props                 # target .NET và thiết lập WPF dùng chung
Directory.Packages.props              # phiên bản package NuGet tập trung
```

## Phát triển

- Mở `src/AutomationPlatform.sln` bằng bản Visual Studio hỗ trợ .NET 10 hoặc build riêng project Presentation bằng `dotnet build`.
- Account Manager hiện không nằm trong solution; build riêng file `.csproj` của project này.
- Các phiên bản NuGet được khai báo tập trung trong `Directory.Packages.props`.
- Chưa có hướng dẫn kiểm thử cấp repo được duy trì trong README này. Kiểm tra luồng thật cần tài khoản, dịch vụ và môi trường phù hợp; không dùng tài khoản thật trong kiểm thử tự động nếu chưa được cho phép.

## Quyền riêng tư và bảo mật

- Coursera nhận thông tin đăng nhập, điều hướng, câu trả lời và tệp người dùng gửi lên trong quá trình sử dụng.
- AgentRouter và model được chọn có thể nhận nội dung trang hoặc bài tập cần xử lý bằng AI.
- Worker điều phối kết nối tới URL do cấu hình cung cấp; chỉ nhập khóa worker vào môi trường tin cậy.
- Không lưu bí mật trong `appsettings.json`, source code, log hoặc commit. Dùng biến môi trường hoặc kho bí mật phù hợp.
- Tách profile cho từng tài khoản và bảo vệ quyền truy cập thư mục profile bằng tài khoản Windows.

## Giới hạn

- Đây là ứng dụng Windows/WPF, không phải ứng dụng web đa nền tảng.
- Trang Coursera và dịch vụ AI/điều phối là dịch vụ bên ngoài; tính khả dụng, giới hạn và điều khoản của chúng nằm ngoài repo.
- Repo không bao gồm dịch vụ điều phối worker hay tài khoản/mẫu dữ liệu dùng chung.
- Không đảm bảo kết quả học tập, điểm số, tính đúng đắn của nội dung AI hoặc hoạt động liên tục không cần giám sát.

## License

Repo hiện chưa khai báo file `LICENSE`. Liên hệ chủ sở hữu repo để xác nhận quyền sử dụng, sửa đổi và phân phối trước khi dùng lại hoặc phát hành bản dẫn xuất.
