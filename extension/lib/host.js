// Talking to Colibri's native-messaging host, and the settings kept in chrome.storage.local.
// Shared by the service worker and the popup.

import { rulesFromConfig, storedRulesOrDefault } from './capture.js';

export const HOST_NAME = 'com.colibri.host';

/**
 * Sends one message to the native host (the browser starts a host process for it) and resolves with its
 * answer. Never rejects: failures resolve to { ok: false, error, nativeError? }, and no answer within
 * timeoutMs resolves to { ok: false, error: 'timeout' }.
 */
export function sendToHost(message, timeoutMs) {
  return new Promise((resolve) => {
    let settled = false;
    const finish = (response) => {
      if (!settled) {
        settled = true;
        clearTimeout(timer);
        resolve(response);
      }
    };
    const timer = setTimeout(() => finish({ ok: false, error: 'timeout' }), timeoutMs);

    try {
      chrome.runtime.sendNativeMessage(HOST_NAME, message, (response) => {
        // Reading lastError also keeps the browser from reporting it as unchecked.
        const lastError = chrome.runtime.lastError;
        if (lastError) {
          finish({ ok: false, error: 'native', nativeError: lastError.message ?? '' });
        } else if (response && typeof response === 'object') {
          finish(response);
        } else {
          finish({ ok: false, error: 'empty' });
        }
      });
    } catch (error) {
      finish({ ok: false, error: 'native', nativeError: String(error) });
    }
  });
}

/** Whether capture is switched on (the popup's toggle); on by default. */
export async function isEnabled() {
  const { enabled } = await chrome.storage.local.get('enabled');
  return enabled !== false;
}

export function setEnabled(enabled) {
  return chrome.storage.local.set({ enabled: Boolean(enabled) });
}

/** The cached capture rules, or Colibri's defaults when none were fetched yet. */
export async function getRules() {
  const { rules } = await chrome.storage.local.get('rules');
  return storedRulesOrDefault(rules);
}

/**
 * Asks Colibri for its current rules and caches them. Only while Colibri runs (checked with "ping"):
 * a "config" message would start Colibri, and the service worker starts often.
 * Resolves with the ping answer, which the popup shows.
 */
export async function refreshRules() {
  const ping = await sendToHost({ type: 'ping' }, 5000);
  if (ping.ok === true) {
    const rules = rulesFromConfig(await sendToHost({ type: 'config' }, 20000));
    if (rules) {
      await chrome.storage.local.set({ rules });
    }
  }

  return ping;
}
