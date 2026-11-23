'use strict';
// HUD de una sesión (Services/Hud/HudWindow.cs lo muestra con Ctrl+Alt+H o Guía + Back).
// Lee /api/hud cada 2 s mientras está abierto. Se maneja con teclado o mando: cruceta o flechas
// para moverse, A o Enter para pulsar, B o Esc para cerrar. El tema es el que se eligió en el panel
// (lo dice el servicio) y el fondo, la pantalla de debajo desenfocada (/hud-backdrop.jpg, que sirve
// la propia ventana del HUD).

const POLL_MS = 2000;
let pollTimer = null;
let open = true;
let lastPadButtons = [];

const byId = id => document.getElementById(id);
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const pct = v => (v === null || v === undefined) ? null : Math.max(0, Math.min(100, v));
const fmt = v => v === null ? '—' : `${Math.round(v)}%`;

function setBackdrop(version) {
  byId('hud-backdrop').style.backgroundImage = `url("/hud-backdrop.jpg?v=${encodeURIComponent(version ?? Date.now())}")`;
}

function meter(label, value, extra) {
  const v = pct(value);
  const level = v === null ? '' : v >= 90 ? ' hud-bad' : v >= 70 ? ' hud-warn' : '';
  return `<div class="hud-meter">
      <div class="hud-meter-top"><span>${esc(label)}</span><b>${extra ?? fmt(v)}</b></div>
      <div class="hud-bar${level}"><i style="width:${v ?? 0}%"></i></div>
    </div>`;
}

function deviceLabel(d) {
  if (d.source === 'moonlight') return t('hud.moonlightPad', { n: d.number });
  return d.label || t('hud.kind.' + d.kind);
}

function render(data) {
  window.Theme?.preview(data.theme, { accent: data.accent, light: data.lightTheme });

  const pc = data.pc;
  const memPct = pc.memTotalGb > 0 ? pc.memUsedGb / pc.memTotalGb * 100 : null;
  byId('hud-pc').innerHTML =
    meter(t('hud.cpu'), pc.cpu) +
    meter(t('hud.gpu'), pc.gpu) +
    meter(t('hud.encode'), pc.gpuEncode) +
    meter(t('hud.memory'), memPct, esc(t('hud.memValue', { used: pc.memUsedGb.toFixed(1), total: pc.memTotalGb.toFixed(0) })));

  const mine = data.sessions.find(s => s.isMine);
  byId('hud-me').innerHTML = mine
    ? esc(t('hud.this')) + ': <b>' + esc(mine.name) + '</b>' + (mine.seat ? ' · ' + esc(t('hud.seat', { name: mine.seat })) : '')
    : '';

  byId('hud-power').hidden = !data.powerAllowed;
  byId('hud-restart-session').hidden = data.restartBlocked === 'none' || !!data.restartBlocked;
  const note = data.restartBlocked === 'keyboard' ? t('hud.restartBlockedKeyboard', { name: data.restartBlockedBy })
             : data.restartBlocked === 'seat' ? t('hud.restartBlockedSeat', { name: data.restartBlockedBy }) : '';
  byId('hud-restart-note').textContent = note;
  byId('hud-restart-note').hidden = !note;

  // Al redibujar la lista solo se recoloca el foco si estaba en ella (no en los botones).
  const inList = byId('hud-list').contains(document.activeElement) || document.activeElement === document.body;
  const focused = document.activeElement?.dataset?.id;
  byId('hud-list').innerHTML = data.sessions.map(s => `
    <article class="hud-sess hud-nav${s.isMine ? ' hud-mine' : ''}" tabindex="0" data-id="${esc(s.id)}">
      <div class="hud-sess-name">${esc(s.name)}
        ${s.isMine ? `<span class="hud-chip hud-me-chip">${esc(t('hud.this'))}</span>` : ''}
        ${s.seat ? `<span class="hud-chip">${esc(t('hud.seat', { name: s.seat }))}</span>` : ''}
        ${s.composite ? `<span class="hud-chip">${esc(t('hud.composite', { name: s.composite }))}</span>` : ''}
        ${s.software ? `<span class="hud-chip hud-warn-chip">${esc(t('hud.software'))}</span>` : ''}
      </div>
      <div class="hud-sess-meters">
        ${meter(t('hud.cpu'), s.cpu)}${meter(t('hud.gpu'), s.gpu)}${meter(t('hud.encode'), s.gpuEncode)}
      </div>
      <ul class="hud-devs">
        ${s.devices.length ? s.devices.map(d => `
          <li><span class="hud-k">${esc(t('hud.kind.' + d.kind))}</span><span>${esc(deviceLabel(d))}</span></li>`).join('')
          : `<li class="hud-none">${esc(t('hud.noDevices'))}</li>`}
      </ul>
    </article>`).join('');

  if (!inList) return;
  const again = focused && byId('hud-list').querySelector(`[data-id="${CSS.escape(focused)}"]`);
  (again || byId('hud-list').querySelector('.hud-sess.hud-mine') || byId('hud-list').querySelector('.hud-sess'))?.focus({ preventScroll: false });
}

async function refresh() {
  try {
    const res = await fetch('/api/hud', { cache: 'no-store' });
    if (!res.ok) throw new Error(res.status);
    render(await res.json());
  } catch {
    byId('hud-list').innerHTML = `<p class="hud-error">${esc(t('hud.error'))}</p>`;
  }
}

function startPolling() {
  stopPolling();
  refresh();
  pollTimer = setInterval(refresh, POLL_MS);
}

function stopPolling() {
  if (pollTimer) clearInterval(pollTimer);
  pollTimer = null;
}

function close() {
  window.chrome?.webview?.postMessage('close');
}

// ── Apagar / reiniciar (solo si la sesión tiene permiso) ────────────────────

const CONFIRM_MS = 4000;
let confirmTimer = null;
let countdownTimer = null;

function resetConfirm() {
  clearTimeout(confirmTimer);
  document.querySelectorAll('#hud-power .hud-btn').forEach(b => {
    b.classList.remove('hud-confirm');
    b.textContent = t('hud.' + b.dataset.action);
  });
}

// Primera pulsación: pide confirmación; segunda (en 4 s): lo hace.
async function powerPressed(btn) {
  if (!btn.classList.contains('hud-confirm')) {
    resetConfirm();
    btn.classList.add('hud-confirm');
    btn.textContent = t('hud.confirm');
    confirmTimer = setTimeout(resetConfirm, CONFIRM_MS);
    return;
  }
  resetConfirm();
  const action = btn.dataset.action;
  try {
    const res = await fetch('/api/hud/power/' + action, { method: 'POST' });
    if (!res.ok) throw new Error(res.status);
    const { delaySeconds } = await res.json();
    showPending(action, delaySeconds);
  } catch {
    showPendingText(t('hud.powerError'), false);
  }
}

function showPending(action, seconds) {
  clearInterval(countdownTimer);
  let left = seconds;
  const tick = () => showPendingText(t(action === 'restart' ? 'hud.restarting' : 'hud.shuttingDown', { s: left }), true);
  tick();
  countdownTimer = setInterval(() => { left = Math.max(0, left - 1); tick(); }, 1000);
  byId('hud-cancel').focus();
}

function showPendingText(text, cancellable) {
  byId('hud-pending-text').textContent = text;
  byId('hud-cancel').hidden = !cancellable;
  byId('hud-pending').hidden = false;
}

async function cancelPower() {
  clearInterval(countdownTimer);
  byId('hud-pending').hidden = true;
  try { await fetch('/api/hud/power/cancel', { method: 'POST' }); } catch { /* el aviso de Windows también deja cancelar */ }
  byId('hud-list').querySelector('.hud-sess.hud-mine, .hud-sess')?.focus();
}

document.querySelectorAll('#hud-power .hud-btn').forEach(b => b.addEventListener('click', () => powerPressed(b)));

// Reiniciar esta sesión: también con doble pulsación. La sesión se cierra y vuelve sola; en una
// compuesta su hueco enseña el logo y "Reconectando" mientras tanto.
let restartConfirmTimer = null;
async function restartSessionPressed(btn) {
  if (!btn.classList.contains('hud-confirm')) {
    btn.classList.add('hud-confirm');
    btn.textContent = t('hud.confirm');
    clearTimeout(restartConfirmTimer);
    restartConfirmTimer = setTimeout(() => { btn.classList.remove('hud-confirm'); btn.textContent = t('hud.restartSession'); }, CONFIRM_MS);
    return;
  }
  clearTimeout(restartConfirmTimer);
  btn.classList.remove('hud-confirm');
  btn.textContent = t('hud.restartSession');
  try {
    const res = await fetch('/api/hud/session/restart', { method: 'POST' });
    if (!res.ok) throw new Error(res.status);
    showPendingText(t('hud.restartingSession'), false);
  } catch {
    showPendingText(t('hud.restartSessionError'), false);
  }
}
byId('hud-restart-session').addEventListener('click', e => restartSessionPressed(e.currentTarget));
byId('hud-cancel').addEventListener('click', cancelPower);

// ── Navegación ───────────────────────────────────────────────────────────────

function moveFocus(step) {
  const items = [...document.querySelectorAll('.hud-nav')].filter(el => el.offsetParent !== null && !el.closest('[hidden]'));
  if (!items.length) return;
  const i = Math.max(0, items.indexOf(document.activeElement));
  items[(i + step + items.length) % items.length].focus();
}

document.addEventListener('keydown', e => {
  if (e.key === 'Escape') { e.preventDefault(); close(); }
  else if (e.key === 'ArrowDown' || e.key === 'ArrowRight') { e.preventDefault(); moveFocus(1); }
  else if (e.key === 'ArrowUp' || e.key === 'ArrowLeft') { e.preventDefault(); moveFocus(-1); }
});
byId('hud-close').addEventListener('click', close);

// Mando: cruceta para moverse, A para pulsar el botón con el foco, B para cerrar.
function pollGamepads() {
  if (open) {
    const pads = navigator.getGamepads ? [...navigator.getGamepads()].filter(Boolean) : [];
    pads.forEach((p, i) => {
      const prev = lastPadButtons[i] || [];
      const pressed = b => p.buttons[b]?.pressed && !prev[b];
      if (pressed(0) && document.activeElement?.tagName === 'BUTTON') document.activeElement.click();   // A
      if (pressed(1)) close();                          // B
      if (pressed(13) || pressed(15)) moveFocus(1);     // abajo, derecha
      if (pressed(12) || pressed(14)) moveFocus(-1);    // arriba, izquierda
      lastPadButtons[i] = p.buttons.map(b => b.pressed);
    });
  }
  requestAnimationFrame(pollGamepads);
}

window.chrome?.webview?.addEventListener('message', e => {
  const msg = typeof e.data === 'string' ? { type: e.data } : e.data || {};
  if (msg.type === 'open') { open = true; lastPadButtons = []; setBackdrop(msg.backdrop); startPolling(); }
  else if (msg.type === 'close') { open = false; stopPolling(); resetConfirm(); }
});

(async () => {
  setBackdrop();
  if (!await I18n.ready) return;
  startPolling();
  requestAnimationFrame(pollGamepads);
  window.addEventListener('langchange', refresh);
})();
