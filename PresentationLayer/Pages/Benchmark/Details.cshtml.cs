using BusinessLayer.DTOs;
using BusinessLayer.Services;
using PRN222_Assignment2.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace PRN222_Assignment2.Pages.Benchmark;

[Authorize(Roles = "Admin,Teacher")]
public class DetailsModel : PageModel
{
    private readonly IBenchmarkService _benchmarkService;

    public DetailsModel(IBenchmarkService benchmarkService)
    {
        _benchmarkService = benchmarkService;
    }

    public ExperimentDto? Experiment { get; set; }
    public IEnumerable<BenchmarkResultDto> Results { get; set; } = [];
    public IEnumerable<TestSetDto> TestSets { get; set; } = [];
    public int SubjectId { get; set; }

    [BindProperty]
    public AddTestCaseViewModel TestCaseInput { get; set; } = new();

    public async Task<IActionResult> OnGetAsync(int experimentId)
    {
        await LoadPageAsync(experimentId);
        if (Experiment == null) return NotFound();
        return Page();
    }

    public async Task<IActionResult> OnPostAddTestCaseAsync(int experimentId)
    {
        if (!ModelState.IsValid)
        {
            TempData["Error"] = "Vui lòng nhập đầy đủ câu hỏi và câu trả lời.";
            return RedirectToPage(new { experimentId });
        }

        await _benchmarkService.AddTestCaseAsync(experimentId, TestCaseInput.Question, TestCaseInput.ExpectedAnswer);
        TempData["Success"] = "Đã thêm Test Case thành công.";
        return RedirectToPage(new { experimentId });
    }

    public async Task<IActionResult> OnPostAddSampleTestCasesAsync(int experimentId)
    {
        var added = await _benchmarkService.AddSampleTestCasesAsync(experimentId);
        TempData["Success"] = added > 0
            ? $"Đã thêm {added} test case mẫu."
            : "Các test case mẫu đã tồn tại.";
        return RedirectToPage(new { experimentId });
    }

    public async Task<IActionResult> OnPostRunExperimentAsync(int experimentId)
    {
        try
        {
            await _benchmarkService.RunExperimentAsync(experimentId);
            TempData["Success"] = "Đã chạy Benchmark thành công!";
        }
        catch (Exception ex)
        {
            TempData["Error"] = "Lỗi khi chạy Benchmark: " + ex.Message;
        }

        return RedirectToPage(new { experimentId });
    }

    private async Task LoadPageAsync(int experimentId)
    {
        Experiment = await _benchmarkService.GetExperimentAsync(experimentId);
        if (Experiment == null) return;

        SubjectId = Experiment.SubjectId;
        Results = await _benchmarkService.GetResultsAsync(experimentId);
        TestSets = await _benchmarkService.GetTestSetsAsync(experimentId);
    }
}
