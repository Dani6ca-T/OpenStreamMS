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

  let state = 'idle', msg = '', extra = '';
  if (updateChecking) { state = 'busy'; msg = t('update.checking'); }
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
  if (state === 'new') {
    actions.push(`<a class="btn btn-primary btn-sm" href="${esc(latest.url)}" target="_blank" rel="noopener">${esc(t('update.download'))}</a>`);
    if (latest.version !== st.dismissed)
      actions.push(`<button type="button" class="btn btn-ghost btn-sm" onclick="dismissUpdate()">${esc(t('update.dismiss'))}</button>`);
  }
  actions.push(`<button type="button" class="btn btn-ghost btn-sm" onclick="loadUpdateStatus(true)"${updateChecking ? ' disabled' : ''}>${esc(t('update.check'))}</button>`);

  box.dataset.state = state;
  box.innerHTML = `
    <div class="about-update-head">
      <span class="about-update-dot" aria-hidden="true"></span>
      <div class="about-update-text">
        <div class="about-update-msg" role="status">${esc(msg)}</div>
        ${extra ? `<div class="about-update-extra">${esc(extra)}</div>` : ''}
      </div>
    </div>
    <div class="about-update-actions">${actions.join('')}</div>
    <label class="toggle about-update-auto" title="${esc(t('update.autoHint'))}">
      <input type="checkbox" ${st.checkEnabled ? 'checked' : ''} onchange="setAutoUpdateCheck(this.checked)">
      <span class="toggle-track"></span>
      <span>${esc(t('update.auto'))}</span>
    </label>`;
}

window.addEventListener('langchange', renderUpdate);

// Al entrar en el panel: versión instalada y, si está activado, comprobación (el servicio la
// guarda 6 h, así que recargar el panel no vuelve a preguntar a GitHub).
document.addEventListener('DOMContentLoaded', async () => {
  if (!await I18n.ready) return;
  loadUpdateStatus(false);
});
