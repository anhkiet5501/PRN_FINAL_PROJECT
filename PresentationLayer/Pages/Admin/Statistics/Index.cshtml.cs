using BusinessLayer.DTOs;
using BusinessLayer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PRN222_Assignment2.Pages.Admin.Statistics;

[Authorize(Roles = "Admin")]
public class IndexModel : PageModel
{
    private readonly IStatisticsService _statisticsService;

    public IndexModel(IStatisticsService statisticsService)
    {
        _statisticsService = statisticsService;
    }

    public AdminStatisticsDto Stats { get; set; } = new();

    public async Task OnGetAsync()
    {
        Stats = await _statisticsService.GetAdminStatisticsAsync();
    }
}
