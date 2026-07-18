using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.ComponentModel.DataAnnotations;

namespace YourProjectName.Pages
{
    public class CheckoutModel : PageModel
    {
        [BindProperty]
        public CheckoutInputModel Input { get; set; }

        public class CheckoutInputModel
        {
            [Required(ErrorMessage = "Vui lòng nhập họ tên của bạn.")]
            public string FullName { get; set; }

            [Required(ErrorMessage = "Vui lòng nhập số điện thoại để hỗ trợ khi cần.")]
            [Phone(ErrorMessage = "Số điện thoại không hợp lệ.")]
            public string PhoneNumber { get; set; }

            // Email bây giờ là bắt buộc cho dịch vụ số
            [Required(ErrorMessage = "Vui lòng nhập email để cấp quyền tài khoản.")]
            [EmailAddress(ErrorMessage = "Email không hợp lệ.")]
            public string Email { get; set; }
        }

        public void OnGet()
        {
            // Lấy thông tin gói dịch vụ Chatbox mà học sinh/sinh viên đang muốn mua
            // Ví dụ: Load giá tiền, tên gói từ Database để hiển thị ra View
        }

        public IActionResult OnPost()
        {
            if (!ModelState.IsValid)
            {
                // Thông tin không hợp lệ, trả về form
                return Page();
            }

            string selectedPaymentMethod = Request.Form["paymentMethod"];

            // 1. Lưu thông tin giao dịch vào Database (Trạng thái: Đang chờ thanh toán)
            // 2. Tích hợp gọi API của các cổng thanh toán (Momo, VNPay, Stripe...) tùy thuộc vào selectedPaymentMethod
            // 3. Nếu chọn VietQR, bạn có thể redirect sang một trang hiển thị mã QR kèm số tiền và nội dung chuyển khoản.

            // Tạm thời redirect sang trang thành công/hướng dẫn thanh toán
            return RedirectToPage("/CheckoutSuccess");
        }
    }
}