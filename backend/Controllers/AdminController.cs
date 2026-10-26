using System.Security.Claims;
using System.Text.Json;
using HeavyTasksCluster.Data;
using HeavyTasksCluster.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HeavyTasksCluster.Controllers;

public class ToggleScaleDto
{
    public bool? Enabled { get; set; }
}

[ApiController]
[Route("api/admin")]
[Authorize]
public class AdminController : ControllerBase
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly RedisClusterService _redis;
    private readonly JacobiWorkerService _worker;
    private readonly AutoScalerService _autoScaler;
    private readonly ClusterSettings _settings;

    public AdminController(
        IDbContextFactory<AppDbContext> dbFactory,
        RedisClusterService redis,
        JacobiWorkerService worker,
        AutoScalerService autoScaler,
        ClusterSettings settings)
    {
        _dbFactory = dbFactory;
        _redis = redis;
        _worker = worker;
        _autoScaler = autoScaler;
        _settings = settings;
    }

    private bool IsAdmin =>
        (User.FindFirstValue(ClaimTypes.Role) ?? User.FindFirstValue("role")) == "admin";

    [HttpGet("overview")]
    public async Task<IActionResult> GetOverview()
    {
        if (!IsAdmin) return StatusCode(403, new { error = "Доступ дозволено лише адміністратору" });

        var nodes = await _redis.GetActiveClusterNodesAsync();
        var queuedIds = await _redis.GetQueuedTaskIdsAsync();
        var autoscaler = await _autoScaler.GetStatusAsync();

        await using var db = await _dbFactory.CreateDbContextAsync();

        var allTasksRaw = await (
            from t in db.Tasks
            join u in db.Users on t.UserId equals u.Id
            orderby t.CreatedAt descending
            select new { Task = t, u.Username, u.Role }
        ).Take(100).ToListAsync();

        var tasksList = new List<object>();
        foreach (var item in allTasksRaw)
        {
            var t = item.Task;
            JsonElement? live = null;
            if (t.Status is "RUNNING" or "QUEUED" or "PENDING")
            {
                live = await _redis.GetTaskProgressAsync(t.Id);
            }

            object? parsedParams = null;
            object? parsedResult = null;
            try { parsedParams = JsonSerializer.Deserialize<JsonElement>(t.ParamsJson); } catch { }
            if (!string.IsNullOrEmpty(t.ResultJson))
            {
                try { parsedResult = JsonSerializer.Deserialize<JsonElement>(t.ResultJson); } catch { }
            }

            int progress = t.Progress;
            string stage = t.Stage;
            string? serverId = t.ServerId;

            if (live.HasValue)
            {
                if (live.Value.TryGetProperty("progress", out var pProp) && pProp.TryGetInt32(out var pVal))
                    progress = Math.Max(progress, pVal);
                if (live.Value.TryGetProperty("stage", out var sProp) && sProp.GetString() is { } sVal)
                    stage = sVal;
                if (live.Value.TryGetProperty("serverId", out var sidProp) && sidProp.GetString() is { } sidVal)
                    serverId = sidVal;
            }

            tasksList.Add(new
            {
                id = t.Id,
                user_id = t.UserId,
                username = item.Username,
                user_role = item.Role,
                status = t.Status,
                @params = parsedParams,
                progress,
                stage,
                result = parsedResult,
                server_id = serverId,
                created_at = t.CreatedAt,
                started_at = t.StartedAt,
                finished_at = t.FinishedAt
            });
        }

        var queueDetails = new List<object>();
        for (int i = 0; i < queuedIds.Count; i++)
        {
            var qId = queuedIds[i];
            var match = allTasksRaw.FirstOrDefault(x => x.Task.Id == qId);
            var waitSec = await _worker.CalculateEstimatedWaitSecondsAsync(i + 1);
            object? pObj = null;
            if (match != null)
            {
                try { pObj = JsonSerializer.Deserialize<JsonElement>(match.Task.ParamsJson); } catch { }
            }

            queueDetails.Add(new
            {
                position = i + 1,
                taskId = qId,
                username = match?.Username ?? "user",
                @params = pObj,
                estimatedWaitSec = waitSec,
                createdAt = match?.Task.CreatedAt
            });
        }

        int totalWorkersCapacity = nodes.Count * _settings.MaxConcurrentTasksPerServer;
        int busyWorkers = nodes.Sum(n =>
            n.TryGetProperty("activeWorkers", out var aw) && aw.TryGetInt32(out var v) ? v : 0);

        return Ok(new
        {
            handledByServerId = _settings.ServerId,
            timestamp = DateTime.UtcNow.ToString("O"),
            clusterSummary = new
            {
                onlineNodesCount = nodes.Count,
                totalWorkersCapacity,
                busyWorkers,
                queueLength = queueDetails.Count,
                runningTasksCount = allTasksRaw.Count(x => x.Task.Status == "RUNNING"),
                completedTasksCount = allTasksRaw.Count(x => x.Task.Status == "COMPLETED")
            },
            autoscaler,
            nodes,
            queue = queueDetails,
            tasks = tasksList
        });
    }

    [HttpPost("tasks/{id:guid}/terminate")]
    public async Task<IActionResult> ForceTerminateTask(Guid id)
    {
        if (!IsAdmin) return StatusCode(403, new { error = "Доступ дозволено лише адміністратору" });

        await using var db = await _dbFactory.CreateDbContextAsync();
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id);
        if (task == null) return NotFound(new { error = "Задачу не знайдено" });

        if (task.Status is "COMPLETED" or "CANCELLED" or "FAILED")
        {
            return BadRequest(new { error = $"Задача вже має фінальний статус {task.Status}" });
        }

        var updated = await _worker.CancelTaskAsync(task, $"ADMIN:{User.Identity?.Name ?? "admin"}");
        return Ok(new
        {
            message = $"Задачу #{id.ToString()[..8]} примусово зупинено адміністратором",
            handledByServerId = _settings.ServerId,
            task = updated != null ? TasksController.FormatTaskDto(updated) : null
        });
    }

    [HttpPost("scale/up")]
    public async Task<IActionResult> ScaleUp()
    {
        if (!IsAdmin) return StatusCode(403, new { error = "Доступ дозволено лише адміністратору" });
        var res = await _autoScaler.ScaleUpAsync("Ручний запуск з адмін-панелі");
        if (!res.Scaled) return BadRequest(new { error = res.Message });

        return Ok(new
        {
            message = res.Message,
            autoscaler = await _autoScaler.GetStatusAsync()
        });
    }

    [HttpPost("scale/down")]
    public async Task<IActionResult> ScaleDown()
    {
        if (!IsAdmin) return StatusCode(403, new { error = "Доступ дозволено лише адміністратору" });
        var res = await _autoScaler.ScaleDownAsync("Ручна зупинка з адмін-панелі");
        if (!res.Scaled) return BadRequest(new { error = res.Message });

        return Ok(new
        {
            message = res.Message,
            autoscaler = await _autoScaler.GetStatusAsync()
        });
    }

    [HttpPost("scale/toggle")]
    public async Task<IActionResult> ToggleAutoScale([FromBody] ToggleScaleDto dto)
    {
        if (!IsAdmin) return StatusCode(403, new { error = "Доступ дозволено лише адміністратору" });
        var cfg = await _autoScaler.ToggleAutoScaleAsync(dto?.Enabled);
        return Ok(new
        {
            message = cfg.AutoScaleEnabled
                ? "Автоматичне масштабування увімкнено"
                : "Автоматичне масштабування вимкнено",
            autoscaler = await _autoScaler.GetStatusAsync()
        });
    }
}
