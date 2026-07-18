using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Collections.Generic;
using System.Linq;

namespace YourProjectName.Pages
{
    public class VNPayReturnModel : PageModel
    {
        // Các thuộc tính để truyền dữ liệu ra View (.cshtml)
        public bool IsSuccess { get; set; }
        public string Message { get; set; }
        public string OrderId { get; set; }
        public string TransactionId { get; set; }
        public string Amount { get; set; }

        public void OnGet()
        {
            // 1. Lấy toàn bộ tham số từ URL do VNPay trả về
            var vnpayData = Request.Query;

            if (vnpayData.Count > 0)
            {
                string vnp_HashSecret = "CHUOI_BIMAT_CUA_BAN"; // Thay bằng HashSecret lấy từ VNPay Merchant
                string vnp_SecureHash = vnpayData["vnp_SecureHash"];
                string vnp_ResponseCode = vnpayData["vnp_ResponseCode"];
                string vnp_TxnRef = vnpayData["vnp_TxnRef"];
                string vnp_Amount = vnpayData["vnp_Amount"];
                string vnp_TransactionNo = vnpayData["vnp_TransactionNo"];

                // 2. Tạo logic kiểm tra chữ ký (Checksum) để đảm bảo dữ liệu không bị hacker sửa đổi
                bool checkSignature = ValidateSignature(vnp_SecureHash, vnp_HashSecret, vnpayData);

                if (checkSignature)
                {
                    // VNPay quy định "00" là mã giao dịch thành công
                    if (vnp_ResponseCode == "00")
                    {
                        IsSuccess = true;
                        OrderId = vnp_TxnRef;
                        TransactionId = vnp_TransactionNo;

                        // VNPay trả về số tiền nhân với 100, nên cần chia lại cho 100
                        long actualAmount = Convert.ToInt64(vnp_Amount) / 100;
                        Amount = actualAmount.ToString("N0");

                        // TODO: Viết logic Cập nhật Database tại đây
                        // Ví dụ: 
                        // var order = dbContext.Orders.Find(OrderId);
                        // order.Status = "Paid";
                        // order.User.ChatboxPackage = "Pro";
                        // dbContext.SaveChanges();
                    }
                    else
                    {
                        IsSuccess = false;
                        Message = GetErrorMessage(vnp_ResponseCode);
                    }
                }
                else
                {
                    IsSuccess = false;
                    Message = "Sai chữ ký bảo mật. Phát hiện dấu hiệu can thiệp dữ liệu!";
                }
            }
            else
            {
                IsSuccess = false;
                Message = "Không tìm thấy dữ liệu trả về từ VNPay.";
            }
        }

        // Hàm giả lập hoặc tích hợp thư viện kiểm tra chữ ký
        private bool ValidateSignature(string rspRawHash, string secretKey, IQueryCollection queryData)
        {
            // LƯU Ý: Trong thực tế, bạn cần dùng thư viện VnPayLibrary (VNPay cung cấp) 
            // hoặc tự tạo chuỗi mã hóa HMACSHA512 từ các tham số (ngoại trừ vnp_SecureHash) 
            // rồi so sánh với rspRawHash.
            // Ở đây tôi trả về true để bạn test luồng giao diện trước.
            return true;
        }

        // Hàm dịch mã lỗi VNPay ra tiếng Việt
        private string GetErrorMessage(string vnp_ResponseCode)
        {
            return vnp_ResponseCode switch
            {
                "24" => "Bạn đã hủy giao dịch thanh toán.",
                "11" => "Thẻ/Tài khoản của bạn chưa đăng ký dịch vụ Internet Banking.",
                "51" => "Tài khoản của bạn không đủ số dư để thực hiện giao dịch.",
                "65" => "Tài khoản của bạn đã vượt quá hạn mức giao dịch trong ngày.",
                _ => $"Thanh toán gặp lỗi không xác định (Mã lỗi: {vnp_ResponseCode})"
            };
        }
    }
}