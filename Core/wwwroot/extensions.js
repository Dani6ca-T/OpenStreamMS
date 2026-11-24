// ── Extensiones ─────────────────────────────────────────────────────────────
// Se instalan desde GitHub (un enlace o un catálogo). Antes de instalar se descarga el
// paquete y se enseña qué hace, de quién es y su SHA-256; se instala exactamente eso.
// Las que tienen pestaña la abren en un iframe aislado (sandbox, origen opaco).

let extState = { installed: [], operation: null, busy: false };
let extCatalogs = [];
let extTimer = null;
let extPreview = null;      // vista previa en el modal de instalación
let extOpWatch = null;      // sondeo mientras corre una operación

// Categorías que entiende el panel, en el orden del backend (ExtensionFormat.KnownCategories).
const EXT_CATEGORIES = ['remote-desktop', 'streaming', 'display', 'input', 'audio', 'network', 'games', 'monitoring', 'tools'];
let extCategory = '';       // filtro elegido ('' = todas); se recuerda en este navegador
try { extCategory = localStorage.getItem('osms.ext.category') || ''; } catch { /* sin almacenamiento */ }

function extCapLabel(cap) { return t(`ext.cap.${cap}`); }
function extCategoryLabel(cat) { return t(`ext.category.${cat}`); }

// Entrada de catálogo de una instalada (para lo que no trae su manifiesto).
function extCatalogEntryOf(x) {
  for (const c of extCatalogs)
    for (const e of c.extensions || [])
      if (e.installedId === x.id) return e;
  return null;
}

// Categorías e icono de una instalada: los suyos o, si no trae, los que le da su catálogo.
function extInstalledCategories(x) {
  if (x.categories && x.categories.length) return x.categories;
  const e = extCatalogEntryOf(x);
  return e && e.categories ? e.categories : [];
}

function extInstalledIcon(x) {
  return x.iconUrl || (extCatalogEntryOf(x) || {}).iconUrl || null;
}

function extIcon(url, name) {
  const letter = esc((name || '?').trim().charAt(0).toUpperCase() || '?');
  // Si la imagen no carga, se queda la inicial (está debajo).
  return `<div class="ext-icon" aria-hidden="true"><span>${letter}</span>${
    url ? `<img src="${esc(url)}" alt="" loading="lazy" decoding="async" onerror="this.remove()">` : ''}</div>`;
}

function extCategoryChips(cats) {
  return (cats || []).map(c => `<span class="chip chip-cat">${esc(extCategoryLabel(c))}</span>`).join('');
}

function setExtCategory(cat) {
  extCategory = cat;
  try { cat ? localStorage.setItem('osms.ext.category', cat) : localStorage.removeItem('osms.ext.category'); } catch { /* idem */ }
  renderExtensions();
}

// Barra de filtros: "Todas" y cada categoría que tenga alguna extensión (instalada o en catálogos).
function renderExtCategoryBar(installed, available) {
  const counts = new Map();
  const seen = new Set();
  const add = (key, cats) => {
    if (seen.has(key)) return;
    seen.add(key);
    for (const c of cats) counts.set(c, (counts.get(c) || 0) + 1);
  };
  for (const x of installed) add(`i:${x.id}`, extInstalledCategories(x));
  for (const { e } of available) add(e.installedId ? `i:${e.installedId}` : `c:${e.repo}/${e.path || ''}`, e.categories || []);

  const present = EXT_CATEGORIES.filter(c => counts.has(c));
  if (extCategory && !present.includes(extCategory)) extCategory = '';
  const bar = byId('ext-categories');
  if (!present.length) { bar.classList.add('hidden'); renderInto(bar, ''); return; }
  bar.classList.remove('hidden');
  const btn = (cat, label, n) =>
    `<button type="button" class="chip chip-btn${extCategory === cat ? ' active' : ''}" aria-pressed="${extCategory === cat}"
             onclick="setExtCategory('${cat}')">${esc(label)}${n === undefined ? '' : ` <b>${n}</b>`}</button>`;
  renderInto(bar, btn('', t('ext.categoryAll'), seen.size) +
                  present.map(c => btn(c, extCategoryLabel(c), counts.get(c))).join(''));
}

async function loadExtensions() {
  const res = await api('GET', '/api/extensions');
  if (!res || !res.ok) return;
  extState = await res.json();
  renderExtensionTabs();
  if (activeTab === 'extensions') renderExtensions();
}

async function loadExtensionCatalogs(refresh = false) {
  const res = await api('GET', `/api/extensions/catalogs${refresh ? '?refresh=true' : ''}`);
  if (!res || !res.ok) return;
  extCatalogs = await res.json();
  if (activeTab === 'extensions') renderExtensions();
  if (!byId('ext-catalogs-modal').classList.contains('hidden')) renderCatalogList();
}

// ── Vista principal ─────────────────────────────────────────────────────────
function renderExtensions() {
  const all = extState.installed || [];
  byId('ext-count').textContent = all.length;

  const q = (byId('ext-search').value || '').trim().toLowerCase();
  const allRows = [];
  for (const c of extCatalogs) {
    for (const e of c.extensions || []) {
      const cats = (e.categories || []).map(extCategoryLabel).join(' ');
      const text = `${e.name} ${e.description || ''} ${e.repo} ${(e.tags || []).join(' ')} ${cats}`.toLowerCase();
      if (!q || text.includes(q)) allRows.push({ c, e });
    }
  }
  renderExtCategoryBar(all, allRows);

  const inCategory = cats => !extCategory || cats.includes(extCategory);
  const list = all.filter(x => inCategory(extInstalledCategories(x)));
  const rows = allRows.filter(({ e }) => inCategory(e.categories || []));
  const emptyFiltered = `<div class="empty-state"><p>${esc(t('ext.noneInCategory', { category: extCategoryLabel(extCategory) }))}</p></div>`;

  renderInto(byId('ext-installed-grid'), list.length
    ? list.map(extCard).join('')
    : all.length && extCategory ? emptyFiltered
    : `<div class="empty-state"><p>${esc(t('ext.noneInstalled'))}</p></div>`);

  const errors = extCatalogs.filter(c => c.error)
    .map(c => `<div class="card-warning">${esc(c.name)}: ${esc(t('ext.catalogError', { msg: c.error }))}</div>`).join('');
  renderInto(byId('ext-available-grid'), errors + (rows.length
    ? rows.map(({ c, e }) => extCatalogCard(c, e)).join('')
    : allRows.length && extCategory ? emptyFiltered
    : `<div class="empty-state"><p>${esc(t('ext.noneAvailable'))}</p></div>`));
}

function extChips(caps) {
  return (caps || []).map(c => `<span class="chip">${esc(extCapLabel(c))}</span>`).join('');
}

function extCard(x) {
  const status = [];
  if (x.broken) status.push(`<span class="chip chip-error">${esc(t('ext.broken'))}</span>`);
  if (!x.enabled) status.push(`<span class="chip">${esc(t('ext.disabled'))}</span>`);
  if (x.componentInstalled === false) status.push(`<span class="chip chip-error">${esc(t('ext.componentMissing'))}</span>`);
  if (x.rebootPending) status.push(`<span class="chip chip-warn">${esc(t('ext.reboot'))}</span>`);
  const cat = extCatalogs.find(c => c.url === x.catalogUrl);
  return `<div class="session-card ext-card${x.enabled ? '' : ' is-stopped'}" id="ext-${esc(x.id)}">
    <div class="card-header">
      ${extIcon(extInstalledIcon(x), x.name)}
      <div class="card-title-wrap">
        <div class="card-title">${esc(x.name)}</div>
        <div class="card-subtitle">${esc(x.version)} · <a href="${esc(x.repoUrl)}" target="_blank" rel="noopener noreferrer">${esc(x.repo)}</a>${
          x.author ? ` · ${esc(x.author)}` : ''}</div>
      </div>
      <button class="icon-btn" type="button" onclick="extMenu(event,'${esc(x.id)}')" aria-label="${esc(t('nav.more'))}">${uiIcon('more')}</button>
    </div>
    ${x.description ? `<p class="ext-desc">${esc(x.description)}</p>` : ''}
    <div class="card-chips">${extCategoryChips(extInstalledCategories(x))}${extChips(x.capabilities)}${status.join('')}${
      cat ? `<span class="chip" title="${esc(cat.url)}">${esc(t('ext.fromCatalog', { catalog: cat.name }))}</span>` : ''}</div>
  </div>`;
}

function extCatalogCard(c, e) {
  const installed = e.installedId ? `<span class="chip chip-ok">${esc(t('ext.installedBadge'))}</span>` : '';
  const pinned = e.sha256Pinned ? `<span class="chip" title="${esc(t('ext.sha256Pinned'))}">${esc(t('ext.pinned'))}</span>` : '';
  const tags = (e.tags || []).map(tag => `<span class="chip">${esc(tag)}</span>`).join('');
  const args = JSON.stringify({ source: e.repo, path: e.path, catalogUrl: c.url }).replace(/"/g, '&quot;');
  return `<div class="session-card ext-card">
    <div class="card-header">
      ${extIcon(e.iconUrl, e.name)}
      <div class="card-title-wrap">
        <div class="card-title">${esc(e.name)}</div>
        <div class="card-subtitle"><a href="${esc(e.repoUrl)}" target="_blank" rel="noopener noreferrer">${esc(e.repo)}${
          e.path ? '/' + esc(e.path) : ''}</a> · ${esc(c.name)}${c.official ? ` <span class="chip chip-ok">${esc(t('ext.official'))}</span>` : ''}</div>
      </div>
    </div>
    ${e.description ? `<p class="ext-desc">${esc(e.description)}</p>` : ''}
    <div class="card-chips">${extCategoryChips(e.categories)}${installed}${pinned}${tags}</div>
    <div class="card-actions">
      <button class="btn btn-primary btn-sm" type="button" onclick="openExtInstall(${args})">${esc(e.installedId ? t('ext.reinstall') : t('ext.install'))}</button>
    </div>
  </div>`;
}

function extMenu(e, id) {
  const x = (extState.installed || []).find(i => i.id === id);
  if (!x) return;
  const busy = extState.busy;
  openMenu(e, [
    { icon: 'download', label: t('ext.menu.update'), run: () => checkExtUpdate(x), disabled: busy, hint: t('ext.busyHint') },
    { icon: 'power', label: t(x.enabled ? 'ext.menu.disable' : 'ext.menu.enable'), run: () => setExtEnabled(x, !x.enabled) },
    ...(x.componentInstalled !== null && x.componentInstalled !== undefined
      ? [{ icon: 'restart', label: t('ext.menu.detect'), run: () => detectExt(x) }] : []),
    { icon: 'log', label: t('ext.menu.log'), run: () => openExtLog(x) },
    { icon: 'external', label: t('ext.menu.repo'), run: () => window.open(x.repoUrl, '_blank', 'noopener') },
    { sep: true },
    { icon: 'trash', label: t('ext.menu.uninstall'), run: () => uninstallExt(x), danger: true, disabled: busy, hint: t('ext.busyHint') },
  ]);
}

// ── Instalar (enlace o catálogo) ────────────────────────────────────────────
function openExtInstall(req) {
  extPreview = null;
  byId('ext-install-title').textContent = t('ext.installTitle');
  byId('ext-link').value = req && req.source ? req.source + (req.path ? `/${req.path}` : '') : '';
  byId('ext-link-row').classList.toggle('hidden', !!(req && req.catalogUrl));
  byId('ext-install-error').style.display = 'none';
  byId('ext-preview').innerHTML = '';
  byId('ext-op').classList.add('hidden');
  setExtInstallButtons('check');
  show('ext-install-modal');
  if (req && req.source) checkExt(req);
  else byId('ext-link').focus();
}

function closeExtInstall() {
  hide('ext-install-modal');
  extPreview = null;
}

function setExtInstallButtons(mode) {
  byId('ext-check-btn').classList.toggle('hidden', mode !== 'check');
  byId('ext-install-btn').classList.toggle('hidden', mode !== 'install');
  byId('ext-install-btn').disabled = mode !== 'install' || !byId('ext-trust')?.checked;
}

function extInstallError(msg) {
  const el = byId('ext-install-error');
  el.textContent = msg;
  el.style.display = '';
}

async function problemText(res) {
  try { const j = await res.json(); return j.detail || j.title || res.statusText; } catch { return res.statusText; }
}

async function checkExt(req) {
  req = req || { source: byId('ext-link').value.trim() };
  if (!req.source) return;
  byId('ext-install-error').style.display = 'none';
  byId('ext-preview').innerHTML = `<p class="muted">${esc(t('ext.checking'))}</p>`;
  byId('ext-check-btn').disabled = true;
  const res = await api('POST', '/api/extensions/preview', req);
  byId('ext-check-btn').disabled = false;
  if (!res) { byId('ext-preview').innerHTML = ''; return; }
  if (!res.ok) {
    byId('ext-preview').innerHTML = '';
    extInstallError(await problemText(res));
    return;
  }
  showExtPreview(await res.json());
}

function showExtPreview(p, { update = false } = {}) {
  extPreview = p;
  byId('ext-install-title').textContent = t(update ? 'ext.updateTitle' : 'ext.installTitle');
  const runAs = p.hookRunAs ? t(`ext.runAs.${p.hookRunAs}`) : '';
  const caps = (p.capabilities || []).map(c => {
    const desc = c === 'hooks' ? t('ext.cap.hooks_desc', { runAs, list: (p.hooks || []).join(', ') })
              : c === 'apps'  ? t('ext.cap.apps_desc', { list: (p.apps || []).join(', ') })
              : c === 'panel' ? t('ext.cap.panel_desc', { title: p.panelTitle || '' })
              : t(`ext.cap.${c}_desc`);
    return `<li><strong>${esc(extCapLabel(c))}</strong> — ${esc(desc)}</li>`;
  }).join('');
  const row = (label, value) => value ? `<div class="ext-kv"><span>${esc(label)}</span><span>${value}</span></div>` : '';
  const notices = [
    p.notice ? `<div class="card-warning"><strong>${esc(t('ext.notice'))}</strong><br>${esc(p.notice)}</div>` : '',
    p.catalogNotice ? `<div class="card-warning"><strong>${esc(t('ext.catalogNotice'))}</strong><br>${esc(p.catalogNotice)}</div>` : '',
    p.replacesOtherSource ? `<div class="card-warning">${esc(t('ext.replaces', { version: p.installedVersion || '' }))}</div>` : '',
    !p.replacesOtherSource && p.installedVersion ? `<p class="muted">${esc(t('ext.updateFrom', { version: p.installedVersion }))}</p>` : '',
    !p.compatible ? `<div class="auth-alert error">${esc(t('ext.incompatible', { min: p.minOsmsVersion || '?' }))}</div>` : '',
  ].join('');
  const author = p.author || t('ext.unknownAuthor');

  byId('ext-preview').innerHTML = `
    <div class="ext-preview-head">
      <h4>${esc(p.name)} <span class="muted">${esc(p.id)}</span></h4>
      ${p.description ? `<p>${esc(p.description)}</p>` : ''}
    </div>
    <div class="ext-kvs">
      ${row(t('ext.author'), esc(author))}
      ${row(t('ext.repo'), `<a href="${esc(p.repoUrl)}" target="_blank" rel="noopener noreferrer">${esc(p.repo)}${p.path ? '/' + esc(p.path) : ''}</a>`)}
      ${row(t('ext.version'), `${esc(p.version)} <span class="muted">(${esc(t(p.isRelease ? 'ext.release' : 'ext.commitOnly'))})</span>`)}
      ${row(t('ext.commit'), `<code>${esc(p.commit)}</code>`)}
      ${row(t('ext.sha256'), `<code class="ext-hash">${esc(p.sha256)}</code><br><span class="muted">${esc(t(p.sha256Pinned ? 'ext.sha256Pinned' : 'ext.sha256Unpinned'))}</span>`)}
      ${row(t('ext.license'), esc(p.license || ''))}
      ${(p.categories || []).length ? row(t('ext.categories'), esc(p.categories.map(extCategoryLabel).join(', '))) : ''}
      ${p.homepage ? row(t('ext.homepage'), `<a href="${esc(p.homepage)}" target="_blank" rel="noopener noreferrer">${esc(p.homepage)}</a>`) : ''}
    </div>
    <ul class="ext-caps">${caps}</ul>
    ${notices}
    <div class="auth-alert error ext-system-warning">${esc(t('ext.systemWarning'))}</div>
    <label class="ext-trust"><input type="checkbox" id="ext-trust" onchange="setExtInstallButtons('install')"> ${esc(t('ext.trust', { author }))}</label>`;
  byId('ext-install-btn').textContent = plainLabel(t(update || p.installedVersion ? 'ext.update' : 'ext.install'));
  setExtInstallButtons(p.compatible ? 'install' : 'none');
}

async function installExt() {
  if (!extPreview || !byId('ext-trust').checked) return;
  const p = extPreview;
  let replace = false;
  if (p.replacesOtherSource) {
    replace = await ask(t('ext.replaces', { version: p.installedVersion || '' }), { danger: true, ok: t('ext.install') });
    if (!replace) return;
  }
  const res = await api('POST', '/api/extensions/install', { token: p.token, sha256: p.sha256, replace });
  if (!res) return;
  if (!res.ok) { extInstallError(await problemText(res)); return; }
  setExtInstallButtons('none');
  watchExtOperation(true);
}

// Sigue la operación en curso: log en el modal (si está abierto) y aviso al terminar.
function watchExtOperation(inModal) {
  clearInterval(extOpWatch);
  if (inModal) byId('ext-op').classList.remove('hidden');
  const tick = async () => {
    await loadExtensions();
    if (inModal) {
      const res = await api('GET', '/api/extensions/operation/log');
      if (res && res.ok) {
        const pre = byId('ext-op-log');
        pre.textContent = (await res.json()).lines.join('\n');
        pre.scrollTop = pre.scrollHeight;
      }
    }
    const op = extState.operation;
    byId('ext-op-state').textContent = op ? t(`ext.kind.${op.kind}`, { name: op.name }) : '';
    if (extState.busy) return;
    clearInterval(extOpWatch);
    extOpWatch = null;
    if (!op) return;
    if (op.state === 'ok') {
      const done = op.kind === 'uninstall' ? 'ext.uninstalledOk' : 'ext.installedOk';
      toast([t(done, { name: op.name }), op.rebootRequired ? t('ext.rebootRequired') : '',
             op.kind !== 'uninstall' ? t('ext.restartSessions') : ''].filter(Boolean).join(' '));
      if (inModal) closeExtInstall();
      loadExtensionCatalogs();
    } else if (op.state === 'error') {
      if (inModal) extInstallError(t('ext.opError', { msg: op.message || '' }));
      else toast(t('ext.opError', { msg: op.message || '' }), true);
    }
  };
  extOpWatch = setInterval(tick, 1000);
  tick();
}

// ── Acciones sobre una instalada ────────────────────────────────────────────
async function checkExtUpdate(x) {
  toast(t('ext.checking'));
  const res = await api('GET', `/api/extensions/${encodeURIComponent(x.id)}/update`);
  if (!res) return;
  if (!res.ok) { toast(await problemText(res), true); return; }
  const u = await res.json();
  if (u.upToDate) { toast(t('ext.upToDate', { name: x.name, version: u.currentVersion })); return; }
  openExtInstall(null);
  byId('ext-link-row').classList.add('hidden');
  showExtPreview(u.preview, { update: true });
}

async function setExtEnabled(x, enabled) {
  const res = await api('POST', `/api/extensions/${encodeURIComponent(x.id)}/enabled`, { enabled });
  if (res && !res.ok) toast(await problemText(res), true);
  else if (res) toast(t('ext.restartSessions'));
  loadExtensions();
}

async function detectExt(x) {
  const res = await api('POST', `/api/extensions/${encodeURIComponent(x.id)}/detect`);
  if (res && !res.ok) toast(await problemText(res), true);
  loadExtensions();
}

async function uninstallExt(x) {
  const extra = x.componentInstalled !== null && x.componentInstalled !== undefined ? t('ext.confirmUninstallComponent') : '';
  if (!await ask(t('ext.confirmUninstall', { name: x.name, extra }), { danger: true, ok: t('ext.menu.uninstall') })) return;
  const res = await api('POST', `/api/extensions/${encodeURIComponent(x.id)}/uninstall`);
  if (!res) return;
  if (!res.ok) { toast(await problemText(res), true); return; }
  if (activeTab === `ext:${x.id}`) switchTab('extensions');
  watchExtOperation(false);
}

async function openExtLog(x) {
  byId('ext-log-title').textContent = t('ext.logTitle', { name: x.name });
  byId('ext-log-body').textContent = t('empty.loading');
  show('ext-log-modal');
  const res = await api('GET', `/api/extensions/${encodeURIComponent(x.id)}/log`);
  if (!res || !res.ok) return;
  const lines = (await res.json()).lines;
  byId('ext-log-body').textContent = lines.length ? lines.join('\n') : t('ext.noLog');
}

// ── Catálogos ───────────────────────────────────────────────────────────────
function openCatalogs() {
  byId('ext-catalog-error').style.display = 'none';
  byId('ext-catalog-url').value = '';
  renderCatalogList();
  show('ext-catalogs-modal');
}

function renderCatalogList() {
  byId('ext-catalog-list').innerHTML = extCatalogs.length ? extCatalogs.map(c => `
    <div class="ext-catalog-row">
      <div>
        <strong>${esc(c.name)}</strong>${c.official ? ` <span class="chip chip-ok">${esc(t('ext.official'))}</span>` : ''}
        <div class="muted"><a href="${esc(c.url)}" target="_blank" rel="noopener noreferrer">${esc(c.url)}</a></div>
        ${c.error ? `<div class="muted ext-error">${esc(t('ext.catalogError', { msg: c.error }))}</div>`
                  : `<div class="muted">${esc(c.description || '')}</div>`}
      </div>
      <button class="btn btn-ghost btn-sm" type="button" data-url="${esc(c.url)}" data-name="${esc(c.name)}"
              onclick="removeCatalog(this.dataset.url, this.dataset.name)">${esc(t('ext.catalogRemove'))}</button>
    </div>`).join('') : `<p class="muted">${esc(t('ext.noCatalogs'))}</p>`;
}

async function addCatalog(ev) {
  ev.preventDefault();
  const url = byId('ext-catalog-url').value.trim();
  if (!url) return;
  const btn = byId('ext-catalog-add');
  btn.disabled = true;
  const res = await api('POST', '/api/extensions/catalogs', { url });
  btn.disabled = false;
  if (!res) return;
  if (!res.ok) {
    const el = byId('ext-catalog-error');
    el.textContent = await problemText(res);
    el.style.display = '';
    return;
  }
  byId('ext-catalog-url').value = '';
  toast(t('ext.catalogAdded'));
  await loadExtensionCatalogs();
}

async function removeCatalog(url, name) {
  if (!await ask(t('ext.confirmRemoveCatalog', { name }), { danger: true, ok: t('ext.catalogRemove') })) return;
  const res = await api('DELETE', `/api/extensions/catalogs?url=${encodeURIComponent(url)}`);
  if (res && !res.ok) { toast(await problemText(res), true); return; }
  await loadExtensionCatalogs();
}

// ── Pestañas propias de las extensiones ─────────────────────────────────────
function renderExtensionTabs() {
  const bar = document.querySelector('.tabs');
  const wanted = (extState.installed || []).filter(x => x.panelUrl);
  bar.querySelectorAll('.tab[data-ext]').forEach(b => {
    if (!wanted.some(x => x.id === b.dataset.ext)) b.remove();
  });
  for (const x of wanted) {
    let b = bar.querySelector(`.tab[data-ext="${CSS.escape(x.id)}"]`);
    if (!b) {
      b = document.createElement('button');
      b.className = 'tab';
      b.type = 'button';
      b.dataset.ext = x.id;
      b.onclick = () => switchTab(`ext:${x.id}`);
      bar.appendChild(b);
    }
    b.textContent = x.panelTitle;
    b.title = x.name;
    b.classList.toggle('active', activeTab === `ext:${x.id}`);
  }
  if (activeTab.startsWith('ext:') && !wanted.some(x => `ext:${x.id}` === activeTab)) switchTab('extensions');
  moveTabIndicator();
}

function showExtensionPanel(id) {
  const x = (extState.installed || []).find(i => i.id === id);
  const frame = byId('ext-frame');
  if (!x || !x.panelUrl) { frame.removeAttribute('src'); return; }
  if (frame.dataset.ext !== id) {
    frame.dataset.ext = id;
    frame.src = x.panelUrl;
  }
}

// Canal de solo lectura para las pestañas: el iframe (origen opaco) no ve la API.
// Pide { osms: 'context' } y recibe idioma, tema, sesiones y puestos (sin credenciales).
window.addEventListener('message', ev => {
  const frame = byId('ext-frame');
  if (!frame || ev.source !== frame.contentWindow || !ev.data || ev.data.osms !== 'context') return;
  frame.contentWindow.postMessage({
    osms: 'context',
    extension: frame.dataset.ext,
    lang: window.I18n ? I18n.lang : 'en',
    theme: document.documentElement.dataset.theme || '',
    sessions: (sessions || []).map(s => ({ id: s.id, name: s.name, state: s.state, local: !!s.local })),
    seats: (typeof seats !== 'undefined' ? seats : []).map(s => ({ id: s.id, name: s.name, state: s.state })),
  }, '*');
});

// ── Arranque ────────────────────────────────────────────────────────────────
function enterExtensionsTab() {
  renderExtensions();
  loadExtensions();
  loadExtensionCatalogs();
  if (!extTimer) extTimer = setInterval(whenVisible(loadExtensions), 5000);
}

function leaveExtensionsTab() {
  clearInterval(extTimer);
  extTimer = null;
}

document.addEventListener('DOMContentLoaded', async () => {
  if (!await I18n.ready) return;
  await loadExtensions();
  if (location.hash === '#extensions') switchTab('extensions');
  else if (location.hash.startsWith('#ext:')) switchTab(location.hash.slice(1));
});
window.addEventListener('langchange', () => { if (activeTab === 'extensions') renderExtensions(); });
