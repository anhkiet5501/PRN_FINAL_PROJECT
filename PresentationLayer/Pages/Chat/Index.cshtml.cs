using BusinessLayer.DTOs;
using BusinessLayer.Services;
using DataAccessLayer.Repositories;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using System.Security.Claims;

namespace PRN222_Assignment2.Pages.Chat;

[IgnoreAntiforgeryToken]
public class IndexModel : PageModel
{
    private readonly IChatService _chatService;
    private readonly ISubjectService _subjectService;
    private readonly IUnitOfWork _uow;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        IChatService chatService,
        ISubjectService subjectService,
        IUnitOfWork uow,
        ILogger<IndexModel> logger)
    {
        _chatService = chatService;
        _subjectService = subjectService;
        _uow = uow;
        _logger = logger;
    }

    // ── Page Properties ───────────────────────────────────────────────
    public IEnumerable<ChatSessionDto> Sessions { get; set; } = [];
    public ChatSessionDto? CurrentSession { get; set; }
    public IEnumerable<ChatMessageDto> Messages { get; set; } = [];
    public int? CurrentSessionId { get; set; }

    public IEnumerable<SubjectDto> AvailableSubjects { get; set; } = [];
    public IEnumerable<EmbeddingModelDto> AvailableEmbeddingModels { get; set; } = [];

    public record AiModelOption(int AiModelId, string ModelName, string Provider);
    public IEnumerable<AiModelOption> AvailableAiModels { get; set; } = [];

    public string UserRole { get; set; } = string.Empty;
    public string SubscriptionPlan { get; set; } = SubscriptionPlanCatalog.Basic;
    public int ShortTermQuestionCount { get; set; } = 0;
    public int MonthlyQuestionCount { get; set; } = 0;

    // ── Helpers ───────────────────────────────────────────────────────
    private int GetUserId() =>
        int.Parse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? "0");

    // ── GET ───────────────────────────────────────────────────────────
    public async Task OnGetAsync(int? sessionId)
    {
        var userId = GetUserId();
        
        // Load User data for limits
        var user = await _uow.Users.GetByIdAsync(userId);
        if (user != null)
        {
            UserRole = user.Role;
            SubscriptionPlan = user.SubscriptionPlan;
            ShortTermQuestionCount = user.ShortTermQuestionCount;
            MonthlyQuestionCount = user.MonthlyQuestionCount;
        }

        Sessions = await _chatService.GetUserSessionsAsync(userId);

        if (sessionId.HasValue)
        {
            CurrentSessionId = sessionId;
            CurrentSession = await _chatService.GetSessionAsync(sessionId.Value);
            if (CurrentSession != null)
                Messages = await _chatService.GetMessagesAsync(sessionId.Value);
        }
        else if (Sessions.Any())
        {
            // Auto-select the most recent session
            var first = Sessions.First();
            CurrentSessionId = first.ChatSessionId;
            CurrentSession = first;
            Messages = await _chatService.GetMessagesAsync(first.ChatSessionId);
        }

        await LoadDropdownsAsync();
    }

    // ── POST: Tạo session mới ─────────────────────────────────────────
    public async Task<IActionResult> OnPostCreateSessionAsync(
        string sessionTitle, int subjectId, int aiModelId, int embeddingModelId)
    {
        var userId = GetUserId();
        var session = await _chatService.CreateSessionAsync(new CreateChatSessionDto
        {
            SubjectId = subjectId,
            AiModelId = aiModelId,
            EmbeddingModelId = embeddingModelId,
            SessionTitle = sessionTitle
        }, userId);

        return RedirectToPage(new { sessionId = session.ChatSessionId });
    }

    // ── POST: Gửi tin nhắn (AJAX HTTP fallback) ───────────────────────
    public async Task<IActionResult> OnPostSendMessageAsync(
        [FromBody] SendMessageDto dto,
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid || dto.ChatSessionId <= 0 || string.IsNullOrWhiteSpace(dto.Question))
        {
            return new JsonResult(new { isError = true, errorMessage = "Yêu cầu không hợp lệ." });
        }

        try
        {
            var result = await _chatService.SendMessageAsync(dto, cancellationToken);
            return new JsonResult(result, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in OnPostSendMessageAsync for session {Id}", dto.ChatSessionId);
            return new JsonResult(new { isError = true, errorMessage = "Lỗi máy chủ. Vui lòng thử lại." });
        }
    }

    // ── GET: Tải tin nhắn của session (AJAX) ─────────────────────────
    public async Task<IActionResult> OnGetSessionMessagesAsync(int sessionId)
    {
        try
        {
            var userId = GetUserId();
            // Verify session belongs to user
            var session = await _chatService.GetSessionAsync(sessionId);
            if (session == null)
                return new JsonResult(new { success = false, message = "Phiên không tồn tại." });

            var messages = await _chatService.GetMessagesAsync(sessionId);
            var currentSession = session;

            return new JsonResult(new
            {
                success = true,
                session = currentSession,
                messages = messages
            }, new System.Text.Json.JsonSerializerOptions
            {
                PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading session messages for {SessionId}", sessionId);
            return new JsonResult(new { success = false, message = "Không thể tải tin nhắn." });
        }
    }

    // ── GET: Tài liệu của môn học (AJAX — cho document sidebar) ──────
    public async Task<IActionResult> OnGetSubjectDocumentsAsync(int subjectId)
    {
        try
        {
            var docs = await _uow.Documents.Query()
                .Where(d => d.Chapter.SubjectId == subjectId && d.Status == "Indexed")
                .OrderBy(d => d.Chapter.ChapterName)
                .ThenBy(d => d.OriginalFileName)
                .Select(d => new
                {
                    d.DocumentId,
                    Name = d.OriginalFileName ?? d.FileName,
                    d.FileType,
                    d.FileSizeBytes,
                    ChapterName = d.Chapter.ChapterName,
                    d.TotalChunks
                })
                .ToListAsync();

            return new JsonResult(new { success = true, documents = docs },
                new System.Text.Json.JsonSerializerOptions
                {
                    PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase
                });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error loading documents for subject {SubjectId}", subjectId);
            return new JsonResult(new { success = false, message = "Không thể tải tài liệu." });
        }
    }

    // ── POST: Xóa session ─────────────────────────────────────────────
    public async Task<IActionResult> OnPostDeleteSessionAsync(int sessionId)
    {
        await _chatService.DeleteSessionAsync(sessionId, GetUserId());
        return RedirectToPage();
    }

    // ── Helpers ───────────────────────────────────────────────────────
    public string ChatReturnUrl =>
        CurrentSessionId.HasValue ? $"/Chat?sessionId={CurrentSessionId.Value}" : "/Chat";

    /// <summary>
    /// Escape HTML and turn [1]/[2] markers into same-tab citation links.
    /// Ensures links work after full page reload (Quay lại).
    /// </summary>
    public string FormatAnswerWithCitations(ChatMessageDto msg)
    {
        var text = msg.Content ?? "";
        var encoded = System.Net.WebUtility.HtmlEncode(text)
            .Replace("\r\n", "\n")
            .Replace("\n", "<br>");

        if (msg.Citations == null || msg.Citations.Count == 0)
            return encoded;

        var byRank = msg.Citations
            .GroupBy(c => c.RetrievalRank > 0 ? c.RetrievalRank : 0)
            .ToDictionary(g => g.Key, g => g.First());

        // Fill missing ranks by order
        for (var i = 0; i < msg.Citations.Count; i++)
        {
            var rank = msg.Citations[i].RetrievalRank > 0 ? msg.Citations[i].RetrievalRank : i + 1;
            byRank.TryAdd(rank, msg.Citations[i]);
        }

        return System.Text.RegularExpressions.Regex.Replace(
            encoded,
            @"\[(?:Context\s*)?(\d+)\]|【(\d+)】",
            m =>
            {
                var n = int.Parse(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
                if (!byRank.TryGetValue(n, out var cite) || cite.DocumentId <= 0)
                    return $"<span class=\"cite-ref\" title=\"Nguồn {n}\">{n}</span>";

                var href =
                    $"/Documents/Details/{cite.DocumentId}?return={Uri.EscapeDataString(ChatReturnUrl)}&tab=chunks&chunk={cite.DocumentChunkId}";
                var title = System.Net.WebUtility.HtmlEncode(cite.DocumentName ?? "Tài liệu");
                return $"<a class=\"cite-ref\" href=\"{href}\" title=\"{title}\">{n}</a>";
            },
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }

    private async Task LoadDropdownsAsync()
    {
        AvailableSubjects = await _subjectService.GetAllAsync();
        AvailableEmbeddingModels = await _subjectService.GetEmbeddingModelsAsync();
        AvailableAiModels = await _uow.AiModels.Query()
            .Where(m => m.IsActive)
            .Select(m => new AiModelOption(m.AiModelId, m.ModelName, m.Provider))
            .ToListAsync();
    }
}
