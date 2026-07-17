using BusinessLayer.DTOs;
using DataAccessLayer.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BusinessLayer.Services;

public interface IStatisticsService
{
    Task<AdminStatisticsDto> GetAdminStatisticsAsync();
}

public class StatisticsService : IStatisticsService
{
    private readonly IUnitOfWork _uow;
    private readonly ILogger<StatisticsService> _logger;

    public StatisticsService(IUnitOfWork uow, ILogger<StatisticsService> logger)
    {
        _uow = uow;
        _logger = logger;
    }

    public async Task<AdminStatisticsDto> GetAdminStatisticsAsync()
    {
        var now = DateTime.UtcNow;
        var monthStart = new DateTime(now.Year, now.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var trendStart = monthStart.AddMonths(-5);
        var last7 = now.AddDays(-7);
        var last30 = now.AddDays(-30);

        var users = _uow.Users.Query();

        var totalUsers = await users.CountAsync();
        var adminCount = await users.CountAsync(u => u.Role == "Admin");
        var teacherCount = await users.CountAsync(u => u.Role == "Teacher");
        var studentCount = await users.CountAsync(u => u.Role == "Student");
        var activeUsers = await users.CountAsync(u => u.IsActive);
        var inactiveUsers = totalUsers - activeUsers;
        var newUsersThisMonth = await users.CountAsync(u => u.CreatedAt >= monthStart);

        var totalSubjects = await _uow.Subjects.CountAsync();
        var totalDocuments = await _uow.Documents.CountAsync();
        var totalChatSessions = await _uow.ChatSessions.CountAsync();

        var teachersWithAssignments = await _uow.SubjectTeachers.Query()
            .Select(st => st.UserId)
            .Distinct()
            .CountAsync();
        var subjectTeacherLinks = await _uow.SubjectTeachers.CountAsync();
        var documentsUploadedThisMonth = await _uow.Documents.CountAsync(d => d.UploadedAt >= monthStart);
        var chatSessionsThisMonth = await _uow.ChatSessions.CountAsync(c => c.CreatedAt >= monthStart);

        var registrationsRaw = await users
            .Where(u => u.CreatedAt >= trendStart)
            .GroupBy(u => new { u.CreatedAt.Year, u.CreatedAt.Month })
            .Select(g => new { g.Key.Year, g.Key.Month, Count = g.Count() })
            .ToListAsync();

        var registrationsByMonth = new List<MonthlyCountDto>();
        for (var i = 0; i < 6; i++)
        {
            var cursor = trendStart.AddMonths(i);
            var match = registrationsRaw.FirstOrDefault(r => r.Year == cursor.Year && r.Month == cursor.Month);
            registrationsByMonth.Add(new MonthlyCountDto
            {
                Year = cursor.Year,
                Month = cursor.Month,
                Label = $"{cursor.Month:D2}/{cursor.Year}",
                Count = match?.Count ?? 0
            });
        }

        var topChatUsers = await _uow.ChatSessions.Query()
            .Where(c => c.User.Role == "Student")
            .GroupBy(c => c.UserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(5)
            .Join(users,
                a => a.UserId,
                u => u.UserId,
                (a, u) => new TopUserActivityDto
                {
                    UserId = u.UserId,
                    DisplayName = u.FullName ?? u.Username,
                    Role = u.Role,
                    Count = a.Count
                })
            .ToListAsync();

        var topUploaders = await _uow.Documents.Query()
            .Where(d => d.UploadedBy.Role != "Admin")
            .GroupBy(d => d.UploadedByUserId)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(5)
            .Join(users,
                a => a.UserId,
                u => u.UserId,
                (a, u) => new TopUserActivityDto
                {
                    UserId = u.UserId,
                    DisplayName = u.FullName ?? u.Username,
                    Role = u.Role,
                    Count = a.Count
                })
            .ToListAsync();

        var knowledgeBase = await BuildKnowledgeBaseAsync();
        var aiRag = await BuildAiRagAsync(users);
        var learning = await BuildLearningAsync(last7, last30);

        _logger.LogInformation(
            "Admin statistics loaded: {TotalUsers} users, {Subjects} subjects, {Documents} documents",
            totalUsers, totalSubjects, totalDocuments);

        // Tokens By Month
        var tokensRaw = await _uow.ChatHistories.Query()
            .Where(h => h.CreatedAt >= trendStart && h.TokenCount != null)
            .GroupBy(h => new { h.CreatedAt.Year, h.CreatedAt.Month })
            .Select(g => new { g.Key.Year, g.Key.Month, Count = g.Sum(x => x.TokenCount) })
            .ToListAsync();

        var tokensByMonth = new List<MonthlyCountDto>();
        for (var i = 0; i < 6; i++)
        {
            var cursor = trendStart.AddMonths(i);
            var match = tokensRaw.FirstOrDefault(r => r.Year == cursor.Year && r.Month == cursor.Month);
            tokensByMonth.Add(new MonthlyCountDto
            {
                Year = cursor.Year,
                Month = cursor.Month,
                Label = $"{cursor.Month:D2}/{cursor.Year}",
                Count = match?.Count ?? 0
            });
        }

        // Payments By Month
        var paymentsRaw = await _uow.PaymentTransactions.Query()
            .Where(p => p.CreatedAt >= trendStart && p.Status == "Success")
            .GroupBy(p => new { p.CreatedAt.Year, p.CreatedAt.Month })
            .Select(g => new { g.Key.Year, g.Key.Month, Revenue = g.Sum(x => x.Amount) })
            .ToListAsync();

        var paymentsByMonth = new List<MonthlyRevenueDto>();
        for (var i = 0; i < 6; i++)
        {
            var cursor = trendStart.AddMonths(i);
            var match = paymentsRaw.FirstOrDefault(r => r.Year == cursor.Year && r.Month == cursor.Month);
            paymentsByMonth.Add(new MonthlyRevenueDto
            {
                Year = cursor.Year,
                Month = cursor.Month,
                Label = $"{cursor.Month:D2}/{cursor.Year}",
                Revenue = match?.Revenue ?? 0m
            });
        }

        // User Payment Stats
        var userPaymentStats = await users
            .Where(u => u.Role == "Student")
            .Select(u => new UserPaymentStatsDto
            {
                UserId = u.UserId,
                FullName = string.IsNullOrWhiteSpace(u.FullName) ? u.Username : u.FullName,
                Email = u.Email ?? "",
                CurrentPlan = string.IsNullOrWhiteSpace(u.SubscriptionPlan) ? "Free" : u.SubscriptionPlan,
                TotalTokensUsed = u.TokensUsed,
                TotalMoneySpent = _uow.PaymentTransactions.Query()
                                    .Where(p => p.UserId == u.UserId && p.Status == "Success")
                                    .Sum(p => (decimal?)p.Amount) ?? 0m
            })
            .OrderByDescending(u => u.TotalMoneySpent)
            .ThenByDescending(u => u.TotalTokensUsed)
            .Take(50)
            .ToListAsync();

        return new AdminStatisticsDto
        {
            TotalUsers = totalUsers,
            AdminCount = adminCount,
            TeacherCount = teacherCount,
            StudentCount = studentCount,
            ActiveUsers = activeUsers,
            InactiveUsers = inactiveUsers,
            NewUsersThisMonth = newUsersThisMonth,
            TotalSubjects = totalSubjects,
            TotalDocuments = totalDocuments,
            TotalChatSessions = totalChatSessions,
            TeachersWithAssignments = teachersWithAssignments,
            SubjectTeacherLinks = subjectTeacherLinks,
            DocumentsUploadedThisMonth = documentsUploadedThisMonth,
            ChatSessionsThisMonth = chatSessionsThisMonth,
            RegistrationsByMonth = registrationsByMonth,
            TopChatUsers = topChatUsers,
            TopUploaders = topUploaders,
            TokensByMonth = tokensByMonth,
            PaymentsByMonth = paymentsByMonth,
            UserPaymentStats = userPaymentStats,
            KnowledgeBase = knowledgeBase,
            AiRag = aiRag,
            Learning = learning
        };
    }

    private async Task<KnowledgeBaseAnalyticsDto> BuildKnowledgeBaseAsync()
    {
        var docs = _uow.Documents.Query();
        var totalDocuments = await docs.CountAsync();
        var pending = await docs.CountAsync(d => d.Status == "Pending");
        var processing = await docs.CountAsync(d => d.Status == "Processing");
        var indexed = await docs.CountAsync(d => d.Status == "Indexed");
        var failed = await docs.CountAsync(d => d.Status == "Failed");

        var totalChunks = await _uow.DocumentChunks.CountAsync();
        var totalEmbeddings = await _uow.DocumentChunks.Query()
            .CountAsync(c => c.EmbeddingJson != null && c.EmbeddingJson != "[]" && c.EmbeddingJson != "");

        var durations = await _uow.DocumentIndexes.Query()
            .Where(i => i.IndexingDurationSeconds != null)
            .Select(i => i.IndexingDurationSeconds!.Value)
            .ToListAsync();

        var finished = indexed + failed;
        var errorRate = finished > 0 ? Math.Round(failed * 100.0 / finished, 1) : 0;

        return new KnowledgeBaseAnalyticsDto
        {
            TotalDocuments = totalDocuments,
            PendingDocuments = pending,
            ProcessingDocuments = processing,
            IndexedDocuments = indexed,
            FailedDocuments = failed,
            TotalChunks = totalChunks,
            TotalEmbeddings = totalEmbeddings,
            AvgIndexingSeconds = durations.Count > 0 ? Math.Round(durations.Average(), 2) : null,
            P95IndexingSeconds = Percentile(durations, 0.95),
            ErrorRatePercent = errorRate
        };
    }

    private async Task<AiRagAnalyticsDto> BuildAiRagAsync(IQueryable<DataAccessLayer.Entities.User> users)
    {
        var totalSessions = await _uow.ChatSessions.CountAsync();
        var totalQuestions = await _uow.ChatHistories.CountAsync(h => h.Role == "user");
        var totalTokens = await _uow.ChatHistories.Query()
            .Where(h => h.TokenCount != null)
            .SumAsync(h => (long)h.TokenCount!.Value);

        var latencies = await _uow.ChatHistories.Query()
            .Where(h => h.Role == "assistant" && h.LatencyMs != null)
            .Select(h => (double)h.LatencyMs!.Value)
            .ToListAsync();

        var withContext = await _uow.ChatHistories.CountAsync(h => h.Role == "assistant" && h.HasContext == true);
        var withoutContext = await _uow.ChatHistories.CountAsync(h => h.Role == "assistant" && h.HasContext == false);
        var contextTotal = withContext + withoutContext;
        // Fallback: assistant rows with citations if HasContext not yet populated
        if (contextTotal == 0)
        {
            var assistantIds = _uow.ChatHistories.Query().Where(h => h.Role == "assistant").Select(h => h.ChatHistoryId);
            withContext = await _uow.ChatCitations.Query()
                .Where(c => assistantIds.Contains(c.ChatHistoryId))
                .Select(c => c.ChatHistoryId)
                .Distinct()
                .CountAsync();
            var assistantCount = await _uow.ChatHistories.CountAsync(h => h.Role == "assistant");
            withoutContext = Math.Max(0, assistantCount - withContext);
            contextTotal = withContext + withoutContext;
        }

        var topSubjects = await _uow.ChatSessions.Query()
            .GroupBy(s => new { s.SubjectId, s.Subject.SubjectName })
            .Select(g => new NamedCountDto { Name = g.Key.SubjectName, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(5)
            .ToListAsync();

        var topDocs = await _uow.ChatCitations.Query()
            .GroupBy(c => new
            {
                c.DocumentChunk.DocumentId,
                Name = c.DocumentChunk.Document.OriginalFileName ?? c.DocumentChunk.Document.FileName
            })
            .Select(g => new NamedCountDto { Name = g.Key.Name, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(5)
            .ToListAsync();

        var topTokenUsers = await users
            .Where(u => u.TokensUsed > 0 && u.Role == "Student")
            .OrderByDescending(u => u.TokensUsed)
            .Take(5)
            .Select(u => new TopUserActivityDto
            {
                UserId = u.UserId,
                DisplayName = u.FullName ?? u.Username,
                Role = u.Role,
                Count = u.TokensUsed
            })
            .ToListAsync();

        return new AiRagAnalyticsDto
        {
            TotalChatSessions = totalSessions,
            TotalQuestions = totalQuestions,
            TotalTokens = totalTokens,
            AvgLatencyMs = latencies.Count > 0 ? Math.Round(latencies.Average(), 0) : null,
            P95LatencyMs = Percentile(latencies, 0.95),
            AnswersWithContext = withContext,
            AnswersWithoutContext = withoutContext,
            ContextHitRatePercent = contextTotal > 0
                ? Math.Round(withContext * 100.0 / contextTotal, 1)
                : 0,
            TopSubjectsAsked = topSubjects,
            TopRetrievedDocuments = topDocs,
            TopTokenUsers = topTokenUsers
        };
    }

    private async Task<LearningAnalyticsDto> BuildLearningAsync(DateTime last7, DateTime last30)
    {
        var popularSubjects = await _uow.ChatHistories.Query()
            .Where(h => h.Role == "user")
            .GroupBy(h => h.ChatSession.Subject.SubjectName)
            .Select(g => new NamedCountDto { Name = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(5)
            .ToListAsync();

        // Peak hours in Vietnam time (UTC+7)
        var questionTimes = await _uow.ChatHistories.Query()
            .Where(h => h.Role == "user")
            .Select(h => h.CreatedAt)
            .ToListAsync();

        var hourBuckets = Enumerable.Range(0, 24)
            .Select(h => new HourlyCountDto
            {
                Hour = h,
                Label = $"{h:D2}h",
                Count = 0
            })
            .ToList();

        foreach (var t in questionTimes)
        {
            var vnHour = t.AddHours(7).Hour;
            hourBuckets[vnHour].Count++;
        }

        var questionsPerDoc = await _uow.ChatCitations.Query()
            .GroupBy(c => new
            {
                c.DocumentChunk.DocumentId,
                Name = c.DocumentChunk.Document.OriginalFileName ?? c.DocumentChunk.Document.FileName
            })
            .Select(g => new NamedCountDto
            {
                Name = g.Key.Name,
                Count = g.Select(x => x.ChatHistoryId).Distinct().Count()
            })
            .OrderByDescending(x => x.Count)
            .Take(5)
            .ToListAsync();

        var active7 = await _uow.ChatSessions.Query()
            .Where(s => (s.LastActivityAt ?? s.CreatedAt) >= last7)
            .Select(s => s.UserId)
            .Distinct()
            .CountAsync();

        var active30 = await _uow.ChatSessions.Query()
            .Where(s => (s.LastActivityAt ?? s.CreatedAt) >= last30)
            .Select(s => s.UserId)
            .Distinct()
            .CountAsync();

        return new LearningAnalyticsDto
        {
            PopularSubjects = popularSubjects,
            PeakHours = hourBuckets,
            QuestionsPerDocument = questionsPerDoc,
            ActiveUsersLast7Days = active7,
            ActiveUsersLast30Days = active30
        };
    }

    private static double? Percentile(List<double> values, double p)
    {
        if (values.Count == 0) return null;
        var sorted = values.OrderBy(v => v).ToList();
        var idx = (int)Math.Ceiling(p * sorted.Count) - 1;
        idx = Math.Clamp(idx, 0, sorted.Count - 1);
        return Math.Round(sorted[idx], 2);
    }
}
