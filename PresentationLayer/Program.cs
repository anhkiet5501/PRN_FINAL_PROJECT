using BusinessLayer.Models;
using BusinessLayer.Models;
using BusinessLayer.Services;
using BusinessLayer.Strategies;
using BusinessLayer.Interfaces;
using DataAccessLayer.Context;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;
using PRN222_Assignment2.Hubs;
using PRN222_Assignment2.Services;
using DotNetEnv;

// ── Load .env file (chỉ dùng cho API keys) ───────────────────────────
var envCandidates = new[]
{
    Path.Combine(Directory.GetCurrentDirectory(), ".env"),
    Path.Combine(Directory.GetCurrentDirectory(), "..", ".env"),
    Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".env"))
};

foreach (var envPath in envCandidates)
{
    if (File.Exists(envPath))
    {
        Env.Load(envPath);
        Console.WriteLine($".env loaded from: {envPath}");
        break;
    }
}

var builder = WebApplication.CreateBuilder(args);

// ── Database — đọc từ appsettings.json (Code First) ──────────────────
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlServer(
        builder.Configuration.GetConnectionString("DefaultConnection"),
        sqlOptions => sqlOptions.EnableRetryOnFailure(
            maxRetryCount: 3,
            maxRetryDelay: TimeSpan.FromSeconds(5),
            errorNumbersToAdd: null)));

// ── Repository & Unit of Work ─────────────────────────────────────────
builder.Services.AddScoped<IUnitOfWork, UnitOfWork>();

// ── Cookie Authentication ─────────────────────────────────────────────
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/Auth/Login";
        options.LogoutPath = "/Auth/Logout";
        options.AccessDeniedPath = "/Auth/AccessDenied";
        options.ExpireTimeSpan = TimeSpan.FromHours(8);
        options.SlidingExpiration = true;
        options.Cookie.HttpOnly = true;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole("Admin"));
    options.AddPolicy("TeacherOrAdmin", policy => policy.RequireRole("Admin", "Teacher"));
});

// ── API Keys Settings — đọc từ .env (ưu tiên) hoặc appsettings.json ──
var apiKeys = new ApiKeysSettings
{
    Gemini        = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                    ?? builder.Configuration["ApiKeys:Gemini"]
                    ?? string.Empty,
    HuggingFace   = Environment.GetEnvironmentVariable("HUGGINGFACE_API_KEY")
                    ?? builder.Configuration["ApiKeys:HuggingFace"]
                    ?? string.Empty,
    OpenAI        = Environment.GetEnvironmentVariable("OPENAI_API_KEY")
                    ?? builder.Configuration["ApiKeys:OpenAI"]
                    ?? string.Empty,
    OllamaBaseUrl = Environment.GetEnvironmentVariable("OLLAMA_BASE_URL")
                    ?? builder.Configuration["ApiKeys:OllamaBaseUrl"]
                    ?? "http://localhost:11434",
};

// Validate Gemini key bắt buộc
if (string.IsNullOrWhiteSpace(apiKeys.Gemini))
{
    throw new InvalidOperationException(
        "Gemini API key chưa được cấu hình. Hãy thêm GEMINI_API_KEY vào file .env");
}
else
{
    Console.WriteLine("API Configuration loaded. Gemini key is present.");
}

// ── HttpClient (for embedding/AI chat calls) ─────────────────────────
builder.Services.AddHttpClient("EmbeddingClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(120);
});
builder.Services.AddHttpClient("GeminiClient", client =>
{
    client.Timeout = TimeSpan.FromSeconds(120);
});

// ── Chunking Strategies (Strategy Pattern — all registered) ──────────
builder.Services.AddSingleton<IChunkingStrategy, FixedSizeChunkingStrategy>();
builder.Services.AddSingleton<IChunkingStrategy, ParagraphChunkingStrategy>();
builder.Services.AddSingleton<IChunkingStrategy, SentenceChunkingStrategy>();
builder.Services.AddSingleton<IChunkingStrategy, RecursiveChunkingStrategy>();

// ── Embedding Provider Factory ────────────────────────────────────────
builder.Services.AddSingleton<EmbeddingProviderFactory>(sp =>
{
    var loggerFactory = sp.GetRequiredService<ILoggerFactory>();
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();

    return new EmbeddingProviderFactory(
        httpClientFactory: () => httpClientFactory.CreateClient("EmbeddingClient"),
        loggerFactory: loggerFactory,
        apiKeys: apiKeys);
});

// ── Business Services ─────────────────────────────────────────────────
builder.Services.AddScoped<IFakeEmailService, FakeEmailService>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<ISubjectService, SubjectService>();
builder.Services.AddScoped<IDocumentRealtimeNotifier, SignalRDocumentRealtimeNotifier>();

builder.Services.AddScoped<IDocumentService, DocumentService>();

// ── Gemini Chat Service (shared HTTP client, fallback model chain) ─────
builder.Services.AddSingleton<IGeminiChatService>(sp =>
{
    var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
    var httpClient = httpClientFactory.CreateClient("GeminiClient");
    var configuration = sp.GetRequiredService<IConfiguration>();
    return new GeminiChatService(httpClient, apiKeys, configuration);
});

builder.Services.AddScoped<IChatService, ChatService>();
builder.Services.AddScoped<IBenchmarkService, BenchmarkService>();
builder.Services.AddScoped<IStatisticsService, StatisticsService>();
builder.Services.AddScoped<ISubscriptionService, SubscriptionService>();
builder.Services.AddScoped<IVNPayService, VNPayService>();

builder.Services.Configure<VNPaySettings>(builder.Configuration.GetSection("VNPay"));

// ── Razor Pages ───────────────────────────────────────────────────────
builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/");
    options.Conventions.AuthorizeFolder("/Admin", "AdminOnly");
    options.Conventions.AuthorizePage("/Documents/View");
    options.Conventions.AuthorizePage("/Documents/Download");
    options.Conventions.AuthorizePage("/Documents/Index", "TeacherOrAdmin");
    options.Conventions.AuthorizePage("/Documents/Details");
    options.Conventions.AuthorizeFolder("/Benchmark", "TeacherOrAdmin");
    options.Conventions.AllowAnonymousToPage("/Auth/Login");
    options.Conventions.AllowAnonymousToPage("/Auth/Register");
    options.Conventions.AllowAnonymousToPage("/Payment/VNPayIpn");
});

// ── Session (for flash messages) ─────────────────────────────────────
builder.Services.AddSession(options =>
{
    options.IdleTimeout = TimeSpan.FromMinutes(30);
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
});

// ── CORS ──────────────────────────────────────────────────────────────
builder.Services.AddCors(options =>
    options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

// ── SignalR ───────────────────────────────────────────────────────────
builder.Services.AddSignalR();

var app = builder.Build();

// ── Middleware Pipeline ───────────────────────────────────────────────
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseCors();
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();
app.MapHub<SubjectHub>("/subjectHub");
app.MapHub<DocumentHub>("/documentHub");
app.MapHub<ChatHub>("/chatHub");

// ── Auto-apply EF Core Migrations on startup ──────────────────────────
using (var scope = app.Services.CreateScope())
{
    try
    {
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        db.Database.Migrate();
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogInformation("Database migration applied successfully.");

        // Upgrade deprecated Gemini embedding model names in existing databases
        var deprecatedModels = db.EmbeddingModels
            .Where(m => m.ModelName == "text-embedding-004" || m.ModelName == "embedding-001")
            .ToList();
        if (deprecatedModels.Count > 0)
        {
            foreach (var model in deprecatedModels)
            {
                model.ModelName = "gemini-embedding-001";
                model.Description = "Google Gemini gemini-embedding-001 (768 dims)";
                if (model.VectorDimension <= 0)
                    model.VectorDimension = 768;
            }
            db.SaveChanges();
            logger.LogInformation("Updated {Count} deprecated embedding model(s) to gemini-embedding-001.", deprecatedModels.Count);
        }

        // Upgrade deprecated AI chat models to gemini-2.0-flash-lite (higher free-tier RPM)
        var deprecatedAiModels = db.AiModels
            .Where(m => m.ModelName == "gemini-1.0-flash-lite"
                     || m.ModelName == "gemini-1.5-flash"
                     || m.ModelName == "gemini-1.5-flash-lite"
                     || m.ModelName == "gemini-2.0-flash"
                     || m.ModelName == "gemini-pro")
            .ToList();
        if (deprecatedAiModels.Count > 0)
        {
            foreach (var model in deprecatedAiModels)
            {
                model.ModelName = "gemini-2.0-flash-lite";
                model.Description = "Google Gemini 2.0 Flash Lite — higher rate limit (30 RPM)";
            }
            db.SaveChanges();
            logger.LogInformation("Updated {Count} deprecated AI model(s) to gemini-2.0-flash-lite.", deprecatedAiModels.Count);
        }

        // Seed a default benchmark experiment if none exists
        if (!db.Experiments.Any())
        {
            var subjectId = db.Documents
                .Where(d => d.Status == "Indexed")
                .Select(d => d.Chapter.SubjectId)
                .FirstOrDefault();

            if (subjectId == 0)
                subjectId = db.Subjects.Select(s => s.SubjectId).FirstOrDefault();

            if (subjectId > 0)
            {
                var experiment = new Experiment
                {
                    SubjectId = subjectId,
                    EmbeddingModelId = 1,
                    AiModelId = 1,
                    ChunkingStrategyId = 1,
                    ExperimentName = "Demo RAG Benchmark - Gemini + FixedSize 512",
                    Description = "Experiment mẫu đánh giá chất lượng RAG pipeline",
                    TopK = 3,
                    Status = "Pending",
                    CreatedAt = DateTime.UtcNow
                };
                db.Experiments.Add(experiment);
                db.SaveChanges();

                var sampleTests = BenchmarkService.GetDefaultSampleTestCases();

                for (var i = 0; i < sampleTests.Length; i++)
                {
                    db.TestSets.Add(new TestSet
                    {
                        ExperimentId = experiment.ExperimentId,
                        Question = sampleTests[i].Question,
                        ExpectedAnswer = sampleTests[i].ExpectedAnswer,
                        OrderIndex = i + 1,
                        CreatedAt = DateTime.UtcNow
                    });
                }

                db.SaveChanges();
                logger.LogInformation(
                    "Seeded benchmark experiment #{Id} with {Count} sample test cases for subject #{SubjectId}.",
                    experiment.ExperimentId, sampleTests.Length, subjectId);
            }
        }
    }
    catch (Exception ex)
    {
        var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
        logger.LogError(ex, "An error occurred while migrating the database.");
    }
}

app.Run();
