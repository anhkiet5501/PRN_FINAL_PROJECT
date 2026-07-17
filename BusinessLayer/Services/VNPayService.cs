using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using BusinessLayer.Models;
using DataAccessLayer.Context;
using DataAccessLayer.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BusinessLayer.Services;

public class VNPayService : IVNPayService
{
    private readonly AppDbContext _db;
    private readonly ISubscriptionService _subscriptionService;
    private readonly VNPaySettings _settings;
    private readonly ILogger<VNPayService> _logger;

    public VNPayService(
        AppDbContext db,
        ISubscriptionService subscriptionService,
        IOptions<VNPaySettings> settings,
        ILogger<VNPayService> logger)
    {
        _db = db;
        _subscriptionService = subscriptionService;
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<(string PaymentUrl, PaymentTransaction Transaction)> CreatePaymentAsync(
        int userId, string planCode, string ipAddress, string baseUrl)
    {
        var plan = SubscriptionPlanCatalog.Get(planCode)
            ?? throw new InvalidOperationException("Gói đăng ký không hợp lệ.");

        if (plan.Price <= 0)
            throw new InvalidOperationException("Gói miễn phí không cần thanh toán VNPay.");

        var orderId = $"{DateTime.UtcNow:yyyyMMddHHmmss}{userId}{Random.Shared.Next(100, 999)}";
        var transaction = new PaymentTransaction
        {
            UserId = userId,
            OrderId = orderId,
            PlanCode = plan.Code,
            Amount = plan.Price,
            Status = "Pending",
            CreatedAt = DateTime.UtcNow
        };

        _db.PaymentTransactions.Add(transaction);
        await _db.SaveChangesAsync();

        var returnUrl = BuildAbsoluteUrl(baseUrl, _settings.ReturnUrl);
        var ipnUrl = BuildAbsoluteUrl(baseUrl, _settings.IpnUrl);
        var createDate = DateTime.Now.ToString("yyyyMMddHHmmss");
        var expireDate = DateTime.Now.AddMinutes(15).ToString("yyyyMMddHHmmss");
        var amount = plan.Price * 100;

        var parameters = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["vnp_Amount"] = amount.ToString(CultureInfo.InvariantCulture),
            ["vnp_Command"] = _settings.Command,
            ["vnp_CreateDate"] = createDate,
            ["vnp_CurrCode"] = _settings.CurrCode,
            ["vnp_ExpireDate"] = expireDate,
            ["vnp_IpAddr"] = NormalizeIp(ipAddress),
            ["vnp_Locale"] = _settings.Locale,
            ["vnp_OrderInfo"] = $"Thanh toan goi {plan.Name}",
            ["vnp_OrderType"] = "other",
            ["vnp_ReturnUrl"] = returnUrl,
            ["vnp_TmnCode"] = _settings.TmnCode,
            ["vnp_TxnRef"] = orderId,
            ["vnp_Version"] = _settings.Version
        };

        var query = BuildQuery(parameters);
        var secureHash = HmacSha512(_settings.HashSecret, query);
        var paymentUrl = $"{_settings.PaymentUrl}?{query}&vnp_SecureHash={secureHash}";

        _logger.LogInformation("Created VNPay payment {OrderId} for user {UserId}, IPN {IpnUrl}", orderId, userId, ipnUrl);
        return (paymentUrl, transaction);
    }

    public async Task<bool> ProcessIpnAsync(IDictionary<string, string> query)
    {
        if (!ValidateSignature(query, out var orderId, out var responseCode, out var transactionNo, out var bankCode))
            return false;

        var transaction = await _db.PaymentTransactions
            .FirstOrDefaultAsync(t => t.OrderId == orderId);

        if (transaction == null)
            return false;

        if (transaction.Status == "Success")
            return true;

        if (responseCode == "00")
        {
            transaction.Status = "Success";
            transaction.VnpTransactionNo = transactionNo;
            transaction.BankCode = bankCode;
            transaction.PaidAt = DateTime.UtcNow;
            transaction.ResponseMessage = "Thanh toán thành công (IPN)";
            await _db.SaveChangesAsync();
            await _subscriptionService.ApplyPlanAsync(transaction.UserId, transaction.PlanCode);
            return true;
        }

        transaction.Status = "Failed";
        transaction.ResponseMessage = $"Thanh toán thất bại (IPN): {responseCode}";
        await _db.SaveChangesAsync();
        return false;
    }

    public async Task<(bool Success, string Message, string? PlanCode)> ValidateReturnAsync(IDictionary<string, string> query)
    {
        if (!ValidateSignature(query, out var orderId, out var responseCode, out var transactionNo, out var bankCode))
            return (false, "Chữ ký VNPay không hợp lệ.", null);

        var transaction = await _db.PaymentTransactions
            .FirstOrDefaultAsync(t => t.OrderId == orderId);

        if (transaction == null)
            return (false, "Không tìm thấy giao dịch.", null);

        if (transaction.Status == "Success")
            return (true, "Thanh toán đã được xác nhận trước đó.", transaction.PlanCode);

        if (responseCode == "00")
        {
            transaction.Status = "Success";
            transaction.VnpTransactionNo = transactionNo;
            transaction.BankCode = bankCode;
            transaction.PaidAt = DateTime.UtcNow;
            transaction.ResponseMessage = "Thanh toán thành công";
            await _db.SaveChangesAsync();
            await _subscriptionService.ApplyPlanAsync(transaction.UserId, transaction.PlanCode);
            return (true, "Thanh toán thành công! Gói đã được kích hoạt.", transaction.PlanCode);
        }

        transaction.Status = "Failed";
        transaction.ResponseMessage = $"Thanh toán thất bại: {responseCode}";
        await _db.SaveChangesAsync();
        return (false, transaction.ResponseMessage, transaction.PlanCode);
    }

    private bool ValidateSignature(
        IDictionary<string, string> query,
        out string orderId,
        out string responseCode,
        out string? transactionNo,
        out string? bankCode)
    {
        orderId = string.Empty;
        responseCode = string.Empty;
        transactionNo = null;
        bankCode = null;

        if (string.IsNullOrWhiteSpace(_settings.HashSecret) || string.IsNullOrWhiteSpace(_settings.TmnCode))
            return false;

        if (!query.TryGetValue("vnp_SecureHash", out var receivedHash) || string.IsNullOrWhiteSpace(receivedHash))
            return false;

        var parameters = query
            .Where(kvp => kvp.Key.StartsWith("vnp_", StringComparison.OrdinalIgnoreCase)
                          && !kvp.Key.Equals("vnp_SecureHash", StringComparison.OrdinalIgnoreCase)
                          && !kvp.Key.Equals("vnp_SecureHashType", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(kvp => kvp.Key, kvp => kvp.Value, StringComparer.Ordinal);

        var data = BuildQuery(new SortedDictionary<string, string>(parameters, StringComparer.Ordinal));
        var computedHash = HmacSha512(_settings.HashSecret, data);

        if (!string.Equals(computedHash, receivedHash, StringComparison.OrdinalIgnoreCase))
            return false;

        orderId = query.TryGetValue("vnp_TxnRef", out var txnRef) ? txnRef : string.Empty;
        responseCode = query.TryGetValue("vnp_ResponseCode", out var code) ? code : string.Empty;
        query.TryGetValue("vnp_TransactionNo", out transactionNo);
        query.TryGetValue("vnp_BankCode", out bankCode);
        return !string.IsNullOrWhiteSpace(orderId);
    }

    private static string BuildAbsoluteUrl(string baseUrl, string path)
    {
        if (path.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            return path;

        return $"{baseUrl.TrimEnd('/')}{path}";
    }

    private static string NormalizeIp(string ipAddress)
    {
        if (string.IsNullOrWhiteSpace(ipAddress) || ipAddress == "::1")
            return "127.0.0.1";

        if (IPAddress.TryParse(ipAddress, out var ip) && ip.IsIPv4MappedToIPv6)
            return ip.MapToIPv4().ToString();

        return ipAddress.Split(',')[0].Trim();
    }

    private static string BuildQuery(SortedDictionary<string, string> parameters) =>
        string.Join("&", parameters.Select(p =>
            $"{System.Net.WebUtility.UrlEncode(p.Key)}={System.Net.WebUtility.UrlEncode(p.Value)}"));

    private static string HmacSha512(string key, string data)
    {
        using var hmac = new HMACSHA512(Encoding.UTF8.GetBytes(key));
        var hash = hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
