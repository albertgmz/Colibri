import { connectionStatus } from '../lib/capture.js';
import { isEnabled, refreshRules, setEnabled } from '../lib/host.js';

const STATUS_MESSAGES = {
  connected: 'popupStatusConnected',
  notRunning: 'popupStatusNotRunning',
  hostMissing: 'popupStatusHostMissing',
  error: 'popupStatusError',
};

for (const element of document.querySelectorAll('[data-i18n]')) {
  element.textContent = chrome.i18n.getMessage(element.dataset.i18n);
}

const toggle = document.getElementById('enabled');
toggle.checked = await isEnabled();
toggle.addEventListener('change', () => setEnabled(toggle.checked));

// Checks the connection and, when Colibri runs, fetches its current capture rules.
const state = connectionStatus(await refreshRules());
document.getElementById('status').dataset.state = state;
document.getElementById('status-text').textContent = chrome.i18n.getMessage(STATUS_MESSAGES[state]);
