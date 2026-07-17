using BusinessLayer.Models;

namespace BusinessLayer.Services;

public static class SubscriptionPlanCatalog
{
    public const string Basic = "Basic";
    public const string Pro = "Pro";
    public const string Ultra = "Ultra";

    private static readonly Dictionary<string, SubscriptionPlanInfo> Plans = new(StringComparer.OrdinalIgnoreCase)
    {
        [Basic] = new SubscriptionPlanInfo
        {
            Code = Basic,
            Name = "Basic",
            Price = 0,
            ShortTermLimit = 10,
            MonthlyLimit = 50,
            Description = "Gói miễn phí — 10 câu/5 giờ, 50 câu/tháng"
        },
        [Pro] = new SubscriptionPlanInfo
        {
            Code = Pro,
            Name = "Pro",
            Price = 29_000,
            ShortTermLimit = 50,
            MonthlyLimit = 200,
            Description = "Gói Pro — 50 câu/5 giờ, 200 câu/tháng"
        },
        [Ultra] = new SubscriptionPlanInfo
        {
            Code = Ultra,
            Name = "Ultra",
            Price = 49_000,
            ShortTermLimit = 1000,
            MonthlyLimit = 5000,
            Description = "Gói Ultra — 1000 câu/5 giờ, 5000 câu/tháng"
        }
    };

    public static IReadOnlyList<SubscriptionPlanInfo> GetAll() => Plans.Values.ToList();

    public static SubscriptionPlanInfo? Get(string? code) =>
        string.IsNullOrWhiteSpace(code) ? null :
        Plans.TryGetValue(code.Trim(), out var plan) ? plan : null;

    public static int GetShortTermLimit(string? code) => Get(code)?.ShortTermLimit ?? 10;

    public static int GetMonthlyLimit(string? code) => Get(code)?.MonthlyLimit ?? 50;
}
