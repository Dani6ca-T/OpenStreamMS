'use strict';
// Sesiones compuestas: varias sesiones RDP en un mismo stream, cada una en su hueco (ver
// StreamSessionService.Composite.cs). Aquí, el editor en rejilla y lo que la compuesta y sus
// miembros enseñan en las tarjetas.

// Plantillas: huecos en orden (el mando N va al hueco N). Las mismas que MosaicLayout.Templates.
const COMP_TEMPLATES = {
  sideBySide: ['Left', 'Right'],
  stacked:    ['Top', 'Bottom'],
  quad:       ['TopLeft', 'TopRight', 'BottomLeft', 'BottomRight'],
  bigLeft:    ['Left', 'TopRight', 'BottomRight'],
  bigTop:     ['Top', 'BottomLeft', 'BottomRight'],
};
// Posición de cada parte en una rejilla de 2×2 (grid-area: fila/columna de inicio / fin).
const COMP_AREA_GRID = {
  Full: '1/1/3/3', Left: '1/1/3/2', Right: '1/2/3/3', Top: '1/1/2/3', Bottom: '2/1/3/3',
  TopLeft: '1/1/2/2', TopRight: '1/2/2/3', BottomLeft: '2/1/3/2', BottomRight: '2/2/3/3',
};
const COMP_RESOLUTIONS = [[1280, 720], [1920, 1080], [2560, 1440], [3840, 2160]];

let compEditingId = null;   // null = compuesta nueva
let compTemplate = 'quad';
let compSlots = [];         // id de sesión por hueco ('' = vacío)

// Sesiones que pueden ir en un hueco: RDP (no la local) y que no sean compuestas.
function compEligible() {
  return sessions.filter(x => !x.local && !x.composite);
}

function compTemplateOf(slots) {
  const areas = (slots || []).map(s => s.area);
  for (const [id, tpl] of Object.entries(COMP_TEMPLATES)) {
    if (areas.every(a => tpl.includes(a))) return id;
  }
  return 'quad';
}

function openCompositeModal(id) {
  const c = id ? sessions.find(x => x.id === id) : null;
  if (c && (c.state === 'Running' || c.state === 'Starting' || c.state === 'Stopping')) {
    toast(t('comp.stopToEdit'), true);
    return;
  }
  if (!c && compEligible().length < 2) {
    toast(t('comp.noEligible'), true);
    return;
  }
  compEditingId = c ? c.id : null;
  compTemplate = c ? compTemplateOf(c.composite) : 'quad';
  const areas = COMP_TEMPLATES[compTemplate];
  compSlots = areas.map(a => (c?.composite || []).find(s => s.area === a)?.session || '');
  if (!c) {
    // Propuesta: las primeras sesiones elegibles, una por hueco.
    compEligible().slice(0, areas.length).forEach((s, i) => { compSlots[i] = s.id; });
  }

  byId('comp-error').style.display = 'none';
  byId('comp-title').textContent = c ? t('comp.title_edit', { name: c.name }) : t('comp.title_new');
  byId('comp-submit-label').textContent = c ? t('comp.save') : t('comp.create');
  byId('comp-name').value = c ? c.name : '';
  byId('comp-res').value = c ? `${c.rdpWidth}x${c.rdpHeight}` : '1920x1080';
  if (!byId('comp-res').value) byId('comp-res').value = '1920x1080';
  byId('comp-fps').value = c ? (c.rdpFrameRate || 60) : 60;
  byId('comp-enabled').checked = !!c?.enabled;
  renderCompositeEditor(c?.compositeInput || '');
  show('comp-modal');
  stopRefresh();
  byId('comp-name').focus();
}

function closeCompositeModal() {
  hide('comp-modal');
  compEditingId = null;
  startRefresh();
}

function selectCompTemplate(id) {
  const before = compSlots.filter(Boolean);
  compTemplate = id;
  compSlots = COMP_TEMPLATES[id].map((_, i) => before[i] || '');
  renderCompositeEditor(byId('comp-input').value);
}

function setCompSlot(i, sessionId) {
  // Una sesión solo puede ir en un hueco: si ya estaba en otro, se intercambian.
  const other = compSlots.indexOf(sessionId);
  if (sessionId && other >= 0 && other !== i) compSlots[other] = compSlots[i];
  compSlots[i] = sessionId;
  renderCompositeEditor(byId('comp-input').value);
}

function renderCompositeEditor(input) {
  const eligible = compEligible();
  byId('comp-templates').innerHTML = Object.entries(COMP_TEMPLATES).map(([id, areas]) => `
    <button type="button" class="comp-tpl${id === compTemplate ? ' active' : ''}" onclick="selectCompTemplate('${id}')"
            aria-pressed="${id === compTemplate}" title="${esc(t('comp.tpl.' + id))}" aria-label="${esc(t('comp.tpl.' + id))}">
      <span class="comp-tpl-map">${areas.map(a => `<i style="grid-area:${COMP_AREA_GRID[a]}"></i>`).join('')}</span>
    </button>`).join('');

  const areas = COMP_TEMPLATES[compTemplate];
  byId('comp-grid').innerHTML = areas.map((a, i) => {
    const options = [`<option value="">${esc(t('comp.slotEmpty'))}</option>`]
      .concat(eligible.map(s => `<option value="${s.id}" ${compSlots[i] === s.id ? 'selected' : ''}>${esc(s.name)}</option>`));
    return `<div class="comp-slot${compSlots[i] ? '' : ' empty'}" style="grid-area:${COMP_AREA_GRID[a]}">
        <span class="comp-slot-n">${i + 1}</span>
        <select aria-label="${esc(t('comp.slot', { n: i + 1 }))}" onchange="setCompSlot(${i}, this.value)">${options.join('')}</select>
        <span class="comp-slot-pad">${uiIcon('gamepad')}${esc(t('comp.pad', { n: i + 1 }))}</span>
      </div>`;
  }).join('');

  const chosen = compSlots.filter(Boolean);
  const sel = byId('comp-input');
  sel.innerHTML = chosen.map(id => {
    const s = sessions.find(x => x.id === id);
    return `<option value="${id}">${esc(s ? s.name : id)}</option>`;
  }).join('');
  sel.value = chosen.includes(input) ? input : (chosen[0] || '');
  sel.disabled = chosen.length === 0;
}

async function submitComposite(e) {
  e?.preventDefault();
  const fail = msg => { const box = byId('comp-error'); box.textContent = msg; box.style.display = ''; };
  const name = byId('comp-name').value.trim();
  if (!name) { fail(t('comp.needName')); byId('comp-name').focus(); return; }
  const areas = COMP_TEMPLATES[compTemplate];
  const slots = areas.map((area, i) => ({ session: compSlots[i], area })).filter(s => s.session);
  if (slots.length < 2) { fail(t('comp.needTwo')); return; }
  const [width, height] = byId('comp-res').value.split('x').map(Number);
  const editing = compEditingId;
  const current = editing && sessions.find(x => x.id === editing);
  const body = {
    name, slots,
    input: byId('comp-input').value || null,
    width, height,
    frameRate: parseInt(byId('comp-fps').value, 10) || 60,
    enabled: byId('comp-enabled').checked,
    sunshineStreamPort: current ? current.sunshineStreamPort : 0,
    streamProtocol: current ? current.streamProtocol : 'Both',
  };
  byId('comp-submit').disabled = true;
  try {
    const res = await api(editing ? 'PUT' : 'POST', editing ? `/api/composites/${editing}` : '/api/composites', body);
    if (!res) return;
    if (!res.ok) { fail(await I18n.responseError(res)); return; }
    toast(editing ? t('comp.saved') : t('comp.created', { name }));
    closeCompositeModal();
    loadSessions();
  } finally {
    byId('comp-submit').disabled = false;
  }
}

// ── Tarjetas ────────────────────────────────────────────────────────────────

// Miniatura de una compuesta: la rejilla con la inicial de la sesión de cada hueco.
function compositeThumb(c) {
  const cells = (c.composite || []).map((slot, i) => {
    const m = sessions.find(x => x.id === slot.session);
    const running = m && m.state === 'Running';
    return `<i class="${running ? 'on' : ''}" style="grid-area:${COMP_AREA_GRID[slot.area] || ''}" title="${esc(`${i + 1} · ${m ? m.name : '?'}`)}">${esc((m ? m.name : '?').slice(0, 1).toUpperCase())}</i>`;
  }).join('');
  return `<div class="screen-thumb screen-composite"><span class="comp-thumb-map">${cells}</span></div>`;
}

// Línea bajo el nombre: qué sesiones junta y quién pone el teclado.
function compositeCardUser(c) {
  const names = (c.composite || []).map((slot, i) => {
    const m = sessions.find(x => x.id === slot.session);
    return `${i + 1} ${m ? m.name : '?'}`;
  });
  const input = sessions.find(x => x.id === c.compositeInput);
  const title = input ? t('comp.card_title', { list: names.join(' · '), input: input.name }) : names.join(' · ');
  return `<span title="${esc(title)}">${esc(t('comp.card', { n: names.length }))}</span>`;
}

// En un miembro de una compuesta en marcha: en qué hueco se ve y que su stream está en pausa.
function compositeMemberChip(s) {
  if (!s.compositeOf) return '';
  const c = sessions.find(x => x.id === s.compositeOf);
  if (!c) return '';
  const i = (c.composite || []).findIndex(slot => slot.session === s.id);
  const slot = c.composite[i];
  const map = (c.composite || []).map(x =>
    `<i class="${x.session === s.id ? 'own' : 'used'}" style="grid-area:${COMP_AREA_GRID[x.area] || ''}"></i>`).join('');
  return `<span class="chip chip-mosaic active" title="${esc(t('comp.memberChip_title', { n: i + 1, name: c.name }))}">` +
         `<span class="mosaic-map comp-map" aria-hidden="true">${slot ? map : ''}</span>${esc(t('comp.memberChip', { name: c.name }))}</span>`;
}
