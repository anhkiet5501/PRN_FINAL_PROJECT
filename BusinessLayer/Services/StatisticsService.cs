using BusinessLayer.DTOs;
using DataAccessLayer.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BusinessLayer.Services;

public interface IStatisticsService
{
    Task<AdminStatisticsDto> GetAdminStatisticsAsync(
        string period = "30d",
        DateTime? from = null,
        DateTime? to = null);
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

    public async Task<AdminStatisticsDto> GetAdminStatisticsAsync(
        string period = "30d",
        DateTime? from = null,
        DateTime? to = null)
    {
        var (fromUtc, toUtc, periodKey, periodLabel) = ResolveRange(period, from, to);
        var hasFrom = fromUtc.HasValue;

        var users = _uow.Users.Query();

        var totalUsers = hasFrom
            ? await users.CountAsync(u => u.CreatedAt >= fromUtc && u.CreatedAt < toUtc)
            : await users.CountAsync();
        var adminCount = hasFrom
            ? await users.CountAsync(u => u.Role == "Admin" && u.CreatedAt >= fromUtc && u.CreatedAt < toUtc)
            : await users.CountAsync(u => u.Role == "Admin");
        var teacherCount = hasFrom
            ? await users.CountAsync(u => u.Role == "Teacher" && u.CreatedAt >= fromUtc && u.CreatedAt < toUtc)
            : await users.CountAsync(u => u.Role == "Teacher");
        var studentCount = hasFrom
            ? await users.CountAsync(u => u.Role == "Student" && u.CreatedAt >= fromUtc && u.CreatedAt < toUtc)
            : await users.CountAsync(u => u.Role == "Student");
        var activeUsers = await users.CountAsync(u => u.IsActive);
        var inactiveUsers = await users.CountAsync(u => !u.IsActive);
        var newUsersInPeriod = hasFrom
            ? await users.CountAsync(u => u.CreatedAt >= fromUtc && u.CreatedAt < toUtc)
            : await users.CountAsync();

        var totalSubjects = await _uow.Subjects.CountAsync();
        var totalDocuments = hasFrom
            ? await _uow.Documents.CountAsync(d => d.UploadedAt >= fromUtc && d.UploadedAt < toUtc)
            : await _uow.Documents.CountAsync();
        var totalChatSessions = hasFrom
            ? await _uow.ChatSessions.CountAsync(c => c.CreatedAt >= fromUtc && c.CreatedAt < toUtc)
            : await _uow.ChatSessions.CountAsync();

        var teachersWithAssignments = await _uow.SubjectTeachers.Query()
            .Select(st => st.UserId)
            .Distinct()
            .CountAsync();
        var subjectTeacherLinks = await _uow.SubjectTeachers.CountAsync();
        var documentsUploadedInPeriod = totalDocuments;
        var chatSessionsInPeriod = totalChatSessions;

        var trendStart = hasFrom ? fromUtc!.Value : DateTime.UtcNow.AddMonths(-5);
        var trendEnd = toUtc;

        var registrationsRaw = await users
            .Where(u => u.CreatedAt >= trendStart && u.CreatedAt < trendEnd)
            .GroupBy(u => new { u.CreatedAt.Year, u.CreatedAt.Month })
            .Select(g => new { g.Key.Year, g.Key.Month, Count = g.Count() })
            .ToListAsync();

        var registrationsByMonth = BuildMonthlyCounts(trendStart, trendEnd, registrationsRaw
            .Select(r => (r.Year, r.Month, r.Count)));

        var sessionsQuery = _uow.ChatSessions.Query().Where(c => c.User.Role == "Student");
        if (hasFrom)
            sessionsQuery = sessionsQuery.Where(c => c.CreatedAt >= fromUtc && c.CreatedAt < toUtc);

        var topChatUsers = await sessionsQuery
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

        var docsQuery = _uow.Documents.Query().Where(d => d.UploadedBy.Role != "Admin");
        if (hasFrom)
            docsQuery = docsQuery.Where(d => d.UploadedAt >= fromUtc && d.UploadedAt < toUtc);

        var topUploaders = await docsQuery
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

        var knowledgeBase = await BuildKnowledgeBaseAsync(fromUtc, toUtc);
        var aiRag = await BuildAiRagAsync(users, fromUtc, toUtc);
        var learning = await BuildLearningAsync(fromUtc, toUtc);

        var tokensQuery = _uow.ChatHistories.Query()
            .Where(h => h.TokenCount != null && h.CreatedAt >= trendStart && h.CreatedAt < trendEnd);
        var tokensRaw = await tokensQuery
            .GroupBy(h => new { h.CreatedAt.Year, h.CreatedAt.Month })
            .Select(g => new { g.Key.Year, g.Key.Month, Count = g.Sum(x => x.TokenCount) })
            .ToListAsync();

        var tokensByMonth = BuildMonthlyCounts(trendStart, trendEnd, tokensRaw
            .Select(r => (r.Year, r.Month, r.Count ?? 0)));

        var tokensByYear = tokensByMonth
            .GroupBy(m => m.Year)
            .OrderBy(g => g.Key)
            .Select(g => new YearlyCountDto
            {
                Year = g.Key,
                Label = g.Key.ToString(),
                Count = g.Sum(x => x.Count)
            })
            .ToList();

        var payStart = hasFrom ? fromUtc!.Value : DateTime.UtcNow.AddYears(-2);
        var paymentsQuery = _uow.PaymentTransactions.Query()
            .Where(p => p.Status == "Success" && p.CreatedAt >= payStart && p.CreatedAt < trendEnd);

        var paymentRows = await paymentsQuery
            .Select(p => new { At = p.PaidAt ?? p.CreatedAt, p.Amount })
            .ToListAsync();

        // Group by Vietnam calendar day (UTC+7)
        var revenueByVnDay = paymentRows
            .GroupBy(p => p.At.AddHours(7).Date)
            .ToDictionary(
                g => g.Key,
                g => new { Revenue = g.Sum(x => (decimal)x.Amount), Count = g.Count() });

        var paymentsByDay = new List<DailyRevenueDto>();
        var daySpan = (trendEnd - payStart).TotalDays;
        if (daySpan <= 62)
        {
            for (var d = payStart.AddHours(7).Date; d <= trendEnd.AddHours(7).Date; d = d.AddDays(1))
            {
                revenueByVnDay.TryGetValue(d, out var match);
                paymentsByDay.Add(new DailyRevenueDto
                {
                    Date = d,
                    Label = d.ToString("dd/MM/yyyy"),
                    Revenue = match?.Revenue ?? 0m,
                    TransactionCount = match?.Count ?? 0
                });
            }
        }
        else
        {
            paymentsByDay = revenueByVnDay
                .OrderBy(kv => kv.Key)
                .Select(kv => new DailyRevenueDto
                {
                    Date = kv.Key,
                    Label = kv.Key.ToString("dd/MM/yyyy"),
                    Revenue = kv.Value.Revenue,
                    TransactionCount = kv.Value.Count
                })
                .ToList();
        }

        var paymentsByMonth = paymentsByDay
            .GroupBy(d => new { d.Date.Year, d.Date.Month })
            .OrderBy(g => g.Key.Year).ThenBy(g => g.Key.Month)
            .Select(g => new MonthlyRevenueDto
            {
                Year = g.Key.Year,
                Month = g.Key.Month,
                Label = $"{g.Key.Month:D2}/{g.Key.Year}",
                Revenue = g.Sum(x => x.Revenue),
                TransactionCount = g.Sum(x => x.TransactionCount)
            })
            .ToList();

        // Fill missing months in range so charts stay continuous
        if (paymentsByMonth.Count == 0 || daySpan <= 400)
        {
            var payMonthMap = paymentsByMonth.ToDictionary(m => (m.Year, m.Month));
            paymentsByMonth = EnumerateMonths(payStart, trendEnd).Select(m =>
            {
                payMonthMap.TryGetValue(m, out var hit);
                return new MonthlyRevenueDto
                {
                    Year = m.Year,
                    Month = m.Month,
                    Label = $"{m.Month:D2}/{m.Year}",
                    Revenue = hit?.Revenue ?? 0m,
                    TransactionCount = hit?.TransactionCount ?? 0
                };
            }).ToList();
        }

        var paymentsByYear = paymentsByMonth
            .GroupBy(m => m.Year)
            .OrderBy(g => g.Key)
            .Select(g => new YearlyRevenueDto
            {
                Year = g.Key,
                Label = g.Key.ToString(),
                Revenue = g.Sum(x => x.Revenue),
                TransactionCount = g.Sum(x => x.TransactionCount)
            })
            .ToList();

        var totalRevenueInPeriod = paymentsByDay.Sum(d => d.Revenue);

        var paymentFilter = _uow.PaymentTransactions.Query().Where(p => p.Status == "Success");
        if (hasFrom)
            paymentFilter = paymentFilter.Where(p => p.CreatedAt >= fromUtc && p.CreatedAt < toUtc);

        var spentByUser = await paymentFilter
            .GroupBy(p => p.UserId)
            .Select(g => new { UserId = g.Key, Total = g.Sum(x => x.Amount) })
            .ToListAsync();

        var spentLookup = spentByUser.ToDictionary(x => x.UserId, x => x.Total);

        var userPaymentStats = await users
            .Where(u => u.Role == "Student")
            .Select(u => new UserPaymentStatsDto
            {
                UserId = u.UserId,
                FullName = string.IsNullOrWhiteSpace(u.FullName) ? u.Username : u.FullName!,
                Email = u.Email ?? "",
                CurrentPlan = string.IsNullOrWhiteSpace(u.SubscriptionPlan) ? "Free" : u.SubscriptionPlan!,
                TotalTokensUsed = u.TokensUsed,
                TotalMoneySpent = 0
            })
            .ToListAsync();

        foreach (var row in userPaymentStats)
        {
            if (spentLookup.TryGetValue(row.UserId, out var spent))
                row.TotalMoneySpent = spent;
        }

        userPaymentStats = userPaymentStats
            .OrderByDescending(u => u.TotalMoneySpent)
            .ThenByDescending(u => u.TotalTokensUsed)
            .Take(50)
            .ToList();

        _logger.LogInformation(
            "Admin statistics loaded for {Period} ({From} → {To})",
            periodKey, fromUtc, toUtc);

        return new AdminStatisticsDto
        {
            Period = periodKey,
            PeriodLabel = periodLabel,
            FromUtc = fromUtc,
            ToUtc = toUtc,
            TotalUsers = totalUsers,
            AdminCount = adminCount,
            TeacherCount = teacherCount,
            StudentCount = studentCount,
            ActiveUsers = activeUsers,
            InactiveUsers = inactiveUsers,
            NewUsersThisMonth = newUsersInPeriod,
            TotalSubjects = totalSubjects,
            TotalDocuments = totalDocuments,
            TotalChatSessions = totalChatSessions,
            TeachersWithAssignments = teachersWithAssignments,
            SubjectTeacherLinks = subjectTeacherLinks,
            DocumentsUploadedThisMonth = documentsUploadedInPeriod,
            ChatSessionsThisMonth = chatSessionsInPeriod,
            RegistrationsByMonth = registrationsByMonth,
            TopChatUsers = topChatUsers,
            TopUploaders = topUploaders,
            TokensByMonth = tokensByMonth,
            TokensByYear = tokensByYear,
            PaymentsByMonth = paymentsByMonth,
            PaymentsByYear = paymentsByYear,
            PaymentsByDay = paymentsByDay,
            TotalRevenueInPeriod = totalRevenueInPeriod,
            UserPaymentStats = userPaymentStats,
            KnowledgeBase = knowledgeBase,
            AiRag = aiRag,
            Learning = learning
        };
    }

    internal static (DateTime? FromUtc, DateTime ToUtc, string Key, string Label) ResolveRange(
        string? period,
        DateTime? from,
        DateTime? to)
    {
        var now = DateTime.UtcNow;
        var key = string.IsNullOrWhiteSpace(period) ? "30d" : period.Trim().ToLowerInvariant();

        // Vietnam local day boundaries → UTC
        var vnNow = now.AddHours(7);
        var vnTodayStart = new DateTime(vnNow.Year, vnNow.Month, vnNow.Day, 0, 0, 0, DateTimeKind.Unspecified);
        var todayStartUtc = DateTime.SpecifyKind(vnTodayStart.AddHours(-7), DateTimeKind.Utc);
        var monthStartVn = new DateTime(vnNow.Year, vnNow.Month, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var monthStartUtc = DateTime.SpecifyKind(monthStartVn.AddHours(-7), DateTimeKind.Utc);

        return key switch
        {
            "today" => (todayStartUtc, now, "today", "Hôm nay"),
            "7d" => (now.AddDays(-7), now, "7d", "7 ngày gần đây"),
            "30d" => (now.AddDays(-30), now, "30d", "30 ngày gần đây"),
            "month" => (monthStartUtc, now, "month", "Tháng này"),
            "3m" => (now.AddMonths(-3), now, "3m", "3 tháng gần đây"),
            "6m" => (now.AddMonths(-6), now, "6m", "6 tháng gần đây"),
            "all" => (null, now, "all", "Toàn thời gian"),
            "custom" => ResolveCustom(from, to, now),
            _ => (now.AddDays(-30), now, "30d", "30 ngày gần đây")
        };
    }

    private static (DateTime? FromUtc, DateTime ToUtc, string Key, string Label) ResolveCustom(
        DateTime? from,
        DateTime? to,
        DateTime now)
    {
        var toUtc = to.HasValue
            ? DateTime.SpecifyKind(to.Value.Date.AddDays(1), DateTimeKind.Utc)
            : now;
        var fromUtc = from.HasValue
            ? DateTime.SpecifyKind(from.Value.Date, DateTimeKind.Utc)
            : now.AddDays(-30);

        if (fromUtc >= toUtc)
            fromUtc = toUtc.AddDays(-1);

        var label = $"{fromUtc:dd/MM/yyyy} – {toUtc.AddDays(-1):dd/MM/yyyy}";
        return (fromUtc, toUtc, "custom", label);
    }

    private static List<(int Year, int Month)> EnumerateMonths(DateTime start, DateTime end)
    {
        var cursor = new DateTime(start.Year, start.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var endMonth = new DateTime(end.Year, end.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var list = new List<(int, int)>();
        while (cursor <= endMonth)
        {
            list.Add((cursor.Year, cursor.Month));
            cursor = cursor.AddMonths(1);
        }
        if (list.Count == 0)
            list.Add((end.Year, end.Month));
        return list;
    }

    private static List<MonthlyCountDto> BuildMonthlyCounts(
        DateTime start,
        DateTime end,
        IEnumerable<(int Year, int Month, int Count)> raw)
    {
        var map = raw.ToDictionary(r => (r.Year, r.Month), r => r.Count);
        return EnumerateMonths(start, end).Select(m => new MonthlyCountDto
        {
            Year = m.Year,
            Month = m.Month,
            Label = $"{m.Month:D2}/{m.Year}",
            Count = map.TryGetValue(m, out var c) ? c : 0
        }).ToList();
    }

    private async Task<KnowledgeBaseAnalyticsDto> BuildKnowledgeBaseAsync(DateTime? fromUtc, DateTime? toUtc)
    {
        var docs = _uow.Documents.Query();
        if (fromUtc.HasValue)
            docs = docs.Where(d => d.UploadedAt >= fromUtc && d.UploadedAt < toUtc);

        var totalDocuments = await docs.CountAsync();
        var pending = await docs.CountAsync(d => d.Status == "Pending");
        var processing = await docs.CountAsync(d => d.Status == "Processing");
        var indexed = await docs.CountAsync(d => d.Status == "Indexed");
        var failed = await docs.CountAsync(d => d.Status == "Failed");

        var chunksQuery = _uow.DocumentChunks.Query();
        if (fromUtc.HasValue)
            chunksQuery = chunksQuery.Where(c => c.Document.UploadedAt >= fromUtc && c.Document.UploadedAt < toUtc);

        var totalChunks = await chunksQuery.CountAsync();
        var totalEmbeddings = await chunksQuery
            .CountAsync(c => c.EmbeddingJson != null && c.EmbeddingJson != "[]" && c.EmbeddingJson != "");

        var indexQuery = _uow.DocumentIndexes.Query()
            .Where(i => i.IndexingDurationSeconds != null);
        if (fromUtc.HasValue)
            indexQuery = indexQuery.Where(i => i.IndexedAt >= fromUtc && i.IndexedAt < toUtc);

        var durations = await indexQuery
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

    private async Task<AiRagAnalyticsDto> BuildAiRagAsync(
        IQueryable<DataAccessLayer.Entities.User> users,
        DateTime? fromUtc,
        DateTime? toUtc)
    {
        var sessions = _uow.ChatSessions.Query();
        var histories = _uow.ChatHistories.Query();
        if (fromUtc.HasValue)
        {
            sessions = sessions.Where(s => s.CreatedAt >= fromUtc && s.CreatedAt < toUtc);
            histories = histories.Where(h => h.CreatedAt >= fromUtc && h.CreatedAt < toUtc);
        }

        var totalSessions = await sessions.CountAsync();
        var totalQuestions = await histories.CountAsync(h => h.Role == "user");
        var totalTokens = await histories
            .Where(h => h.TokenCount != null)
            .SumAsync(h => (long?)h.TokenCount) ?? 0L;

        var latencies = await histories
            .Where(h => h.Role == "assistant" && h.LatencyMs != null)
            .Select(h => (double)h.LatencyMs!.Value)
            .ToListAsync();

        var withContext = await histories.CountAsync(h => h.Role == "assistant" && h.HasContext == true);
        var withoutContext = await histories.CountAsync(h => h.Role == "assistant" && h.HasContext == false);
        var contextTotal = withContext + withoutContext;
        if (contextTotal == 0)
        {
            var assistantIds = histories.Where(h => h.Role == "assistant").Select(h => h.ChatHistoryId);
            withContext = await _uow.ChatCitations.Query()
                .Where(c => assistantIds.Contains(c.ChatHistoryId))
                .Select(c => c.ChatHistoryId)
                .Distinct()
                .CountAsync();
            var assistantCount = await histories.CountAsync(h => h.Role == "assistant");
            withoutContext = Math.Max(0, assistantCount - withContext);
            contextTotal = withContext + withoutContext;
        }

        var topSubjects = await sessions
            .GroupBy(s => new { s.SubjectId, s.Subject.SubjectName })
            .Select(g => new NamedCountDto { Name = g.Key.SubjectName, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(5)
            .ToListAsync();

        var citations = _uow.ChatCitations.Query();
        if (fromUtc.HasValue)
            citations = citations.Where(c => c.ChatHistory.CreatedAt >= fromUtc && c.ChatHistory.CreatedAt < toUtc);

        var topDocs = await citations
            .GroupBy(c => new
            {
                c.DocumentChunk.DocumentId,
                Name = c.DocumentChunk.Document.OriginalFileName ?? c.DocumentChunk.Document.FileName
            })
            .Select(g => new NamedCountDto { Name = g.Key.Name, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(5)
            .ToListAsync();

        // Token leaders in period from chat histories
        List<TopUserActivityDto> topTokenUsers;
        if (fromUtc.HasValue)
        {
            topTokenUsers = await histories
                .Where(h => h.TokenCount != null && h.ChatSession.User.Role == "Student")
                .GroupBy(h => new
                {
                    h.ChatSession.UserId,
                    DisplayName = h.ChatSession.User.FullName ?? h.ChatSession.User.Username,
                    h.ChatSession.User.Role
                })
                .Select(g => new TopUserActivityDto
                {
                    UserId = g.Key.UserId,
                    DisplayName = g.Key.DisplayName,
                    Role = g.Key.Role,
                    Count = g.Sum(x => x.TokenCount ?? 0)
                })
                .OrderByDescending(x => x.Count)
                .Take(5)
                .ToListAsync();
        }
        else
        {
            topTokenUsers = await users
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
        }

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

    private async Task<LearningAnalyticsDto> BuildLearningAsync(DateTime? fromUtc, DateTime? toUtc)
    {
        var histories = _uow.ChatHistories.Query();
        if (fromUtc.HasValue)
            histories = histories.Where(h => h.CreatedAt >= fromUtc && h.CreatedAt < toUtc);

        var popularSubjects = await histories
            .Where(h => h.Role == "user")
            .GroupBy(h => h.ChatSession.Subject.SubjectName)
            .Select(g => new NamedCountDto { Name = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(5)
            .ToListAsync();

        var questionTimes = await histories
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

        var citations = _uow.ChatCitations.Query();
        if (fromUtc.HasValue)
            citations = citations.Where(c => c.ChatHistory.CreatedAt >= fromUtc && c.ChatHistory.CreatedAt < toUtc);

        var questionsPerDoc = await citations
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

        var now = toUtc ?? DateTime.UtcNow;
        var last7 = now.AddDays(-7);
        var last30 = now.AddDays(-30);
        // When filtering, active users = distinct users with activity in selected range
        var sessions = _uow.ChatSessions.Query();
        if (fromUtc.HasValue)
        {
            var activeInRange = await sessions
                .Where(s => (s.LastActivityAt ?? s.CreatedAt) >= fromUtc && (s.LastActivityAt ?? s.CreatedAt) < toUtc)
                .Select(s => s.UserId)
                .Distinct()
                .CountAsync();

            return new LearningAnalyticsDto
            {
                PopularSubjects = popularSubjects,
                PeakHours = hourBuckets,
                QuestionsPerDocument = questionsPerDoc,
                ActiveUsersLast7Days = activeInRange,
                ActiveUsersLast30Days = activeInRange
            };
        }

        var active7 = await sessions
            .Where(s => (s.LastActivityAt ?? s.CreatedAt) >= last7)
            .Select(s => s.UserId)
            .Distinct()
            .CountAsync();

        var active30 = await sessions
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
