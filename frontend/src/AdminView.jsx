import React from 'react';

export default function AdminView({
  adminData,
  onRefresh,
  onBack,
  onScaleUp,
  onScaleDown,
  onToggleAutoScale,
  onForceTerminate
}) {
  const sc = adminData?.autoscaler || {};
  const nodes = adminData?.nodes || [];
  const queue = adminData?.queue || [];
  const tasks = adminData?.tasks || [];

  return (
    <div>
      <div className="admin-header-bar">
        <div>
          <h2>Адміністрування кластера (C# ASP.NET Core)</h2>
          <p className="muted">Моніторинг вузлів, черги задач та керування масштабуванням</p>
        </div>
        <div className="admin-header-actions">
          <button type="button" className="btn-secondary btn-sm" onClick={onRefresh}>
            Оновити дані
          </button>
          <button type="button" className="btn-secondary btn-sm" onClick={onBack}>
            Повернутися до задач
          </button>
        </div>
      </div>

      <div className="panel">
        <h3 className="panel-title">Горизонтальне масштабування серверів</h3>
        <div className="scaling-row">
          <div>
            Активних серверів: <strong>{sc.activeCount || nodes.length || 2}</strong> / Максимум:{' '}
            <strong>{sc.maxInstances || 5}</strong> (мінімум: {sc.minInstances || 2})
            <div className="muted">{sc.lastScaleEvent || 'Масштабування в нормі'}</div>
          </div>
          <div className="scaling-controls">
            <label className="checkbox-label">
              <input
                type="checkbox"
                checked={Boolean(sc.autoScaleEnabled)}
                onChange={(e) => onToggleAutoScale(e.target.checked)}
              />
              <span>Автоматичне масштабування при черзі</span>
            </label>
            <button
              type="button"
              className="btn-secondary btn-sm"
              disabled={(sc.desiredInstances || 2) >= (sc.maxInstances || 5)}
              onClick={onScaleUp}
            >
              + Запустити сервер
            </button>
            <button
              type="button"
              className="btn-secondary btn-sm"
              disabled={(sc.desiredInstances || 2) <= (sc.minInstances || 2)}
              onClick={onScaleDown}
            >
              - Зупинити сервер
            </button>
          </div>
        </div>
      </div>

      <div className="panel mt-16">
        <h3 className="panel-title">1. Стан серверів кластера</h3>
        <div className="table-scroll">
          <table className="data-table">
            <thead>
              <tr>
                <th>Сервер</th>
                <th>Статус</th>
                <th>CPU %</th>
                <th>RAM</th>
                <th>Активні воркери</th>
                <th>Uptime</th>
              </tr>
            </thead>
            <tbody>
              {nodes.length === 0 ? (
                <tr>
                  <td colSpan={6} className="muted">
                    Немає даних про вузли
                  </td>
                </tr>
              ) : (
                nodes.map((n) => (
                  <tr key={n.serverId}>
                    <td>
                      <strong>{n.serverId}</strong>{' '}
                      {n.isDynamicNode ? <span className="muted">(динамічний)</span> : null}
                    </td>
                    <td>
                      <span className="status-tag status-COMPLETED">{n.status || 'ONLINE'}</span>
                    </td>
                    <td>{n.cpuUsagePercent ?? 0}%</td>
                    <td>
                      {n.memoryUsedMb ?? 0} МБ ({n.memoryUsagePercent ?? 0}%)
                    </td>
                    <td>
                      {n.activeWorkers ?? 0} / {n.maxWorkers ?? 2}
                    </td>
                    <td>{n.uptimeSec ?? 0} с</td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      </div>

      <div className="panel mt-16">
        <h3 className="panel-title">2. Поточна черга задач</h3>
        <div className="table-scroll">
          <table className="data-table">
            <thead>
              <tr>
                <th>Позиція</th>
                <th>ID задачі</th>
                <th>Користувач</th>
                <th>Параметри</th>
                <th>Орієнтовний час очікування</th>
              </tr>
            </thead>
            <tbody>
              {queue.length === 0 ? (
                <tr>
                  <td colSpan={5} className="muted">
                    Черга порожня — всі задачі обробляються одразу
                  </td>
                </tr>
              ) : (
                queue.map((q) => (
                  <tr key={q.taskId}>
                    <td>#{q.position}</td>
                    <td>{String(q.taskId).slice(0, 8)}</td>
                    <td>{q.username}</td>
                    <td>
                      N={q.params?.matrixSize || '-'}, ітер: {q.params?.maxIterations || '-'}
                    </td>
                    <td>~{q.estimatedWaitSec || 10} с</td>
                  </tr>
                ))
              )}
            </tbody>
          </table>
        </div>
      </div>

      <div className="panel mt-16">
        <h3 className="panel-title">3. Усі задачі користувачів</h3>
        <div className="table-scroll">
          <table className="data-table">
            <thead>
              <tr>
                <th>ID</th>
                <th>Користувач</th>
                <th>Параметри</th>
                <th>Сервер</th>
                <th>Статус</th>
                <th>Прогрес</th>
                <th>Дія</th>
              </tr>
            </thead>
            <tbody>
              {tasks.length === 0 ? (
                <tr>
                  <td colSpan={7} className="muted">
                    Задач немає
                  </td>
                </tr>
              ) : (
                tasks.map((t) => {
                  const isActive = ['RUNNING', 'QUEUED', 'PENDING'].includes(t.status);
                  return (
                    <tr key={t.id}>
                      <td>{String(t.id).slice(0, 8)}</td>
                      <td>{t.username || t.user_id}</td>
                      <td>
                        N={t.params?.matrixSize || '-'}, ітер: {t.params?.maxIterations || '-'}
                      </td>
                      <td>{t.server_id || 'У черзі'}</td>
                      <td>
                        <span className={`status-tag status-${t.status}`}>{t.status}</span>
                      </td>
                      <td style={{ minWidth: '150px' }}>
                        <div className="progress-track">
                          <div
                            className="progress-fill"
                            style={{ width: `${Math.min(100, t.progress || 0)}%` }}
                          />
                        </div>
                        <span className="muted">
                          {t.progress || 0}% — {t.stage || ''}
                        </span>
                      </td>
                      <td>
                        {isActive ? (
                          <button
                            type="button"
                            className="btn-danger btn-sm"
                            onClick={() => onForceTerminate(t.id)}
                          >
                            Примусово зупинити
                          </button>
                        ) : (
                          <span className="muted">Завершено</span>
                        )}
                      </td>
                    </tr>
                  );
                })
              )}
            </tbody>
          </table>
        </div>
      </div>
    </div>
  );
}
