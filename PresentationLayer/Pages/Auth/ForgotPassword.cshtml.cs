using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using BusinessLayer.Services;

using Microsoft.AspNetCore.Authorization;

namespace PRN222_Assignment2.Pages.Auth;

[AllowAnonymous]
public class ForgotPasswordModel : PageModel
{
    private readonly IAuthService _authService;

    public ForgotPasswordModel(IAuthService authService)
    {
        _authService = authService;
    }

    [BindProperty]
    public string Email { get; set; } = string.Empty;

    public string? ErrorMessage { get; set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Email))
        {
            ErrorMessage = "Vui lòng nhập email.";
            return Page();
        }

        var success = await _authService.SendPasswordResetCodeAsync(Email);
        if (!success)
        {
            ErrorMessage = "Không tìm thấy email này trong hệ thống hoặc tài khoản đang bị khóa.";
            return Page();
        }

        // Chuyển sang trang Reset mật khẩu và truyền email qua TempData (an toàn)
        TempData["ResetEmail"] = Email;
        TempData["SuccessMessage"] = "Mã xác nhận đã được gửi đến email của bạn. Có hiệu lực trong 5 phút.";
        return RedirectToPage("/Auth/ResetPassword");
    }
}
