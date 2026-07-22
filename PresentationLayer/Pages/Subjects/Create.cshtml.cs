using BusinessLayer.DTOs;
using BusinessLayer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.AspNetCore.SignalR;
using PRN222_Assignment2.Hubs;

namespace PRN222_Assignment2.Pages.Subjects;

public class CreateModel : PageModel
{
    private readonly ISubjectService _subjectService;
    private readonly IHubContext<SubjectHub> _hubContext;
    private readonly IAuthService _authService;

    public CreateModel(ISubjectService subjectService, IHubContext<SubjectHub> hubContext, IAuthService authService)
    {
        _subjectService = subjectService;
        _hubContext = hubContext;
        _authService = authService;
    }

    [BindProperty]
    public CreateSubjectDto SubjectDto { get; set; } = new();

    public List<SelectListItem> Teachers { get; set; } = new();

    public async Task OnGetAsync()
    {
        await LoadTeachersAsync();
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (!ModelState.IsValid)
        {
            await LoadTeachersAsync();
            return Page();
        }

        try
        {
            var newSubject = await _subjectService.CreateAsync(SubjectDto);
            await _hubContext.Clients.All.SendAsync("ReceiveSubjectUpdate", "create", newSubject);

            TempData["Success"] = "Đã tạo môn học thành công.";
            return RedirectToPage("/Subjects/Index");
        }
        catch (Exception ex)
        {
            TempData["Error"] = $"Lỗi khi tạo môn học: {ex.Message}";
            await LoadTeachersAsync();
            return Page();
        }
    }

    private async Task LoadTeachersAsync()
    {
        var users = await _authService.GetAllUsersAsync();
        Teachers = users.Where(u => u.Role == "Teacher")
                        .Select(u => new SelectListItem 
                        { 
                            Value = u.UserId.ToString(), 
                            Text = $"{u.FullName ?? u.Username} ({u.Email})" 
                        }).ToList();
    }
}
