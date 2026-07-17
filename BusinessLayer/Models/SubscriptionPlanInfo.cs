namespace BusinessLayer.Models;

public class SubscriptionPlanInfo
{
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public long Price { get; set; }
    public int ShortTermLimit { get; set; }
    public int MonthlyLimit { get; set; }
    public string Description { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
}
