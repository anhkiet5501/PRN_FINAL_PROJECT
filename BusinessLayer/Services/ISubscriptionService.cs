namespace BusinessLayer.Services;

public interface ISubscriptionService
{
    Task ApplyPlanAsync(int userId, string planCode);
    Task<(bool Allowed, string Message)> CheckQuotaAsync(int userId);
    Task IncrementUsageAsync(int userId);
}
