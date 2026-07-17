using System.Net;
using System.Text;
using System.Text.Json;
using BusinessLayer.DTOs;
using BusinessLayer.Helpers;
using BusinessLayer.Models;
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
    private readonly ApiKeysSettings _apiKeys;

    private const int MaxHistoryMessages = 10; // context window limit

    public ChatService(
        IUnitOfWork uow,
        EmbeddingProviderFactory embeddingFactory,
        ILogger<ChatService> logger,
        ApiKeysSettings apiKeys)
    {
        _uow = uow;
        _embeddingFactory = embeddingFactory;
        _logger = logger;
        _apiKeys = apiKeys;
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
                contextBuilder.AppendLine($"[Context {i + 1}] (from '{chunk.ChapterName}' — {chunk.OriginalFileName}):");
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
                    Always cite which context number(s) you used.
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
                    If you use the provided context, cite which context number(s) you used.
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

    // ── Gemini Chat API Call with 429 Retry ──────────────────────────

    /// <summary>Upgrade deprecated/low-quota models to gemini-2.0-flash-lite (higher RPM).</summary>
    private static string NormalizeChatModelName(string modelName)
    {
        if (string.IsNullOrWhiteSpace(modelName))
            return "gemini-2.0-flash-lite";

        return modelName.Trim().ToLowerInvariant() switch
        {
            "gemini-1.0-flash-lite" or "gemini-1.5-flash" or "gemini-1.5-flash-lite"
                or "gemini-2.0-flash" or "gemini-pro" => "gemini-2.0-flash-lite",
            _ => modelName.Trim()
        };
    }

    private static int GetRateLimitDelayMs(HttpResponseMessage? response, int attempt)
    {
        // Free tier often needs longer waits — start at 5s, exponential backoff
        var delayMs = Math.Max(5000, 5000 * (int)Math.Pow(2, attempt - 1));
        if (response?.Headers.TryGetValues("Retry-After", out var values) == true
            && int.TryParse(values.FirstOrDefault(), out var retryAfter))
        {
            delayMs = Math.Max(delayMs, retryAfter * 1000);
        }

        return delayMs;
    }

    private async Task<(string Text, int TotalTokens)> CallGeminiWithRetryAsync(
        string modelName,
        string systemPrompt,
        List<(string Role, string Content)> history,
        string userQuestion,
        CancellationToken cancellationToken)
    {
        modelName = NormalizeChatModelName(modelName);
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:generateContent?key={_apiKeys.Gemini}";

        // Build contents array (history + current question)
        var contents = new List<object>();
        foreach (var (role, content) in history)
        {
            var geminiRole = role == "assistant" ? "model" : "user";
            contents.Add(new { role = geminiRole, parts = new[] { new { text = content } } });
        }
        contents.Add(new { role = "user", parts = new[] { new { text = userQuestion } } });

        var payload = new
        {
            system_instruction = new { parts = new[] { new { text = systemPrompt } } },
            contents
        };

        using var httpClient = new HttpClient();
        int attempt = 0;
        const int maxRetries = 5;

        while (true)
        {
            try
            {
                var json = JsonSerializer.Serialize(payload);
                using var content = new StringContent(json, Encoding.UTF8, "application/json");
                using var response = await httpClient.PostAsync(url, content, cancellationToken);

                // Handle 429 Rate Limit
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    attempt++;
                    if (attempt >= maxRetries)
                        throw new RateLimitException("Gemini chat API rate limit exceeded after max retries");

                    var delayMs = GetRateLimitDelayMs(response, attempt);
                    _logger.LogWarning("Gemini API rate limit (429) hit. Retry attempt {Attempt}/{MaxRetries} waiting {Delay}ms before retrying.", attempt, maxRetries, delayMs);
                    await Task.Delay(delayMs, cancellationToken);
                    continue;
                }

                // Handle 503 Service Unavailable (overloaded)
                if (response.StatusCode == HttpStatusCode.ServiceUnavailable)
                {
                    attempt++;
                    if (attempt >= maxRetries) 
                    {
                        _logger.LogError("Gemini API is unavailable after {MaxRetries} attempts.", maxRetries);
                        throw new Exception("Gemini API unavailable");
                    }
                    var retryDelay = 3000 * attempt;
                    _logger.LogWarning("Gemini API service unavailable (503). Retrying attempt {Attempt}/{MaxRetries} in {Delay}ms.", attempt, maxRetries, retryDelay);
                    await Task.Delay(retryDelay, cancellationToken);
                    continue;
                }

                response.EnsureSuccessStatusCode();

                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                return ParseGeminiResponse(body);
            }
            catch (RateLimitException) { throw; }
            catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.TooManyRequests)
            {
                attempt++;
                if (attempt >= maxRetries) throw;
                await Task.Delay(2000 * (int)Math.Pow(2, attempt), cancellationToken);
            }
        }
    }

    private static (string Text, int TotalTokens) ParseGeminiResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var text = root
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text")
            .GetString() ?? "No response generated.";

        var totalTokens = 0;
        if (root.TryGetProperty("usageMetadata", out var usage))
        {
            if (usage.TryGetProperty("totalTokenCount", out var totalProp) && totalProp.TryGetInt32(out var total))
                totalTokens = total;
            else
            {
                var prompt = usage.TryGetProperty("promptTokenCount", out var p) && p.TryGetInt32(out var pv) ? pv : 0;
                var candidates = usage.TryGetProperty("candidatesTokenCount", out var c) && c.TryGetInt32(out var cv) ? cv : 0;
                totalTokens = prompt + candidates;
            }
        }

        return (text, totalTokens);
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
                contextBuilder.AppendLine($"[Context {i + 1}] (from '{chunk.ChapterName}' — {chunk.OriginalFileName}):");
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
                    Always cite which context number(s) you used.
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
                    If you use the provided context, cite which context number(s) you used.
                    Be concise, accurate, and educational.

                    === CONTEXT ===
                    {contextBuilder}
                    === END CONTEXT ===
                    """;
            }

            // Brief pause between embed + generate to avoid burst rate limits on free tier
            await Task.Delay(1200, cancellationToken);

            // 6. Call Gemini API (non-streaming — more reliable on free tier) + simulate typing
            var chatModel = NormalizeChatModelName(session.AiModel.ModelName);
            if (chatModel != session.AiModel.ModelName)
            {
                _logger.LogWarning(
                    "Chat model '{OldModel}' upgraded to '{NewModel}' for higher rate limit.",
                    session.AiModel.ModelName, chatModel);
            }

            var (fullAnswer, _) = await CallGeminiWithRetryAsync(
                chatModel,
                systemPrompt,
                history.Select(h => (h.Role, h.Content)).ToList(),
                dto.Question,
                cancellationToken);

            await EmitAnswerInChunksAsync(fullAnswer, onChunk, cancellationToken);

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

            // Update session last activity
            var sessionEntity = await _uow.ChatSessions.GetByIdAsync(dto.ChatSessionId);
            if (sessionEntity != null)
            {
                sessionEntity.LastActivityAt = DateTime.UtcNow;
                _uow.ChatSessions.Update(sessionEntity);
            }
            await _uow.SaveChangesAsync();

            return new ChatResponseDto
            {
                Answer = fullAnswer,
                LatencyMs = (int)stopwatch.ElapsedMilliseconds,
                Citations = retrievedChunks.Select((c, idx) => new CitationDto
                {
                    DocumentChunkId = c.DocumentChunkId,
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

    private async Task<string> CallGeminiStreamingAsync(
        string modelName,
        string systemPrompt,
        List<(string Role, string Content)> history,
        string userQuestion,
        Func<string, Task> onChunk,
        CancellationToken cancellationToken)
    {
        modelName = NormalizeChatModelName(modelName);
        var url = $"https://generativelanguage.googleapis.com/v1beta/models/{modelName}:streamGenerateContent?alt=sse&key={_apiKeys.Gemini}";

        var contents = new List<object>();
        foreach (var (role, content) in history)
        {
            var geminiRole = role == "assistant" ? "model" : "user";
            contents.Add(new { role = geminiRole, parts = new[] { new { text = content } } });
        }
        contents.Add(new { role = "user", parts = new[] { new { text = userQuestion } } });

        var payload = new
        {
            system_instruction = new { parts = new[] { new { text = systemPrompt } } },
            contents
        };

        var json = JsonSerializer.Serialize(payload);

        int attempt = 0;
        const int maxStreamingRetries = 2;

        while (true)
        {
            using var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
            using var requestContent = new StringContent(json, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = requestContent };
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

            // Handle 429 Rate Limit — retry streaming briefly, then fall back to generateContent
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                attempt++;
                if (attempt >= maxStreamingRetries)
                {
                    _logger.LogWarning(
                        "Streaming rate limited after {Attempt} attempts — falling back to generateContent.",
                        attempt);
                    var (fallbackText, _) = await CallGeminiWithRetryAsync(
                        modelName, systemPrompt, history, userQuestion, cancellationToken);
                    await onChunk(fallbackText);
                    return fallbackText;
                }

                var delayMs = GetRateLimitDelayMs(response, attempt);
                _logger.LogWarning(
                    "Gemini streaming API rate limit (429) hit. Retry attempt {Attempt}/{MaxRetries} waiting {Delay}ms.",
                    attempt, maxStreamingRetries, delayMs);
                await Task.Delay(delayMs, cancellationToken);
                continue;
            }

            response.EnsureSuccessStatusCode();

            var fullText = new StringBuilder();
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var reader = new System.IO.StreamReader(stream);


        while (!reader.EndOfStream && !cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (!line.StartsWith("data: ")) continue;

            var dataJson = line["data: ".Length..];
            if (dataJson == "[DONE]") break;

            try
            {
                using var doc = JsonDocument.Parse(dataJson);
                var text = doc.RootElement
                    .GetProperty("candidates")[0]
                    .GetProperty("content")
                    .GetProperty("parts")[0]
                    .GetProperty("text")
                    .GetString();

                if (!string.IsNullOrEmpty(text))
                {
                    fullText.Append(text);
                    await onChunk(text);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not parse SSE chunk: {Line}", line);
            }
        }

            return fullText.Length > 0 ? fullText.ToString() : "No response generated.";
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

