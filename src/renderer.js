let config = {};
let availableMods = [];
let installedMods = [];
let downloadInProgress = false;

const $ = (id) => document.getElementById(id);

function showToast(msg, type = '') {
  const t = $('toast');
  t.textContent = msg;
  t.className = 'toast visible ' + type;
  clearTimeout(t._hide);
  t._hide = setTimeout(() => t.classList.remove('visible'), 3500);
}

function setStatus(text, ok = true) {
  $('statusText').textContent = text;
  const dot = $('statusBadge').querySelector('.status-dot');
  dot.style.background = ok ? 'var(--accent2)' : 'var(--danger)';
}

function updateCounts() {
  $('availableCount').textContent = availableMods.length;
  $('installedCount').textContent = installedMods.length;
}

function showProgress(show) {
  $('progressSection').style.display = show ? 'block' : 'none';
}

function formatSize(bytes) {
  if (!bytes) return '0 B';
  const units = ['B', 'KB', 'MB', 'GB'];
  let i = 0;
  let size = bytes;
  while (size >= 1024 && i < units.length - 1) { size /= 1024; i++; }
  return size.toFixed(i === 0 ? 0 : 1) + ' ' + units[i];
}

function updateProgress(pct, file, downloaded, total) {
  $('progressFill').style.width = pct + '%';
  const sizeStr = total ? `${formatSize(downloaded)} / ${formatSize(total)}` : `${pct}%`;
  $('progressLabel').textContent = file ? `${file} — ${sizeStr}` : sizeStr;
}

async function init() {
  const constants = await window.api.getConstants();
  $('serverInfo').innerHTML = `SCUM Server | ${constants.serverIp} <span class="copy-icon">📋</span>`;

  $('serverInfo').addEventListener('click', async () => {
    await window.api.copyText(constants.serverIp);
    showToast('IP скопирован: ' + constants.serverIp, 'success');
  });

  config = await window.api.getConfig();
  let gamePath = config.gamePath || '';

  if (!gamePath || !(await window.api.findGameExe(gamePath))) {
    const detected = await window.api.detectScum();
    if (detected) gamePath = detected;
  }

  config.gamePath = gamePath || 'C:\\Program Files (x86)\\Steam\\steamapps\\common\\SCUM';
  config.modsPath = config.modsPath || '';
  config.launchMode = config.launchMode || 'dx11';

  // Set active mode button
  document.querySelectorAll('.mode-btn').forEach((b) => b.classList.toggle('active', b.dataset.mode === config.launchMode));

  updatePaths();
  await window.api.saveConfig(config);
  await loadMods();
}

async function updatePaths() {
  $('gamePathInput').value = config.gamePath;
  const exe = await window.api.findGameExe(config.gamePath);
  const found = !!exe;
  $('gameStatus').innerHTML = `<span class="status-dot small" style="background:${found ? 'var(--accent2)' : 'var(--danger)'}"></span><span>${found ? 'SCUM найден' : 'SCUM не найден'}</span>`;

  if (!config.modsPath) {
    config.modsPath = await window.api.getModsPath(config.gamePath);
    await window.api.saveConfig(config);
  }
  $('modsPathInput').value = config.modsPath;
}

async function loadMods() {
  setStatus('Загрузка списка модов...');
  showProgress(true);
  updateProgress(0, 'Подключение к GitHub...');

  try {
    availableMods = await window.api.fetchMods();
    installedMods = await window.api.scanMods(config.modsPath);
    updateCounts();
    setStatus(`Доступно модов: ${availableMods.length}`);
  } catch (e) {
    setStatus(`Ошибка: ${e.message}`, false);
    showToast('Не удалось загрузить список модов', 'error');
  }
  showProgress(false);
}

async function scanInstalled() {
  installedMods = await window.api.scanMods(config.modsPath);
  updateCounts();
}

// Download
$('downloadBtn').addEventListener('click', async () => {
  if (downloadInProgress) return;
  if (!availableMods.length) {
    showToast('Список модов пуст. Подождите загрузки.', 'error');
    return;
  }

  downloadInProgress = true;
  $('downloadBtn').disabled = true;
  $('deleteBtn').disabled = true;
  showProgress(true);

  window.api.onDownloadProgress((data) => {
    updateProgress(data.percent, data.file, data.downloaded, data.total);
    setStatus(`📥 ${data.file} (${data.percent}%)`);
  });

  try {
    const result = await window.api.downloadAllMods(availableMods, config.modsPath);
    await scanInstalled();
    if (result.success > 0) {
      showToast(`✅ Скачано: ${result.success}, ошибок: ${result.errors}`, 'success');
      setStatus(`Скачано модов: ${result.success}`);
    } else {
      showToast('Все моды уже установлены', 'success');
      setStatus('Все моды уже установлены');
    }
  } catch (e) {
    showToast(`Ошибка: ${e.message}`, 'error');
    setStatus('Ошибка скачивания', false);
  }

  showProgress(false);
  $('downloadBtn').disabled = false;
  $('deleteBtn').disabled = false;
  downloadInProgress = false;
});

// Delete
$('deleteBtn').addEventListener('click', async () => {
  await scanInstalled();
  if (!installedMods.length) {
    showToast('Моды не установлены');
    return;
  }
  if (!confirm(`Удалить все ${installedMods.length} модов?`)) return;

  try {
    const removed = await window.api.deleteAllMods(config.modsPath);
    await scanInstalled();
    showToast(`✅ Удалено модов: ${removed}`, 'success');
    setStatus(`Удалено модов: ${removed}`);
  } catch (e) {
    showToast(`Ошибка: ${e.message}`, 'error');
  }
});

// Launch
$('launchBtn').addEventListener('click', async () => {
  const exe = await window.api.findGameExe(config.gamePath);
  if (!exe) {
    showToast('SCUM не найден! Укажите путь к игре.', 'error');
    return;
  }

  try {
    await window.api.launchGame({ gamePath: config.gamePath, mode: config.launchMode });
    setStatus(`🚀 Запуск SCUM (${config.launchMode.toUpperCase()})...`);
    showToast('SCUM запущен!', 'success');
  } catch (e) {
    showToast(`Ошибка: ${e.message}`, 'error');
    setStatus('Ошибка запуска', false);
  }
});

// Mode select
document.querySelectorAll('.mode-btn').forEach((btn) => {
  btn.addEventListener('click', async () => {
    document.querySelectorAll('.mode-btn').forEach((b) => b.classList.remove('active'));
    btn.classList.add('active');
    config.launchMode = btn.dataset.mode;
    await window.api.saveConfig({ launchMode: config.launchMode });
  });
});

// Browse game
$('browseGameBtn').addEventListener('click', async () => {
  const folder = await window.api.browseFolder(config.gamePath);
  if (folder) {
    config.gamePath = folder;
    config.modsPath = '';
    updatePaths();
    await window.api.saveConfig({ gamePath: folder });
    await scanInstalled();
  }
});

// Manual game path input
$('gamePathInput').addEventListener('change', async () => {
  const val = $('gamePathInput').value.trim();
  if (!val || val === config.gamePath) return;
  config.gamePath = val;
  config.modsPath = '';
  await window.api.saveConfig({ gamePath: val, modsPath: '' });
  updatePaths();
  await loadMods();
});

// Browse mods
$('browseModsBtn').addEventListener('click', async () => {
  const folder = await window.api.browseFolder(config.modsPath);
  if (folder) {
    config.modsPath = folder;
    $('modsPathInput').value = folder;
    await window.api.saveConfig({ modsPath: folder });
    await scanInstalled();
  }
});

// Manual mods path input
$('modsPathInput').addEventListener('change', async () => {
  const val = $('modsPathInput').value.trim();
  if (!val || val === config.modsPath) return;
  config.modsPath = val;
  await window.api.saveConfig({ modsPath: val });
  await scanInstalled();
});

// Init
init();

// Particles
const canvas = document.getElementById('particles-canvas');
const ctx = canvas.getContext('2d');
let particles = [];
let animId;

function resizeCanvas() {
  canvas.width = window.innerWidth;
  canvas.height = window.innerHeight;
}
window.addEventListener('resize', resizeCanvas);
resizeCanvas();

function createParticle() {
  const size = Math.random() * 2.5 + 0.5;
  return {
    x: Math.random() * canvas.width,
    y: canvas.height + size,
    size,
    speedY: -(Math.random() * 0.4 + 0.1),
    speedX: (Math.random() - 0.5) * 0.2,
    opacity: Math.random() * 0.5 + 0.1,
    hue: Math.random() > 0.3 ? 150 : 120,
  };
}

for (let i = 0; i < 80; i++) {
  const p = createParticle();
  p.y = Math.random() * canvas.height;
  particles.push(p);
}

function animateParticles() {
  ctx.clearRect(0, 0, canvas.width, canvas.height);

  for (let i = 0; i < particles.length; i++) {
    const p = particles[i];
    p.x += p.speedX;
    p.y += p.speedY;

    if (p.y < -10) {
      Object.assign(p, createParticle());
      p.y = canvas.height + p.size;
    }
    if (p.x < -10) p.x = canvas.width + 10;
    if (p.x > canvas.width + 10) p.x = -10;

    ctx.beginPath();
    ctx.arc(p.x, p.y, p.size, 0, Math.PI * 2);
    ctx.fillStyle = `rgba(0,255,136,${p.opacity * 0.6})`;
    ctx.fill();

    if (p.size > 1.5) {
      ctx.beginPath();
      ctx.arc(p.x, p.y, p.size * 2, 0, Math.PI * 2);
      ctx.fillStyle = `rgba(0,255,136,${p.opacity * 0.08})`;
      ctx.fill();
    }
  }

  animId = requestAnimationFrame(animateParticles);
}
animateParticles();
