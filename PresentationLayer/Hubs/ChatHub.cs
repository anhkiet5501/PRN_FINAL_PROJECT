using BusinessLayer.DTOs;
using BusinessLayer.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using System.Security.Claims;

namespace PRN222_Assignment2.Hubs;

/// <summary>
/// SignalR Hub for real-time streaming chat.
/// Client calls SendStreamingMessage → receives ReceiveChunk per token → StreamComplete or StreamError.
/// </summary>
[Authorize]
public class ChatHub : Hub
{
    private readonly IChatService _chatService;
    private readonly ILogger<ChatHub> _logger;

    public ChatHub(IChatService chatService, ILogger<ChatHub> logger)
    {
        _chatService = chatService;
        _logger = logger;
    }

    /// <summary>
    /// Client invokes this to send a message and receive streaming response.
    /// Clients listen for: ReceiveChunk, StreamComplete, StreamError
    /// </summary>
    public async Task SendStreamingMessage(ChatStreamRequestDto request)
    {
        var userId = GetUserId();
        if (userId <= 0)
        {
            await Clients.Caller.SendAsync("StreamError", "Phiên đăng nhập hết hạn, vui lòng đăng nhập lại.", false);
            return;
        }

        if (request == null || string.IsNullOrWhiteSpace(request.Question) || request.ChatSessionId <= 0)
        {
            await Clients.Caller.SendAsync("StreamError", "Tin nhắn hoặc phiên chat không hợp lệ.", false);
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Context.ConnectionAborted);
        cts.CancelAfter(TimeSpan.FromSeconds(90));

        ChatResponseDto result;
        try
        {
            result = await _chatService.SendMessageStreamingAsync(
                new SendMessageDto
                {
                    ChatSessionId = request.ChatSessionId,
                    Question = request.Question,
                    RestrictToDocs = request.RestrictToDocs,
                    SelectedDocIds = request.SelectedDocIds
                },
                async chunk =>
                {
                    if (!cts.Token.IsCancellationRequested)
                        await Clients.Caller.SendAsync("ReceiveChunk", chunk, cts.Token);
                },
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            await Clients.Caller.SendAsync("StreamError", "Hết thời gian chờ phản hồi. Vui lòng thử lại.", false);
            return;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "ChatHub streaming error for session {SessionId}", request.ChatSessionId);
            await Clients.Caller.SendAsync("StreamError", "Lỗi hệ thống: " + ex.Message, false);
            return;
        }

        if (result.IsError)
        {
            await Clients.Caller.SendAsync("StreamError", result.ErrorMessage ?? "Lỗi không xác định.", false);
            return;
        }

        // Notify client stream complete with citations and latency
        await Clients.Caller.SendAsync("StreamComplete", new
        {
            answer = result.Answer,
            latencyMs = result.LatencyMs,
            citations = result.Citations
        });
    }

    private int GetUserId()
    {
        var value = Context.User?.FindFirstValue(ClaimTypes.NameIdentifier);
        return int.TryParse(value, out var id) ? id : 0;
    }
}

/// <summary>DTO received from browser when sending a streaming chat message.</summary>
public class ChatStreamRequestDto
{
    public int ChatSessionId { get; set; }
    public string Question { get; set; } = string.Empty;
    public bool RestrictToDocs { get; set; } = true;
    public List<int> SelectedDocIds { get; set; } = [];
}
