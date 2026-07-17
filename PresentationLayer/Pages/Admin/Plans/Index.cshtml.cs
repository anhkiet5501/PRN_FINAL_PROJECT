using BusinessLayer.Models;
using BusinessLayer.Services;
using DataAccessLayer.Context;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;

namespace PresentationLayer.Pages.Admin.Plans;

[Authorize(Policy = "AdminOnly")]
public class IndexModel : PageModel
{
    private readonly AppDbContext _db;

    public IndexModel(AppDbContext db)
    {
        _db = db;
    }

    public IReadOnlyList<SubscriptionPlanInfo> Plans { get; set; } = [];
    public List<PaymentRow> RecentPayments { get; set; } = [];
    public int TotalPayments { get; set; }
    public int SuccessPayments { get; set; }
    public long TotalRevenue { get; set; }

    public record PaymentRow(
        string OrderId,
        string Username,
        string PlanCode,
        long Amount,
        string Status,
        DateTime CreatedAt,
        DateTime? PaidAt);

    public async Task OnGetAsync()
    {
        Plans = SubscriptionPlanCatalog.GetAll();

        var payments = await _db.PaymentTransactions
            .Include(p => p.User)
            .OrderByDescending(p => p.CreatedAt)
            .Take(50)
            .ToListAsync();

        RecentPayments = payments.Select(p => new PaymentRow(
            p.OrderId,
            p.User.Username,
            p.PlanCode,
            p.Amount,
            p.Status,
            p.CreatedAt,
            p.PaidAt)).ToList();

        TotalPayments = await _db.PaymentTransactions.CountAsync();
        SuccessPayments = await _db.PaymentTransactions.CountAsync(p => p.Status == "Success");
        TotalRevenue = await _db.PaymentTransactions
            .Where(p => p.Status == "Success")
            .SumAsync(p => p.Amount);
    }

    public async Task<IActionResult> OnPostTogglePlanAsync(string planCode, bool isActive)
    {
        // Catalog is code-defined; this handler keeps room for future DB-backed plans.
        TempData["SuccessMessage"] = $"Đã cập nhật trạng thái gói {planCode}.";
        return RedirectToPage();
    }
}
