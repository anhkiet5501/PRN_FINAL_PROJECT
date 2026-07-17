using BusinessLayer.DTOs;
using BusinessLayer.Helpers;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BusinessLayer.Services;

public interface IBenchmarkService
{
    Task<IEnumerable<ExperimentDto>> GetExperimentsAsync(int subjectId);
    Task<ExperimentDto?> GetExperimentAsync(int experimentId);
    Task<ExperimentDto> CreateExperimentAsync(CreateExperimentDto dto);
    Task<bool> AddTestCaseAsync(int experimentId, string question, string expectedAnswer);
    Task<int> AddSampleTestCasesAsync(int experimentId);
    Task RunExperimentAsync(int experimentId, CancellationToken cancellationToken = default);
    Task<IEnumerable<BenchmarkResultDto>> GetResultsAsync(int experimentId);
    Task<IEnumerable<TestSetDto>> GetTestSetsAsync(int experimentId);
}

public class BenchmarkService : IBenchmarkService
{
    private readonly IUnitOfWork _uow;
    private readonly IChatService _chatService;
    private readonly EmbeddingProviderFactory _embeddingFactory;
    private readonly ILogger<BenchmarkService> _logger;

    public BenchmarkService(
        IUnitOfWork uow,
        IChatService chatService,
        EmbeddingProviderFactory embeddingFactory,
        ILogger<BenchmarkService> logger)
    {
        _uow = uow;
        _chatService = chatService;
        _embeddingFactory = embeddingFactory;
        _logger = logger;
    }

    public async Task<IEnumerable<ExperimentDto>> GetExperimentsAsync(int subjectId)
    {
        return await _uow.Experiments.Query()
            .Where(e => e.SubjectId == subjectId)
            .OrderByDescending(e => e.CreatedAt)
            .Select(e => new ExperimentDto
            {
                ExperimentId = e.ExperimentId,
                ExperimentName = e.ExperimentName,
                SubjectName = e.Subject.SubjectName,
                EmbeddingModelName = e.EmbeddingModel.ModelName,
                AiModelName = e.AiModel.ModelName,
                ChunkingStrategyName = e.ChunkingStrategy.StrategyName,
                Status = e.Status,
                TestSetCount = e.TestSets.Count,
                AvgFaithfulness = e.BenchmarkResults.Any()
                    ? (double?)e.BenchmarkResults.Average(r => (double?)r.FaithfulnessScore) : null,
                AvgRelevance = e.BenchmarkResults.Any()
                    ? (double?)e.BenchmarkResults.Average(r => (double?)r.RelevanceScore) : null,
                CreatedAt = e.CreatedAt,
                CompletedAt = e.CompletedAt
            })
            .ToListAsync();
    }

    public async Task<ExperimentDto?> GetExperimentAsync(int experimentId)
    {
        return await _uow.Experiments.Query()
            .Where(e => e.ExperimentId == experimentId)
            .Select(e => new ExperimentDto
            {
                ExperimentId = e.ExperimentId,
                ExperimentName = e.ExperimentName,
                SubjectId = e.SubjectId,
                SubjectName = e.Subject.SubjectName,
                EmbeddingModelName = e.EmbeddingModel.ModelName,
                AiModelName = e.AiModel.ModelName,
                ChunkingStrategyName = e.ChunkingStrategy.StrategyName,
                Status = e.Status,
                TestSetCount = e.TestSets.Count,
                AvgFaithfulness = e.BenchmarkResults.Any()
                    ? (double?)e.BenchmarkResults.Average(r => (double?)r.FaithfulnessScore) : null,
                AvgRelevance = e.BenchmarkResults.Any()
                    ? (double?)e.BenchmarkResults.Average(r => (double?)r.RelevanceScore) : null,
                CreatedAt = e.CreatedAt,
                CompletedAt = e.CompletedAt
            })
            .FirstOrDefaultAsync();
    }

    public async Task<ExperimentDto> CreateExperimentAsync(CreateExperimentDto dto)
    {
        var experiment = new Experiment
        {
            SubjectId = dto.SubjectId,
            EmbeddingModelId = dto.EmbeddingModelId,
            AiModelId = dto.AiModelId,
            ChunkingStrategyId = dto.ChunkingStrategyId,
            ExperimentName = dto.ExperimentName,
            Description = dto.Description,
            TopK = dto.TopK,
            Status = "Pending",
            CreatedAt = DateTime.UtcNow
        };
        await _uow.Experiments.AddAsync(experiment);
        await _uow.SaveChangesAsync();

        return await _uow.Experiments.Query()
            .Where(e => e.ExperimentId == experiment.ExperimentId)
            .Select(e => new ExperimentDto
            {
                ExperimentId = e.ExperimentId,
                ExperimentName = e.ExperimentName,
                SubjectName = e.Subject.SubjectName,
                EmbeddingModelName = e.EmbeddingModel.ModelName,
                AiModelName = e.AiModel.ModelName,
                ChunkingStrategyName = e.ChunkingStrategy.StrategyName,
                Status = e.Status,
                TestSetCount = 0,
                CreatedAt = e.CreatedAt
            })
            .FirstAsync();
    }

    public async Task<bool> AddTestCaseAsync(int experimentId, string question, string expectedAnswer)
    {
        var count = await _uow.TestSets.CountAsync(t => t.ExperimentId == experimentId);
        var testCase = new TestSet
        {
            ExperimentId = experimentId,
            Question = question,
            ExpectedAnswer = expectedAnswer,
            OrderIndex = count + 1,
            CreatedAt = DateTime.UtcNow
        };
        await _uow.TestSets.AddAsync(testCase);
        await _uow.SaveChangesAsync();
        return true;
    }

    public async Task<int> AddSampleTestCasesAsync(int experimentId)
    {
        var samples = GetDefaultSampleTestCases();

        var added = 0;
        foreach (var (question, expected) in samples)
        {
            var exists = await _uow.TestSets.AnyAsync(t =>
                t.ExperimentId == experimentId && t.Question == question);
            if (exists) continue;

            await AddTestCaseAsync(experimentId, question, expected);
            added++;
        }

        return added;
    }

    public static (string Question, string ExpectedAnswer)[] GetDefaultSampleTestCases() =>
    [
        (
            "PRN222 là môn học gì?",
            "PRN222 là môn lập trình mạng, học phát triển ứng dụng web với ASP.NET Core, Razor Pages, Entity Framework và SignalR."
        ),
        (
            "Assignment 2 yêu cầu làm gì?",
            "Xây dựng hệ thống RAG-LMS: upload tài liệu, chia chunk, tạo embedding, chat AI hỏi đáp theo tài liệu và module benchmark đánh giá chất lượng RAG."
        ),
        (
            "RAG hoạt động như thế nào trong hệ thống này?",
            "Hệ thống embed câu hỏi, tìm các đoạn tài liệu liên quan bằng cosine similarity, rồi đưa ngữ cảnh vào prompt để AI trả lời dựa trên tài liệu môn học."
        ),
        (
            "Chunking strategy Fixed Size 512 là gì?",
            "Là cách chia tài liệu thành các đoạn có kích thước cố định 512 token, có overlap 64 token để giữ ngữ cảnh giữa các chunk."
        ),
        (
            "Ai Model gemini-2.0-flash-lite dùng để làm gì?",
            "Dùng để sinh câu trả lời chat AI cho sinh viên, dựa trên ngữ cảnh tài liệu đã retrieve từ RAG pipeline."
        )
    ];

    public async Task RunExperimentAsync(int experimentId, CancellationToken cancellationToken = default)
    {
        var experiment = await _uow.Experiments.Query()
            .Include(e => e.TestSets)
            .Include(e => e.EmbeddingModel)
            .Include(e => e.AiModel)
            .FirstOrDefaultAsync(e => e.ExperimentId == experimentId)
            ?? throw new Exception($"Experiment {experimentId} not found");

        if (!experiment.TestSets.Any())
            throw new Exception("Experiment chưa có test case nào. Hãy thêm ít nhất 1 câu hỏi trước khi chạy.");

        // Mark as Running
        experiment.Status = "Running";
        experiment.StartedAt = DateTime.UtcNow;
        _uow.Experiments.Update(experiment);
        await _uow.SaveChangesAsync();

        try
        {
            // Create a temporary chat session for this experiment
            var tempSession = new ChatSession
            {
                UserId = 1, // System user
                SubjectId = experiment.SubjectId,
                AiModelId = experiment.AiModelId,
                EmbeddingModelId = experiment.EmbeddingModelId,
                SessionTitle = $"[Benchmark] {experiment.ExperimentName}",
                TopK = experiment.TopK,
                CreatedAt = DateTime.UtcNow,
                IsActive = true
            };
            await _uow.ChatSessions.AddAsync(tempSession);
            await _uow.SaveChangesAsync();

            foreach (var testCase in experiment.TestSets.OrderBy(t => t.OrderIndex))
            {
                cancellationToken.ThrowIfCancellationRequested();

                var sw = System.Diagnostics.Stopwatch.StartNew();
                string? generatedAnswer = null;
                string? errorMessage = null;

                try
                {
                    var response = await _chatService.SendMessageAsync(
                        new SendMessageDto
                        {
                            ChatSessionId = tempSession.ChatSessionId,
                            Question = testCase.Question
                        },
                        cancellationToken);

                    sw.Stop();
                    generatedAnswer = response.Answer;
                    if (response.IsError) errorMessage = response.ErrorMessage;
                }
                catch (Exception ex)
                {
                    sw.Stop();
                    errorMessage = ex.Message;
                    _logger.LogError(ex, "Benchmark test case {Id} failed", testCase.TestSetId);
                }

                // Compute simple scores (cosine similarity between answers using embedding)
                decimal faithfulness = 0, relevance = 0;
                if (generatedAnswer != null && !string.IsNullOrEmpty(testCase.ExpectedAnswer))
                {
                    try
                    {
                        var provider = _embeddingFactory.Create(experiment.EmbeddingModel);
                        var genVec = await provider.EmbedAsync(generatedAnswer, cancellationToken);
                        var expVec = await provider.EmbedAsync(testCase.ExpectedAnswer, cancellationToken);
                        var similarity = (decimal)VectorHelper.CosineSimilarity(genVec, expVec);
                        faithfulness = Math.Clamp(similarity, 0, 1);
                        relevance = Math.Clamp(similarity, 0, 1);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not compute similarity score for test case {Id}", testCase.TestSetId);
                    }
                }

                var result = new BenchmarkResult
                {
                    ExperimentId = experimentId,
                    TestSetId = testCase.TestSetId,
                    GeneratedAnswer = generatedAnswer,
                    FaithfulnessScore = faithfulness,
                    RelevanceScore = relevance,
                    LatencyMs = (int)sw.ElapsedMilliseconds,
                    ErrorMessage = errorMessage,
                    ExecutedAt = DateTime.UtcNow
                };
                await _uow.BenchmarkResults.AddAsync(result);
                await _uow.SaveChangesAsync();

                // Throttle between test cases to avoid Gemini quota
                await Task.Delay(2000, cancellationToken);
            }

            experiment.Status = "Completed";
            experiment.CompletedAt = DateTime.UtcNow;
        }
        catch (OperationCanceledException)
        {
            experiment.Status = "Failed";
            experiment.ErrorMessage = "Experiment was cancelled.";
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Experiment {Id} failed", experimentId);
            experiment.Status = "Failed";
            experiment.ErrorMessage = ex.Message.Length > 1900 ? ex.Message[..1900] : ex.Message;
        }
        finally
        {
            _uow.Experiments.Update(experiment);
            await _uow.SaveChangesAsync();
        }
    }

    public async Task<IEnumerable<BenchmarkResultDto>> GetResultsAsync(int experimentId)
    {
        return await _uow.BenchmarkResults.Query()
            .Where(r => r.ExperimentId == experimentId)
            .Include(r => r.TestSet)
            .OrderBy(r => r.TestSet.OrderIndex)
            .Select(r => new BenchmarkResultDto
            {
                BenchmarkResultId = r.BenchmarkResultId,
                Question = r.TestSet.Question,
                ExpectedAnswer = r.TestSet.ExpectedAnswer,
                GeneratedAnswer = r.GeneratedAnswer,
                FaithfulnessScore = r.FaithfulnessScore,
                RelevanceScore = r.RelevanceScore,
                ContextRecallScore = r.ContextRecallScore,
                AnswerSimilarityScore = r.AnswerSimilarityScore,
                LatencyMs = r.LatencyMs,
                ErrorMessage = r.ErrorMessage
            })
            .ToListAsync();
    }

    public async Task<IEnumerable<TestSetDto>> GetTestSetsAsync(int experimentId)
    {
        return await _uow.TestSets.Query()
            .Where(t => t.ExperimentId == experimentId)
            .OrderBy(t => t.OrderIndex)
            .Select(t => new TestSetDto
            {
                TestSetId = t.TestSetId,
                Question = t.Question,
                ExpectedAnswer = t.ExpectedAnswer,
                OrderIndex = t.OrderIndex
            })
            .ToListAsync();
    }
}
