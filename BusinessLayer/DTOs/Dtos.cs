namespace BusinessLayer.DTOs;

// ── Auth DTOs ────────────────────────────────────────────────────────

public class LoginDto
{
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public bool RememberMe { get; set; }
}

public class UserDto
{
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public string? FullName { get; set; }
    public bool IsActive { get; set; }
    public int TokensUsed { get; set; }
    public DateTime CreatedAt { get; set; }
}

public class CreateUserDto
{
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string Role { get; set; } = "Student";
    public string? FullName { get; set; }
}

public class UpdateUserDto
{
    public string? FullName { get; set; }
    public string Role { get; set; } = string.Empty;
    public string? Email { get; set; }
    public string? Password { get; set; }
}

// ── Subject / Chapter DTOs ───────────────────────────────────────────

public class SubjectDto
{
    public int SubjectId { get; set; }
    public string SubjectCode { get; set; } = string.Empty;
    public string SubjectName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public int ChapterCount { get; set; }
    public int DocumentCount { get; set; }
}

public class CreateSubjectDto
{
    public string SubjectCode { get; set; } = string.Empty;
    public string SubjectName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int? TeacherId { get; set; }
}

public class ChapterDto
{
    public int ChapterId { get; set; }
    public int SubjectId { get; set; }
    public string SubjectName { get; set; } = string.Empty;
    public string ChapterName { get; set; } = string.Empty;
    public int OrderIndex { get; set; }
    public string? Description { get; set; }
    public int DocumentCount { get; set; }
    public List<DocumentDto> Documents { get; set; } = new();
}

public class CreateChapterDto
{
    public int SubjectId { get; set; }
    public string ChapterName { get; set; } = string.Empty;
    public int OrderIndex { get; set; }
    public string? Description { get; set; }
}

// ── Document DTOs ────────────────────────────────────────────────────

public class DocumentDto
{
    public int DocumentId { get; set; }
    public int ChapterId { get; set; }
    public string ChapterName { get; set; } = string.Empty;
    public string SubjectName { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string? OriginalFileName { get; set; }
    public string? FileType { get; set; }
    public long FileSizeBytes { get; set; }
    public string Status { get; set; } = string.Empty;
    public string? ErrorMessage { get; set; }
    public int? TotalChunks { get; set; }
    public DateTime UploadedAt { get; set; }
    public DateTime? IndexedAt { get; set; }
    public string UploadedByFullName { get; set; } = string.Empty;
}

public class UploadDocumentDto
{
    public int ChapterId { get; set; }
    public int EmbeddingModelId { get; set; }
    public int ChunkingStrategyId { get; set; }
    /// <summary>Raw file bytes — populated by the Razor Page before calling the service</summary>
    public byte[] FileBytes { get; set; } = [];
    public string OriginalFileName { get; set; } = string.Empty;
    public string FileType { get; set; } = string.Empty;
    public long FileSizeBytes { get; set; }
}

// ── Chat DTOs ────────────────────────────────────────────────────────

public class ChatSessionDto
{
    public int ChatSessionId { get; set; }
    public int SubjectId { get; set; }
    public string SessionTitle { get; set; } = string.Empty;
    public string SubjectName { get; set; } = string.Empty;
    public string AiModelName { get; set; } = string.Empty;
    public int MessageCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastActivityAt { get; set; }
}

public class CreateChatSessionDto
{
    public int SubjectId { get; set; }
    public int AiModelId { get; set; }
    public int EmbeddingModelId { get; set; }
    public string SessionTitle { get; set; } = "New Chat";
    public int TopK { get; set; } = 3;
}

public class ChatMessageDto
{
    public int ChatHistoryId { get; set; }
    public string Role { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public int? LatencyMs { get; set; }
    public int? TokenCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public List<CitationDto> Citations { get; set; } = [];
}

public class CitationDto
{
    public int DocumentChunkId { get; set; }
    public string ChunkText { get; set; } = string.Empty;
    public string DocumentName { get; set; } = string.Empty;
    public string ChapterName { get; set; } = string.Empty;
    public double SimilarityScore { get; set; }
    public int RetrievalRank { get; set; }
}

public class SendMessageDto
{
    public int ChatSessionId { get; set; }
    public string Question { get; set; } = string.Empty;
    public bool RestrictToDocs { get; set; } = true;
    public List<int> SelectedDocIds { get; set; } = [];
}

public class ChatResponseDto
{
    public string Answer { get; set; } = string.Empty;
    public int LatencyMs { get; set; }
    public int TokensUsedThisMessage { get; set; }
    public int UserTokensUsed { get; set; }
    public List<CitationDto> Citations { get; set; } = [];
    public bool IsError { get; set; }
    public string? ErrorMessage { get; set; }
}

// ── Embedding / Chunking DTOs ────────────────────────────────────────

public class EmbeddingModelDto
{
    public int EmbeddingModelId { get; set; }
    public string ModelName { get; set; } = string.Empty;
    public string Provider { get; set; } = string.Empty;
    public int VectorDimension { get; set; }
    public bool IsDefault { get; set; }
    public bool IsActive { get; set; }
}

public class ChunkingStrategyDto
{
    public int ChunkingStrategyId { get; set; }
    public string StrategyName { get; set; } = string.Empty;
    public string StrategyType { get; set; } = string.Empty;
    public int ChunkSize { get; set; }
    public int ChunkOverlap { get; set; }
    public bool IsDefault { get; set; }
}

// ── Benchmark DTOs ───────────────────────────────────────────────────

public class ExperimentDto
{
    public int ExperimentId { get; set; }
    public int SubjectId { get; set; }
    public string ExperimentName { get; set; } = string.Empty;
    public string SubjectName { get; set; } = string.Empty;
    public string EmbeddingModelName { get; set; } = string.Empty;
    public string AiModelName { get; set; } = string.Empty;
    public string ChunkingStrategyName { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public int TestSetCount { get; set; }
    public double? AvgFaithfulness { get; set; }
    public double? AvgRelevance { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
}

public class CreateExperimentDto
{
    public int SubjectId { get; set; }
    public int EmbeddingModelId { get; set; }
    public int AiModelId { get; set; }
    public int ChunkingStrategyId { get; set; }
    public string ExperimentName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public int TopK { get; set; } = 3;
}

public class BenchmarkResultDto
{
    public int BenchmarkResultId { get; set; }
    public string Question { get; set; } = string.Empty;
    public string ExpectedAnswer { get; set; } = string.Empty;
    public string? GeneratedAnswer { get; set; }
    public decimal? FaithfulnessScore { get; set; }
    public decimal? RelevanceScore { get; set; }
    public decimal? ContextRecallScore { get; set; }
    public decimal? AnswerSimilarityScore { get; set; }
    public int? LatencyMs { get; set; }
    public string? ErrorMessage { get; set; }
}

public class TestSetDto
{
    public int TestSetId { get; set; }
    public string Question { get; set; } = string.Empty;
    public string ExpectedAnswer { get; set; } = string.Empty;
    public int OrderIndex { get; set; }
}

// ── Admin Statistics DTOs ────────────────────────────────────────────

public class MonthlyCountDto
{
    public int Year { get; set; }
    public int Month { get; set; }
    public string Label { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class TopUserActivityDto
{
    public int UserId { get; set; }
    public string DisplayName { get; set; } = string.Empty;
    public string Role { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class NamedCountDto
{
    public string Name { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class HourlyCountDto
{
    public int Hour { get; set; }
    public string Label { get; set; } = string.Empty;
    public int Count { get; set; }
}

public class KnowledgeBaseAnalyticsDto
{
    public int TotalDocuments { get; set; }
    public int PendingDocuments { get; set; }
    public int ProcessingDocuments { get; set; }
    public int IndexedDocuments { get; set; }
    public int FailedDocuments { get; set; }
    public int TotalChunks { get; set; }
    public int TotalEmbeddings { get; set; }
    public double? AvgIndexingSeconds { get; set; }
    public double? P95IndexingSeconds { get; set; }
    public double ErrorRatePercent { get; set; }
}

public class AiRagAnalyticsDto
{
    public int TotalChatSessions { get; set; }
    public int TotalQuestions { get; set; }
    public long TotalTokens { get; set; }
    public double? AvgLatencyMs { get; set; }
    public double? P95LatencyMs { get; set; }
    public int AnswersWithContext { get; set; }
    public int AnswersWithoutContext { get; set; }
    public double ContextHitRatePercent { get; set; }
    public List<NamedCountDto> TopSubjectsAsked { get; set; } = new();
    public List<NamedCountDto> TopRetrievedDocuments { get; set; } = new();
    public List<TopUserActivityDto> TopTokenUsers { get; set; } = new();
}

public class LearningAnalyticsDto
{
    public List<NamedCountDto> PopularSubjects { get; set; } = new();
    public List<HourlyCountDto> PeakHours { get; set; } = new();
    public List<NamedCountDto> QuestionsPerDocument { get; set; } = new();
    public int ActiveUsersLast7Days { get; set; }
    public int ActiveUsersLast30Days { get; set; }
}

public class MonthlyRevenueDto
{
    public int Year { get; set; }
    public int Month { get; set; }
    public string Label { get; set; } = string.Empty;
    public decimal Revenue { get; set; }
}

public class UserPaymentStatsDto
{
    public int UserId { get; set; }
    public string FullName { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string CurrentPlan { get; set; } = string.Empty;
    public int TotalTokensUsed { get; set; }
    public decimal TotalMoneySpent { get; set; }
}

public class AdminStatisticsDto
{
    public string Period { get; set; } = "30d";
    public string PeriodLabel { get; set; } = "30 ngày gần đây";
    public DateTime? FromUtc { get; set; }
    public DateTime? ToUtc { get; set; }

    public int TotalUsers { get; set; }
    public int AdminCount { get; set; }
    public int TeacherCount { get; set; }
    public int StudentCount { get; set; }
    public int ActiveUsers { get; set; }
    public int InactiveUsers { get; set; }
    public int NewUsersThisMonth { get; set; }

    public int TotalSubjects { get; set; }
    public int TotalDocuments { get; set; }
    public int TotalChatSessions { get; set; }

    public int TeachersWithAssignments { get; set; }
    public int SubjectTeacherLinks { get; set; }
    public int DocumentsUploadedThisMonth { get; set; }
    public int ChatSessionsThisMonth { get; set; }

    public List<MonthlyCountDto> RegistrationsByMonth { get; set; } = new();
    public List<TopUserActivityDto> TopChatUsers { get; set; } = new();
    public List<TopUserActivityDto> TopUploaders { get; set; } = new();

    public List<MonthlyCountDto> TokensByMonth { get; set; } = new();
    public List<MonthlyRevenueDto> PaymentsByMonth { get; set; } = new();
    public List<UserPaymentStatsDto> UserPaymentStats { get; set; } = new();

    public KnowledgeBaseAnalyticsDto KnowledgeBase { get; set; } = new();
    public AiRagAnalyticsDto AiRag { get; set; } = new();
    public LearningAnalyticsDto Learning { get; set; } = new();
}
