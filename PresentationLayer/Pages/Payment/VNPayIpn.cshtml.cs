using BusinessLayer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PresentationLayer.Pages.Payment;

public class VNPayIpnModel : PageModel
{
    private readonly IVNPayService _vnPayService;

    public VNPayIpnModel(IVNPayService vnPayService)
    {
        _vnPayService = vnPayService;
    }

    public async Task<IActionResult> OnGetAsync()
    {
        var query = Request.Query.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value.ToString());

        var ok = await _vnPayService.ProcessIpnAsync(query);
        return Content(ok ? "OK" : "INVALID", "text/plain");
    }
}
