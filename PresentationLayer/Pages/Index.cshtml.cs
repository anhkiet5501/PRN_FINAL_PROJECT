using BusinessLayer.DTOs;
using BusinessLayer.Services;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PRN222_Assignment2.Pages;

public class IndexModel : PageModel
{
    private readonly ILogger<IndexModel> _logger;
    private readonly IStatisticsService _statisticsService;

    public IndexModel(ILogger<IndexModel> logger, IStatisticsService statisticsService)
    {
        _logger = logger;
        _statisticsService = statisticsService;
    }

    public AdminStatisticsDto? AdminStats { get; set; }

    public async Task OnGetAsync()
    {
        _logger.LogInformation("Navigated to Home Index page.");

        if (User.IsInRole("Admin"))
        {
            AdminStats = await _statisticsService.GetAdminStatisticsAsync();
        }
    }
}
