'use strict';
// ── Versión instalada y actualizaciones ──────────────────────────────────────
// «Acerca de» enseña la versión y si hay una nueva en GitHub. Lo mira el servicio
// (UpdateChecker: caché de 6 h, se puede desactivar). Con una versión nueva aparece una píldora
// en la barra hasta que se instala o se pide no avisar de esa versión.
// Depende de app.js (api, t, esc, byId, show, toast) e i18n.js.

let updateStatus = null;
let updateChecking = false;

async function loadUpdateStatus(force) {
  if (updateChecking) return;
  updateChecking = true;
  renderUpdate();
  try {
    const res = await api(force ? 'POST' : 'GET', force ? '/api/updates/check' : '/api/updates');
    if (res && res.ok) updateStatus = await res.json();
    else if (res && force) toast(await I18n.responseError(res) || t('toast.connectionError'), true);
  } finally {
    updateChecking = false;
    renderUpdate();
  }
}

function openAbout() {
  document.querySelectorAll('.nav-pop').forEach(p => p.classList.add('hidden'));
  renderUpdate();
  show('about-modal');
}

async function saveUpdateSettings(body) {
  const res = await api('PUT', '/api/updates/settings', body);
  if (!res) return false;
  if (!res.ok) { toast(await I18n.responseError(res) || t('toast.connectionError'), true); return false; }
  updateStatus = await res.json();
  renderUpdate();
  return true;
}

async function setAutoUpdateCheck(on) {
  if (await saveUpdateSettings({ checkForUpdates: on }) && on) loadUpdateStatus(false);
  else renderUpdate();   // si falló, el interruptor vuelve a como estaba
}

async function dismissUpdate() {
  const v = updateStatus?.latest?.version;
  if (v) await saveUpdateSettings({ dismissed: v });
}

const updateDate = iso => {
  try { return new Date(iso).toLocaleDateString((I18n.lang || 'en').replace('_', '-'), { dateStyle: 'medium' }); }
  catch { return ''; }
};

function renderUpdate() {
  const st = updateStatus;
  const ver = byId('about-version');
  if (ver) {
    ver.textContent = st ? t('about.version', { version: st.currentVersion }) : '';
    ver.classList.toggle('about-version-dev', !!st?.devBuild);
    if (st?.devBuild) ver.insertAdjacentHTML('beforeend', ` <span class="about-dev-tag">${esc(t('about.devBuild'))}</span>`);
  }

  const latest = st?.latest;
  const badge = byId('update-badge');
  if (badge) {
    const announce = !!(st?.updateAvailable && latest && latest.version !== st.dismissed);
    badge.hidden = !announce;
    if (announce) {
      byId('update-badge-ver').textContent = latest.version;
      badge.title = t('update.badgeTitle', { version: latest.version });
    }
  }

  const box = byId('about-update');
  if (!box) return;
  if (!st) { box.innerHTML = updateChecking ? `<p class="about-update-msg">${esc(t('update.checking'))}</p>` : ''; return; }

  let state = 'idle', msg = '', extra = '', progress = null;
  const inst = updateInstall;
  if (inst && inst.state !== 'idle' && inst.state !== 'failed') {
    state = 'busy';
    if (inst.state === 'downloading') {
      msg = t('update.install.downloading', { version: inst.version, progress: inst.progress ?? 0 });
      progress = inst.progress ?? 0;
    } else if (inst.state === 'verifying') msg = t('update.install.verifying');
    else { msg = t('update.install.installing', { version: inst.version }); extra = t('update.install.wait'); }
  }
  else if (inst && inst.state === 'failed') {
    state = 'error';
    msg = t(INSTALL_ERRORS[inst.error] || 'update.install.err.launch');
    if (inst.logPath) extra = t('update.install.log', { path: inst.logPath });
  }
  else if (updateChecking) { state = 'busy'; msg = t('update.checking'); }
  else if (st.devBuild) {
    state = 'dev'; msg = t('update.devBuild');
    if (latest) extra = t('update.latestIs', { version: latest.version });
  }
  else if (st.updateAvailable && latest) {
    state = 'new'; msg = t('update.available', { version: latest.version });
    if (latest.publishedAt) extra = t('update.published', { date: updateDate(latest.publishedAt) });
  }
  else if (st.error) { state = 'error'; msg = t(st.error === 'ratelimit' ? 'update.rateLimit' : 'update.error'); }
  else if (latest) { state = 'ok'; msg = t('update.upToDate'); }
  else if (st.checkedAt) { state = 'ok'; msg = t('update.noReleases'); }
  else if (!st.checkEnabled) { msg = t('update.off'); }

  const actions = [];
  const installing = state === 'busy' && !!inst;
  const offer = !installing && st.updateAvailable && latest && !st.devBuild;
  if (offer && latest.installable)
    actions.push(`<button type="button" class="btn btn-primary btn-sm" onclick="installUpdate()">${esc(t('update.install.btn'))}</button>`);
  if (offer)
    actions.push(`<a class="btn ${latest.installable ? 'btn-ghost' : 'btn-primary'} btn-sm" href="${esc(latest.url)}" target="_blank" rel="noopener">${esc(t('update.download'))}</a>`);
  if (offer && state === 'new' && latest.version !== st.dismissed)
    actions.push(`<button type="button" class="btn btn-ghost btn-sm" onclick="dismissUpdate()">${esc(t('update.dismiss'))}</button>`);
  if (!installing)
    actions.push(`<button type="button" class="btn btn-ghost btn-sm" onclick="loadUpdateStatus(true)"${updateChecking ? ' disabled' : ''}>${esc(t('update.check'))}</button>`);

  box.dataset.state = state;
  box.innerHTML = `
    <div class="about-update-head">
      <span class="about-update-dot" aria-hidden="true"></span>
      <div class="about-update-text">
        <div class="about-update-msg" role="status">${esc(msg)}</div>
        ${extra ? `<div class="about-update-extra">${esc(extra)}</div>` : ''}
        ${progress != null ? `<div class="about-update-bar" role="progressbar" aria-valuemin="0" aria-valuemax="100" aria-valuenow="${progress}"><span style="width:${progress}%"></span></div>` : ''}
      </div>
    </div>
    <div class="about-update-actions">${actions.join('')}</div>
    <label class="toggle about-update-auto" title="${esc(t('update.autoHint'))}">
      <input type="checkbox" ${st.checkEnabled ? 'checked' : ''} onchange="setAutoUpdateCheck(this.checked)">
      <span class="toggle-track"></span>
      <span>${esc(t('update.auto'))}</span>
    </label>`;
}

// ── Actualizar desde el panel ────────────────────────────────────────────────
// El servicio descarga el instalador, lo comprueba y lo ejecuta en silencio (UpdateInstaller).
// El instalador para el servicio: el panel deja de responder un rato y se recarga solo cuando el
// servicio vuelve con la versión nueva.
let updateInstall = null;
let updatePolling = false;
const INSTALL_ERRORS = {
  download: 'update.install.err.download', size: 'update.install.err.hash', hash: 'update.install.err.hash',
  launch: 'update.install.err.launch', busy: 'update.install.err.busy', notavailable: 'update.install.err.notavailable',
  noinstaller: 'update.install.err.noinstaller', devbuild: 'update.install.err.devbuild', timeout: 'update.install.err.timeout',
  aborted: 'update.install.err.aborted',
};
const updateSleep = ms => new Promise(r => setTimeout(r, ms));

async function installUpdate() {
  const v = updateStatus?.latest?.version;
  if (!v || updatePolling) return;
  if (!await ask(t('update.install.confirm', { version: v }), { title: t('update.install.title'), ok: t('update.install.ok'), danger: true })) return;
  const res = await api('POST', '/api/updates/install', { version: v });
  if (!res) return;
  if (!res.ok) {
    let code = '';
    try { code = (await res.json()).error || ''; } catch { /* sin cuerpo */ }
    toast(t(INSTALL_ERRORS[code] || 'update.install.err.launch'), true);
    if (code === 'notavailable') loadUpdateStatus(true);
    return;
  }
  updateInstall = await res.json();
  renderUpdate();
  pollInstall(v);
}

// Sin api(): mientras el servicio se reinicia las peticiones fallan y no hay que avisar de cada una.
async function updateFetch(path) {
  try {
    const r = await fetch(path, { credentials: 'same-origin', cache: 'no-store' });
    if (r.status === 401) { location.reload(); return null; }
    return r.ok ? await r.json() : null;
  } catch { return null; }
}

async function pollInstall(target) {
  if (updatePolling) return;
  updatePolling = true;
  const started = Date.now();
  let sawDown = false, backSince = 0;
  try {
    while (Date.now() - started < 15 * 60 * 1000) {
      await updateSleep(1500);
      const st = await updateFetch('/api/updates/install');
      if (st && st.state !== 'idle') {
        updateInstall = st;
        if (st.state === 'failed') return;
      } else {
        // El servicio está parado (st null) o ha vuelto (idle): ¿con la versión nueva?
        if (!st) sawDown = true;
        updateInstall = { ...(updateInstall || {}), state: 'installing', version: target };
        const u = st && await updateFetch('/api/updates');
        if (u && u.currentVersion === target) {
          toast(t('update.install.done', { version: target }));
          setTimeout(() => location.reload(), 1500);
          return;
        }
        // Volvió con la versión de antes: el instalador falló o no llegó a cambiar nada.
        if (u && sawDown) {
          backSince ||= Date.now();
          if (Date.now() - backSince > 60 * 1000) { updateInstall = { ...updateInstall, state: 'failed', error: 'timeout' }; return; }
        }
      }
      renderUpdate();
    }
    updateInstall = { ...(updateInstall || {}), state: 'failed', version: target, error: 'timeout' };
  } finally {
    updatePolling = false;
    renderUpdate();
  }
}

window.addEventListener('langchange', renderUpdate);

// Al entrar en el panel: versión instalada y, si está activado, comprobación (el servicio la
// guarda 6 h, así que recargar el panel no vuelve a preguntar a GitHub). Si ya hay una
// actualización en marcha (desde otra pestaña), se sigue.
document.addEventListener('DOMContentLoaded', async () => {
  if (!await I18n.ready) return;
  loadUpdateStatus(false);
  const inst = await updateFetch('/api/updates/install');
  if (inst && ['downloading', 'verifying', 'installing'].includes(inst.state)) {
    updateInstall = inst;
    renderUpdate();
    pollInstall(inst.version);
  }
});
