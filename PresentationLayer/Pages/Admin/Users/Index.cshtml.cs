using BusinessLayer.DTOs;
using BusinessLayer.Services;
using PRN222_Assignment2.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PRN222_Assignment2.Pages.Admin.Users;

[Authorize(Roles = "Admin")]
public class IndexModel : PageModel
{
    private readonly IAuthService _authService;

    public IndexModel(IAuthService authService)
    {
        _authService = authService;
    }

    public IEnumerable<UserDto> Users { get; set; } = new List<UserDto>();

    [BindProperty]
    public CreateUserViewModel Input { get; set; } = new();

    [BindProperty]
    public UpdateUserDto UpdateInput { get; set; } = new();

    public async Task OnGetAsync()
    {
        Users = await _authService.GetAllUsersAsync();
    }

    public async Task<IActionResult> OnPostCreateUserAsync()
    {
        // Remove UpdateInput errors when creating a user
        // Remove anything that is not related to Input
        var keysToRemove = ModelState.Keys.Where(k => !k.StartsWith(nameof(Input))).ToList();
        foreach (var key in keysToRemove)
        {
            ModelState.Remove(key);
        }

        if (!ModelState.IsValid)
        {
            var errors = ModelState.Values.SelectMany(v => v.Errors).Select(e => e.ErrorMessage);
            TempData["Error"] = "Lỗi nhập liệu: " + string.Join(" ", errors);
            return RedirectToPage();
        }

        try
        {
            var dto = new CreateUserDto
            {
                FullName = Input.FullName,
                Username = Input.Username,
                Email = Input.Email,
                Password = Input.Password,
                Role = Input.Role
            };
            await _authService.RegisterAsync(dto);
            TempData["Success"] = $"Đã tạo user {Input.Username} thành công!";
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostEditUserAsync(int editUserId)
    {
        // Remove Input errors when editing a user
        // For edit, we don't need strict ModelState validation since we manually handle empty fields
        // and UpdateUserDto has no required validation attributes.
        // If there are specific validation needs, we check them manually.
        if (string.IsNullOrWhiteSpace(UpdateInput.Role))
        {
            TempData["Error"] = "Lỗi nhập liệu: Vai trò không được để trống.";
            return RedirectToPage();
        }

        try
        {
            var result = await _authService.UpdateUserAsync(editUserId, UpdateInput);
            if (result) TempData["Success"] = "Cập nhật thành công!";
            else TempData["Error"] = "Không tìm thấy user.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostDeleteUserAsync(int userId)
    {
        try
        {
            var result = await _authService.DeleteUserAsync(userId);
            if (result) TempData["Success"] = "Đã xóa User!";
            else TempData["Error"] = "Xóa thất bại!";
        }
        catch (InvalidOperationException ex)
        {
            TempData["Error"] = ex.Message;
        }

        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostImportCsvAsync(IFormFile csvFile)
    {
        if (csvFile == null || csvFile.Length == 0)
        {
            TempData["Error"] = "Vui lòng chọn file CSV.";
            return RedirectToPage();
        }

        try
        {
            using var stream = csvFile.OpenReadStream();
            var (successCount, skipCount) = await _authService.ImportUsersFromCsvAsync(stream);
            
            TempData["Success"] = $"Import thành công {successCount} users. Bỏ qua {skipCount} bản ghi lỗi/trùng lặp.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = "Lỗi khi import file: " + ex.Message;
        }

        return RedirectToPage();
    }
}
