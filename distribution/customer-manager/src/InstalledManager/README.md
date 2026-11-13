# Customer Manager cài tại chỗ

Project này được đối chiếu với mã nguồn của bản đang cài tại `D:\Acose\CustomerManager\Source` trước khi nâng từ 1.1.1 lên 1.1.2. Project `../Manager` là bản đóng gói có cách lưu cấu hình khác; dùng project này để bảo trì bản cài tại chỗ.

## Build

Tại gốc repo:

```powershell
dotnet publish distribution/customer-manager/src/InstalledManager/Acose.CustomerManager.csproj -c Release -o artifacts/activity-skips-manager
dotnet build src/AutomationPlatform.Presentation/AutomationPlatform.Presentation.csproj -c Release -r win-x64
node --test tools/tests/activity-skips.test.cjs
```

Manager 1.1.2 có nút **Bỏ qua dạng bài** trên thanh công cụ. Tùy chọn PDF và đánh giá chéo được lưu riêng trong `Data/activity-skips.json`, áp dụng cho khóa mở sau khi lưu, kể cả khóa đang xếp hàng. Với worker đang chạy, dừng rồi chạy lại để đổi lựa chọn. Khóa mở kiểm tra bằng tay vẫn dùng để duyệt bình thường.

## Kiểm tra

Các lệnh self-test dùng đường dẫn báo cáo mới, chưa tồn tại:

```powershell
artifacts/activity-skips-manager/Acose.CustomerManager.exe --activity-skips-self-test artifacts/skips-test.json
artifacts/activity-skips-manager/Acose.CustomerManager.exe --website-import-self-test artifacts/import-test.json
dotnet run --project tools/ActivitySkipsChecks/ActivitySkipsChecks.csproj -- D:/Acose/CustomerManager/Engine
```

Self-test dùng fixture trong thư mục tạm; kiểm tra Engine chỉ khởi tạo đối tượng WPF, không mở trình duyệt hay gửi bài lên Coursera.

`tools/Deploy-InstalledActivitySkips.ps1` cập nhật đúng bản cài đã đối chiếu hash. Script yêu cầu Manager/Worker đóng và Worker Host dừng, sao lưu file chương trình, đối chiếu dữ liệu trước/sau và khôi phục khi có lỗi. Hash đầu vào được ghim cho bản trước 1.1.2; các lần phát hành tiếp theo cần kiểm tra và cập nhật baseline.
