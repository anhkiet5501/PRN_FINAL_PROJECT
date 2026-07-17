using DataAccessLayer.Context;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;

namespace BusinessLayer.Services;

public class SubscriptionService : ISubscriptionService
{
    private readonly AppDbContext _db;

    public SubscriptionService(AppDbContext db)
    {
        _db = db;
    }

    public async Task ApplyPlanAsync(int userId, string planCode)
    {
        var plan = SubscriptionPlanCatalog.Get(planCode)
            ?? throw new InvalidOperationException("Gói đăng ký không hợp lệ.");

        var user = await _db.Users.FindAsync(userId)
            ?? throw new InvalidOperationException("Người dùng không tồn tại.");

        user.SubscriptionPlan = plan.Code;
        user.SubscriptionExpiry = plan.Price > 0 ? DateTime.UtcNow.AddDays(30) : null;
        user.ShortTermQuestionCount = 0;
        user.MonthlyQuestionCount = 0;
        user.ShortTermResetDate = DateTime.UtcNow.AddHours(5);
        user.QuotaResetDate = new DateTime(DateTime.UtcNow.Year, DateTime.UtcNow.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task<(bool Allowed, string Message)> CheckQuotaAsync(int userId)
    {
        var user = await _db.Users.FindAsync(userId);
        if (user == null || user.Role != "Student")
            return (true, string.Empty);

        await ResetQuotasIfNeededAsync(user);

        var plan = SubscriptionPlanCatalog.Get(user.SubscriptionPlan) ?? SubscriptionPlanCatalog.Get(SubscriptionPlanCatalog.Basic)!;
        if (user.ShortTermQuestionCount >= plan.ShortTermLimit)
            return (false, $"Bạn đã hết lượt hỏi trong chu kỳ 5 giờ ({plan.ShortTermLimit} câu). Vui lòng nâng cấp gói hoặc chờ reset.");

        if (user.MonthlyQuestionCount >= plan.MonthlyLimit)
            return (false, $"Bạn đã hết lượt hỏi trong tháng ({plan.MonthlyLimit} câu). Vui lòng nâng cấp gói.");

        return (true, string.Empty);
    }

    public async Task IncrementUsageAsync(int userId)
    {
        var user = await _db.Users.FindAsync(userId);
        if (user == null || user.Role != "Student")
            return;

        await ResetQuotasIfNeededAsync(user);
        user.ShortTermQuestionCount += 1;
        user.MonthlyQuestionCount += 1;
        user.UpdatedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    private async Task ResetQuotasIfNeededAsync(User user)
    {
        var now = DateTime.UtcNow;
        var changed = false;

        if (user.ShortTermResetDate == null || now >= user.ShortTermResetDate)
        {
            user.ShortTermQuestionCount = 0;
            user.ShortTermResetDate = now.AddHours(5);
            changed = true;
        }

        if (user.QuotaResetDate == null || now >= user.QuotaResetDate)
        {
            user.MonthlyQuestionCount = 0;
            user.QuotaResetDate = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc).AddMonths(1);
            changed = true;
        }

        if (user.SubscriptionExpiry.HasValue && now > user.SubscriptionExpiry.Value && user.SubscriptionPlan != SubscriptionPlanCatalog.Basic)
        {
            user.SubscriptionPlan = SubscriptionPlanCatalog.Basic;
            user.SubscriptionExpiry = null;
            changed = true;
        }

        if (changed)
            await _db.SaveChangesAsync();
    }
}
