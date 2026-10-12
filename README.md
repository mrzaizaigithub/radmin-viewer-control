# radmin-viewer-control

`RadminAuto.exe` là một ứng dụng bọc Radmin Viewer: gọi từ cmd, truyền **IP, user, mật khẩu**, nó sẽ mở Radmin Viewer và **tự đăng nhập**.

Radmin Viewer không có tham số dòng lệnh cho user/mật khẩu (chỉ có `/connect:ip:port`). Vì vậy RadminAuto làm như sau:

1. Chạy `Radmin.exe /connect:<ip>:<port>` (kèm `/fullscreen`, `/noinput`... nếu có yêu cầu).
2. Chờ hộp thoại đăng nhập của Radmin hiện ra. Hộp thoại được nhận diện là cửa sổ của tiến trình `Radmin.exe` có ô mật khẩu.
3. Ghi user/mật khẩu vào các ô (dùng `WM_SETTEXT`) rồi bấm OK. Nếu cách bấm trước không làm hộp thoại đóng thì thử cách tiếp theo: click nút OK (`BM_CLICK`), gửi lệnh OK cho hộp thoại, gửi Enter vào ô mật khẩu, cuối cùng đưa hộp thoại lên trước và nhấn Enter thật.
4. Kiểm tra hộp thoại đã đóng và Radmin không hỏi lại. Nếu hỏi lại thì coi như sai mật khẩu.

Ứng dụng không phụ thuộc ngôn ngữ giao diện của Radmin và không cần cửa sổ ở trạng thái active, vì nó không giả lập gõ phím.

## Cách dùng

```bat
:: Cách ngắn: ip[:port] user password
RadminAuto.exe 192.168.1.10 admin MatKhau123

:: Port khác + toàn màn hình
RadminAuto.exe 192.168.1.10:5000 admin MatKhau123 --fullscreen

:: Dạng tham số có tên
RadminAuto.exe --host 192.168.1.10 --port 4899 --user admin --pass MatKhau123 --viewonly -v

:: Server chỉ dùng mật khẩu (Radmin security, không có user)
RadminAuto.exe --host 192.168.1.10 --pass MatKhau123

:: Không để lộ mật khẩu trên dòng lệnh: đọc từ biến môi trường
set RADMIN_PW=MatKhau123
RadminAuto.exe --host 192.168.1.10 --user admin --pass-env RADMIN_PW
```

| Tham số | Ý nghĩa |
|---|---|
| `--host`, `--port` | IP/tên máy và port của Radmin Server (mặc định 4899) |
| `-u`, `--user` | Tên đăng nhập |
| `-p`, `--pass` | Mật khẩu |
| `--pass-env VAR` | Lấy mật khẩu từ biến môi trường `VAR` |
| `--domain` | Domain (nếu đăng nhập kiểu Windows security có ô domain) |
| `--through h[:p]` | Kết nối qua một Radmin Server trung gian |
| `--fullscreen` | Mở toàn màn hình |
| `--viewonly` | Chỉ xem, không điều khiển |
| `--radmin PATH` | Đường dẫn `Radmin.exe` (hoặc đặt biến `RADMIN_PATH`) |
| `--timeout N` | Số giây chờ hộp thoại đăng nhập (mặc định 30) |
| `--no-submit` | Chỉ điền, không bấm OK (để kiểm tra) |
| `-v` | In chi tiết quá trình |
| `-- ...` | Mọi thứ sau `--` được truyền thẳng cho `Radmin.exe` |

Nếu không có `--radmin`, chương trình tìm `Radmin.exe` lần lượt ở: biến `RADMIN_PATH`, cùng thư mục với `RadminAuto.exe`, rồi `C:\Program Files (x86)\Radmin Viewer 3\` và `C:\Program Files\Radmin Viewer 3\`.

### Mã thoát (để ứng dụng khác gọi và kiểm tra)

| Mã | Ý nghĩa |
|---|---|
| 0 | Đăng nhập thành công |
| 1 | Sai tham số |
| 2 | Không tìm thấy `Radmin.exe` |
| 3 | Không thấy hộp thoại đăng nhập (máy không kết nối được, hoặc Radmin đã lưu sẵn mật khẩu) |
| 4 | Đăng nhập bị từ chối / hộp thoại vẫn mở |
| 5 | Không ghi được vào hộp thoại |

### Gọi từ chương trình khác

```bat
RadminAuto.exe %IP% %USER% %PASS%
if errorlevel 1 echo Ket noi that bai, ma loi %errorlevel%
```

```python
import subprocess
r = subprocess.run(["RadminAuto.exe", "192.168.1.10", "admin", "MatKhau123"])
print("OK" if r.returncode == 0 else f"Loi {r.returncode}")
```

## Build

**Cách 1, trên Windows** (không cần cài thêm gì, dùng `csc.exe` có sẵn của .NET Framework 4.x):

```bat
build.bat
```

File được tạo ra là `bin\RadminAuto.exe`.

**Cách 2, GitHub Actions:** mỗi lần push, workflow `build` sẽ build trên Windows. Bạn tải `RadminAuto.exe` ở mục *Artifacts* của lần chạy đó.

## Lưu ý

- `--no-submit` chỉ điền, **không** bấm OK. Muốn tự đăng nhập thì bỏ cờ này.
- Khi có lỗi, chạy với `-v`. Chương trình sẽ in danh sách control trong hộp thoại và từng cách bấm OK đã thử. Gửi phần log này để chỉnh cho đúng bản Radmin của bạn.

- Nếu Radmin Viewer chạy bằng quyền Administrator thì `RadminAuto.exe` cũng phải chạy bằng quyền Administrator. Windows chặn tiến trình quyền thấp gửi thông điệp vào cửa sổ quyền cao.
- Mật khẩu truyền qua dòng lệnh có thể bị tiến trình khác trên máy đọc được. Nên dùng `--pass-env` hoặc tự lấy mật khẩu từ nơi lưu an toàn rồi mới gọi.
- Nếu đã tick "lưu mật khẩu" trong Radmin cho máy đó, Radmin có thể không hiện hộp thoại nữa. Khi ấy RadminAuto sẽ trả về mã 3 dù Radmin vẫn kết nối bình thường.
