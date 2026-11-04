'use strict';
let maintenanceStatus = null;
const maintenanceMutations = /(?:startSession|stopSession|retrySession|setEnabled|openNewModal|submitSession|saveSession|deleteSession|deleteOrphan|openSeatModal|submitSeat|saveSeat|deleteSeat|seatAction|setSeatAutoStart|openMapZone|pickSeatArea|driverInstall|driverUninstall|rdpInstall|rdpUpdate|rdpUninstall|powerAction|openSunMgrModal|sunmgrPair|sunmgrUnpair|sunmgrUnpairAll|testCredentials|testSeatMonitor|testSeatAudio|toggleMicMeter)\s*\(/;
function maintenanceRequestAllowed(method,path) {
  if (path === '/api/settings/operation-mode') return true;
  if (path === '/api/settings/logging' && method === 'PUT') return true;
  return ['GET','HEAD','OPTIONS'].includes(method) && !/\/identify\/|\/sunshine|\/clients$|\/pairings$/.test(path);
}
function maintenanceLocked() { return !maintenanceStatus || maintenanceStatus.activeMode !== 'Normal'; }
function applyMaintenanceControls() {
  const locked = maintenanceLocked();
  document.querySelectorAll('[onclick],[onchange],[onsubmit]').forEach(el => {
    if (!maintenanceMutations.test([el.getAttribute('onclick'),el.getAttribute('onchange'),el.getAttribute('onsubmit')].join(' '))) return;
    if (locked) {
      if (!el.hasAttribute('data-maintenance-disabled')) el.dataset.maintenanceDisabled = el.disabled ? 'was-disabled' : 'enabled';
      el.disabled = true; el.setAttribute('aria-disabled','true'); el.title = t('maintenance.blocked');
    } else if (el.hasAttribute('data-maintenance-disabled')) {
      el.disabled = el.dataset.maintenanceDisabled === 'was-disabled';
      delete el.dataset.maintenanceDisabled; el.removeAttribute('aria-disabled'); el.removeAttribute('title');
    }
  });
}
function renderMaintenance() {
  const state = maintenanceStatus;
  const banner = byId('maintenance-banner');
  banner.hidden = !!state && state.activeMode === 'Normal' && !state.restartRequired;
  banner.textContent = !state ? t('maintenance.unavailable') : state.restartRequired ? t('maintenance.restart') : t('maintenance.blocked');
  byId('maintenance-current').textContent = state ? t(state.activeMode === 'Safe' ? 'maintenance.safe' : 'maintenance.normal') : t('maintenance.unavailable');
  byId('maintenance-restart').hidden = !state?.restartRequired;
  byId('maintenance-recovery').hidden = state?.activeMode !== 'Safe' || !state?.residualState?.needsAttention;
  byId('maintenance-save').disabled = !state;
  applyMaintenanceControls();
}
async function refreshMaintenance() {
  const response = await api('GET','/api/settings/operation-mode');
  try {
    const state = response?.ok ? await response.json() : null;
    maintenanceStatus = state && ['Safe','Normal'].includes(state.activeMode) && ['Safe','Normal'].includes(state.nextMode) ? state : null;
  } catch { maintenanceStatus = null; }
  if (maintenanceStatus) byId('maintenance-mode').value = maintenanceStatus.nextMode;
  renderMaintenance();
}
async function saveOperationMode() {
  if (!maintenanceStatus) return;
  const mode = byId('maintenance-mode').value;
  const confirmNormal = mode === 'Normal' && await ask(t('maintenance.confirm'), {danger:true,ok:t('maintenance.normal')});
  if (mode === 'Normal' && !confirmNormal) return;
  byId('maintenance-save').disabled = true;
  try {
    const response = await api('PUT','/api/settings/operation-mode',{operationMode:mode,revision:maintenanceStatus.revision,confirmNormal});
    if (!response?.ok) { toast(t('maintenance.unavailable'),true); return; }
    maintenanceStatus = await response.json(); renderMaintenance(); toast(t('maintenance.restart'));
  } finally { byId('maintenance-save').disabled = !maintenanceStatus; }
}
document.addEventListener('DOMContentLoaded',async()=>{
  if (!await I18n.ready) return;
  await refreshMaintenance();
  const observer = new MutationObserver(()=>applyMaintenanceControls());
  observer.observe(byId('app'),{childList:true,subtree:true});
});
window.addEventListener('langchange',renderMaintenance);
