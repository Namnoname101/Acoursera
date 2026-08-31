# ACOSE Order Entry

Trang Fly độc lập chỉ dành cho việc tạo và theo dõi đơn trực tiếp. Trang chuyển tiếp một
tập API quản trị giới hạn tới ACOSE Control Server; nó không chứa sẵn Admin API key và
không lưu tài khoản hoặc mật khẩu Google.

## Chạy cục bộ

```powershell
npm start
```

Mặc định trang kết nối tới `https://coursera-cookie-srv.fly.dev`. Có thể đặt
`CONTROL_SERVER_URL` sang một máy chủ thử nghiệm khi chạy test tích hợp.
