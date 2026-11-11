[English](https://github.com/AJ-comp/Qora/blob/main/vscode/README.md) · [한국어](https://github.com/AJ-comp/Qora/blob/main/vscode/README.ko.md) · [日本語](https://github.com/AJ-comp/Qora/blob/main/vscode/README.ja.md) · **Tiếng Việt**

# Qora Language

**Qora** là một ngôn ngữ lượng tử đồ chơi nhỏ, xây trên bộ máy phân tích cú pháp
[Janglim](https://www.nuget.org/packages/Janglim). Bạn viết mạch bằng cú pháp kiểu Q# / C# và nó chuyển
đổi sang **OpenQASM 3**. Tiện ích này cung cấp cho tệp `.qor` khả năng tô sáng cú pháp, chú thích khi di
chuột, **báo lỗi thời gian thực**, một lệnh **chuyển đổi** và thực thi cục bộ giữ nguyên kiểu của giá trị trả về.

Nhánh này chuẩn bị tiện ích **0.33.0** với **Qora 0.40.0**; cả hai phiên bản đều chưa được phát hành.
Xem [ghi chú phát hành tiện ích](CHANGELOG.md#0330--unreleased) và
[hướng dẫn chuyển đổi mã nguồn](https://github.com/AJ-comp/Qora/blob/main/CHANGELOG.md#0400--unreleased).

## Tính năng

- **Tô sáng cú pháp** — từ khóa (`operation`/`use`/`new`/`if`/`for`/…), kiểu (`Qubit`/`bool`/`int`/`float`/`bit`/`angle`), cổng (`H`/`CNOT`/`Rx`/…), `pi`, số, toán tử
- **Chú thích khi di chuột** — di chuột lên một cổng hoặc từ khóa để xem mô tả ngắn (ví dụ `Rx`, `CNOT`, `M`)
- **Báo lỗi thời gian thực** — bộ phân tích chạy khi bạn gõ và gạch chân token gây lỗi
- **Phản hồi trên status bar** — tệp `.qor` đang mở hiển thị `Qora: OK`, số lỗi hoặc trạng thái parser
- **CodeLens cho Main** — phía trên `operation Main()` có lối tắt để chuyển sang OpenQASM và xem các giai đoạn biên dịch
- **Hướng dẫn bắt đầu** — theo dõi luồng Qora đầu tiên từ trang Getting Started của VS Code
- **Nút trên thanh tiêu đề editor** — khi mở tệp `.qor`, bạn có thể chuyển đổi, xem giai đoạn hoặc mở ví dụ ngay
- **Ví dụ dùng ngay** — chạy **`Qora: Mở ví dụ`** để mở `demo.qor`, hoặc **`Qora: Tạo ví dụ Bell mới`** để bắt đầu từ một mạch Bell nhỏ
- **Chạy chương trình** — **`Qora: Chạy chương trình`** (hoặc CodeLens ▶ phía trên `Main`) thực thi tệp trên trình mô phỏng Amazon Braket cục bộ và nhóm các giá trị trả về của operation đầu vào theo tần suất. Với `void`, chỉ hiển thị thành công và số shot, không có biểu đồ. Lần đầu tự động thiết lập trình mô phỏng (tải một lần ~200 MB vào bộ nhớ riêng của tiện ích).
- **Chuyển đổi sang OpenQASM** — chạy **`Qora: Transpile to OpenQASM`** từ Command Palette; kết quả mở ở cửa sổ bên cạnh
- **Hiển thị các giai đoạn biên dịch** — chạy **`Qora: Hiển thị các giai đoạn biên dịch`** để theo dõi AST, HIR giữ cấu trúc nguồn, MIR SSA/CFG cùng kết quả phân tích, đích OpenQASM có kiểu và OpenQASM cuối cùng; tự cập nhật khi lưu
- **Đoạn mã mẫu (snippets)** — `operation`, `main`, `use`, `measure`, `for`, `if`, `bell`
- **Khớp / tự đóng dấu ngoặc**

## Kết quả thực thi

Kết quả chỉ gồm các giá trị `bool`, `bit`, `int`, `float` hoặc `angle` (radian) được operation đầu vào trả về rõ ràng.
Giá trị `bool` hiển thị là `true`/`false`, còn kết quả đo kiểu `bit` vẫn là `0`/`1`.
Ví dụ, `operation Main(): bit { use q = Qubit[1]; H(q[0]); return M(q[0]); }` trả về một bit cho mỗi shot.
Kết quả đo trong biến cục bộ và giá trị trả về của helper không tự trở thành đầu ra; trình chạy cũng không thêm phép đo cuối cùng.
Số nguyên lớn được giữ chính xác dưới dạng thập phân. Trình biên dịch tùy chỉnh phải cung cấp metadata `entryResult` hiện tại; bản cũ cần được cập nhật thay vì dùng kết quả đo nội bộ làm giá trị trả về.

## Giới hạn đã biết của trình mô phỏng

Thiết lập tiêu chuẩn dùng Braket SDK 1.127.8 và default simulator 1.43.1, không chứa các bản sửa chưa phát hành dùng khi kiểm chứng cục bộ.
Bộ kiểm thử thực thi Qora gồm 54 trường hợp chỉ vượt qua khi kết hợp bốn bản sửa Braket; cài đặt thông thường vẫn bị hạn chế ở mảng Boolean và các đường thực thi bị ảnh hưởng khác.
Ngay cả khi kết hợp các bản sửa, việc đo phần tử của tham số helper `Qubit[]` và một số bố trí qubit không liên tiếp của cổng có điều khiển vẫn chưa được sửa.
Hãy xem [giới hạn backend](https://github.com/AJ-comp/Qora/blob/main/CHANGELOG.md#known-execution-limitations) trước khi dùng các chương trình bị ảnh hưởng.
Biên dịch thành công không đảm bảo trình mô phỏng luôn trả về kết quả đúng.

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
