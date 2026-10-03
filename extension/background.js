// Service worker: hands matching browser downloads and "Download with Colibri" links to Colibri.
//
// Capture works in chrome.downloads.onDeterminingFilename. The browser does not finish (or even name)
// the download until every listener has called suggest(), and by then it knows the server's file name and
// size. So the download is held there while Colibri is asked:
//   - Colibri accepted it  -> the browser download is cancelled and removed from the download list;
//   - anything else (rules do not match, capture switched off, host missing, Colibri not answering,
//     an error, or no answer within 20 s) -> suggest() is called with no name and the browser carries on
//     as if the extension were not there.
// Every path ends in exactly one of the two, so a download is never lost.

import { buildAddMessage, cookieHeader, cookiesAllowed, decideCapture, downloadFileName, knownSize, linkReferrer } from './lib/capture.js';
import { getRules, isEnabled, refreshRules, sendToHost } from './lib/host.js';

const CAPTURE_TIMEOUT_MS = 20000;
const MENU_ID = 'colibri-download-link';
const BADGE_MS = 5000;

// The service worker starts on browser start and again whenever an event wakes it: keep the rules fresh.
refreshRules().catch(() => {});

chrome.runtime.onInstalled.addListener(() => {
  // Menus persist across service worker restarts; they are (re)created once per install or update.
  chrome.contextMenus.removeAll(() => {
    chrome.contextMenus.create({
      id: MENU_ID,
      title: chrome.i18n.getMessage('contextMenuDownload'),
      contexts: ['link'],
      targetUrlPatterns: ['http://*/*', 'https://*/*', 'ftp://*/*'],
    });
  });
});

chrome.downloads.onDeterminingFilename.addListener((item, suggest) => {
  captureOrRelease(item, suggest);
  return true; // suggest() is called later, asynchronously.
});

chrome.contextMenus.onClicked.addListener((info, tab) => {
  if (info.menuItemId === MENU_ID) {
    sendLink(info, tab).catch(() => flagFailure());
  }
});

async function captureOrRelease(item, suggest) {
  let released = false;
  const release = () => {
    if (!released) {
      released = true;
      try {
        suggest(); // No suggestion: the browser keeps its own file name and goes on.
      } catch {
        // The download is gone already.
      }
    }
  };

  try {
    const [enabled, rules] = await Promise.all([isEnabled(), getRules()]);
    if (!decideCapture({ enabled, rules, item }).capture) {
      release();
      return;
    }

    const message = buildAddMessage({
      url: item.url,
      finalUrl: item.finalUrl,
      fileName: downloadFileName(item),
      referrer: item.referrer,
      cookies: cookiesAllowed(item.url, item.finalUrl) ? await cookiesFor(item.url) : '',
      userAgent: navigator.userAgent,
      size: knownSize(item),
      mimeType: item.mime,
    });
    if (!message) {
      release();
      return;
    }

    const response = await sendToHost(message, CAPTURE_TIMEOUT_MS);
    if (response.ok !== true) {
      release();
      return;
    }

    // Colibri has it: drop the browser's copy and its entry in the download list.
    try {
      await chrome.downloads.cancel(item.id);
    } catch {
      // Already gone.
    }

    release();
    await eraseWhenStopped(item.id);
  } catch {
    release();
  }
}

/**
 * Removes a cancelled download from the download list. Edge writes the entry to its history when the
 * cancel has gone through (after suggest()), so erasing earlier lets it come back on the next start.
 */
async function eraseWhenStopped(id) {
  try {
    for (let attempt = 0; attempt < 25; attempt++) {
      const [download] = await chrome.downloads.search({ id });
      if (!download || download.state !== 'in_progress') {
        break;
      }

      await new Promise((resolve) => setTimeout(resolve, 200));
    }

    await chrome.downloads.erase({ id });
  } catch {
    // Already gone.
  }
}

async function sendLink(info, tab) {
  if (tab?.incognito) {
    flagFailure();
    return;
  }

  const message = buildAddMessage({
    url: info.linkUrl,
    referrer: linkReferrer(info.pageUrl, info.linkUrl),
    cookies: await cookiesFor(info.linkUrl),
    userAgent: navigator.userAgent,
  });
  const response = message ? await sendToHost(message, CAPTURE_TIMEOUT_MS) : { ok: false };
  if (response.ok !== true) {
    flagFailure();
  }
}

async function cookiesFor(url) {
  try {
    return cookieHeader(await chrome.cookies.getAll({ url }));
  } catch {
    return '';
  }
}

/** A short "!" on the toolbar button: the link could not be handed to Colibri. */
function flagFailure() {
  chrome.action.setBadgeBackgroundColor({ color: '#d93025' });
  chrome.action.setBadgeText({ text: '!' });
  setTimeout(() => chrome.action.setBadgeText({ text: '' }), BADGE_MS);
}
