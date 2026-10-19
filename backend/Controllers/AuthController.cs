using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using HeavyTasksCluster.Data;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace HeavyTasksCluster.Controllers;

public class AuthRequestDto
{
    public string Username { get; set; } = "";
    public string Password { get; set; } = "";
}

[ApiController]
[Route("api/auth")]
public class AuthController : ControllerBase
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ClusterSettings _settings;

    public AuthController(IDbContextFactory<AppDbContext> dbFactory, ClusterSettings settings)
    {
        _dbFactory = dbFactory;
        _settings = settings;
    }

    [HttpPost("register")]
    public async Task<IActionResult> Register([FromBody] AuthRequestDto req)
    {
        var username = (req.Username ?? "").Trim();
        var password = req.Password ?? "";

        if (username.Length < 3 || username.Length > 50)
        {
            return BadRequest(new { error = "Логін має містити від 3 до 50 символів" });
        }
        if (password.Length < 4)
        {
            return BadRequest(new { error = "Пароль має містити щонайменше 4 символи" });
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var exists = await db.Users.AnyAsync(u => u.Username.ToLower() == username.ToLower());
        if (exists)
        {
            return Conflict(new { error = "Користувач з таким логіном вже існує" });
        }

        var role = username.Equals("admin", StringComparison.OrdinalIgnoreCase) ? "admin" : "user";
        var user = new UserEntity
        {
            Username = username,
            PasswordHash = BCrypt.Net.BCrypt.HashPassword(password, 10),
            Role = role,
            CreatedAt = DateTime.UtcNow
        };

        db.Users.Add(user);
        await db.SaveChangesAsync();

        var token = GenerateJwtToken(user);
        return StatusCode(201, new
        {
            message = "Реєстрація успішна",
            token,
            serverId = _settings.ServerId,
            user = new
            {
                id = user.Id,
                username = user.Username,
                role = user.Role,
                createdAt = user.CreatedAt
            }
        });
    }

    [HttpPost("login")]
    public async Task<IActionResult> Login([FromBody] AuthRequestDto req)
    {
        var username = (req.Username ?? "").Trim();
        var password = req.Password ?? "";

        if (string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
        {
            return BadRequest(new { error = "Вкажіть логін та пароль" });
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower());
        if (user == null || !BCrypt.Net.BCrypt.Verify(password, user.PasswordHash))
        {
            return Unauthorized(new { error = "Невірний логін або пароль" });
        }

        var token = GenerateJwtToken(user);
        return Ok(new
        {
            message = "Авторизація успішна",
            token,
            serverId = _settings.ServerId,
            user = new
            {
                id = user.Id,
                username = user.Username,
                role = user.Role,
                createdAt = user.CreatedAt
            }
        });
    }

    [Authorize]
    [HttpGet("me")]
    public async Task<IActionResult> GetMe()
    {
        var idStr = User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("id");
        if (!int.TryParse(idStr, out var userId))
        {
            return Unauthorized(new { error = "Недійсний токен" });
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user == null)
        {
            return NotFound(new { error = "Користувача не знайдено" });
        }

        return Ok(new
        {
            serverId = _settings.ServerId,
            user = new
            {
                id = user.Id,
                username = user.Username,
                role = user.Role,
                createdAt = user.CreatedAt
            }
        });
    }

    private string GenerateJwtToken(UserEntity user)
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim("id", user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim("username", user.Username),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim("role", user.Role)
        };

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(_settings.JwtSecret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var token = new JwtSecurityToken(
            claims: claims,
            expires: DateTime.UtcNow.AddHours(24),
            signingCredentials: creds);

        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
