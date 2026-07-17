using DataAccessLayer.Context;
using DataAccessLayer.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace PresentationLayer.Pages.Admin.Subscriptions;

[Authorize(Policy = "AdminOnly")]
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;

    public IndexModel(AppDbContext db)
    {
        _db = db;
    }

    public int TotalStudents { get; set; }
    public int FreeCount { get; set; }
    public int BasicCount { get; set; }
    public int UltraCount { get; set; }

    public List<User> Students { get; set; } = [];

    public async Task OnGetAsync()
    {
        var query = _db.Users.Where(u => u.Role == "Student");

        TotalStudents = await query.CountAsync();
        FreeCount = await query.CountAsync(u => u.SubscriptionPlan == "Free");
        BasicCount = await query.CountAsync(u => u.SubscriptionPlan == "Basic");
        UltraCount = await query.CountAsync(u => u.SubscriptionPlan == "Ultra");

        Students = await query
            .OrderByDescending(u => u.CreatedAt)
            .ToListAsync();
    }

    public async Task<IActionResult> OnPostEditPlanAsync(int userId, string plan)
    {
        var user = await _db.Users.FindAsync(userId);
        if (user != null && user.Role == "Student")
        {
            user.SubscriptionPlan = plan;
            if (plan == "Ultra")
            {
                user.SubscriptionExpiry = DateTime.UtcNow.AddDays(30); // Giả lập Ultra 1 tháng
            }
            await _db.SaveChangesAsync();
            TempData["SuccessMessage"] = $"Đã cập nhật gói của {user.Username} thành {plan}";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostResetQuotaAsync(int userId)
    {
        var user = await _db.Users.FindAsync(userId);
        if (user != null && user.Role == "Student")
        {
            user.ShortTermQuestionCount = 0;
            user.MonthlyQuestionCount = 0;
            await _db.SaveChangesAsync();
            TempData["SuccessMessage"] = $"Đã reset lượt hỏi của {user.Username}";
        }
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRevokePlanAsync(int userId)
    {
        var user = await _db.Users.FindAsync(userId);
        if (user != null && user.Role == "Student")
        {
            user.SubscriptionPlan = "Free";
            user.SubscriptionExpiry = null;
            await _db.SaveChangesAsync();
            TempData["SuccessMessage"] = $"Đã thu hồi gói của {user.Username}";
        }
        return RedirectToPage();
    }
}
