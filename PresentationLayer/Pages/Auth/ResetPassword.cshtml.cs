using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using BusinessLayer.Services;

using Microsoft.AspNetCore.Authorization;

namespace PRN222_Assignment2.Pages.Auth;

[AllowAnonymous]
public class ResetPasswordModel : PageModel
{
    private readonly IAuthService _authService;

    public ResetPasswordModel(IAuthService authService)
    {
        _authService = authService;
    }

    [BindProperty]
    public string Email { get; set; } = string.Empty;

    [BindProperty]
    public string Code { get; set; } = string.Empty;

    [BindProperty]
    public string NewPassword { get; set; } = string.Empty;

    [BindProperty]
    public string ConfirmPassword { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }

    public IActionResult OnGet()
    {
        if (TempData["ResetEmail"] is string email)
        {
            Email = email;
            // Giữ lại email cho trường hợp post lỗi
            TempData.Keep("ResetEmail"); 
            return Page();
        }

        // Nếu người dùng vào thẳng trang này mà không có email từ bước 1
        return RedirectToPage("/Auth/ForgotPassword");
    }

    public async Task<IActionResult> OnPostAsync()
    {
        TempData.Keep("ResetEmail");

        if (string.IsNullOrWhiteSpace(Code) || string.IsNullOrWhiteSpace(NewPassword))
        {
            ErrorMessage = "Vui lòng nhập đầy đủ mã xác nhận và mật khẩu mới.";
            return Page();
        }

        if (NewPassword != ConfirmPassword)
        {
            ErrorMessage = "Mật khẩu xác nhận không khớp.";
            return Page();
        }

        var success = await _authService.ResetPasswordAsync(Email, Code, NewPassword);

        if (!success)
        {
            ErrorMessage = "Mã xác nhận không chính xác hoặc đã hết hạn (sau 5 phút).";
            return Page();
        }

        // Xóa email lưu trong TempData vì đã dùng xong
        TempData.Remove("ResetEmail");
        
        TempData["SuccessMessage"] = "Đổi mật khẩu thành công! Vui lòng đăng nhập bằng mật khẩu mới.";
        return RedirectToPage("/Auth/Login");
    }
}
