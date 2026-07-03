[English](https://github.com/AJ-comp/Qora/blob/main/vscode/README.md) · [한국어](https://github.com/AJ-comp/Qora/blob/main/vscode/README.ko.md) · [日本語](https://github.com/AJ-comp/Qora/blob/main/vscode/README.ja.md) · **Tiếng Việt**

# Qora Language

**Qora** là một ngôn ngữ lượng tử đồ chơi nhỏ, xây trên bộ máy phân tích cú pháp
[Janglim](https://www.nuget.org/packages/Janglim). Bạn viết mạch bằng cú pháp kiểu Q# / C# và nó chuyển
đổi sang **OpenQASM 3**. Tiện ích này cung cấp cho tệp `.qor` khả năng tô sáng cú pháp, chú thích khi di
chuột, **báo lỗi thời gian thực** và một lệnh **chuyển đổi**.

## Tính năng

- **Tô sáng cú pháp** — từ khóa (`operation`/`use`/`if`/`for`/…), kiểu (`Qubit`/`int`/`bit`), cổng (`H`/`CNOT`/`Rx`/…), `pi`, số, toán tử
- **Chú thích khi di chuột** — di chuột lên một cổng hoặc từ khóa để xem mô tả ngắn (ví dụ `Rx`, `CNOT`, `M`)
- **Báo lỗi thời gian thực** — bộ phân tích chạy khi bạn gõ và gạch chân token gây lỗi
- **Phản hồi trên status bar** — tệp `.qor` đang mở hiển thị `Qora: OK`, số lỗi hoặc trạng thái parser
- **CodeLens cho Main** — phía trên `operation Main()` có lối tắt để chuyển sang OpenQASM và xem các giai đoạn biên dịch
- **Hướng dẫn bắt đầu** — theo dõi luồng Qora đầu tiên từ trang Getting Started của VS Code
- **Nút trên thanh tiêu đề editor** — khi mở tệp `.qor`, bạn có thể chuyển đổi, xem giai đoạn hoặc mở ví dụ ngay
- **Ví dụ dùng ngay** — chạy **`Qora: Mở ví dụ`** để mở `demo.qor`, hoặc **`Qora: Tạo ví dụ Bell mới`** để bắt đầu từ một mạch Bell nhỏ
- **Chạy chương trình** — **`Qora: Chạy chương trình`** (hoặc CodeLens ▶ phía trên `Main`) thực thi tệp trên trình mô phỏng lượng tử thực (Amazon Braket cục bộ) và hiển thị biểu đồ kết quả đo. Lần chạy đầu tiên sẽ tự động thiết lập trình mô phỏng (tải một lần ~200 MB vào bộ nhớ riêng của tiện ích — bạn không cần cài gì cả).
- **Chuyển đổi sang OpenQASM** — chạy **`Qora: Transpile to OpenQASM`** từ Command Palette; kết quả mở ở cửa sổ bên cạnh
- **Hiển thị các giai đoạn biên dịch** — chạy **`Qora: Hiển thị các giai đoạn biên dịch`** để xem pipeline: AST → QoraIR → IR nghịch đảo (khi dùng `Adjoint`) → OpenQASM; tự cập nhật khi lưu
- **Đoạn mã mẫu (snippets)** — `operation`, `main`, `use`, `measure`, `for`, `if`, `bell`
- **Khớp / tự đóng dấu ngoặc**

## Bộ phân tích được đóng gói sẵn

Báo lỗi và chuyển đổi do bộ phân tích Qora (.NET) đảm nhiệm, và nó được **đóng gói ngay trong tiện ích
dưới dạng nhị phân self-contained** — không cần cài .NET riêng. Các bản dựng theo nền tảng đã có sẵn
(Windows x64 / macOS Apple Silicon / Linux x64) và tiện ích tự động chạy bản phù hợp.

> Trên nền tảng chưa hỗ trợ, tô sáng / chú thích / snippet vẫn hoạt động, chỉ tắt báo lỗi / chuyển đổi.
> Khi đó bạn có thể trỏ `qora.command` (một tệp thực thi Qora) hoặc `qora.args` (+ `dotnet` với một `Qora.dll`) tới bản dựng của mình.

## Cài đặt

| Cài đặt | Mặc định | Mô tả |
|---|---|---|
| `qora.command` | *(trống → dùng bộ phân tích đóng gói sẵn)* | Ghi đè bằng một tệp thực thi Qora (nâng cao) |
| `qora.args` | `[]` | Đối số thêm cho `qora.command` (ví dụ đường dẫn `Qora.dll`) |

## Giấy phép

MIT — mã nguồn tại [github.com/AJ-comp/Qora](https://github.com/AJ-comp/Qora).
