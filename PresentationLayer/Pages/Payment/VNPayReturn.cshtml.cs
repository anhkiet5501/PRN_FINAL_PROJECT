using BusinessLayer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PresentationLayer.Pages.Payment;

public class VNPayReturnModel : PageModel
{
    private readonly IVNPayService _vnPayService;

    public VNPayReturnModel(IVNPayService vnPayService)
    {
        _vnPayService = vnPayService;
    }

    public bool IsSuccess { get; set; }
    public string Message { get; set; } = string.Empty;
    public string? PlanCode { get; set; }

    public async Task<IActionResult> OnGetAsync()
    {
        var query = Request.Query.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.ToString());

        var result = await _vnPayService.ValidateReturnAsync(query);
        IsSuccess = result.Success;
        Message = result.Message;
        PlanCode = result.PlanCode;
        return Page();
    }
}
