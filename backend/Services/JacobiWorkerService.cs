using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using HeavyTasksCluster.Data;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

namespace HeavyTasksCluster.Services;

public class TaskParamsDto
{
    public int MatrixSize { get; set; } = 3;
    public int MaxIterations { get; set; } = 5000;
    public double Tolerance { get; set; } = 1e-6;
    public int TimeoutSec { get; set; } = 60;
    public string InputMode { get; set; } = "custom";
    public double[][]? CustomMatrix { get; set; }
    public double[]? CustomVector { get; set; }
    public double DiagFormula { get; set; } = 50;
    public double OffDiagFormula { get; set; } = 1;
    public double RhsFormula { get; set; } = 10;
}

public class ActiveWorkerHandle
{
    public Guid TaskId { get; init; }
    public int UserId { get; init; }
    public CancellationTokenSource Cts { get; init; } = new();
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public string? TerminatedReason { get; set; }
}

public class JacobiWorkerService
{
    private readonly ConcurrentDictionary<Guid, ActiveWorkerHandle> _activeWorkers = new();
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly RedisClusterService _redis;
    private readonly ClusterSettings _settings;
    private readonly SemaphoreSlim _queueLock = new(1, 1);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    public JacobiWorkerService(
        IDbContextFactory<AppDbContext> dbFactory,
        RedisClusterService redis,
        ClusterSettings settings)
    {
        _dbFactory = dbFactory;
        _redis = redis;
        _settings = settings;

        _redis.Sub.Subscribe(RedisChannel.Literal(RedisClusterService.TaskCancelChannel), async (_, msg) =>
        {
            if (msg.IsNullOrEmpty) return;
            try
            {
                using var doc = JsonDocument.Parse((string)msg!);
                if (doc.RootElement.TryGetProperty("taskId", out var tidProp) &&
                    Guid.TryParse(tidProp.GetString(), out var taskId))
                {
                    if (_activeWorkers.ContainsKey(taskId))
                    {
                        await TerminateLocalWorkerAsync(taskId, "CANCELLED", "Виконання скасовано користувачем");
                    }
                }
            }
            catch { }
        });

        _redis.Sub.Subscribe(RedisChannel.Literal(RedisClusterService.TaskQueueChannel), async (_, _) =>
        {
            if (HasFreeWorkerSlot)
            {
                await ProcessNextFromQueueAsync();
            }
        });
    }

    public int ActiveCount => _activeWorkers.Count;
    public bool HasFreeWorkerSlot => _activeWorkers.Count < _settings.MaxConcurrentTasksPerServer;
    public IReadOnlyCollection<Guid> RunningTaskIds => _activeWorkers.Keys.ToList();

    public async Task<(bool Queued, int? QueuePosition, int? EstimatedWaitSec, TaskEntity Task)> ScheduleOrEnqueueTaskAsync(TaskEntity task)
    {
        if (HasFreeWorkerSlot)
        {
            _ = Task.Run(() => StartTaskComputationAsync(task));
            return (false, null, null, task);
        }

        var qPos = (int)await _redis.EnqueueTaskAsync(task.Id);
        var waitSec = await CalculateEstimatedWaitSecondsAsync(qPos);
        var stageText = $"У черзі очікування: Позиція #{qPos} (орієнтовний час очікування: ~{waitSec} сек)";

        await using var db = await _dbFactory.CreateDbContextAsync();
        var dbTask = await db.Tasks.FirstOrDefaultAsync(t => t.Id == task.Id);
        if (dbTask != null)
        {
            dbTask.Status = "QUEUED";
            dbTask.Progress = 0;
            dbTask.Stage = stageText;
            dbTask.ServerId = null;
            await db.SaveChangesAsync();
            task = dbTask;
        }

        await _redis.SetTaskProgressAsync(task.Id, new
        {
            taskId = task.Id,
            userId = task.UserId,
            status = "QUEUED",
            progress = 0,
            stage = stageText,
            serverId = (string?)null,
            queuePosition = qPos,
            estimatedWaitSec = waitSec,
            updatedAt = DateTime.UtcNow.ToString("O")
        });

        return (true, qPos, waitSec, task);
    }

    public async Task ProcessNextFromQueueAsync()
    {
        if (!await _queueLock.WaitAsync(0)) return;
        try
        {
            while (HasFreeWorkerSlot)
            {
                var nextId = await _redis.DequeueNextTaskAsync();
                if (nextId == null) break;

                if (await _redis.IsTaskCancelledAsync(nextId.Value)) continue;

                await using var db = await _dbFactory.CreateDbContextAsync();
                var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == nextId.Value);
                if (task == null || (task.Status != "QUEUED" && task.Status != "PENDING")) continue;

                _ = Task.Run(() => StartTaskComputationAsync(task));
                await RefreshQueueEstimatesAsync();
            }
        }
        finally
        {
            _queueLock.Release();
        }
    }

    public async Task<TaskEntity?> CancelTaskAsync(TaskEntity task, string requestedBy)
    {
        await _redis.RemoveTaskFromQueueAsync(task.Id);
        await _redis.PublishCancelCommandAsync(task.Id, requestedBy);

        if (_activeWorkers.ContainsKey(task.Id))
        {
            await TerminateLocalWorkerAsync(task.Id, "CANCELLED", "Виконання скасовано користувачем");
            await using var dbLocal = await _dbFactory.CreateDbContextAsync();
            return await dbLocal.Tasks.FirstOrDefaultAsync(t => t.Id == task.Id);
        }

        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.Tasks.FirstOrDefaultAsync(t => t.Id == task.Id);
        if (entity != null)
        {
            entity.Status = "CANCELLED";
            entity.Stage = "Виконання скасовано користувачем";
            entity.ResultJson = JsonSerializer.Serialize(new { cancelled = true, reason = "Скасовано за запитом користувача" }, JsonOpts);
            entity.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        await _redis.SetTaskProgressAsync(task.Id, new
        {
            taskId = task.Id,
            userId = task.UserId,
            status = "CANCELLED",
            progress = entity?.Progress ?? 0,
            stage = "Виконання скасовано користувачем",
            serverId = entity?.ServerId ?? _settings.ServerId,
            result = new { cancelled = true },
            updatedAt = DateTime.UtcNow.ToString("O")
        });

        await RefreshQueueEstimatesAsync();
        return entity;
    }

    public async Task TerminateLocalWorkerAsync(Guid taskId, string finalStatus, string reasonStage)
    {
        if (!_activeWorkers.TryRemove(taskId, out var handle)) return;

        handle.TerminatedReason = finalStatus;
        handle.Cts.Cancel();

        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId);
        var lastProgress = entity?.Progress ?? 0;

        object resultObj = finalStatus == "CANCELLED"
            ? new { cancelled = true, reason = reasonStage, stoppedAtProgress = lastProgress }
            : new { error = reasonStage, timeout = true, stoppedAtProgress = lastProgress };

        if (entity != null)
        {
            entity.Status = finalStatus;
            entity.Stage = reasonStage;
            entity.ServerId = _settings.ServerId;
            entity.ResultJson = JsonSerializer.Serialize(resultObj, JsonOpts);
            entity.FinishedAt = DateTime.UtcNow;
            await db.SaveChangesAsync();
        }

        await _redis.SetTaskProgressAsync(taskId, new
        {
            taskId,
            userId = handle.UserId,
            status = finalStatus,
            progress = lastProgress,
            stage = reasonStage,
            serverId = _settings.ServerId,
            result = resultObj,
            updatedAt = DateTime.UtcNow.ToString("O")
        });

        _ = Task.Run(ProcessNextFromQueueAsync);
    }

    private async Task StartTaskComputationAsync(TaskEntity initialTask)
    {
        var taskId = initialTask.Id;
        var userId = initialTask.UserId;
        if (await _redis.IsTaskCancelledAsync(taskId)) return;

        var dto = JsonSerializer.Deserialize<TaskParamsDto>(initialTask.ParamsJson, JsonOpts) ?? new TaskParamsDto();
        var maxTimeSec = Math.Min(dto.TimeoutSec > 0 ? dto.TimeoutSec : _settings.MaxExecutionTimeSec, _settings.MaxExecutionTimeSec);

        var handle = new ActiveWorkerHandle
        {
            TaskId = taskId,
            UserId = userId
        };
        handle.Cts.CancelAfter(TimeSpan.FromSeconds(maxTimeSec));

        _activeWorkers[taskId] = handle;
        var token = handle.Cts.Token;

        var startStage = "Формування матриці коефіцієнтів A та вектора b";
        await UpdateDbProgressAsync(taskId, 2, startStage, "RUNNING");
        await _redis.SetTaskProgressAsync(taskId, new
        {
            taskId,
            userId,
            status = "RUNNING",
            progress = 2,
            stage = startStage,
            serverId = _settings.ServerId,
            updatedAt = DateTime.UtcNow.ToString("O")
        });

        try
        {
            var sw = Stopwatch.StartNew();
            int n = Math.Max(2, dto.MatrixSize);
            int totalIterations = Math.Max(10, dto.MaxIterations);
            double eps = dto.Tolerance > 0 ? dto.Tolerance : 1e-6;

            double sizeRatio = Math.Min(1.0, n / 3000.0);
            double iterRatio = Math.Min(1.0, totalIterations / 50000.0);
            double complexityScore = 0.5 * sizeRatio + 0.5 * iterRatio;
            double targetDurationMs = (8.0 + complexityScore * 30.0) * 1000.0;

            var A = new double[n * n];
            var b = new double[n];
            var x = new double[n];
            var xNew = new double[n];

            if (dto.InputMode == "custom" && dto.CustomMatrix != null && dto.CustomVector != null && dto.CustomMatrix.Length == n)
            {
                for (int i = 0; i < n; i++)
                {
                    int rowOffset = i * n;
                    for (int j = 0; j < n; j++)
                    {
                        A[rowOffset + j] = j < dto.CustomMatrix[i].Length ? dto.CustomMatrix[i][j] : 0.0;
                    }
                    b[i] = i < dto.CustomVector.Length ? dto.CustomVector[i] : 0.0;
                }
            }
            else
            {
                double diagVal = dto.DiagFormula != 0 ? dto.DiagFormula : n + 10;
                double offVal = dto.OffDiagFormula != 0 ? dto.OffDiagFormula : 1.0;
                double rhsVal = dto.RhsFormula != 0 ? dto.RhsFormula : 10.0;

                for (int i = 0; i < n; i++)
                {
                    int rowOffset = i * n;
                    double offSum = 0;
                    for (int j = 0; j < n; j++)
                    {
                        if (i != j)
                        {
                            double val = offVal / (1.0 + Math.Abs(i - j));
                            A[rowOffset + j] = val;
                            offSum += Math.Abs(val);
                        }
                    }
                    A[rowOffset + i] = Math.Max(Math.Abs(diagVal), offSum + 1.0);
                    b[i] = rhsVal * ((i % 5) + 1);
                }
            }

            int lastReportedProgress = 5;
            double finalResidual = double.PositiveInfinity;
            int completedIterations = 0;

            for (int iter = 1; iter <= totalIterations; iter++)
            {
                token.ThrowIfCancellationRequested();

                double maxDiff = 0;
                for (int i = 0; i < n; i++)
                {
                    int rowOffset = i * n;
                    double diag = A[rowOffset + i];
                    double sigma = 0;
                    for (int j = 0; j < n; j++)
                    {
                        if (j != i) sigma += A[rowOffset + j] * x[j];
                    }

                    double rawNext = (b[i] - sigma) / diag;
                    double alpha = iter == totalIterations ? 1.0 : Math.Min(1.0, 25.0 / totalIterations);
                    double updated = x[i] + alpha * (rawNext - x[i]);

                    double diff = Math.Abs(updated - x[i]);
                    if (diff > maxDiff) maxDiff = diff;
                    xNew[i] = updated;
                }

                Array.Copy(xNew, x, n);
                completedIterations = iter;
                finalResidual = maxDiff;

                double expectedElapsedMs = ((double)iter / totalIterations) * targetDurationMs;
                double actualElapsedMs = sw.Elapsed.TotalMilliseconds;
                if (expectedElapsedMs > actualElapsedMs + 20)
                {
                    await Task.Delay((int)(expectedElapsedMs - actualElapsedMs), token);
                }

                int currentProgress = Math.Min(98, 5 + (int)Math.Floor(((double)iter / totalIterations) * 93.0));
                if (currentProgress > lastReportedProgress)
                {
                    lastReportedProgress = currentProgress;
                    var stageMsg = $"Ітерація {iter}/{totalIterations} (похибка: {maxDiff:E3})";
                    await UpdateDbProgressAsync(taskId, currentProgress, stageMsg, "RUNNING");
                    await _redis.SetTaskProgressAsync(taskId, new
                    {
                        taskId,
                        userId,
                        status = "RUNNING",
                        progress = currentProgress,
                        stage = stageMsg,
                        serverId = _settings.ServerId,
                        iteration = iter,
                        residual = maxDiff,
                        updatedAt = DateTime.UtcNow.ToString("O")
                    });
                }
            }

            for (int extra = 0; extra < 200; extra++)
            {
                double maxDiff = 0;
                for (int i = 0; i < n; i++)
                {
                    int rowOffset = i * n;
                    double diag = A[rowOffset + i];
                    double sigma = 0;
                    for (int j = 0; j < n; j++)
                    {
                        if (j != i) sigma += A[rowOffset + j] * x[j];
                    }
                    double nextVal = (b[i] - sigma) / diag;
                    double d = Math.Abs(nextVal - x[i]);
                    if (d > maxDiff) maxDiff = d;
                    xNew[i] = nextVal;
                }
                Array.Copy(xNew, x, n);
                finalResidual = maxDiff;
                if (maxDiff < eps) break;
            }

            double maxResidualError = 0;
            for (int i = 0; i < n; i++)
            {
                int rowOffset = i * n;
                double ax = 0;
                for (int j = 0; j < n; j++) ax += A[rowOffset + j] * x[j];
                double err = Math.Abs(ax - b[i]);
                if (err > maxResidualError) maxResidualError = err;
            }

            sw.Stop();
            if (!_activeWorkers.TryRemove(taskId, out _)) return;

            var durationSec = Math.Round(sw.Elapsed.TotalSeconds, 2);
            var sampleSolution = x.Take(Math.Min(20, n)).Select(v => Math.Round(v, 6)).ToArray();

            var resultPayload = new
            {
                method = "Метод Якобі (СЛАР) — C# Worker",
                inputMode = dto.InputMode,
                matrixSize = n,
                completedIterations,
                maxIterations = totalIterations,
                tolerance = eps,
                finalStepDiff = Math.Round(finalResidual, 10),
                maxResidualError = Math.Round(maxResidualError, 10),
                durationSec,
                sampleSolution
            };

            await using (var db = await _dbFactory.CreateDbContextAsync())
            {
                var entity = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId);
                if (entity != null)
                {
                    entity.Status = "COMPLETED";
                    entity.Progress = 100;
                    entity.Stage = "Обчислення завершено";
                    entity.ServerId = _settings.ServerId;
                    entity.ResultJson = JsonSerializer.Serialize(resultPayload, JsonOpts);
                    entity.FinishedAt = DateTime.UtcNow;
                    await db.SaveChangesAsync();
                }
            }

            await _redis.SetTaskProgressAsync(taskId, new
            {
                taskId,
                userId,
                status = "COMPLETED",
                progress = 100,
                stage = "Обчислення завершено",
                serverId = _settings.ServerId,
                result = resultPayload,
                updatedAt = DateTime.UtcNow.ToString("O")
            });

            await ProcessNextFromQueueAsync();
        }
        catch (OperationCanceledException)
        {
            if (_activeWorkers.ContainsKey(taskId))
            {
                await TerminateLocalWorkerAsync(
                    taskId,
                    "FAILED",
                    $"Перевищено максимальний час обчислення ({maxTimeSec} сек) — виконання перервано");
            }
        }
        catch (Exception ex)
        {
            await TerminateLocalWorkerAsync(taskId, "FAILED", $"Помилка обчислення: {ex.Message}");
        }
    }

    private async Task UpdateDbProgressAsync(Guid taskId, int progress, string stage, string status)
    {
        await using var db = await _dbFactory.CreateDbContextAsync();
        var entity = await db.Tasks.FirstOrDefaultAsync(t => t.Id == taskId);
        if (entity == null || entity.Status is "COMPLETED" or "CANCELLED" or "FAILED") return;

        entity.Progress = progress;
        entity.Stage = stage;
        entity.Status = status;
        entity.ServerId = _settings.ServerId;
        entity.StartedAt ??= DateTime.UtcNow;
        await db.SaveChangesAsync();
    }

    public async Task<int> CalculateEstimatedWaitSecondsAsync(int queuePosition = 1)
    {
        int pos = Math.Max(1, queuePosition);
        var nodes = await _redis.GetActiveClusterNodesAsync();
        await using var db = await _dbFactory.CreateDbContextAsync();
        var running = await db.Tasks.Where(t => t.Status == "RUNNING").ToListAsync();

        int activeServers = Math.Max(1, nodes.Count);
        int totalCapacity = Math.Max(1, activeServers * _settings.MaxConcurrentTasksPerServer);

        if (running.Count == 0) return pos * 12;

        var remTimes = running
            .Select(t => Math.Max(2, (int)Math.Round(18.0 * ((100.0 - Math.Clamp(t.Progress, 1, 99)) / 100.0))))
            .OrderBy(x => x)
            .ToList();

        int waveIndex = (pos - 1) / totalCapacity;
        int slotIndex = (pos - 1) % remTimes.Count;
        int baseWait = remTimes[slotIndex];

        return Math.Max(3, baseWait + waveIndex * 18 + (pos - 1) * 2);
    }

    public async Task RefreshQueueEstimatesAsync()
    {
        var queuedIds = await _redis.GetQueuedTaskIdsAsync();
        if (queuedIds.Count == 0) return;

        await using var db = await _dbFactory.CreateDbContextAsync();
        for (int i = 0; i < queuedIds.Count; i++)
        {
            var qId = queuedIds[i];
            int pos = i + 1;
            var task = await db.Tasks.FirstOrDefaultAsync(t => t.Id == qId);
            if (task == null || task.Status != "QUEUED")
            {
                await _redis.RemoveTaskFromQueueAsync(qId);
                continue;
            }

            int waitSec = await CalculateEstimatedWaitSecondsAsync(pos);
            string stageText = $"У черзі очікування: Позиція #{pos} (орієнтовний час очікування: ~{waitSec} сек)";
            task.Stage = stageText;
            await db.SaveChangesAsync();

            await _redis.SetTaskProgressAsync(qId, new
            {
                taskId = qId,
                userId = task.UserId,
                status = "QUEUED",
                progress = 0,
                stage = stageText,
                serverId = (string?)null,
                queuePosition = pos,
                estimatedWaitSec = waitSec,
                updatedAt = DateTime.UtcNow.ToString("O")
            });
        }
    }
}
