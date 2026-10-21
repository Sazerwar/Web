import React, { useState, useEffect, useRef, useCallback } from 'react';
import AdminView from './AdminView.jsx';

function createDefaultMatrix(n) {
  const mat = [];
  const vec = [];
  for (let i = 0; i < n; i++) {
    const row = [];
    for (let j = 0; j < n; j++) {
      row.push(i === j ? 25 : 1);
    }
    mat.push(row);
    vec.push((i + 1) * 10);
  }
  return { mat, vec };
}

export default function App() {
  const [token, setToken] = useState(() => localStorage.getItem('slar_jwt_token') || '');
  const [user, setUser] = useState(null);
  const [lastServerId, setLastServerId] = useState('-');
  const [currentView, setCurrentView] = useState('user');

  const [authMode, setAuthMode] = useState('login');
  const [username, setUsername] = useState('');
  const [password, setPassword] = useState('');
  const [authError, setAuthError] = useState('');

  const [inputMode, setInputMode] = useState('custom');
  const [matrixSize, setMatrixSize] = useState(3);
  const [maxIterations, setMaxIterations] = useState(5000);
  const [tolerance, setTolerance] = useState(0.000001);
  const [timeoutSec, setTimeoutSec] = useState(60);

  const [customMatrix, setCustomMatrix] = useState(() => createDefaultMatrix(3).mat);
  const [customVector, setCustomVector] = useState(() => createDefaultMatrix(3).vec);

  const [diagFormula, setDiagFormula] = useState(50);
  const [offDiagFormula, setOffDiagFormula] = useState(1);
  const [rhsFormula, setRhsFormula] = useState(10);

  const [formError, setFormError] = useState('');
  const [formMsg, setFormMsg] = useState('');
  const [submitting, setSubmitting] = useState(false);

  const [tasks, setTasks] = useState([]);
  const [historyMeta, setHistoryMeta] = useState({ totalTasksInHistory: 0, maxUserTasksHistory: 20 });
  const [historyModalOpen, setHistoryModalOpen] = useState(false);
  const [adminData, setAdminData] = useState(null);
  const [nowTick, setNowTick] = useState(Date.now());

  const sseRef = useRef(null);

  const apiFetch = useCallback(
    async (url, options = {}) => {
      const headers = {
        'Content-Type': 'application/json',
        ...(options.headers || {})
      };
      if (token) {
        headers['Authorization'] = `Bearer ${token}`;
      }
      const res = await fetch(url, { ...options, headers });
      const sid = res.headers.get('X-Server-Id') || res.headers.get('x-server-id');
      if (sid) setLastServerId(sid);

      let data = {};
      try {
        data = await res.json();
      } catch {}

      if (!res.ok) {
        const msg =
          data.details && Array.isArray(data.details)
            ? `${data.error}: ${data.details.join(' ')}`
            : data.error || `Помилка запиту (${res.status})`;
        throw new Error(msg);
      }
      return data;
    },
    [token]
  );

  const loadTasks = useCallback(async () => {
    if (!token) return;
    try {
      const data = await apiFetch('/api/tasks');
      if (data.handledByServerId) setLastServerId(data.handledByServerId);
      setTasks(data.tasks || []);
      if (data.meta) setHistoryMeta(data.meta);
    } catch {}
  }, [apiFetch, token]);

  const loadAdminOverview = useCallback(async () => {
    if (!token || user?.role !== 'admin') return;
    try {
      const data = await apiFetch('/api/admin/overview');
      if (data.handledByServerId) setLastServerId(data.handledByServerId);
      setAdminData(data);
    } catch {}
  }, [apiFetch, token, user]);

  useEffect(() => {
    if (!token) {
      setUser(null);
      return;
    }
    let cancelled = false;
    (async () => {
      try {
        const me = await apiFetch('/api/auth/me');
        if (!cancelled) {
          setUser(me.user);
          if (me.serverId) setLastServerId(me.serverId);
        }
      } catch {
        if (!cancelled) {
          localStorage.removeItem('slar_jwt_token');
          setToken('');
          setUser(null);
        }
      }
    })();
    return () => {
      cancelled = true;
    };
  }, [token, apiFetch]);

  useEffect(() => {
    if (!user || !token) return;

    loadTasks();

    const es = new EventSource(`/api/tasks/events?token=${encodeURIComponent(token)}`);
    sseRef.current = es;

    es.addEventListener('connected', (e) => {
      try {
        const d = JSON.parse(e.data);
        if (d.serverId) setLastServerId(d.serverId);
      } catch {}
    });

    es.addEventListener('task:update', (e) => {
      try {
        const upd = JSON.parse(e.data);
        setTasks((prev) => {
          const idx = prev.findIndex((t) => t.id === upd.taskId);
          if (idx === -1) {
            loadTasks();
            return prev;
          }
          const copy = [...prev];
          const existing = copy[idx];
          copy[idx] = {
            ...existing,
            status: upd.status || existing.status,
            progress: typeof upd.progress === 'number' ? upd.progress : existing.progress,
            stage: upd.stage || existing.stage,
            server_id: upd.serverId !== undefined ? upd.serverId : existing.server_id,
            result: upd.result || existing.result,
            liveProgress: upd
          };
          return copy;
        });
      } catch {}
    });

    const pollTimer = setInterval(() => {
      loadTasks();
      setNowTick(Date.now());
    }, 2000);

    return () => {
      es.close();
      clearInterval(pollTimer);
    };
  }, [user, token, loadTasks]);

  useEffect(() => {
    if (currentView !== 'admin' || user?.role !== 'admin') return;
    loadAdminOverview();
    const adminTimer = setInterval(loadAdminOverview, 2000);
    return () => clearInterval(adminTimer);
  }, [currentView, user, loadAdminOverview]);

  const handleMatrixSizeChange = (rawVal) => {
    const val = Number(rawVal);
    setMatrixSize(rawVal);
    if (inputMode === 'custom' && Number.isInteger(val) && val >= 2 && val <= 10) {
      setCustomMatrix((prevMat) => {
        const nextMat = [];
        for (let i = 0; i < val; i++) {
          const row = [];
          for (let j = 0; j < val; j++) {
            const oldVal = prevMat[i]?.[j];
            row.push(oldVal !== undefined ? oldVal : i === j ? 25 : 1);
          }
          nextMat.push(row);
        }
        return nextMat;
      });
      setCustomVector((prevVec) => {
        const nextVec = [];
        for (let i = 0; i < val; i++) {
          const oldB = prevVec[i];
          nextVec.push(oldB !== undefined ? oldB : (i + 1) * 10);
        }
        return nextVec;
      });
    }
  };

  const handleModeChange = (newMode) => {
    setInputMode(newMode);
    if (newMode === 'custom') {
      const clamped = Math.min(10, Math.max(2, Number(matrixSize) || 3));
      handleMatrixSizeChange(clamped);
    } else if (Number(matrixSize) < 10) {
      setMatrixSize(600);
    }
  };

  const handleCellChange = (r, c, value) => {
    setCustomMatrix((prev) => {
      const copy = prev.map((row) => [...row]);
      copy[r][c] = value;
      return copy;
    });
  };

  const handleVectorChange = (r, value) => {
    setCustomVector((prev) => {
      const copy = [...prev];
      copy[r] = value;
      return copy;
    });
  };

  const handleAuthSubmit = async (e) => {
    e.preventDefault();
    setAuthError('');
    try {
      const endpoint = authMode === 'login' ? '/api/auth/login' : '/api/auth/register';
      const data = await apiFetch(endpoint, {
        method: 'POST',
        body: JSON.stringify({ username: username.trim(), password })
      });
      localStorage.setItem('slar_jwt_token', data.token);
      setToken(data.token);
      setUser(data.user);
      if (data.serverId) setLastServerId(data.serverId);
    } catch (err) {
      setAuthError(err.message);
    }
  };

  const handleLogout = () => {
    localStorage.removeItem('slar_jwt_token');
    setToken('');
    setUser(null);
    setCurrentView('user');
  };

  const handleTaskSubmit = async (e) => {
    e.preventDefault();
    setFormError('');
    setFormMsg('');

    const n = Number(matrixSize);
    const iters = Number(maxIterations);
    const eps = Number(tolerance);
    const timeout = Number(timeoutSec);

    if (!Number.isInteger(n) || n < 2 || n > 3000) {
      setFormError('Розмірність системи N має бути цілим числом від 2 до 3000.');
      return;
    }
    if (inputMode === 'custom' && n > 10) {
      setFormError('Ручне введення таблиці підтримується для N від 2 до 10. Для більших N оберіть задання за формулою.');
      return;
    }
    if (!Number.isInteger(iters) || iters < 10 || iters > 50000) {
      setFormError('Кількість ітерацій має бути від 10 до 50000.');
      return;
    }
    if (isNaN(eps) || eps <= 0 || eps > 1) {
      setFormError('Точність (epsilon) має бути додатним числом (0 < eps <= 1).');
      return;
    }
    if (!Number.isInteger(timeout) || timeout < 2 || timeout > 60) {
      setFormError('Максимальний час обчислення має бути від 2 до 60 секунд.');
      return;
    }

    const payload = {
      inputMode,
      matrixSize: n,
      maxIterations: iters,
      tolerance: eps,
      timeoutSec: timeout
    };

    if (inputMode === 'custom') {
      const matNums = [];
      for (let i = 0; i < n; i++) {
        const rowNums = [];
        for (let j = 0; j < n; j++) {
          const v = Number(customMatrix[i]?.[j]);
          if (isNaN(v)) {
            setFormError(`Некоректне число у комірці A[${i + 1}][${j + 1}].`);
            return;
          }
          rowNums.push(v);
        }
        if (Math.abs(rowNums[i]) < 1e-12) {
          setFormError(`Діагональний елемент A[${i + 1}][${i + 1}] не може дорівнювати 0.`);
          return;
        }
        matNums.push(rowNums);
      }
      const vecNums = [];
      for (let i = 0; i < n; i++) {
        const bVal = Number(customVector[i]);
        if (isNaN(bVal)) {
          setFormError(`Некоректне число у комірці b[${i + 1}].`);
          return;
        }
        vecNums.push(bVal);
      }
      payload.customMatrix = matNums;
      payload.customVector = vecNums;
    } else {
      payload.diagFormula = Number(diagFormula) || 50;
      payload.offDiagFormula = Number(offDiagFormula) || 1;
      payload.rhsFormula = Number(rhsFormula) || 10;
    }

    setSubmitting(true);
    try {
      const res = await apiFetch('/api/tasks', {
        method: 'POST',
        body: JSON.stringify(payload)
      });
      setFormMsg(res.message || 'Задачу створено');
      await loadTasks();
    } catch (err) {
      setFormError(err.message);
    } finally {
      setSubmitting(false);
    }
  };

  const handleCancelTask = async (taskId) => {
    try {
      await apiFetch(`/api/tasks/${taskId}/cancel`, { method: 'POST' });
      await loadTasks();
    } catch (err) {
      alert(err.message);
    }
  };

  const handleDeleteTask = async (taskId) => {
    try {
      await apiFetch(`/api/tasks/${taskId}`, { method: 'DELETE' });
      await loadTasks();
    } catch (err) {
      alert(err.message);
    }
  };

  const handleRerunTask = (t) => {
    const p = t.params || {};
    const mode = p.inputMode === 'custom' ? 'custom' : 'formula';
    setInputMode(mode);
    const n = p.matrixSize || 3;
    setMatrixSize(n);
    setMaxIterations(p.maxIterations || 5000);
    setTolerance(p.tolerance || 0.000001);
    setTimeoutSec(p.timeoutSec || 60);

    if (mode === 'custom' && Array.isArray(p.customMatrix) && Array.isArray(p.customVector)) {
      setCustomMatrix(p.customMatrix);
      setCustomVector(p.customVector);
    } else {
      setDiagFormula(p.diagFormula ?? 50);
      setOffDiagFormula(p.offDiagFormula ?? 1);
      setRhsFormula(p.rhsFormula ?? 10);
    }
    setHistoryModalOpen(false);
  };

  const handleScaleUp = async () => {
    try {
      await apiFetch('/api/admin/scale/up', { method: 'POST' });
      await loadAdminOverview();
    } catch (err) {
      alert(err.message);
    }
  };

  const handleScaleDown = async () => {
    try {
      await apiFetch('/api/admin/scale/down', { method: 'POST' });
      await loadAdminOverview();
    } catch (err) {
      alert(err.message);
    }
  };

  const handleToggleAutoScale = async (enabled) => {
    try {
      await apiFetch('/api/admin/scale/toggle', {
        method: 'POST',
        body: JSON.stringify({ enabled })
      });
      await loadAdminOverview();
    } catch (err) {
      alert(err.message);
    }
  };

  const handleForceTerminate = async (taskId) => {
    try {
      await apiFetch(`/api/admin/tasks/${taskId}/terminate`, { method: 'POST' });
      await loadAdminOverview();
    } catch (err) {
      alert(err.message);
    }
  };

  const activeSize = Math.min(10, Math.max(2, Number(matrixSize) || 3));

  return (
    <div>
      <header className="topbar">
        <div className="topbar-inner">
          <div className="topbar-left">
            <span className="app-title">Обчислення СЛАР (Метод Якобі)</span>
            <span className="server-indicator">Сервер: {lastServerId}</span>
          </div>
          {user && (
            <div className="topbar-right">
              <button
                type="button"
                className="btn-secondary btn-sm"
                onClick={() => setHistoryModalOpen(true)}
              >
                Історія задач ({historyMeta.totalTasksInHistory || tasks.length}/
                {historyMeta.maxUserTasksHistory || 20})
              </button>
              {user.role === 'admin' && (
                <button
                  type="button"
                  className="btn-secondary btn-sm"
                  onClick={() => setCurrentView((v) => (v === 'admin' ? 'user' : 'admin'))}
                >
                  {currentView === 'admin' ? 'Робоча панель' : 'Адміністрування'}
                </button>
              )}
              <span className="user-label">
                {user.username} ({user.role})
              </span>
              <button type="button" className="btn-secondary btn-sm" onClick={handleLogout}>
                Вийти
              </button>
            </div>
          )}
        </div>
      </header>

      <main className="container">
        {!user ? (
          <section className="auth-wrapper">
            <div className="auth-card">
              <h2>Вхід у систему</h2>
              <p className="muted">Захищене з&apos;єднання HTTPS (C# ASP.NET Core + React)</p>

              <div className="auth-tabs">
                <button
                  type="button"
                  className={`tab-btn ${authMode === 'login' ? 'active' : ''}`}
                  onClick={() => {
                    setAuthMode('login');
                    setAuthError('');
                  }}
                >
                  Вхід
                </button>
                <button
                  type="button"
                  className={`tab-btn ${authMode === 'register' ? 'active' : ''}`}
                  onClick={() => {
                    setAuthMode('register');
                    setAuthError('');
                  }}
                >
                  Реєстрація
                </button>
              </div>

              <form className="form-stack" onSubmit={handleAuthSubmit}>
                <label>
                  Логін
                  <input
                    type="text"
                    required
                    value={username}
                    onChange={(e) => setUsername(e.target.value)}
                    placeholder="Введіть логін"
                  />
                </label>
                <label>
                  Пароль
                  <input
                    type="password"
                    required
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                    placeholder="Введіть пароль"
                  />
                </label>
                {authError && <div className="error-box">{authError}</div>}
                <button type="submit" className="btn-primary">
                  {authMode === 'login' ? 'Увійти' : 'Зареєструватися'}
                </button>
              </form>

              <div className="auth-hint">
                Обліковий запис адміністратора за замовчуванням: <strong>admin</strong> /{' '}
                <strong>admin123</strong>
              </div>
            </div>
          </section>
        ) : currentView === 'admin' && user.role === 'admin' ? (
          <AdminView
            adminData={adminData}
            onRefresh={loadAdminOverview}
            onBack={() => setCurrentView('user')}
            onScaleUp={handleScaleUp}
            onScaleDown={handleScaleDown}
            onToggleAutoScale={handleToggleAutoScale}
            onForceTerminate={handleForceTerminate}
          />
        ) : (
          <section className="workspace-grid">
            <div className="panel">
              <h3 className="panel-title">Параметри нової задачі (A · x = b)</h3>

              <form className="form-stack" onSubmit={handleTaskSubmit}>
                <label>
                  Спосіб задання коефіцієнтів системи
                  <select value={inputMode} onChange={(e) => handleModeChange(e.target.value)}>
                    <option value="custom">
                      Ручне введення матриці A та вектора b (для N від 2 до 10)
                    </option>
                    <option value="formula">
                      Задання коефіцієнтів за формулою (для великих N до 3000)
                    </option>
                  </select>
                </label>

                <div className="form-row-2">
                  <label>
                    Кількість невідомих (N)
                    <input
                      type="number"
                      min={2}
                      max={3000}
                      value={matrixSize}
                      onChange={(e) => handleMatrixSizeChange(e.target.value)}
                      required
                    />
                    <span className="muted">Максимум: 3000</span>
                  </label>
                  <label>
                    Кількість ітерацій
                    <input
                      type="number"
                      min={10}
                      max={50000}
                      value={maxIterations}
                      onChange={(e) => setMaxIterations(e.target.value)}
                      required
                    />
                    <span className="muted">Максимум: 50000</span>
                  </label>
                </div>

                <div className="form-row-2">
                  <label>
                    Точність (epsilon)
                    <input
                      type="number"
                      step="any"
                      value={tolerance}
                      onChange={(e) => setTolerance(e.target.value)}
                      required
                    />
                  </label>
                  <label>
                    Макс. час обчислення (с)
                    <input
                      type="number"
                      min={2}
                      max={60}
                      value={timeoutSec}
                      onChange={(e) => setTimeoutSec(e.target.value)}
                      required
                    />
                    <span className="muted">Ліміт: 60 сек</span>
                  </label>
                </div>

                {inputMode === 'custom' ? (
                  <div className="sub-box">
                    <div className="sub-box-header">
                      Введіть коефіцієнти матриці A та вільні члени b:
                    </div>
                    <div className="matrix-grid-container">
                      <table className="matrix-table">
                        <thead>
                          <tr>
                            <th></th>
                            {Array.from({ length: activeSize }).map((_, c) => (
                              <th key={c}>x{c + 1}</th>
                            ))}
                            <th></th>
                            <th>b</th>
                          </tr>
                        </thead>
                        <tbody>
                          {Array.from({ length: activeSize }).map((_, r) => (
                            <tr key={r}>
                              <td>
                                <strong>Рядок {r + 1}</strong>
                              </td>
                              {Array.from({ length: activeSize }).map((__, c) => (
                                <td key={c}>
                                  <input
                                    type="number"
                                    step="any"
                                    value={customMatrix[r]?.[c] ?? 0}
                                    onChange={(e) => handleCellChange(r, c, e.target.value)}
                                  />
                                </td>
                              ))}
                              <td className="matrix-sep">=</td>
                              <td>
                                <input
                                  type="number"
                                  step="any"
                                  value={customVector[r] ?? 0}
                                  onChange={(e) => handleVectorChange(r, e.target.value)}
                                />
                              </td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                    <p className="muted small-note">
                      Примітка: для збіжності методу Якобі діагональні елементи A[i][i] мають
                      переважати суму інших елементів рядка.
                    </p>
                  </div>
                ) : (
                  <div className="sub-box">
                    <div className="sub-box-header">
                      Параметри генерації матриці A та вектора b:
                    </div>
                    <div className="form-row-2">
                      <label>
                        Діагональний коефіцієнт A[i][i]
                        <input
                          type="number"
                          step="any"
                          value={diagFormula}
                          onChange={(e) => setDiagFormula(e.target.value)}
                        />
                      </label>
                      <label>
                        Позадіагональний коефіцієнт A[i][j]
                        <input
                          type="number"
                          step="any"
                          value={offDiagFormula}
                          onChange={(e) => setOffDiagFormula(e.target.value)}
                        />
                      </label>
                    </div>
                    <label style={{ marginTop: '8px' }}>
                      Базове значення вектора b[i]
                      <input
                        type="number"
                        step="any"
                        value={rhsFormula}
                        onChange={(e) => setRhsFormula(e.target.value)}
                      />
                    </label>
                  </div>
                )}

                {formError && <div className="error-box">{formError}</div>}
                {formMsg && <div className="ok-box">{formMsg}</div>}

                <button type="submit" className="btn-primary" disabled={submitting}>
                  {submitting ? 'Відправлення...' : 'Обчислити систему'}
                </button>
              </form>
            </div>

            <div className="panel">
              <div className="panel-header-inline">
                <h3 className="panel-title">Поточні задачі та черга виконання</h3>
                <button type="button" className="btn-secondary btn-sm" onClick={loadTasks}>
                  Оновити
                </button>
              </div>

              <div className="tasks-list">
                {tasks.length === 0 ? (
                  <div className="empty-box">
                    Немає запущених задач. Задайте параметри ліворуч та натисніть «Обчислити
                    систему».
                  </div>
                ) : (
                  tasks.slice(0, 8).map((t) => {
                    const p = t.params || {};
                    const isActive = ['RUNNING', 'QUEUED', 'PENDING'].includes(t.status);
                    const progress = Math.min(100, Math.max(0, Number(t.progress || 0)));

                    let stageText = t.stage || '';
                    if (t.status === 'QUEUED' && t.liveProgress?.estimatedWaitSec) {
                      const updatedAtMs = t.liveProgress.updatedAt
                        ? new Date(t.liveProgress.updatedAt).getTime()
                        : nowTick;
                      const elapsedSec = Math.max(0, Math.floor((nowTick - updatedAtMs) / 1000));
                      const remSec = Math.max(2, t.liveProgress.estimatedWaitSec - elapsedSec);
                      const qPos = t.liveProgress.queuePosition || 1;
                      stageText = `У черзі очікування: Позиція #${qPos} (орієнтовний час: ~${remSec} с)`;
                    }

                    let fillClass = 'progress-fill';
                    if (t.status === 'QUEUED') fillClass += ' queued';
                    else if (t.status === 'COMPLETED') fillClass += ' completed';
                    else if (t.status === 'CANCELLED' || t.status === 'FAILED')
                      fillClass += ' failed';

                    let resultLine = null;
                    if (t.status === 'COMPLETED' && t.result?.sampleSolution) {
                      const xs = t.result.sampleSolution
                        .slice(0, 6)
                        .map((v, idx) => `x${idx + 1}=${v}`)
                        .join(', ');
                      resultLine = `Розв'язок: [${xs}${
                        p.matrixSize > 6 ? ', ...' : ''
                      }] | Похибка A·x-b: ${t.result.maxResidualError ?? 0} | Час: ${
                        t.result.durationSec ?? '-'
                      } с`;
                    } else if (t.status === 'FAILED' && t.result?.error) {
                      resultLine = `Помилка: ${t.result.error}`;
                    }

                    return (
                      <div key={t.id} className="task-item">
                        <div className="task-item-top">
                          <div className="task-title">
                            Задача #{String(t.id).slice(0, 8)}{' '}
                            <span className="muted">
                              (N={p.matrixSize || '-'}, ітерацій: {p.maxIterations || '-'})
                            </span>
                          </div>
                          <div className="task-meta">
                            <span>
                              Сервер: <strong>{t.server_id || 'У черзі'}</strong>
                            </span>
                            <span className={`status-tag status-${t.status}`}>{t.status}</span>
                          </div>
                        </div>

                        <div className="progress-track">
                          <div
                            className={fillClass}
                            style={{ width: `${t.status === 'QUEUED' ? 100 : progress}%` }}
                          />
                        </div>

                        <div className="task-item-bottom">
                          <div className="task-stage">
                            {stageText} (<strong>{progress}%</strong>)
                          </div>
                          <div className="task-actions">
                            {isActive ? (
                              <button
                                type="button"
                                className="btn-danger btn-sm"
                                onClick={() => handleCancelTask(t.id)}
                              >
                                Скасувати
                              </button>
                            ) : (
                              <button
                                type="button"
                                className="btn-secondary btn-sm"
                                onClick={() => handleRerunTask(t)}
                              >
                                Повторити
                              </button>
                            )}
                          </div>
                        </div>

                        {resultLine && <div className="result-summary">{resultLine}</div>}
                      </div>
                    );
                  })
                )}
              </div>
            </div>
          </section>
        )}
      </main>

      {historyModalOpen && (
        <div className="modal-backdrop" onClick={() => setHistoryModalOpen(false)}>
          <div className="modal-window" onClick={(e) => e.stopPropagation()}>
            <div className="modal-header">
              <h3>Історія задач користувача (зберігається до 20 записів)</h3>
              <button
                type="button"
                className="btn-secondary btn-sm"
                onClick={() => setHistoryModalOpen(false)}
              >
                Закрити
              </button>
            </div>
            <div className="modal-body">
              <div className="table-scroll">
                <table className="data-table">
                  <thead>
                    <tr>
                      <th>ID</th>
                      <th>Параметри</th>
                      <th>Сервер</th>
                      <th>Статус</th>
                      <th>Прогрес</th>
                      <th>Результат x</th>
                      <th>Дії</th>
                    </tr>
                  </thead>
                  <tbody>
                    {tasks.length === 0 ? (
                      <tr>
                        <td colSpan={7} className="muted">
                          Історія задач порожня
                        </td>
                      </tr>
                    ) : (
                      tasks.map((t) => {
                        const p = t.params || {};
                        const isActive = ['RUNNING', 'QUEUED', 'PENDING'].includes(t.status);
                        const sol = t.result?.sampleSolution
                          ? t.result.sampleSolution
                              .slice(0, 4)
                              .map((v, i) => `x${i + 1}=${v}`)
                              .join(', ')
                          : t.stage || '-';

                        return (
                          <tr key={t.id}>
                            <td>{String(t.id).slice(0, 8)}</td>
                            <td>
                              N={p.matrixSize || '-'}, ітер: {p.maxIterations || '-'}
                            </td>
                            <td>{t.server_id || 'У черзі'}</td>
                            <td>
                              <span className={`status-tag status-${t.status}`}>{t.status}</span>
                            </td>
                            <td>{t.progress || 0}%</td>
                            <td style={{ maxWidth: '240px', fontSize: '12px' }}>{sol}</td>
                            <td>
                              <div className="task-actions">
                                <button
                                  type="button"
                                  className="btn-secondary btn-sm"
                                  onClick={() => handleRerunTask(t)}
                                >
                                  Повторити
                                </button>
                                {isActive ? (
                                  <button
                                    type="button"
                                    className="btn-danger btn-sm"
                                    onClick={() => handleCancelTask(t.id)}
                                  >
                                    Скасувати
                                  </button>
                                ) : (
                                  <button
                                    type="button"
                                    className="btn-danger btn-sm"
                                    onClick={() => handleDeleteTask(t.id)}
                                  >
                                    Видалити
                                  </button>
                                )}
                              </div>
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
        </div>
      )}
    </div>
  );
}
