using System.ComponentModel.DataAnnotations;

namespace DataAccessLayer.Entities;

public class PaymentTransaction
{
    [Key]
    public int PaymentTransactionId { get; set; }

    public int UserId { get; set; }

    [Required, MaxLength(50)]
    public string OrderId { get; set; } = string.Empty;

    [Required, MaxLength(20)]
    public string PlanCode { get; set; } = string.Empty;

    public long Amount { get; set; }

    [Required, MaxLength(20)]
    public string Status { get; set; } = "Pending"; // Pending | Success | Failed

    [MaxLength(100)]
    public string? VnpTransactionNo { get; set; }

    [MaxLength(20)]
    public string? BankCode { get; set; }

    [MaxLength(500)]
    public string? ResponseMessage { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateTime? PaidAt { get; set; }

    public User User { get; set; } = null!;
}
