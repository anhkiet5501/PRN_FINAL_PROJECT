using DataAccessLayer.Entities;

namespace BusinessLayer.Services;

public interface IVNPayService
{
    Task<(string PaymentUrl, PaymentTransaction Transaction)> CreatePaymentAsync(
        int userId, string planCode, string ipAddress, string baseUrl);

    Task<bool> ProcessIpnAsync(IDictionary<string, string> query);

    Task<(bool Success, string Message, string? PlanCode)> ValidateReturnAsync(IDictionary<string, string> query);
}
