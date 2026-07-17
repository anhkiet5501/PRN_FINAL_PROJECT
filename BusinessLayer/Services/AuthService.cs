using BusinessLayer.DTOs;
using BusinessLayer.Helpers;
using DataAccessLayer.Entities;
using DataAccessLayer.Repositories;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Caching.Memory;

namespace BusinessLayer.Services;

public interface IAuthService
{
    Task<UserDto?> LoginAsync(LoginDto dto);
    Task<UserDto> RegisterAsync(CreateUserDto dto);
    Task<UserDto?> GetByIdAsync(int userId);
    Task<IEnumerable<UserDto>> GetAllUsersAsync();
    Task<bool> ToggleActiveAsync(int userId);
    Task<bool> ChangePasswordAsync(int userId, string currentPassword, string newPassword);
    Task<(int successCount, int skipCount)> ImportUsersFromCsvAsync(Stream csvStream);
    Task<bool> UpdateUserAsync(int userId, UpdateUserDto dto);
    Task<bool> DeleteUserAsync(int userId);
    Task<bool> SendPasswordResetCodeAsync(string email);
    Task<bool> ResetPasswordAsync(string email, string code, string newPassword);
}

public class AuthService : IAuthService
{
    private readonly IUnitOfWork _uow;
    private readonly ILogger<AuthService> _logger;
    private readonly IEmailService _emailService;
    private readonly Microsoft.Extensions.Caching.Memory.IMemoryCache _cache;

    public AuthService(IUnitOfWork uow, ILogger<AuthService> logger, IEmailService emailService, Microsoft.Extensions.Caching.Memory.IMemoryCache cache)
    {
        _uow = uow;
        _logger = logger;
        _emailService = emailService;
        _cache = cache;
    }

    public async Task<UserDto?> LoginAsync(LoginDto dto)
    {
        var user = await _uow.Users
            .FirstOrDefaultAsync(u => (u.Username == dto.Username || u.Email == dto.Username) && u.IsActive);

        if (user is null) return null;
        if (!SecurityHelper.VerifyPassword(dto.Password, user.PasswordHash)) return null;

        return MapToDto(user);
    }

    public async Task<UserDto> RegisterAsync(CreateUserDto dto)
    {
        // Check uniqueness
        if (await _uow.Users.AnyAsync(u => u.Username == dto.Username))
            throw new InvalidOperationException($"Username '{dto.Username}' is already taken.");
        if (await _uow.Users.AnyAsync(u => u.Email == dto.Email))
            throw new InvalidOperationException($"Email '{dto.Email}' is already registered.");

        var user = new User
        {
            Username = dto.Username,
            Email = dto.Email,
            PasswordHash = SecurityHelper.HashPassword(dto.Password),
            Role = dto.Role,
            FullName = dto.FullName,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        await _uow.Users.AddAsync(user);
        await _uow.SaveChangesAsync();

        _logger.LogInformation("User '{Username}' registered with role '{Role}'", user.Username, user.Role);
        return MapToDto(user);
    }

    public async Task<UserDto?> GetByIdAsync(int userId)
    {
        var user = await _uow.Users.GetByIdAsync(userId);
        return user is null ? null : MapToDto(user);
    }

    public async Task<IEnumerable<UserDto>> GetAllUsersAsync()
    {
        var users = await _uow.Users.GetAllAsync();
        return users.Select(MapToDto);
    }

    public async Task<bool> ToggleActiveAsync(int userId)
    {
        var user = await _uow.Users.GetByIdAsync(userId);
        if (user is null) return false;

        user.IsActive = !user.IsActive;
        user.UpdatedAt = DateTime.UtcNow;
        _uow.Users.Update(user);
        await _uow.SaveChangesAsync();
        return true;
    }

    public async Task<bool> ChangePasswordAsync(int userId, string currentPassword, string newPassword)
    {
        var user = await _uow.Users.GetByIdAsync(userId);
        if (user is null) return false;
        if (!SecurityHelper.VerifyPassword(currentPassword, user.PasswordHash)) return false;

        user.PasswordHash = SecurityHelper.HashPassword(newPassword);
        user.UpdatedAt = DateTime.UtcNow;
        _uow.Users.Update(user);
        await _uow.SaveChangesAsync();
        return true;
    }

    public async Task<bool> SendPasswordResetCodeAsync(string email)
    {
        var user = await _uow.Users.FirstOrDefaultAsync(u => u.Email == email && u.IsActive);
        if (user == null) return false;

        // Sinh mã OTP 6 số
        var otp = Random.Shared.Next(100000, 999999).ToString();
        var cacheKey = $"pwd_reset_{email.ToLower()}";

        // Lưu vào MemoryCache với thời gian sống 5 phút
        _cache.Set(cacheKey, otp, TimeSpan.FromMinutes(5));

        // Gửi email
        var subject = "Mã xác nhận đặt lại mật khẩu";
        var body = $"Xin chào {user.FullName},\n\nMã xác nhận để đặt lại mật khẩu của bạn là: {otp}\n\nMã này sẽ hết hạn sau 5 phút.\nVui lòng không chia sẻ mã này với bất kỳ ai.\n\nTrân trọng,\nRAG LMS Team";
        await _emailService.SendEmailAsync(user.Email, subject, body);

        _logger.LogInformation("Password reset code generated and sent to {Email}", email);
        return true;
    }

    public async Task<bool> ResetPasswordAsync(string email, string code, string newPassword)
    {
        var cacheKey = $"pwd_reset_{email.ToLower()}";
        
        if (!_cache.TryGetValue(cacheKey, out string? cachedOtp) || cachedOtp != code)
        {
            return false; // Mã sai hoặc đã hết hạn
        }

        var user = await _uow.Users.FirstOrDefaultAsync(u => u.Email == email && u.IsActive);
        if (user == null) return false;

        // Đổi mật khẩu
        user.PasswordHash = SecurityHelper.HashPassword(newPassword);
        user.UpdatedAt = DateTime.UtcNow;
        _uow.Users.Update(user);
        await _uow.SaveChangesAsync();

        // Xóa mã khỏi cache
        _cache.Remove(cacheKey);

        _logger.LogInformation("Password successfully reset for {Email}", email);
        return true;
    }

    public async Task<(int successCount, int skipCount)> ImportUsersFromCsvAsync(Stream csvStream)
    {
        using var reader = new StreamReader(csvStream);
        using var csv = new CsvHelper.CsvReader(reader, System.Globalization.CultureInfo.InvariantCulture);
        
        // Define expected CSV record structure
        var records = csv.GetRecordsAsync<CsvUserRecord>();
        int successCount = 0;
        int skipCount = 0;

        await foreach (var record in records)
        {
            if (string.IsNullOrWhiteSpace(record.Username) || string.IsNullOrWhiteSpace(record.Email))
            {
                skipCount++;
                continue;
            }

            // Check if user exists
            if (await _uow.Users.AnyAsync(u => u.Username == record.Username || u.Email == record.Email))
            {
                skipCount++;
                continue;
            }

            // Generate a random password if not provided
            var rawPassword = string.IsNullOrWhiteSpace(record.Password) 
                ? Guid.NewGuid().ToString("N")[..8] 
                : record.Password;

            var user = new User
            {
                Username = record.Username,
                Email = record.Email,
                FullName = record.FullName,
                Role = string.IsNullOrWhiteSpace(record.Role) ? "Student" : record.Role,
                PasswordHash = SecurityHelper.HashPassword(rawPassword),
                IsActive = true,
                CreatedAt = DateTime.UtcNow
            };

            await _uow.Users.AddAsync(user);
            successCount++;

            // Send notification email
            var subject = "Tài khoản RAG LMS của bạn đã được tạo";
            var body = $@"Xin chào {user.FullName},
Tài khoản đăng nhập hệ thống RAG LMS của bạn đã được khởi tạo.
- Tên đăng nhập: {user.Username}
- Mật khẩu: {rawPassword}
Vui lòng đăng nhập và đổi mật khẩu sớm nhất có thể.
";
            await _emailService.SendEmailAsync(user.Email, subject, body);
        }

        if (successCount > 0)
        {
            await _uow.SaveChangesAsync();
        }

        return (successCount, skipCount);
    }

    private class CsvUserRecord
    {
        public string Username { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public string Password { get; set; } = string.Empty;
    }

    private static UserDto MapToDto(User user) => new()
    {
        UserId = user.UserId,
        Username = user.Username,
        Email = user.Email,
        Role = user.Role,
        FullName = user.FullName,
        IsActive = user.IsActive,
        TokensUsed = user.TokensUsed,
        CreatedAt = user.CreatedAt
    };

    public async Task<bool> UpdateUserAsync(int userId, UpdateUserDto dto)
    {
        var user = await _uow.Users.GetByIdAsync(userId);
        if (user is null) return false;

        user.FullName = dto.FullName;
        user.Role = string.IsNullOrWhiteSpace(dto.Role) ? "Student" : dto.Role;
        if (!string.IsNullOrWhiteSpace(dto.Email)) user.Email = dto.Email;
        if (!string.IsNullOrWhiteSpace(dto.Password)) user.PasswordHash = SecurityHelper.HashPassword(dto.Password);
        user.UpdatedAt = DateTime.UtcNow;
        
        _uow.Users.Update(user);
        await _uow.SaveChangesAsync();
        return true;
    }

    public async Task<bool> DeleteUserAsync(int userId)
    {
        var user = await _uow.Users.GetByIdAsync(userId);
        if (user is null) return false;

        if (string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Không thể xóa tài khoản Admin.");

        _uow.Users.Remove(user);
        await _uow.SaveChangesAsync();
        return true;
    }
}
