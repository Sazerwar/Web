using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace HeavyTasksCluster.Data;

public class ClusterSettings
{
    public string ServerId { get; set; } = "csharp-app-1";
    public string JwtSecret { get; set; } = "";
    public int MaxMatrixSize { get; set; } = 3000;
    public int MaxIterations { get; set; } = 50000;
    public int MaxExecutionTimeSec { get; set; } = 60;
    public int MaxUserTasksHistory { get; set; } = 20;
    public int MaxConcurrentTasksPerServer { get; set; } = 2;
    public DateTime StartedAt { get; set; } = DateTime.UtcNow;
}

[Table("users")]
public class UserEntity
{
    [Key]
    [Column("id")]
    public int Id { get; set; }

    [Required]
    [MaxLength(100)]
    [Column("username")]
    public string Username { get; set; } = "";

    [Required]
    [Column("password_hash")]
    public string PasswordHash { get; set; } = "";

    [Required]
    [MaxLength(20)]
    [Column("role")]
    public string Role { get; set; } = "user";

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

[Table("tasks")]
public class TaskEntity
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("user_id")]
    public int UserId { get; set; }

    [Required]
    [MaxLength(20)]
    [Column("status")]
    public string Status { get; set; } = "PENDING";

    [Column("params", TypeName = "jsonb")]
    public string ParamsJson { get; set; } = "{}";

    [Column("progress")]
    public int Progress { get; set; } = 0;

    [Column("stage")]
    public string Stage { get; set; } = "Ініціалізація";

    [Column("result", TypeName = "jsonb")]
    public string? ResultJson { get; set; }

    [Column("server_id")]
    public string? ServerId { get; set; }

    [Column("created_at")]
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    [Column("started_at")]
    public DateTime? StartedAt { get; set; }

    [Column("finished_at")]
    public DateTime? FinishedAt { get; set; }
}

public class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options) { }

    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<TaskEntity> Tasks => Set<TaskEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<UserEntity>()
            .HasIndex(u => u.Username)
            .IsUnique();

        modelBuilder.Entity<TaskEntity>()
            .HasIndex(t => t.UserId);

        modelBuilder.Entity<TaskEntity>()
            .HasIndex(t => t.Status);
    }
}

public static class DbSeeder
{
    public static async Task EnsureDefaultAdminAsync(AppDbContext db)
    {
        var adminExists = await db.Users.AnyAsync(u => u.Username == "admin");
        if (!adminExists)
        {
            var hash = BCrypt.Net.BCrypt.HashPassword("admin123", 10);
            db.Users.Add(new UserEntity
            {
                Username = "admin",
                PasswordHash = hash,
                Role = "admin",
                CreatedAt = DateTime.UtcNow
            });
            await db.SaveChangesAsync();
            Console.WriteLine("[DbSeeder] Default C# admin account ensured (admin / admin123)");
        }
    }
}
