using System.Security.Claims;
using System.Text.Json;
using HeavyTasksCluster.Data;
using HeavyTasksCluster.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HeavyTasksCluster.Controllers;

[ApiController]
[Route("api/tasks")]
[Authorize]
public class TasksController : ControllerBase
{
    private const int MaxActiveTasksPerUser = 8;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly JacobiWorkerService _worker;
    private readonly RedisClusterService _redis;
    private readonly SseHubService _sseHub;
    private readonly ClusterSettings _settings;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public TasksController(
        IDbContextFactory<AppDbContext> dbFactory,
        JacobiWorkerService worker,
        RedisClusterService redis,
        SseHubService sseHub,
        ClusterSettings settings)
    {
        _dbFactory = dbFactory;
        _worker = worker;
        _redis = redis;
        _sseHub = sseHub;
        _settings = settings;
    }

    private int CurrentUserId =>
        int.TryParse(User.FindFirstValue(ClaimTypes.NameIdentifier) ?? User.FindFirstValue("id"), out var id) ? id : 0;

    private string CurrentUserRole =>
        User.FindFirstValue(ClaimTypes.Role) ?? User.FindFirstValue("role") ?? "user";

    [HttpGet("events")]
    public async Task StreamEvents()
    {
        await _sseHub.HandleSseStreamAsync(HttpContext, CurrentUserId, CurrentUserRole);
    }

    [HttpPost]
    public async Task<IActionResult> CreateTask([FromBody] TaskParamsDto body)
    {
        var userId = CurrentUserId;
        await using var db = await _dbFactory.CreateDbContextAsync();

        var totalHistory = await db.Tasks.CountAsync(t => t.UserId == userId);
        if (totalHistory >= _settings.MaxUserTasksHistory)
        {
            return StatusCode(429, new
            {
                error = $"Перевищено ліміт історії задач ({_settings.MaxUserTasksHistory}). Видаліть старі завершені задачі перед створенням нової."
            });
        }

        var activeCount = await db.Tasks.CountAsync(t =>
            t.UserId == userId && (t.Status == "PENDING" || t.Status == "QUEUED" || t.Status == "RUNNING"));
        if (activeCount >= MaxActiveTasksPerUser)
        {
            return StatusCode(429, new
            {
                error = $"Максимальна кількість одночасно активних або очікуючих задач для одного користувача — {MaxActiveTasksPerUser}."
            });
        }

        var errors = ValidateComplexity(body);
        if (errors.Count > 0)
        {
            return BadRequest(new
            {
                error = "Параметри задачі перевищують допустимі обмеження",
                details = errors,
                limits = new
                {
                    maxMatrixSize = _settings.MaxMatrixSize,
                    maxIterations = _settings.MaxIterations,
                    maxExecutionTimeSec = _settings.MaxExecutionTimeSec
                }
            });
        }

        var normalizedParams = new TaskParamsDto
        {
            MatrixSize = body.MatrixSize,
            MaxIterations = body.MaxIterations,
            Tolerance = body.Tolerance > 0 ? body.Tolerance : 1e-6,
            TimeoutSec = body.TimeoutSec > 0 ? body.TimeoutSec : _settings.MaxExecutionTimeSec,
            InputMode = body.InputMode == "custom" ? "custom" : "formula",
            CustomMatrix = body.InputMode == "custom" ? body.CustomMatrix : null,
            CustomVector = body.InputMode == "custom" ? body.CustomVector : null,
            DiagFormula = body.DiagFormula != 0 ? body.DiagFormula : 50,
            OffDiagFormula = body.OffDiagFormula != 0 ? body.OffDiagFormula : 1,
            RhsFormula = body.RhsFormula != 0 ? body.RhsFormula : 10
        };

        var taskEntity = new TaskEntity
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Status = "PENDING",
            ParamsJson = JsonSerializer.Serialize(normalizedParams, JsonOpts),
            Progress = 0,
            Stage = "Задача прийнята сервером",
            ServerId = _settings.ServerId,
            CreatedAt = DateTime.UtcNow
        };

        db.Tasks.Add(taskEntity);
        await db.SaveChangesAsync();

        var (queued, qPos, waitSec, updatedTask) = await _worker.ScheduleOrEnqueueTaskAsync(taskEntity);

        return StatusCode(201, new
        {
            message = queued
                ? $"Усі воркери зайняті. Задачу поставлено в чергу (#{qPos}, очікування ~{waitSec} с)"
                : $"Задачу передано на виконання серверу {_settings.ServerId}",
            handledByServerId = _settings.ServerId,
            queued,
            queuePosition = qPos,
            estimatedWaitSec = waitSec,
            task = FormatTaskDto(updatedTask)
        });
    }

    [HttpGet]
    public async Task<IActionResult> ListUserTasks()
    {
        var userId = CurrentUserId;
        await using var db = await _dbFactory.CreateDbContextAsync();

        var list = await db.Tasks
            .Where(t => t.UserId == userId)
            .OrderByDescending(t => t.CreatedAt)
            .Take(_settings.MaxUserTasksHistory)
            .ToListAsync();

        var enriched = new List<object>();
        foreach (var t in list)
        {
            object? liveProgress = null;
            if (t.Status is "RUNNING" or "QUEUED" or "PENDING")
            {
                liveProgress = await _redis.GetTaskProgressAsync(t.Id);
            }
            enriched.Add(FormatTaskDto(t, liveProgress));
        }

        var activeCount = list.Count(t => t.Status is "PENDING" or "QUEUED" or "RUNNING");

        return Ok(new
        {
            handledByServerId = _settings.ServerId,
            meta = new
            {
                totalTasksInHistory = list.Count,
                maxUserTasksHistory = _settings.MaxUserTasksHistory,
                activeTasksCount = activeCount,
                maxActiveTasksPerUser = MaxActiveTasksPerUser,
                complexityLimits = new
                {
                    maxMatrixSize = _settings.MaxMatrixSize,
                    maxIterations = _settings.MaxIterations,
                    maxExecutionTimeSec = _settings.MaxExecutionTimeSec
                }
            },
            tasks = enriched
        });
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetTaskById(Guid id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id);
        if (task == null) return NotFound(new { error = "Задачу не знайдено" });

        if (task.UserId != CurrentUserId && CurrentUserRole != "admin")
        {
            return StatusCode(403, new { error = "Немає доступу до чужої задачі" });
        }

        var live = await _redis.GetTaskProgressAsync(task.Id);
        return Ok(new
        {
            handledByServerId = _settings.ServerId,
            task = FormatTaskDto(task, live)
        });
    }

    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> CancelTask(Guid id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id);
        if (task == null) return NotFound(new { error = "Задачу не знайдено" });

        if (task.UserId != CurrentUserId && CurrentUserRole != "admin")
        {
            return StatusCode(403, new { error = "Немає прав на зупинку цієї задачі" });
        }

        if (task.Status is "COMPLETED" or "CANCELLED" or "FAILED")
        {
            return BadRequest(new { error = $"Задача вже завершена зі статусом {task.Status}" });
        }

        var updated = await _worker.CancelTaskAsync(task, User.Identity?.Name ?? "user");
        return Ok(new
        {
            message = "Виконання задачі успішно скасовано",
            handledByServerId = _settings.ServerId,
            task = updated != null ? FormatTaskDto(updated) : null
        });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteTask(Guid id)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == id);
        if (task == null) return NotFound(new { error = "Задачу не знайдено" });

        if (task.UserId != CurrentUserId && CurrentUserRole != "admin")
        {
            return StatusCode(403, new { error = "Немає прав на видалення цієї задачі" });
        }

        if (task.Status is "RUNNING" or "QUEUED" or "PENDING")
        {
            return BadRequest(new { error = "Спочатку скасуйте активну задачу перед видаленням з історії" });
        }

        db.Tasks.Remove(task);
        await db.SaveChangesAsync();

        return Ok(new
        {
            message = "Задачу видалено з історії",
            deletedId = id
        });
    }

    private List<string> ValidateComplexity(TaskParamsDto body)
    {
        var errors = new List<string>();

        if (body.MatrixSize < 2 || body.MatrixSize > _settings.MaxMatrixSize)
        {
            errors.Add($"Розмірність матриці N має бути цілим числом від 2 до {_settings.MaxMatrixSize}. Отримано: {body.MatrixSize}");
        }

        if (body.MaxIterations < 10 || body.MaxIterations > _settings.MaxIterations)
        {
            errors.Add($"Кількість ітерацій має бути в межах від 10 до {_settings.MaxIterations}. Отримано: {body.MaxIterations}");
        }

        if (body.Tolerance <= 0 || body.Tolerance > 1)
        {
            errors.Add("Точність (tolerance) має бути додатним числом у діапазоні (0, 1].");
        }

        if (body.TimeoutSec < 2 || body.TimeoutSec > _settings.MaxExecutionTimeSec)
        {
            errors.Add($"Максимальний час виконання має бути від 2 до {_settings.MaxExecutionTimeSec} секунд.");
        }

        if (body.InputMode == "custom")
        {
            if (body.MatrixSize > 10)
            {
                errors.Add("Ручне введення всієї таблиці дозволено для N <= 10. Для більших N оберіть формульне задання.");
            }
            if (body.CustomMatrix == null || body.CustomMatrix.Length != body.MatrixSize)
            {
                errors.Add($"Матриця A має містити рівно {body.MatrixSize} рядків.");
            }
            else
            {
                for (int i = 0; i < body.MatrixSize; i++)
                {
                    if (body.CustomMatrix[i] == null || body.CustomMatrix[i].Length != body.MatrixSize)
                    {
                        errors.Add($"Рядок {i + 1} матриці A має містити {body.MatrixSize} елементів.");
                        break;
                    }
                    if (Math.Abs(body.CustomMatrix[i][i]) < 1e-12)
                    {
                        errors.Add($"Діагональний елемент A[{i + 1}][{i + 1}] не може дорівнювати 0 для методу Якобі.");
                        break;
                    }
                }
            }

            if (body.CustomVector == null || body.CustomVector.Length != body.MatrixSize)
            {
                errors.Add($"Вектор вільних членів b має містити рівно {body.MatrixSize} значень.");
            }
        }

        return errors;
    }

    public static object FormatTaskDto(TaskEntity t, object? liveProgress = null)
    {
        object? parsedParams = null;
        object? parsedResult = null;
        try { parsedParams = JsonSerializer.Deserialize<JsonElement>(t.ParamsJson); } catch { }
        if (!string.IsNullOrEmpty(t.ResultJson))
        {
            try { parsedResult = JsonSerializer.Deserialize<JsonElement>(t.ResultJson); } catch { }
        }

        return new
        {
            id = t.Id,
            user_id = t.UserId,
            status = t.Status,
            @params = parsedParams,
            progress = t.Progress,
            stage = t.Stage,
            result = parsedResult,
            server_id = t.ServerId,
            created_at = t.CreatedAt,
            started_at = t.StartedAt,
            finished_at = t.FinishedAt,
            liveProgress
        };
    }
}
