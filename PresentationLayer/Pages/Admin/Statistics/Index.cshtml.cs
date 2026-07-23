using BusinessLayer.DTOs;
using BusinessLayer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

    [BindProperty(SupportsGet = true)]
    public string Period { get; set; } = "30d";

    [BindProperty(SupportsGet = true)]
    public DateTime? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTime? To { get; set; }

    public async Task OnGetAsync()
    {
        if (string.IsNullOrWhiteSpace(Period))
            Period = "30d";

        Stats = await _statisticsService.GetAdminStatisticsAsync(Period, From, To);
        Period = Stats.Period;
    }
}
