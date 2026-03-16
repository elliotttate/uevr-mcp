// When served by the UEVR plugin (port 8899), use same origin.
// Otherwise (file://, different port, etc.), connect to the plugin on 8899.
const API_BASE = (window.location.port === '8899')
  ? window.location.origin
  : 'http://localhost:8899';
let pollCount = 0;

function row(label, value, cls = '') {
  return `<div class='row'><span class='label'>${label}</span><span class='value ${cls}'>${value}</span></div>`;
}

function fmt(n, d = 2) {
  return n != null ? Number(n).toFixed(d) : '?';
}

function esc(s) { return s ? s.replace(/&/g,'&amp;').replace(/"/g,'&quot;').replace(/</g,'&lt;') : ''; }

function setDot(cardId, ok) {
  const dot = document.querySelector(`#${cardId} .dot`);
  if (dot) dot.className = ok ? 'dot' : 'dot error';
}

async function fetchJson(path) {
  const r = await fetch(API_BASE + path);
  return r.json();
}

function formatUptime(s) {
  if (s == null) return '?';
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  if (h > 0) return `${h}h ${m}m ${sec}s`;
  if (m > 0) return `${m}m ${sec}s`;
  return `${sec}s`;
}

function quatToEuler(q) {
  const { x, y, z, w } = q;
  const sinr = 2 * (w * x + y * z);
  const cosr = 1 - 2 * (x * x + y * y);
  const roll = Math.atan2(sinr, cosr) * 180 / Math.PI;

  const sinp = 2 * (w * y - z * x);
  const pitch = Math.abs(sinp) >= 1
    ? (Math.sign(sinp) * 90)
    : (Math.asin(sinp) * 180 / Math.PI);

  const siny = 2 * (w * z + x * y);
  const cosy = 1 - 2 * (y * y + z * z);
  const yaw = Math.atan2(siny, cosy) * 180 / Math.PI;

  return { pitch, yaw, roll };
}

// ---- Status card ----
async function updateStatus() {
  try {
    const d = await fetchJson('/api/status');
    const el = document.getElementById('status-content');
    setDot('status-card', !d.error);
    if (d.error) { el.innerHTML = `<span class='error-msg'>${d.error}</span>`; return; }

    let badges = '';
    badges += d.runtime_ready
      ? "<span class='badge badge-active'>Runtime Ready</span>"
      : "<span class='badge badge-inactive'>Runtime Not Ready</span>";
    badges += d.hmd_active
      ? "<span class='badge badge-vr'>HMD Active</span>"
      : "<span class='badge badge-inactive'>HMD Inactive</span>";

    el.innerHTML =
      `<div class='status-badges'>${badges}</div>` +
      row('VR Runtime', d.vr_runtime || '?', 'vr-active') +
      row('Uptime', formatUptime(d.uptime_seconds)) +
      row('Tick Count', (d.tick_count || 0).toLocaleString()) +
      row('Queue Depth', d.queue_depth || 0);
  } catch(e) {
    setDot('status-card', false);
  }
}

// ---- Game Info card ----
async function updateGameInfo() {
  try {
    const d = await fetchJson('/api/game_info');
    const el = document.getElementById('gameinfo-content');
    setDot('gameinfo-card', !d.error);
    if (d.error) { el.innerHTML = `<span class='error-msg'>${d.error}</span>`; return; }

    // Update page title with game name
    const name = d.gameName || 'Unknown';
    document.getElementById('game-title').textContent = name.replace('.exe', '');

    el.innerHTML =
      row('Game', name, 'highlight') +
      row('VR Runtime', d.vrRuntime || '?', 'vr-active') +
      row('HMD Active', d.hmdActive ? 'Yes' : 'No', d.hmdActive ? 'highlight' : 'warn') +
      row('HTTP Port', d.httpPort || 8899) +
      row('Uptime', formatUptime(d.uptimeSeconds));
  } catch(e) {
    setDot('gameinfo-card', false);
  }
}

// ---- Player card ----
async function updatePlayer() {
  try {
    const d = await fetchJson('/api/player');
    const el = document.getElementById('player-content');
    setDot('player-card', !d.error);
    if (d.error) { el.innerHTML = `<span class='error-msg'>${d.error}</span>`; return; }

    let html = '';
    if (d.controller) {
      html += row('Controller', d.controller.class || '?', 'highlight');
      html += row('Ctrl Address', d.controller.address || '?');
    } else {
      html += row('Controller', 'None', 'warn');
    }
    if (d.pawn) {
      html += row('Pawn', d.pawn.class || '?', 'highlight');
      html += row('Pawn Address', d.pawn.address || '?');
    } else {
      html += row('Pawn', 'None', 'warn');
    }
    el.innerHTML = html;
  } catch(e) {
    setDot('player-card', false);
  }
}

// ---- Camera card ----
async function updateCamera() {
  try {
    const d = await fetchJson('/api/camera');
    const el = document.getElementById('camera-content');
    setDot('camera-card', !d.error);
    if (d.error) { el.innerHTML = `<span class='error-msg'>${d.error}</span>`; return; }

    el.innerHTML =
      row('Position', `${fmt(d.position.x)}, ${fmt(d.position.y)}, ${fmt(d.position.z)}`) +
      row('Rotation', `${fmt(d.rotation.x, 1)}, ${fmt(d.rotation.y, 1)}, ${fmt(d.rotation.z, 1)}`) +
      row('FOV', fmt(d.fov, 1) + '\u00B0');
  } catch(e) {
    setDot('camera-card', false);
  }
}

// ---- FPS graph ----
const FPS_HISTORY_SIZE = 120;
const fpsHistory = [];
let lastTickCount = null;
let lastTickTime = null;

async function updateFPS() {
  try {
    const d = await fetchJson('/api/status');
    if (d.error) { setDot('fps-card', false); return; }

    const tickCount = d.tick_count;
    const now = performance.now();

    if (lastTickCount !== null && lastTickTime !== null) {
      const dTicks = tickCount - lastTickCount;
      const dTime = (now - lastTickTime) / 1000;
      if (dTime > 0 && dTicks >= 0) {
        const fps = dTicks / dTime;
        fpsHistory.push(fps);
        if (fpsHistory.length > FPS_HISTORY_SIZE) fpsHistory.shift();

        document.getElementById('fps-value').textContent = `${fps.toFixed(1)} fps`;
        setDot('fps-card', true);
        drawFPSGraph();
      }
    }

    lastTickCount = tickCount;
    lastTickTime = now;
  } catch(e) {
    setDot('fps-card', false);
  }
}

function drawFPSGraph() {
  const canvas = document.getElementById('fps-canvas');
  if (!canvas) return;

  const rect = canvas.parentElement.getBoundingClientRect();
  canvas.width = rect.width - 40;

  const ctx = canvas.getContext('2d');
  const w = canvas.width;
  const h = canvas.height;

  ctx.clearRect(0, 0, w, h);
  if (fpsHistory.length < 2) return;

  const maxFps = Math.max(90, ...fpsHistory);

  // Target lines
  for (const target of [30, 60, 90]) {
    if (target > maxFps) continue;
    const y = h - (target / maxFps) * h;
    ctx.strokeStyle = '#21262d';
    ctx.lineWidth = 1;
    ctx.setLineDash([4, 4]);
    ctx.beginPath();
    ctx.moveTo(0, y);
    ctx.lineTo(w, y);
    ctx.stroke();
    ctx.setLineDash([]);

    ctx.fillStyle = '#484f58';
    ctx.font = '10px monospace';
    ctx.fillText(target + '', 2, y - 2);
  }

  // FPS line
  const step = w / (FPS_HISTORY_SIZE - 1);
  const startIdx = FPS_HISTORY_SIZE - fpsHistory.length;

  ctx.beginPath();
  for (let i = 0; i < fpsHistory.length; i++) {
    const x = (startIdx + i) * step;
    const y = h - (fpsHistory[i] / maxFps) * h;
    if (i === 0) ctx.moveTo(x, y);
    else ctx.lineTo(x, y);
  }
  ctx.strokeStyle = '#58a6ff';
  ctx.lineWidth = 1.5;
  ctx.stroke();

  // Fill under line
  const lastX = (startIdx + fpsHistory.length - 1) * step;
  const firstX = startIdx * step;
  ctx.lineTo(lastX, h);
  ctx.lineTo(firstX, h);
  ctx.closePath();
  ctx.fillStyle = 'rgba(88, 166, 255, 0.08)';
  ctx.fill();

  // Current dot
  const current = fpsHistory[fpsHistory.length - 1];
  const dotColor = current >= 55 ? '#3fb950' : current >= 30 ? '#d29922' : '#f85149';
  const dotX = lastX;
  const dotY = h - (current / maxFps) * h;
  ctx.beginPath();
  ctx.arc(dotX, dotY, 3, 0, Math.PI * 2);
  ctx.fillStyle = dotColor;
  ctx.fill();
}

// ---- VR Status card ----
async function updateVR() {
  try {
    const d = await fetchJson('/api/vr/status');
    const el = document.getElementById('vr-content');
    setDot('vr-card', !d.error);
    if (d.error) { el.innerHTML = `<span class='error-msg'>${d.error}</span>`; return; }

    const runtime = d.isOpenXR ? 'OpenXR' : d.isOpenVR ? 'OpenVR' : 'Unknown';

    let badges = '';
    badges += d.hmdActive
      ? "<span class='badge badge-vr'>HMD Active</span>"
      : "<span class='badge badge-inactive'>HMD Inactive</span>";
    badges += d.runtimeReady
      ? "<span class='badge badge-active'>Ready</span>"
      : "<span class='badge badge-inactive'>Not Ready</span>";
    if (d.usingControllers) badges += "<span class='badge badge-info'>Controllers</span>";

    el.innerHTML =
      `<div class='status-badges'>${badges}</div>` +
      row('Runtime', runtime, 'vr-active') +
      row('Resolution', `${d.resolution.width} x ${d.resolution.height}`) +
      row('Controllers', d.usingControllers ? 'Active' : 'Inactive', d.usingControllers ? 'highlight' : '');
  } catch(e) {
    setDot('vr-card', false);
  }
}

// ---- VR Poses card ----
async function updatePoses() {
  try {
    const d = await fetchJson('/api/vr/poses');
    const el = document.getElementById('poses-content');
    setDot('poses-card', !d.error);
    if (d.error) { el.innerHTML = `<span class='error-msg'>${d.error}</span>`; return; }

    function poseBlock(label, pose) {
      const p = pose.position;
      const euler = quatToEuler(pose.rotation);
      return `<div class='pose-group'>
        <div class='pose-group-title'>${label}</div>
        ${row('Position', `${fmt(p.x)}, ${fmt(p.y)}, ${fmt(p.z)}`)}
        ${row('Rotation', `P:${fmt(euler.pitch,1)} Y:${fmt(euler.yaw,1)} R:${fmt(euler.roll,1)}`)}
      </div>`;
    }

    let html = poseBlock('HMD', d.hmd);
    html += poseBlock('Left Grip', d.leftController.grip);
    html += poseBlock('Right Grip', d.rightController.grip);
    html += row('Standing Origin', `${fmt(d.standingOrigin.x)}, ${fmt(d.standingOrigin.y)}, ${fmt(d.standingOrigin.z)}`);

    el.innerHTML = html;
  } catch(e) {
    setDot('poses-card', false);
  }
}

// ---- VR Settings card ----
let vrSettingsLoaded = false;

async function updateVRSettings() {
  try {
    const d = await fetchJson('/api/vr/settings');
    const el = document.getElementById('vrsettings-content');
    setDot('vrsettings-card', !d.error);
    if (d.error) { el.innerHTML = `<span class='error-msg'>${d.error}</span>`; return; }

    if (!vrSettingsLoaded) {
      vrSettingsLoaded = true;
      el.innerHTML = `
        <div class='setting-row'>
          <div><div class='setting-label'>Snap Turn</div><div class='setting-sublabel'>Rotate in fixed increments</div></div>
          <div class='toggle ${d.snapTurnEnabled ? 'on' : ''}' id='toggle-snap' data-key='snapTurnEnabled'></div>
        </div>
        <div class='setting-row'>
          <div><div class='setting-label'>Decoupled Pitch</div><div class='setting-sublabel'>Separate head pitch from camera</div></div>
          <div class='toggle ${d.decoupledPitchEnabled ? 'on' : ''}' id='toggle-pitch' data-key='decoupledPitchEnabled'></div>
        </div>
        <div class='setting-row'>
          <div><div class='setting-label'>Aim Allowed</div><div class='setting-sublabel'>Enable controller aiming</div></div>
          <div class='toggle ${d.aimAllowed ? 'on' : ''}' id='toggle-aim' data-key='aimAllowed'></div>
        </div>
        <div class='setting-row'>
          <div><div class='setting-label'>Aim Method</div></div>
          <div class='select-wrap'>
            <select id='select-aim-method'>
              <option value='0' ${d.aimMethod === 0 ? 'selected' : ''}>Head</option>
              <option value='1' ${d.aimMethod === 1 ? 'selected' : ''}>Right Controller</option>
              <option value='2' ${d.aimMethod === 2 ? 'selected' : ''}>Left Controller</option>
            </select>
          </div>
        </div>
        <div class='btn-row'>
          <button class='btn btn-secondary' id='btn-recenter'>Recenter View</button>
          <button class='btn btn-secondary' id='btn-save-config'>Save Config</button>
          <button class='btn btn-secondary' id='btn-reload-config'>Reload Config</button>
        </div>`;

      // Toggle handlers
      el.querySelectorAll('.toggle').forEach(toggle => {
        toggle.addEventListener('click', async () => {
          const key = toggle.dataset.key;
          const newVal = !toggle.classList.contains('on');
          toggle.classList.toggle('on');
          await fetch(API_BASE + '/api/vr/settings', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ [key]: newVal })
          });
        });
      });

      // Aim method select
      document.getElementById('select-aim-method').addEventListener('change', async (e) => {
        await fetch(API_BASE + '/api/vr/settings', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({ aimMethod: parseInt(e.target.value) })
        });
      });

      // Button handlers
      document.getElementById('btn-recenter').addEventListener('click', async () => {
        await fetch(API_BASE + '/api/vr/recenter', { method: 'POST' });
      });
      document.getElementById('btn-save-config').addEventListener('click', async () => {
        await fetch(API_BASE + '/api/vr/config/save', { method: 'POST' });
      });
      document.getElementById('btn-reload-config').addEventListener('click', async () => {
        vrSettingsLoaded = false;
        await fetch(API_BASE + '/api/vr/config/reload', { method: 'POST' });
      });
    } else {
      // Update toggle states without rebuilding DOM
      const snap = document.getElementById('toggle-snap');
      const pitch = document.getElementById('toggle-pitch');
      const aim = document.getElementById('toggle-aim');
      const method = document.getElementById('select-aim-method');
      if (snap) snap.className = `toggle ${d.snapTurnEnabled ? 'on' : ''}`;
      if (pitch) pitch.className = `toggle ${d.decoupledPitchEnabled ? 'on' : ''}`;
      if (aim) aim.className = `toggle ${d.aimAllowed ? 'on' : ''}`;
      if (method) method.value = d.aimMethod;
    }
  } catch(e) {
    setDot('vrsettings-card', false);
  }
}

// ---- Lua card ----
let luaLoaded = false;

async function updateLua() {
  try {
    const d = await fetchJson('/api/lua/state');
    const el = document.getElementById('lua-content');
    setDot('lua-card', !d.error);
    if (d.error) { el.innerHTML = `<span class='error-msg'>${d.error}</span>`; return; }

    if (!luaLoaded) {
      luaLoaded = true;
      el.innerHTML =
        row('Initialized', d.initialized ? 'Yes' : 'No', d.initialized ? 'highlight' : 'warn') +
        row('Memory', `${d.memoryKB} KB`) +
        row('Exec Count', d.execCount) +
        row('Frame Callbacks', d.frameCallbackCount) +
        `<textarea class='lua-editor' id='lua-code' placeholder='Enter Lua code...' spellcheck='false'>print("Hello from UEVR!")</textarea>
        <div class='btn-row'>
          <button class='btn btn-primary' id='btn-lua-exec'>Execute</button>
          <button class='btn btn-danger' id='btn-lua-reset'>Reset State</button>
        </div>
        <div class='lua-output' id='lua-output' style='display:none'></div>`;

      document.getElementById('btn-lua-exec').addEventListener('click', async () => {
        const code = document.getElementById('lua-code').value;
        const output = document.getElementById('lua-output');
        output.style.display = 'block';
        output.textContent = 'Executing...';
        output.className = 'lua-output';

        try {
          const r = await fetch(API_BASE + '/api/lua/exec', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ code })
          });
          const result = await r.json();

          if (result.error) {
            output.textContent = 'Error: ' + result.error;
            output.className = 'lua-output has-error';
          } else {
            let text = result.output || '';
            if (result.returnValue) text += (text ? '\n' : '') + 'Return: ' + result.returnValue;
            output.textContent = text || 'OK (no output)';
            output.className = 'lua-output has-result';
          }
        } catch(e) {
          output.textContent = 'Request failed: ' + e.message;
          output.className = 'lua-output has-error';
        }

        // Refresh state
        luaLoaded = false;
      });

      document.getElementById('btn-lua-reset').addEventListener('click', async () => {
        await fetch(API_BASE + '/api/lua/reset', { method: 'POST' });
        luaLoaded = false;
        document.getElementById('lua-output').style.display = 'none';
      });
    } else {
      // Update stats rows
      const rows = el.querySelectorAll('.row');
      for (const r of rows) {
        const lbl = r.querySelector('.label')?.textContent;
        const val = r.querySelector('.value');
        if (!val) continue;
        if (lbl === 'Memory') val.textContent = `${d.memoryKB} KB`;
        else if (lbl === 'Exec Count') val.textContent = d.execCount;
        else if (lbl === 'Frame Callbacks') val.textContent = d.frameCallbackCount;
      }
    }
  } catch(e) {
    setDot('lua-card', false);
  }
}

// ---- Console / CVar Manager ----
let allCVars = [];
let cvarsLoaded = false;
let cvarSearchTimer = null;
let activeCvarTab = 'all';

// Persistence via localStorage
const CATEGORY_MAP = {
  'r': 'Rendering', 'ai': 'AI', 'fx': 'Effects', 'audio': 'Audio',
  'net': 'Networking', 'ui': 'UI', 'vr': 'VR', 't': 'Texture',
  'sg': 'Scalability', 'p': 'Physics', 'stat': 'Statistics',
  'show': 'Debug', 'log': 'Logging', 'foliage': 'Foliage',
  'landscape': 'Landscape', 'streaming': 'Streaming', 'shadow': 'Shadows',
  'light': 'Lighting', 'post': 'PostProcess', 'bloom': 'PostProcess',
  'dof': 'PostProcess', 'motion': 'PostProcess', 'screen': 'PostProcess',
  'temporal': 'PostProcess', 'console': 'Console', 'game': 'Game',
  'world': 'World', 'player': 'Player', 'camera': 'Camera',
  'input': 'Input', 'hmd': 'VR', 'oculus': 'VR', 'steamvr': 'VR',
  'openvr': 'VR', 'openxr': 'VR', 'material': 'Materials',
  'particle': 'Particles', 'lod': 'LOD'
};

function computeCategory(name) {
  if (!name) return 'Other';
  const dot = name.indexOf('.');
  const under = name.indexOf('_');
  let sep = dot >= 0 ? dot : -1;
  if (under >= 0 && (sep < 0 || under < sep)) sep = under;
  if (sep < 0) return 'Other';
  const prefix = name.substring(0, sep).toLowerCase();
  return CATEGORY_MAP[prefix] || (prefix.charAt(0).toUpperCase() + prefix.slice(1));
}

function loadCvarStorage(key, fallback) {
  try { return JSON.parse(localStorage.getItem(key)) || fallback; } catch { return fallback; }
}
function saveCvarStorage(key, data) {
  localStorage.setItem(key, JSON.stringify(data));
}

let cvarFavorites = new Set(loadCvarStorage('uevr-cvar-favorites', []));
let cvarOriginals = loadCvarStorage('uevr-cvar-originals', {});
let cvarModified = loadCvarStorage('uevr-cvar-modified', {});

function saveFavorites() { saveCvarStorage('uevr-cvar-favorites', [...cvarFavorites]); }
function saveOriginals() { saveCvarStorage('uevr-cvar-originals', cvarOriginals); }
function saveModified() { saveCvarStorage('uevr-cvar-modified', cvarModified); }

async function loadAllCVars() {
  if (cvarsLoaded) return;
  try {
    // Load names + categories (values=false for speed with old DLL)
    const d = await fetchJson('/api/console/cvars?limit=5000&values=false');
    if (d.error) { showConsoleStatus(d.error, false); return; }
    allCVars = (d.variables || []).map(v => {
      if (!v.category) v.category = computeCategory(v.name);
      // Store original value if API provides it
      if (v.value !== undefined && !(v.name in cvarOriginals)) {
        cvarOriginals[v.name] = v.value;
      }
      return v;
    });
    saveOriginals();
    cvarsLoaded = true;
    document.getElementById('cvar-total').textContent = `(${allCVars.length})`;
    setDot('console-card', true);

    // Load values for favorites in background
    fetchValuesForFavorites();
  } catch(e) {
    showConsoleStatus('Failed to load CVars: ' + e.message, false);
    setDot('console-card', false);
  }
}

async function fetchValuesForFavorites() {
  for (const name of cvarFavorites) {
    const cv = allCVars.find(c => c.name === name);
    if (cv && cv.int === undefined) {
      await fetchCvarValue(cv);
    }
  }
  if (cvarFavorites.size > 0) renderActiveTab();
}

async function fetchCvarValue(cv) {
  try {
    const val = await fetchJson(`/api/console/cvar?name=${encodeURIComponent(cv.name)}`);
    if (!val.error) {
      cv.int = val.int;
      cv.float = val.float;
      const interpreted = interpretValue(val.int, val.float);
      // Only store originals for real variables (not commands)
      if (interpreted !== null && interpreted !== '...' && !(cv.name in cvarOriginals)) {
        cvarOriginals[cv.name] = interpreted;
        saveOriginals();
      }
    }
  } catch {}
}

async function fetchValuesForItems(container) {
  const items = [...container.querySelectorAll('.cvar-item')];
  let needsRerender = false;
  for (const item of items) {
    const name = item.dataset.name;
    const cv = allCVars.find(c => c.name === name);
    if (cv && cv.int === undefined) {
      await fetchCvarValue(cv);
      needsRerender = true;
    }
  }
  // Re-render the container since CMD vs variable may have changed
  if (needsRerender) {
    const names = items.map(i => i.dataset.name);
    const cvars = names.map(n => allCVars.find(c => c.name === n)).filter(Boolean);
    container.innerHTML = cvars.map(createCvarItemHTML).join('');
    attachCvarEvents(container);
  }
}

// Detect garbage pointer reads from console commands
function isGarbageInt(v) { return v !== undefined && Math.abs(v) > 100000000; }
function isGarbageFloat(v) { return v !== undefined && v !== 0 && (Math.abs(v) < 1e-10 || Math.abs(v) > 1e15); }

function interpretValue(intVal, floatVal) {
  if (intVal === undefined) return '...'; // Not yet fetched
  const intBad = isGarbageInt(intVal);
  const floatBad = isGarbageFloat(floatVal);
  if (intBad && floatBad) return null;          // Both garbage = command
  if (!intBad && !floatBad && intVal !== floatVal) return floatVal; // Float has precision
  if (!intBad) return intVal;                   // Int is valid
  return floatVal;                              // Int garbage, float valid
}

function getDisplayValue(v) {
  if (v.name in cvarModified) return cvarModified[v.name];
  if (v.value !== undefined) return v.value; // New API returns pre-interpreted value
  return interpretValue(v.int, v.float);
}

function getFilteredCVars(includeCommands = false) {
  const q = (document.getElementById('cvar-search').value || '').toLowerCase();
  let list = allCVars;
  if (!includeCommands) {
    // Only show items with real values (actual CVars, not console commands)
    list = list.filter(v => {
      if (v.int === undefined) return true; // Not yet fetched — keep for now
      return interpretValue(v.int, v.float) !== null; // Has a real value
    });
  }
  if (!q) return list;
  return list.filter(v => v.name.toLowerCase().includes(q));
}

function createCvarItemHTML(v) {
  const cat = v.category || 'Other';
  const n = esc(v.name);
  const isFav = cvarFavorites.has(v.name);
  const isChanged = v.name in cvarModified;
  const original = cvarOriginals[v.name];
  const val = getDisplayValue(v);
  const isCmd = val === null; // Garbage value = console command
  const isLoading = val === '...';

  let html = `<div class='cvar-item' data-name='${n}'>`;
  html += `<span class='cvar-name${isCmd ? ' is-cmd' : ''}' title='${n}'>${n}</span>`;
  html += `<span class='cvar-cat-badge'>${esc(cat)}</span>`;

  if (isCmd) {
    // Console command — no editable value
    html += `<span class='cvar-cmd-badge'>CMD</span>`;
    html += `<button class='cvar-btn cvar-btn-exec' data-action='exec'>Exec</button>`;
  } else if (isLoading) {
    // Value not yet fetched
    html += `<span style='color:#484f58;font-size:0.8em'>loading...</span>`;
  } else {
    // Variable — show editable value
    html += `<input class='cvar-input${isChanged ? ' modified' : ''}' value='${esc(String(val))}' data-action='input'>`;
    html += `<button class='cvar-btn cvar-btn-apply' data-action='apply'>Apply</button>`;
    if (isChanged && original !== undefined) {
      html += `<span class='cvar-original' title='Original: ${esc(String(original))}'>(was ${esc(String(original))})</span>`;
      html += `<button class='cvar-btn cvar-btn-reset' data-action='reset'>Reset</button>`;
    }
  }

  html += `<button class='cvar-btn-fav${isFav ? ' active' : ''}' data-action='fav' title='${isFav ? 'Remove from favorites' : 'Add to favorites'}'>${isFav ? '\u2605' : '\u2606'}</button>`;
  html += `</div>`;
  return html;
}

function attachCvarEvents(container) {
  container.querySelectorAll('.cvar-item').forEach(item => {
    const name = item.dataset.name;

    item.querySelectorAll('[data-action]').forEach(el => {
      const action = el.dataset.action;
      if (action === 'apply') {
        el.addEventListener('click', () => applyCvarValue(name, item.querySelector('.cvar-input').value));
      } else if (action === 'reset') {
        el.addEventListener('click', () => resetCvar(name));
      } else if (action === 'exec') {
        el.addEventListener('click', () => execCvarCommand(name));
      } else if (action === 'fav') {
        el.addEventListener('click', () => { toggleFavorite(name); renderActiveTab(); });
      } else if (action === 'input') {
        el.addEventListener('keydown', (e) => {
          if (e.key === 'Enter') applyCvarValue(name, el.value);
        });
      }
    });
  });
}

async function applyCvarValue(name, value) {
  try {
    const numVal = Number(value);
    const body = { name, value: isNaN(numVal) ? value : numVal };
    const r = await fetch(API_BASE + '/api/console/cvar', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    });
    const result = await r.json();
    if (result.error) {
      showConsoleStatus(result.error, false);
    } else {
      cvarModified[name] = isNaN(numVal) ? value : numVal;
      saveModified();
      // Update allCVars in memory
      const cv = allCVars.find(c => c.name === name);
      if (cv) {
        cv.int = result.newInt;
        cv.float = result.newFloat;
      }
      showConsoleStatus(`${name} = ${value}`, true);
      renderActiveTab();
    }
  } catch(e) {
    showConsoleStatus('Failed: ' + e.message, false);
  }
}

async function resetCvar(name) {
  const original = cvarOriginals[name];
  if (original === undefined) return;
  delete cvarModified[name];
  saveModified();
  try {
    const numVal = Number(original);
    const body = { name, value: isNaN(numVal) ? original : numVal };
    const r = await fetch(API_BASE + '/api/console/cvar', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(body)
    });
    const result = await r.json();
    // Update in-memory values from API response
    const cv = allCVars.find(c => c.name === name);
    if (cv && result.newInt !== undefined) {
      cv.int = result.newInt;
      cv.float = result.newFloat;
    }
    showConsoleStatus(`Reset ${name} to ${original}`, true);
  } catch(e) {
    showConsoleStatus('Reset failed: ' + e.message, false);
  }
  renderActiveTab();
}

function toggleFavorite(name) {
  if (cvarFavorites.has(name)) cvarFavorites.delete(name);
  else cvarFavorites.add(name);
  saveFavorites();
}

async function execCvarCommand(name) {
  try {
    const r = await fetch(API_BASE + '/api/console/command', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ command: name })
    });
    const result = await r.json();
    showConsoleStatus(result.error ? 'Error: ' + result.error : 'Executed: ' + name, !result.error);
  } catch(e) {
    showConsoleStatus('Failed: ' + e.message, false);
  }
}

function renderFavoritesTab() {
  const panel = document.getElementById('cvar-panel-favorites');
  const filtered = getFilteredCVars().filter(v => cvarFavorites.has(v.name));
  if (filtered.length === 0) {
    panel.innerHTML = `<div class='cvar-panel-empty'>${cvarFavorites.size === 0
      ? 'No favorites yet. Use the \u2606 button on any CVar to add it.'
      : 'No favorites match the current search filter.'}</div>`;
    return;
  }
  panel.innerHTML = filtered.map(createCvarItemHTML).join('');
  attachCvarEvents(panel);
}

function renderChangedTab() {
  const panel = document.getElementById('cvar-panel-changed');
  const modNames = new Set(Object.keys(cvarModified));
  const filtered = getFilteredCVars().filter(v => modNames.has(v.name));
  if (filtered.length === 0) {
    panel.innerHTML = `<div class='cvar-panel-empty'>${modNames.size === 0
      ? 'No values have been changed. Edit a CVar value and click Apply.'
      : 'No changed CVars match the current search filter.'}</div>`;
    return;
  }
  panel.innerHTML = filtered.map(createCvarItemHTML).join('');
  attachCvarEvents(panel);
}

function renderAllTab() {
  const panel = document.getElementById('cvar-panel-all');
  const filtered = getFilteredCVars();
  if (filtered.length === 0) {
    panel.innerHTML = `<div class='cvar-panel-empty'>No CVars match the current search filter.</div>`;
    return;
  }

  // Group by category
  const groups = {};
  for (const v of filtered) {
    const cat = v.category || 'Other';
    if (!groups[cat]) groups[cat] = [];
    groups[cat].push(v);
  }

  // Sort categories alphabetically
  const sortedCats = Object.keys(groups).sort();
  let html = '';
  for (const cat of sortedCats) {
    const items = groups[cat];
    html += `<div class='cvar-category' data-cat='${esc(cat)}'>`;
    html += `<span class='cvar-category-arrow'>\u25B6</span>`;
    html += `${esc(cat)} <span class='cvar-category-count'>(${items.length})</span>`;
    html += `</div>`;
    html += `<div class='cvar-category-items' data-cat='${esc(cat)}'>`;
    html += items.map(createCvarItemHTML).join('');
    html += `</div>`;
  }
  panel.innerHTML = html;

  // Category expand/collapse
  panel.querySelectorAll('.cvar-category').forEach(header => {
    header.addEventListener('click', () => {
      const cat = header.dataset.cat;
      const items = panel.querySelector(`.cvar-category-items[data-cat="${cat}"]`);
      const arrow = header.querySelector('.cvar-category-arrow');
      if (items.classList.contains('open')) {
        items.classList.remove('open');
        arrow.classList.remove('open');
      } else {
        items.classList.add('open');
        arrow.classList.add('open');
        if (!items.dataset.eventsAttached) {
          items.dataset.eventsAttached = 'true';
          attachCvarEvents(items);
          // Lazy-load values for visible items
          fetchValuesForItems(items);
        }
      }
    });
  });
}

function renderActiveTab() {
  if (activeCvarTab === 'favorites') { renderFavoritesTab(); fetchValuesForPanel('cvar-panel-favorites'); }
  else if (activeCvarTab === 'changed') { renderChangedTab(); fetchValuesForPanel('cvar-panel-changed'); }
  else if (activeCvarTab === 'all') renderAllTab();
  // Update changed tab badge
  const changedCount = Object.keys(cvarModified).length;
  const changedBtn = document.querySelector('.cvar-tab[data-tab="changed"]');
  if (changedBtn) changedBtn.textContent = changedCount > 0 ? `Changed (${changedCount})` : 'Changed';
}

async function fetchValuesForPanel(panelId) {
  const panel = document.getElementById(panelId);
  if (panel) await fetchValuesForItems(panel);
}

function showConsoleStatus(msg, ok) {
  const el = document.getElementById('console-status');
  el.textContent = msg;
  el.className = 'console-status ' + (ok ? 'console-ok' : 'console-err');
}

async function execConsoleCommand() {
  const input = document.getElementById('console-input');
  const cmd = input.value.trim();
  if (!cmd) return;

  try {
    const r = await fetch(API_BASE + '/api/console/command', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ command: cmd })
    });
    const result = await r.json();
    if (result.error) {
      showConsoleStatus('Error: ' + result.error, false);
    } else {
      showConsoleStatus('Executed: ' + cmd, true);
      input.value = '';
    }
  } catch(e) {
    showConsoleStatus('Failed: ' + e.message, false);
  }
}

// ---- Spawned Objects card ----
async function updateSpawned() {
  try {
    const d = await fetchJson('/api/blueprint/spawned');
    const el = document.getElementById('spawned-content');
    setDot('spawned-card', !d.error);
    if (d.error) { el.innerHTML = `<span class='error-msg'>${d.error}</span>`; return; }

    document.getElementById('spawned-count').textContent = `(${d.count || 0})`;

    if (!d.spawned || d.spawned.length === 0) {
      el.innerHTML = '<span style="color:#8b949e;font-size:0.85em">No objects spawned via MCP</span>';
      return;
    }

    el.innerHTML = d.spawned.map(obj => {
      const cls = obj.class || obj.fullName || '?';
      const addr = obj.address || '?';
      return `<div class='spawned-item'>
        <span class='spawned-class' title='${esc(obj.fullName || cls)}'>${esc(cls)}</span>
        <span class='spawned-addr'>${esc(addr)}</span>
      </div>`;
    }).join('');
  } catch(e) {
    setDot('spawned-card', false);
  }
}

// ---- Polling setup ----
function startPolling() {
  // Fast polling (500ms)
  setInterval(() => { updateCamera(); updateFPS(); }, 500);

  // Medium polling (2s)
  setInterval(() => { updateStatus(); updatePlayer(); updatePoses(); }, 2000);

  // Slow polling (5s)
  setInterval(() => { updateVR(); updateVRSettings(); updateLua(); updateSpawned(); }, 5000);

  // Game info (10s)
  setInterval(() => { updateGameInfo(); }, 10000);

  // Poll counter
  setInterval(() => {
    pollCount++;
    document.getElementById('poll-info').textContent = `Poll cycle #${pollCount}`;
  }, 2000);
}

// ---- Console event setup ----
function setupConsoleEvents() {
  // Tab switching
  document.querySelectorAll('.cvar-tab').forEach(tab => {
    tab.addEventListener('click', () => {
      document.querySelectorAll('.cvar-tab').forEach(t => t.classList.remove('active'));
      document.querySelectorAll('.cvar-panel').forEach(p => p.style.display = 'none');
      tab.classList.add('active');
      activeCvarTab = tab.dataset.tab;
      document.getElementById('cvar-panel-' + activeCvarTab).style.display = '';
      renderActiveTab();
    });
  });

  // Search with debounce
  const searchInput = document.getElementById('cvar-search');
  searchInput.addEventListener('input', () => {
    clearTimeout(cvarSearchTimer);
    cvarSearchTimer = setTimeout(() => renderActiveTab(), 300);
  });

  // Console command exec
  const execBtn = document.getElementById('console-exec-btn');
  execBtn.addEventListener('click', execConsoleCommand);

  const consoleInput = document.getElementById('console-input');
  consoleInput.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') execConsoleCommand();
  });
}

// ---- Init ----
(async function init() {
  // Initial fetch
  await Promise.allSettled([
    updateStatus(),
    updateGameInfo(),
    updatePlayer(),
    updateCamera(),
    updateVR(),
    updatePoses(),
    updateVRSettings(),
    updateLua(),
    updateSpawned(),
  ]);

  setupConsoleEvents();

  // Load CVars then render initial tab
  await loadAllCVars();
  renderActiveTab();

  startPolling();
})();
