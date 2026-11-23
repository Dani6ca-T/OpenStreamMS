'use strict';
// HUD de una sesión (Services/Hud/HudWindow.cs lo muestra con Ctrl+Alt+H o Guía + Back).
// Lee /api/hud cada 2 s mientras está abierto. Se maneja con teclado o mando: cruceta o flechas
// para moverse entre sesiones, B o Esc para cerrar.

const POLL_MS = 2000;
let pollTimer = null;
let open = true;
let lastPadButtons = [];

const byId = id => document.getElementById(id);
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));
const pct = v => (v === null || v === undefined) ? null : Math.max(0, Math.min(100, v));
const fmt = v => v === null ? '—' : `${Math.round(v)}%`;

function meter(label, value, extra) {
  const v = pct(value);
  const level = v === null ? '' : v >= 90 ? 'bad' : v >= 70 ? 'warn' : '';
  return `<div class="meter">
      <div class="meter-top"><span>${esc(label)}</span><b>${extra ?? fmt(v)}</b></div>
      <div class="bar ${level}"><i style="width:${v ?? 0}%"></i></div>
    </div>`;
}

function deviceLabel(d) {
  if (d.source === 'moonlight') return t('hud.moonlightPad', { n: d.number });
  return d.label || t('hud.kind.' + d.kind);
}

function render(data) {
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

  const focused = document.activeElement?.dataset?.id;
  byId('hud-list').innerHTML = data.sessions.map(s => `
    <article class="sess${s.isMine ? ' mine' : ''}" tabindex="0" data-id="${esc(s.id)}">
      <div class="sess-name">${esc(s.name)}
        ${s.isMine ? `<span class="chip me">${esc(t('hud.this'))}</span>` : ''}
        ${s.seat ? `<span class="chip">${esc(t('hud.seat', { name: s.seat }))}</span>` : ''}
        ${s.composite ? `<span class="chip">${esc(t('hud.composite', { name: s.composite }))}</span>` : ''}
        ${s.software ? `<span class="chip warn">${esc(t('hud.software'))}</span>` : ''}
      </div>
      <div class="sess-meters">
        ${meter(t('hud.cpu'), s.cpu)}${meter(t('hud.gpu'), s.gpu)}${meter(t('hud.encode'), s.gpuEncode)}
      </div>
      <ul class="devs">
        ${s.devices.length ? s.devices.map(d => `
          <li><span class="k">${esc(t('hud.kind.' + d.kind))}</span><span>${esc(deviceLabel(d))}</span></li>`).join('')
          : `<li class="none">${esc(t('hud.noDevices'))}</li>`}
      </ul>
    </article>`).join('');

  const again = focused && byId('hud-list').querySelector(`[data-id="${CSS.escape(focused)}"]`);
  (again || byId('hud-list').querySelector('.sess.mine') || byId('hud-list').querySelector('.sess'))?.focus({ preventScroll: false });
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

function moveFocus(step) {
  const items = [...document.querySelectorAll('.sess')];
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

// Mando: cruceta para moverse, B para cerrar (A queda para las acciones de la próxima versión).
function pollGamepads() {
  if (open) {
    const pads = navigator.getGamepads ? [...navigator.getGamepads()].filter(Boolean) : [];
    pads.forEach((p, i) => {
      const prev = lastPadButtons[i] || [];
      const pressed = b => p.buttons[b]?.pressed && !prev[b];
      if (pressed(1)) close();                          // B
      if (pressed(13) || pressed(15)) moveFocus(1);     // abajo, derecha
      if (pressed(12) || pressed(14)) moveFocus(-1);    // arriba, izquierda
      lastPadButtons[i] = p.buttons.map(b => b.pressed);
    });
  }
  requestAnimationFrame(pollGamepads);
}

window.chrome?.webview?.addEventListener('message', e => {
  if (e.data === 'open') { open = true; lastPadButtons = []; startPolling(); }
  else if (e.data === 'close') { open = false; stopPolling(); }
});

(async () => {
  if (!await I18n.ready) return;
  startPolling();
  requestAnimationFrame(pollGamepads);
  window.addEventListener('langchange', refresh);
})();
