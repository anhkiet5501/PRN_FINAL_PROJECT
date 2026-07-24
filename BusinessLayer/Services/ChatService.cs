using System.Text;
using BusinessLayer.DTOs;
using BusinessLayer.Helpers;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BusinessLayer.Services;

public interface IChatService
{
    Task<ChatSessionDto?> GetSessionAsync(int sessionId);
    Task<IEnumerable<ChatSessionDto>> GetUserSessionsAsync(int userId);
    Task<ChatSessionDto> CreateSessionAsync(CreateChatSessionDto dto, int userId);
    Task<IEnumerable<ChatMessageDto>> GetMessagesAsync(int sessionId);
    Task<ChatResponseDto> SendMessageAsync(SendMessageDto dto, CancellationToken cancellationToken = default);
    Task<ChatResponseDto> SendMessageStreamingAsync(SendMessageDto dto, Func<string, Task> onChunk, CancellationToken cancellationToken = default);
    Task<bool> DeleteSessionAsync(int sessionId, int userId);
}

public class ChatService : IChatService
{
    private readonly IUnitOfWork _uow;
    private readonly EmbeddingProviderFactory _embeddingFactory;
    private readonly ILogger<ChatService> _logger;
    private readonly IGeminiChatService _geminiChat;
    private readonly ISubscriptionService _subscriptionService;

    private const int MaxHistoryMessages = 10; // context window limit

    public ChatService(
        IUnitOfWork uow,
        EmbeddingProviderFactory embeddingFactory,
        ILogger<ChatService> logger,
        IGeminiChatService geminiChat,
        ISubscriptionService subscriptionService)
    {
        _uow = uow;
        _embeddingFactory = embeddingFactory;
        _logger = logger;
        _geminiChat = geminiChat;
        _subscriptionService = subscriptionService;
    }

    // ── Session Management ────────────────────────────────────────────

    public async Task<ChatSessionDto?> GetSessionAsync(int sessionId)
    {
        return await _uow.ChatSessions.Query()
            .Where(s => s.ChatSessionId == sessionId)
            .Select(s => new ChatSessionDto
            {
                ChatSessionId = s.ChatSessionId,
                SubjectId = s.SubjectId,
                SessionTitle = s.SessionTitle,
                SubjectName = s.Subject.SubjectName,
                AiModelName = s.AiModel.ModelName,
                MessageCount = s.ChatHistories.Count,
                CreatedAt = s.CreatedAt,
                LastActivityAt = s.LastActivityAt
            })
            .FirstOrDefaultAsync();
    }

    public async Task<IEnumerable<ChatSessionDto>> GetUserSessionsAsync(int userId)
    {
        return await _uow.ChatSessions.Query()
            .Where(s => s.UserId == userId && s.IsActive)
            .OrderByDescending(s => s.LastActivityAt ?? s.CreatedAt)
            .Select(s => new ChatSessionDto
            {
                ChatSessionId = s.ChatSessionId,
                SubjectId = s.SubjectId,
                SessionTitle = s.SessionTitle,
                SubjectName = s.Subject.SubjectName,
                AiModelName = s.AiModel.ModelName,
                MessageCount = s.ChatHistories.Count,
                CreatedAt = s.CreatedAt,
                LastActivityAt = s.LastActivityAt
            })
            .ToListAsync();
    }

    public async Task<ChatSessionDto> CreateSessionAsync(CreateChatSessionDto dto, int userId)
    {
        var session = new ChatSession
        {
            UserId = userId,
            SubjectId = dto.SubjectId,
            AiModelId = dto.AiModelId,
            EmbeddingModelId = dto.EmbeddingModelId,
            SessionTitle = dto.SessionTitle,
            TopK = dto.TopK,
            CreatedAt = DateTime.UtcNow,
            IsActive = true
        };
        await _uow.ChatSessions.AddAsync(session);
        await _uow.SaveChangesAsync();

        return await GetSessionAsync(session.ChatSessionId)
            ?? throw new Exception("Session not found after creation");
    }

    public async Task<IEnumerable<ChatMessageDto>> GetMessagesAsync(int sessionId)
    {
        var histories = await _uow.ChatHistories.Query()
            .Where(h => h.ChatSessionId == sessionId)
            .OrderBy(h => h.CreatedAt)
            .Include(h => h.Citations)
                .ThenInclude(c => c.DocumentChunk)
                    .ThenInclude(dc => dc.Document)
                        .ThenInclude(d => d.Chapter)
            .ToListAsync();

        return histories.Select(h => new ChatMessageDto
        {
            ChatHistoryId = h.ChatHistoryId,
            Role = h.Role,
            Content = h.Content,
            LatencyMs = h.LatencyMs,
            TokenCount = h.TokenCount,
            CreatedAt = h.CreatedAt,
            Citations = h.Citations.Select(c => new CitationDto
            {
                DocumentChunkId = c.DocumentChunkId,
                DocumentId = c.DocumentChunk.DocumentId,
                ChunkIndex = c.DocumentChunk.ChunkIndex,
                ChunkText = c.DocumentChunk.ChunkText,
                DocumentName = c.DocumentChunk.Document.OriginalFileName ?? c.DocumentChunk.Document.FileName,
                ChapterName = c.DocumentChunk.Document.Chapter.ChapterName,
                SimilarityScore = c.SimilarityScore,
                RetrievalRank = c.RetrievalRank
            }).ToList()
        });
    }

    // ── Main RAG Chat ─────────────────────────────────────────────────

    public async Task<ChatResponseDto> SendMessageAsync(
        SendMessageDto dto,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Load session
            var session = await _uow.ChatSessions.Query()
                .Include(s => s.AiModel)
                .Include(s => s.EmbeddingModel)
                .FirstOrDefaultAsync(s => s.ChatSessionId == dto.ChatSessionId)
                ?? throw new Exception("Chat session not found");

            var quotaCheck = await _subscriptionService.CheckQuotaAsync(session.UserId);
            if (!quotaCheck.Allowed)
            {
                return new ChatResponseDto { IsError = true, ErrorMessage = quotaCheck.Message };
            }

            // 1. Embed the user question
            var embeddingProvider = _embeddingFactory.Create(session.EmbeddingModel);
            float[] questionVector;
            try
            {
                questionVector = await embeddingProvider.EmbedAsync(dto.Question, cancellationToken);
            }
            catch (RateLimitException)
            {
                return new ChatResponseDto
                {
                    IsError = true,
                    ErrorMessage = "⚠️ Embedding API rate limit reached. Please wait a moment and try again."
                };
            }

            // 2. Retrieve all indexed chunks for this subject (filtered by SelectedDocIds if any)
            var subjectId = session.SubjectId;
            var query = _uow.DocumentChunks.Query()
                .Where(c => c.Document.Chapter.SubjectId == subjectId
                         && c.EmbeddingModelId == session.EmbeddingModelId
                         && c.Document.Status == "Indexed");

            if (dto.SelectedDocIds != null && dto.SelectedDocIds.Count > 0)
            {
                query = query.Where(c => dto.SelectedDocIds.Contains(c.DocumentId));
            }

            var allChunks = await query
                .Select(c => new
                {
                    c.DocumentChunkId,
                    c.DocumentId,
                    c.ChunkIndex,
                    c.ChunkText,
                    c.EmbeddingJson,
                    c.Document.OriginalFileName,
                    ChapterName = c.Document.Chapter.ChapterName
                })
                .ToListAsync();

            if (allChunks.Count == 0 && dto.RestrictToDocs)
            {
                return new ChatResponseDto
                {
                    IsError = true,
                    ErrorMessage = "⚠️ Không tìm thấy tài liệu phù hợp (hoặc bạn chưa chọn tài liệu nào)."
                };
            }

            // 3. Compute cosine similarity → Top-K chunks
            var retrievedChunks = new List<dynamic>();
            if (allChunks.Count > 0)
            {
                var chunkVectors = allChunks
                    .Select(c => VectorHelper.DeserializeEmbedding(c.EmbeddingJson))
                    .ToList();

                var topK = VectorHelper.TopKSimilar(questionVector, chunkVectors, session.TopK);
                retrievedChunks = topK
                    .Select(r => (dynamic)new
                    {
                        allChunks[r.Index].DocumentChunkId,
                        allChunks[r.Index].DocumentId,
                        allChunks[r.Index].ChunkIndex,
                        allChunks[r.Index].ChunkText,
                        allChunks[r.Index].OriginalFileName,
                        allChunks[r.Index].ChapterName,
                        SimilarityScore = r.Score,
                        Rank = r.Index
                    })
                    .ToList();
            }

            // 4. Load recent conversation history for context
            var history = await _uow.ChatHistories.Query()
                .Where(h => h.ChatSessionId == dto.ChatSessionId)
                .OrderByDescending(h => h.CreatedAt)
                .Take(MaxHistoryMessages)
                .OrderBy(h => h.CreatedAt)
                .Select(h => new { h.Role, h.Content })
                .ToListAsync();

            // 5. Build Gemini prompt
            var contextBuilder = new StringBuilder();
            for (int i = 0; i < retrievedChunks.Count; i++)
            {
                var chunk = retrievedChunks[i];
                contextBuilder.AppendLine($"[{i + 1}] (from '{chunk.ChapterName}' — {chunk.OriginalFileName}):");
                contextBuilder.AppendLine(chunk.ChunkText);
                contextBuilder.AppendLine();
            }

            string systemPrompt;
            if (dto.RestrictToDocs)
            {
                systemPrompt = $"""
                    You are an intelligent learning assistant for a course management system.
                    Answer the student's question based ONLY on the provided context below.
                    If the context doesn't contain enough information, say so clearly.
                    IMPORTANT: After every claim taken from context, cite the source as [1], [2], etc. matching the context numbers.
                    Be concise, accurate, and educational.

                    === CONTEXT ===
                    {contextBuilder}
                    === END CONTEXT ===
                    """;
            }
            else
            {
                systemPrompt = $"""
                    You are an intelligent learning assistant for a course management system.
                    You can answer the student's question using your own knowledge, but you should also refer to the provided context below if it's helpful.
                    If you use the provided context, cite the source as [1], [2], etc. after the relevant sentence.
                    Be concise, accurate, and educational.

                    === CONTEXT ===
                    {contextBuilder}
                    === END CONTEXT ===
                    """;
            }

            // Brief pause between embed + generate to avoid burst rate limits on free tier
            await Task.Delay(1200, cancellationToken);

            // 6. Call Gemini API with retry on 429
            var (answer, totalTokens) = await CallGeminiWithRetryAsync(
                session.AiModel.ModelName,
                systemPrompt,
                history.Select(h => (h.Role, h.Content)).ToList(),
                dto.Question,
                cancellationToken);

            stopwatch.Stop();
            var hasContext = retrievedChunks.Count > 0;

            // 7. Save user message to history
            var userMessage = new ChatHistory
            {
                ChatSessionId = dto.ChatSessionId,
                Role = "user",
                Content = dto.Question,
                CreatedAt = DateTime.UtcNow
            };
            await _uow.ChatHistories.AddAsync(userMessage);
            await _uow.SaveChangesAsync();

            // 8. Save assistant response + citations
            var assistantMessage = new ChatHistory
            {
                ChatSessionId = dto.ChatSessionId,
                Role = "assistant",
                Content = answer,
                TokenCount = totalTokens,
                LatencyMs = (int)stopwatch.ElapsedMilliseconds,
                HasContext = hasContext,
                CreatedAt = DateTime.UtcNow
            };
            await _uow.ChatHistories.AddAsync(assistantMessage);
            await _uow.SaveChangesAsync();

            // Save citations
            var citations = retrievedChunks
                .Select((c, idx) => new ChatCitation
                {
                    ChatHistoryId = assistantMessage.ChatHistoryId,
                    DocumentChunkId = c.DocumentChunkId,
                    SimilarityScore = c.SimilarityScore,
                    RetrievalRank = idx + 1
                })
                .ToList();

            await _uow.ChatCitations.AddRangeAsync(citations);

            // Update session last activity + accumulate user tokens
            var sessionEntity = await _uow.ChatSessions.GetByIdAsync(dto.ChatSessionId);
            var userTokensUsed = 0;
            if (sessionEntity != null)
            {
                sessionEntity.LastActivityAt = DateTime.UtcNow;
                _uow.ChatSessions.Update(sessionEntity);

                var user = await _uow.Users.GetByIdAsync(sessionEntity.UserId);
                if (user != null)
                {
                    user.TokensUsed += totalTokens;
                    user.UpdatedAt = DateTime.UtcNow;
                    _uow.Users.Update(user);
                    userTokensUsed = user.TokensUsed;
                    await _subscriptionService.IncrementUsageAsync(user.UserId);
                }
            }
            await _uow.SaveChangesAsync();

            return new ChatResponseDto
            {
                Answer = answer,
                LatencyMs = (int)stopwatch.ElapsedMilliseconds,
                TokensUsedThisMessage = totalTokens,
                UserTokensUsed = userTokensUsed,
                Citations = retrievedChunks.Select((c, idx) => new CitationDto
                {
                    DocumentChunkId = c.DocumentChunkId,
                    DocumentId = c.DocumentId,
                    ChunkIndex = c.ChunkIndex,
                    ChunkText = c.ChunkText,
                    DocumentName = c.OriginalFileName ?? "Unknown",
                    ChapterName = c.ChapterName,
                    SimilarityScore = c.SimilarityScore,
                    RetrievalRank = idx + 1
                }).ToList()
            };
        }
        catch (OperationCanceledException)
        {
            return new ChatResponseDto { IsError = true, ErrorMessage = "Request was cancelled." };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing chat message for session {SessionId}", dto.ChatSessionId);
            return new ChatResponseDto { IsError = true, ErrorMessage = $"An error occurred: {ex.Message}" };
        }
    }

    // ── Gemini Chat — delegate to IGeminiChatService ─────────────────────

    private async Task<(string Text, int TotalTokens)> CallGeminiWithRetryAsync(
        string modelName,
        string systemPrompt,
        List<(string Role, string Content)> history,
        string userQuestion,
        CancellationToken cancellationToken)
    {
        // Build full prompt: system instruction + history + current question
        var promptBuilder = new StringBuilder();
        promptBuilder.AppendLine(systemPrompt);
        promptBuilder.AppendLine();
        foreach (var (role, content) in history)
        {
            var label = role == "assistant" ? "Assistant" : "User";
            promptBuilder.AppendLine($"{label}: {content}");
        }
        promptBuilder.AppendLine($"User: {userQuestion}");
        promptBuilder.AppendLine("Assistant:");

        var answer = await _geminiChat.GenerateAnswerAsync(promptBuilder.ToString(), modelName);

        if (answer.StartsWith("⚠️") || answer.StartsWith("Lỗi khi gọi AI"))
            throw new RateLimitException(answer);

        // Estimate tokens (rough: 1 token ≈ 4 chars)
        var totalTokens = (promptBuilder.Length + answer.Length) / 4;
        return (answer, totalTokens);
    }

    /// <summary>Build a single-string prompt from system prompt + history + current question.</summary>
    private static string BuildGeminiPrompt(
        string systemPrompt,
        List<(string Role, string Content)> history,
        string userQuestion)
    {
        var sb = new StringBuilder();
        sb.AppendLine(systemPrompt);
        sb.AppendLine();
        foreach (var (role, content) in history)
        {
            var label = role == "assistant" ? "Assistant" : "User";
            sb.AppendLine($"{label}: {content}");
        }
        sb.AppendLine($"User: {userQuestion}");
        sb.AppendLine("Assistant:");
        return sb.ToString();
    }

    /// <summary>Send answer word-by-word to client for typing effect (incremental chunks).</summary>
    private static async Task EmitAnswerInChunksAsync(
        string text,
        Func<string, Task> onChunk,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(text)) return;

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < words.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var piece = i == 0 ? words[i] : " " + words[i];
            await onChunk(piece);
            if (i < words.Length - 1)
                await Task.Delay(10, cancellationToken);
        }
    }

    public async Task<ChatResponseDto> SendMessageStreamingAsync(
        SendMessageDto dto,
        Func<string, Task> onChunk,
        CancellationToken cancellationToken = default)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            // Load session
            var session = await _uow.ChatSessions.Query()
                .Include(s => s.AiModel)
                .Include(s => s.EmbeddingModel)
                .FirstOrDefaultAsync(s => s.ChatSessionId == dto.ChatSessionId)
                ?? throw new Exception("Chat session not found");

            var quotaCheck = await _subscriptionService.CheckQuotaAsync(session.UserId);
            if (!quotaCheck.Allowed)
            {
                return new ChatResponseDto { IsError = true, ErrorMessage = quotaCheck.Message };
            }

            // 1. Embed the user question
            var embeddingProvider = _embeddingFactory.Create(session.EmbeddingModel);
            float[] questionVector;
            try
            {
                questionVector = await embeddingProvider.EmbedAsync(dto.Question, cancellationToken);
            }
            catch (RateLimitException)
            {
                return new ChatResponseDto
                {
                    IsError = true,
                    ErrorMessage = "⚠️ Embedding API rate limit reached. Please wait a moment and try again."
                };
            }

            // 2. Retrieve all indexed chunks for this subject (ignore SelectedDocIds per requirements)
            var subjectId = session.SubjectId;
            var query = _uow.DocumentChunks.Query()
                .Where(c => c.Document.Chapter.SubjectId == subjectId
                         && c.EmbeddingModelId == session.EmbeddingModelId
                         && c.Document.Status == "Indexed");

            var allChunks = await query
                .Select(c => new
                {
                    c.DocumentChunkId,
                    c.DocumentId,
                    c.ChunkIndex,
                    c.ChunkText,
                    c.EmbeddingJson,
                    c.Document.OriginalFileName,
                    ChapterName = c.Document.Chapter.ChapterName
                })
                .ToListAsync();

            if (allChunks.Count == 0 && dto.RestrictToDocs)
            {
                return new ChatResponseDto
                {
                    IsError = true,
                    ErrorMessage = "⚠️ Không tìm thấy tài liệu phù hợp (hoặc bạn chưa chọn tài liệu nào)."
                };
            }

            // 3. Compute cosine similarity → Top-K chunks
            var retrievedChunks = new List<dynamic>();
            if (allChunks.Count > 0)
            {
                var chunkVectors = allChunks
                    .Select(c => VectorHelper.DeserializeEmbedding(c.EmbeddingJson))
                    .ToList();

                var topK = VectorHelper.TopKSimilar(questionVector, chunkVectors, session.TopK);
                retrievedChunks = topK
                    .Select(r => (dynamic)new
                    {
                        allChunks[r.Index].DocumentChunkId,
                        allChunks[r.Index].DocumentId,
                        allChunks[r.Index].ChunkIndex,
                        allChunks[r.Index].ChunkText,
                        allChunks[r.Index].OriginalFileName,
                        allChunks[r.Index].ChapterName,
                        SimilarityScore = r.Score,
                        Rank = r.Index
                    })
                    .ToList();
            }

            // 4. Load recent conversation history for context
            var history = await _uow.ChatHistories.Query()
                .Where(h => h.ChatSessionId == dto.ChatSessionId)
                .OrderByDescending(h => h.CreatedAt)
                .Take(MaxHistoryMessages)
                .OrderBy(h => h.CreatedAt)
                .Select(h => new { h.Role, h.Content })
                .ToListAsync();

            // 5. Build Gemini prompt
            var contextBuilder = new StringBuilder();
            for (int i = 0; i < retrievedChunks.Count; i++)
            {
                var chunk = retrievedChunks[i];
                contextBuilder.AppendLine($"[{i + 1}] (from '{chunk.ChapterName}' — {chunk.OriginalFileName}):");
                contextBuilder.AppendLine(chunk.ChunkText);
                contextBuilder.AppendLine();
            }

            string systemPrompt;
            if (dto.RestrictToDocs)
            {
                systemPrompt = $"""
                    Bạn là một trợ lý AI thông minh cho hệ thống học tập.
                    BẠN CHỈ ĐƯỢC PHÉP trả lời dựa trên nội dung tài liệu (CONTEXT) bên dưới. Tuyệt đối không được sử dụng kiến thức bên ngoài.
                    Nếu ngữ cảnh không có thông tin để trả lời, hãy nói: "Tôi không tìm thấy thông tin trong tài liệu".
                    Nếu người dùng yêu cầu thay đổi ngôn ngữ (vd: dịch sang tiếng Anh) hoặc định dạng câu trả lời (vd: lập bảng, tóm tắt), hãy ÁP DỤNG yêu cầu đó cho nội dung lấy từ CONTEXT. Bạn không được từ chối việc định dạng nếu trong CONTEXT có dữ liệu.
                    BẮT BUỘC: Sau mỗi ý lấy từ CONTEXT, ghi chú nguồn dạng [1], [2], ... khớp với số CONTEXT.

                    === CONTEXT ===
                    {contextBuilder}
                    === END CONTEXT ===
                    """;
            }
            else
            {
                systemPrompt = $"""
                    Bạn là một trợ lý AI thông minh cho hệ thống học tập.
                    Bạn có thể sử dụng kiến thức bên ngoài, nhưng hãy ưu tiên sử dụng nội dung tài liệu (CONTEXT) bên dưới nếu có liên quan.
                    Nếu người dùng yêu cầu thay đổi ngôn ngữ (vd: dịch sang tiếng Anh) hoặc định dạng câu trả lời, hãy thực hiện theo.
                    Nếu bạn sử dụng thông tin từ CONTEXT, BẮT BUỘC ghi chú nguồn dạng [1], [2], ... sau câu tương ứng.

                    === CONTEXT ===
                    {contextBuilder}
                    === END CONTEXT ===
                    """;
            }

            // Brief pause between embed + generate to avoid burst rate limits on free tier
            await Task.Delay(1200, cancellationToken);

            // 6. Call Gemini API via streaming (real SSE) or fallback to blocking
            var chatModel = session.AiModel.ModelName;
            string fullAnswer;
            try
            {
                var sb = new StringBuilder();
                await foreach (var chunk in _geminiChat.GenerateStreamingAnswerAsync(
                    BuildGeminiPrompt(systemPrompt, history.Select(h => (h.Role, h.Content)).ToList(), dto.Question),
                    chatModel, cancellationToken))
                {
                    sb.Append(chunk);
                    await onChunk(chunk);
                }
                fullAnswer = sb.Length > 0 ? sb.ToString() : "No response generated.";
            }
            catch
            {
                // Fallback to blocking if streaming fails
                var (fallbackAnswer, _) = await CallGeminiWithRetryAsync(
                    chatModel, systemPrompt,
                    history.Select(h => (h.Role, h.Content)).ToList(),
                    dto.Question, cancellationToken);
                fullAnswer = fallbackAnswer;
                await EmitAnswerInChunksAsync(fullAnswer, onChunk, cancellationToken);
            }

            stopwatch.Stop();

            // 7. Save user message to history
            var userMessage = new ChatHistory
            {
                ChatSessionId = dto.ChatSessionId,
                Role = "user",
                Content = dto.Question,
                TokenCount = dto.Question.Split(' ').Length,
                CreatedAt = DateTime.UtcNow
            };
            await _uow.ChatHistories.AddAsync(userMessage);
            await _uow.SaveChangesAsync();

            // 8. Save assistant response + citations
            var assistantMessage = new ChatHistory
            {
                ChatSessionId = dto.ChatSessionId,
                Role = "assistant",
                Content = fullAnswer,
                TokenCount = fullAnswer.Split(' ').Length,
                LatencyMs = (int)stopwatch.ElapsedMilliseconds,
                CreatedAt = DateTime.UtcNow
            };
            await _uow.ChatHistories.AddAsync(assistantMessage);
            await _uow.SaveChangesAsync();

            var citations = retrievedChunks
                .Select((c, idx) => new ChatCitation
                {
                    ChatHistoryId = assistantMessage.ChatHistoryId,
                    DocumentChunkId = c.DocumentChunkId,
                    SimilarityScore = c.SimilarityScore,
                    RetrievalRank = idx + 1
                })
                .ToList();
            await _uow.ChatCitations.AddRangeAsync(citations);

            // Update session last activity & User Question Counts
            var sessionEntity = await _uow.ChatSessions.GetByIdAsync(dto.ChatSessionId);
            if (sessionEntity != null)
            {
                sessionEntity.LastActivityAt = DateTime.UtcNow;
                _uow.ChatSessions.Update(sessionEntity);

                var user = await _uow.Users.GetByIdAsync(sessionEntity.UserId);
                if (user != null)
                {
                    await _subscriptionService.IncrementUsageAsync(user.UserId);
                }
            }
            await _uow.SaveChangesAsync();

            return new ChatResponseDto
            {
                Answer = fullAnswer,
                LatencyMs = (int)stopwatch.ElapsedMilliseconds,
                Citations = retrievedChunks.Select((c, idx) => new CitationDto
                {
                    DocumentChunkId = c.DocumentChunkId,
                    DocumentId = c.DocumentId,
                    ChunkIndex = c.ChunkIndex,
                    ChunkText = c.ChunkText,
                    DocumentName = c.OriginalFileName ?? "Unknown",
                    ChapterName = c.ChapterName,
                    SimilarityScore = c.SimilarityScore,
                    RetrievalRank = idx + 1
                }).ToList()
            };
        }
        catch (RateLimitException)
        {
            return new ChatResponseDto
            {
                IsError = true,
                ErrorMessage = "⚠️ Gemini API đang bận — vui lòng chờ 30 giây rồi thử lại. (API rate limit)"
            };
        }
        catch (OperationCanceledException)
        {
            return new ChatResponseDto { IsError = true, ErrorMessage = "Request was cancelled." };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error in streaming chat for session {SessionId}", dto.ChatSessionId);
            return new ChatResponseDto { IsError = true, ErrorMessage = $"An error occurred: {ex.Message}" };
        }
    }



    public async Task<bool> DeleteSessionAsync(int sessionId, int userId)
    {
        var session = await _uow.ChatSessions
            .FirstOrDefaultAsync(s => s.ChatSessionId == sessionId && s.UserId == userId);
        if (session is null) return false;

        session.IsActive = false;
        _uow.ChatSessions.Update(session);
        await _uow.SaveChangesAsync();
        return true;
    }
}

