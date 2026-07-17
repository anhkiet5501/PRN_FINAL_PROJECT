using BusinessLayer.Models;
using BusinessLayer.Services;
using DataAccessLayer.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Security.Claims;

namespace PresentationLayer.Pages.Subscription;

public class IndexModel : PageModel
{
    private readonly IUnitOfWork _uow;
    private readonly ISubscriptionService _subscriptionService;
    private readonly IVNPayService _vnPayService;

    public IndexModel(
        IUnitOfWork uow,
        ISubscriptionService subscriptionService,
        IVNPayService vnPayService)
    {
        _uow = uow;
        _subscriptionService = subscriptionService;
        _vnPayService = vnPayService;
    }

    public string CurrentPlan { get; set; } = SubscriptionPlanCatalog.Basic;
    public DateTime? SubscriptionExpiry { get; set; }
    public IReadOnlyList<SubscriptionPlanInfo> Plans { get; set; } = [];

    private int GetUserId() =>
        int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");

    public async Task<IActionResult> OnGetAsync()
    {
        if (!User.IsInRole("Student"))
            return RedirectToPage("/Index");

        var user = await _uow.Users.GetByIdAsync(GetUserId());
        if (user == null)
            return RedirectToPage("/Auth/Login");

        CurrentPlan = user.SubscriptionPlan;
        SubscriptionExpiry = user.SubscriptionExpiry;
        Plans = SubscriptionPlanCatalog.GetAll();
        return Page();
    }

    public async Task<IActionResult> OnPostActivateBasicAsync()
    {
        if (!User.IsInRole("Student"))
            return RedirectToPage("/Index");

        await _subscriptionService.ApplyPlanAsync(GetUserId(), SubscriptionPlanCatalog.Basic);
        TempData["SuccessMessage"] = "Đã kích hoạt gói Basic miễn phí.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostPurchaseAsync(string planCode)
    {
        if (!User.IsInRole("Student"))
            return RedirectToPage("/Index");

        var plan = SubscriptionPlanCatalog.Get(planCode);
        if (plan == null)
        {
            TempData["ErrorMessage"] = "Gói không hợp lệ.";
            return RedirectToPage();
        }

        if (plan.Price <= 0)
        {
            await _subscriptionService.ApplyPlanAsync(GetUserId(), plan.Code);
            TempData["SuccessMessage"] = $"Đã kích hoạt gói {plan.Name}.";
            return RedirectToPage();
        }

        try
        {
            var baseUrl = $"{Request.Scheme}://{Request.Host}";
            var ip = HttpContext.Connection.RemoteIpAddress?.ToString() ?? "127.0.0.1";
            var (paymentUrl, _) = await _vnPayService.CreatePaymentAsync(GetUserId(), plan.Code, ip, baseUrl);
            return Redirect(paymentUrl);
        }
        catch (Exception ex)
        {
            TempData["ErrorMessage"] = ex.Message;
            return RedirectToPage();
        }
    }
}
