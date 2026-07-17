using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Logging;

namespace BusinessLayer.Services;

public interface IEmailService
{
    Task SendEmailAsync(string toEmail, string subject, string body);
}

public class EmailService : IEmailService
{
    private readonly ILogger<EmailService> _logger;
    private readonly string _smtpServer;
    private readonly int _smtpPort;
    private readonly string _senderEmail;
    private readonly string _senderPassword;

    public EmailService(ILogger<EmailService> logger)
    {
        _logger = logger;
        
        // Gmail SMTP Config
        _smtpServer = "smtp.gmail.com";
        _smtpPort = 587;
        _senderEmail = Environment.GetEnvironmentVariable("SMTP_EMAIL") ?? "anhkiet5501@thptdongdo.edu.vn";
        _senderPassword = Environment.GetEnvironmentVariable("SMTP_PASSWORD") ?? "Mrpurple123@";
    }

    public async Task SendEmailAsync(string toEmail, string subject, string body)
    {
        try
        {
            var mailMessage = new MailMessage
            {
                From = new MailAddress(_senderEmail, "EduManager System"),
                Subject = subject,
                Body = body,
                IsBodyHtml = false,
            };
            mailMessage.To.Add(toEmail);

            using var smtpClient = new SmtpClient(_smtpServer, _smtpPort)
            {
                Credentials = new NetworkCredential(_senderEmail, _senderPassword),
                EnableSsl = true
            };

            await smtpClient.SendMailAsync(mailMessage);
            _logger.LogInformation("Real email sent successfully to {Email}", toEmail);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send real email to {Email}", toEmail);
            throw; // Re-throw để phía trên nhận biết được lỗi
        }
    }
}
