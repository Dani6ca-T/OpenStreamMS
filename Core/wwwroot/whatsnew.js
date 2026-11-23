'use strict';
// ── Novedades de la versión ──────────────────────────────────────────────────
// Ventana por pasos que presenta lo nuevo de una versión mayor. Se abre sola una vez por
// versión (WHATS_NEW_VERSION) al entrar en el panel, y luego desde la píldora «2.1» de la barra
// y desde «Más». Mismo lenguaje visual que la guía (guide.js): clases guide-* y ga-*.
// Depende de app.js (t, esc, uiIcon, show, hide, byId), seats.js (switchTab) y
// composites.js (openCompositeModal).

const WHATS_NEW_VERSION = '2.1';
const WHATS_NEW_KEY = 'openstreamms.whatsNewSeen';
const WN_STEPS = ['intro', 'hud', 'composite', 'more'];
let wnStep = 0;

function whatsNewSeen() {
  try { return localStorage.getItem(WHATS_NEW_KEY) === WHATS_NEW_VERSION; } catch { return true; }
}

function markWhatsNewSeen() {
  try { localStorage.setItem(WHATS_NEW_KEY, WHATS_NEW_VERSION); } catch { /* sin almacenamiento: se verá otra vez */ }
  byId('wn-badge')?.classList.remove('wn-badge-new');
}

// «No volver a mostrar hasta que haya novedades»: se guarda en el servicio, así vale para cualquier
// navegador o equipo; con otra WHATS_NEW_VERSION vuelve a abrirse sola una vez.
async function dismissWhatsNew() {
  closeWhatsNew();
  const res = await api('PUT', '/api/settings/whatsnew', { version: WHATS_NEW_VERSION });
  if (res && !res.ok) toast(await I18n.responseError(res) || t('toast.connectionError'), true);
}

async function whatsNewDismissedOnServer() {
  try {
    const res = await fetch('/api/settings', { credentials: 'same-origin', cache: 'no-store' });
    if (!res.ok) return false;
    return (await res.json()).whatsNewDismissed === WHATS_NEW_VERSION;
  } catch { return false; }
}

function openWhatsNew(step = 0) {
  wnStep = Math.max(0, Math.min(WN_STEPS.length - 1, step));
  document.querySelectorAll('.nav-pop').forEach(p => p.classList.add('hidden'));
  show('whatsnew-modal');
  renderWhatsNew(0);
}

function closeWhatsNew() {
  hide('whatsnew-modal');
  markWhatsNewSeen();
}

function wnGo(step) {
  if (step < 0 || step >= WN_STEPS.length || step === wnStep) return;
  const dir = step > wnStep ? 1 : -1;
  wnStep = step;
  renderWhatsNew(dir);
}

// Cierra y lleva a la función: la novedad se prueba al momento.
function wnTry(action) {
  closeWhatsNew();
  if (action === 'composite') { switchTab('sessions'); openCompositeModal(); }
}

function renderWhatsNew(dir) {
  const id = WN_STEPS[wnStep];
  const body = byId('whatsnew-body');
  body.innerHTML = `<div class="guide-step wn-step wn-step-${id} ${dir > 0 ? 'guide-in-next' : dir < 0 ? 'guide-in-prev' : 'guide-in'}">${WN_RENDER[id]()}</div>`;
  byId('whatsnew-dots').innerHTML = WN_STEPS.map((s, i) =>
    `<button type="button" class="guide-dot${i === wnStep ? ' active' : ''}${i < wnStep ? ' done' : ''}"
       onclick="wnGo(${i})" aria-label="${esc(t('wn.' + s + '.nav'))}" title="${esc(t('wn.' + s + '.nav'))}"></button>`).join('');
  byId('whatsnew-progress').style.setProperty('--p', `${(wnStep / (WN_STEPS.length - 1)) * 100}%`);
  byId('whatsnew-back').disabled = wnStep === 0;
  const last = wnStep === WN_STEPS.length - 1;
  byId('whatsnew-next').textContent = t(last ? 'wn.finish' : 'guide.next');
  byId('whatsnew-next').onclick = last ? closeWhatsNew : () => wnGo(wnStep + 1);
  byId('whatsnew-step-label').textContent = t('guide.stepOf', { n: wnStep + 1, total: WN_STEPS.length });
}

const wnPoints = (prefix, icons) => `<ul class="guide-points wn-points">${icons.map((icon, i) =>
  guidePoint(icon, t(`${prefix}.p${i + 1}t`), t(`${prefix}.p${i + 1}`), i)).join('')}</ul>`;

const wnCta = (action, label, ghost) =>
  `<div class="wn-cta"><button type="button" class="btn ${ghost ? 'btn-ghost' : 'btn-primary'} wn-cta-btn" onclick="wnTry('${action}')">${esc(label)}<span aria-hidden="true">→</span></button></div>`;

const WN_RENDER = {
  intro: () => `
    <div class="wn-hero">
      ${WN_ART.intro}
      <div class="wn-hero-num" aria-hidden="true">2.1</div>
    </div>
    <h2 class="guide-title wn-title">${esc(t('wn.intro.title'))}</h2>
    <p class="guide-lead">${esc(t('wn.intro.lead'))}</p>
    <div class="wn-teasers">
      <button type="button" class="guide-choice wn-teaser" style="--i:0" onclick="wnGo(1)">
        ${uiIcon('screen')}<b>${esc(t('wn.hud.nav'))}</b><span>${esc(t('wn.intro.hud'))}</span></button>
      <button type="button" class="guide-choice wn-teaser" style="--i:1" onclick="wnGo(2)">
        ${uiIcon('grid')}<b>${esc(t('wn.composite.nav'))}</b><span>${esc(t('wn.intro.composite'))}</span></button>
      <button type="button" class="guide-choice wn-teaser" style="--i:2" onclick="wnGo(3)">
        ${uiIcon('gamepad')}<b>${esc(t('wn.more.nav'))}</b><span>${esc(t('wn.intro.more'))}</span></button>
    </div>`,

  hud: () => `
    <div class="guide-hero wn-art-wrap">${WN_ART.hud}</div>
    <span class="wn-kicker">${esc(t('wn.kicker.star'))}</span>
    <h2 class="guide-title wn-title">${esc(t('wn.hud.title'))}</h2>
    <p class="guide-lead">${esc(t('wn.hud.lead'))}</p>
    ${wnPoints('wn.hud', ['pc', 'users', 'restart', 'power'])}
    <p class="guide-tip">${uiIcon('gamepad')}<span>${esc(t('wn.hud.note'))}</span></p>`,

  composite: () => `
    <div class="guide-hero wn-art-wrap">${WN_ART.composite}</div>
    <h2 class="guide-title wn-title">${esc(t('wn.composite.title'))}</h2>
    <p class="guide-lead">${esc(t('wn.composite.lead'))}</p>
    ${wnPoints('wn.composite', ['pc', 'users', 'moon'])}
    ${wnCta('composite', t('wn.composite.cta'))}`,

  more: () => `
    <h2 class="guide-title wn-title">${esc(t('wn.more.title'))}</h2>
    <p class="guide-lead">${esc(t('wn.more.lead'))}</p>
    <div class="wn-grid">
      ${[['gamepad', 'pads'], ['gear', 'type'], ['restart', 'fix']].map(([icon, k], i) => `
        <div class="guide-action wn-mini" style="--i:${i}">${uiIcon(icon)}<b>${esc(t(`wn.more.${k}T`))}</b><span>${esc(t(`wn.more.${k}`))}</span></div>`).join('')}
      <a class="guide-action wn-mini wn-discord" style="--i:3" href="https://discord.gg/uAu9WWqzw" target="_blank" rel="noopener">
        <svg class="ui-icon" viewBox="0 0 24 24" aria-hidden="true"><path fill="currentColor" stroke="none" d="M20.3 4.4A19.6 19.6 0 0 0 15.4 3l-.6 1.3a18.2 18.2 0 0 0-5.6 0L8.6 3a19.5 19.5 0 0 0-4.9 1.5C.6 9.1-.3 13.7.1 18.2a19.7 19.7 0 0 0 6 3l1.3-2.1c-.7-.3-1.4-.6-2-1l.5-.4a14 14 0 0 0 12.2 0l.5.4c-.6.4-1.3.7-2 1l1.3 2.1a19.6 19.6 0 0 0 6-3c.5-5.2-.9-9.8-3.6-13.8zM8.1 15.4c-1.2 0-2.2-1.1-2.2-2.4s1-2.4 2.2-2.4 2.2 1.1 2.2 2.4-1 2.4-2.2 2.4zm7.8 0c-1.2 0-2.2-1.1-2.2-2.4s1-2.4 2.2-2.4 2.2 1.1 2.2 2.4-1 2.4-2.2 2.4z"/></svg>
        <b>${esc(t('wn.more.discordT'))}</b><span>${esc(t('wn.more.discord'))}</span></a>
    </div>`,
};

// Ilustraciones: SVG en línea con los colores del tema y las animaciones ga-* de la guía.
const WN_ART = {
  intro: `<svg class="wn-hero-art" viewBox="0 0 320 170" aria-hidden="true">
      <circle class="ga-ring wn-ring" cx="160" cy="85" r="44"/><circle class="ga-ring wn-ring ga-d1" cx="160" cy="85" r="44"/><circle class="ga-ring wn-ring ga-d2" cx="160" cy="85" r="44"/>
      <circle class="wn-orbit" cx="160" cy="85" r="70"/>
      <g class="wn-orbiter"><circle cx="230" cy="85" r="5"/></g>
      <path class="wn-spark" d="M58 40l3 8 8 3-8 3-3 8-3-8-8-3 8-3z"/>
      <path class="wn-spark wn-s2" d="M262 32l2 6 6 2-6 2-2 6-2-6-6-2 6-2z"/>
      <path class="wn-spark wn-s3" d="M270 128l2.5 7 7 2.5-7 2.5-2.5 7-2.5-7-7-2.5 7-2.5z"/>
      <path class="wn-spark wn-s4" d="M44 120l2 5 5 2-5 2-2 5-2-5-5-2 5-2z"/>
    </svg>`,

  // El HUD sobre el juego: el uso del PC, las sesiones y el mando que lo abre.
  hud: `<svg class="guide-art wn-art" viewBox="0 0 300 170" aria-hidden="true">
      <rect class="ga-dev wn-tv" x="40" y="8" width="220" height="122" rx="9"/>
      <path class="ga-line" d="M130 142h40M150 130v12"/>
      <rect class="wn-q wn-q1" x="48" y="16" width="204" height="106" rx="5"/>
      <rect class="wn-tile wn-t1" x="62" y="28" width="80" height="18" rx="4"/>
      <rect class="wn-tile wn-t2" x="62" y="52" width="80" height="18" rx="4"/>
      <rect class="wn-tile wn-t3" x="62" y="76" width="80" height="18" rx="4"/>
      <rect class="wn-tile wn-t4" x="152" y="28" width="86" height="66" rx="5"/>
      <path class="ga-line" d="M70 37h40M70 61h56M70 85h28"/>
      <path class="ga-flow" d="M150 162 C150 150 150 140 150 132"/>
      <path class="wn-pad" d="M140 150h20a4 4 0 0 1 4 5l-2 8a2.5 2.5 0 0 1-4.5.5l-2-4h-9l-2 4a2.5 2.5 0 0 1-4.5-.5l-2-8a4 4 0 0 1 4-5z"/>
    </svg>`,

  // Varias sesiones que se juntan en un solo stream, en la tele.
  composite: `<svg class="guide-art wn-art" viewBox="0 0 300 160" aria-hidden="true">
      <rect class="ga-win ga-d0" x="10" y="14" width="56" height="34" rx="4"/>
      <rect class="ga-win ga-d1" x="10" y="58" width="56" height="34" rx="4"/>
      <rect class="ga-win ga-d2" x="10" y="102" width="56" height="34" rx="4"/>
      <path class="ga-flow" d="M66 31 C110 31 120 60 156 66"/>
      <path class="ga-flow ga-d1" d="M66 75 C110 75 120 78 156 78"/>
      <path class="ga-flow ga-d2" d="M66 119 C110 119 120 96 156 90"/>
      <rect class="ga-dev wn-tv" x="160" y="22" width="130" height="92" rx="7"/>
      <path class="ga-line" d="M205 132h40M225 114v18"/>
      <rect class="wn-tile wn-t1" x="168" y="30" width="55" height="36" rx="3"/>
      <rect class="wn-tile wn-t2" x="227" y="30" width="55" height="36" rx="3"/>
      <rect class="wn-tile wn-t3" x="168" y="70" width="55" height="36" rx="3"/>
      <rect class="wn-tile wn-t4" x="227" y="70" width="55" height="36" rx="3"/>
    </svg>`,
};

document.addEventListener('keydown', e => {
  if (byId('whatsnew-modal')?.classList.contains('hidden') !== false) return;
  if (e.target.closest('input, select, textarea')) return;
  if (e.key === 'ArrowRight') wnGo(wnStep + 1);
  if (e.key === 'ArrowLeft')  wnGo(wnStep - 1);
});

window.addEventListener('langchange', () => {
  if (!byId('whatsnew-modal').classList.contains('hidden')) renderWhatsNew(0);
});

// Al entrar: la primera vez con esta versión se abre sola (salvo si se pidió la guía por enlace).
// /#whatsnew la abre siempre.
document.addEventListener('DOMContentLoaded', async () => {
  if (!await I18n.ready) return;
  if (location.hash === '#whatsnew') { openWhatsNew(); return; }
  let seen = whatsNewSeen();
  if (!seen && await whatsNewDismissedOnServer()) { markWhatsNewSeen(); seen = true; }
  if (!seen) byId('wn-badge')?.classList.add('wn-badge-new');
  if (seen || /^#guide/.test(location.hash)) return;
  setTimeout(() => {
    if (document.querySelector('.modal-backdrop:not(.hidden)')) return;   // otra ventana abierta: ya se verá desde la píldora
    openWhatsNew();
  }, 700);
});
