// The service worker against a fake `chrome` object: every path must end with the browser download either
// handed over (cancelled and erased after Colibri said ok) or released with exactly one suggest() call.
import assert from 'node:assert/strict';
import { beforeEach, mock, test } from 'node:test';

const listeners = {};
let state;

function resetState() {
  state = {
    storage: {},
    storageThrows: false,
    hostReply: () => ({ ok: true }),
    sent: [],
    cancelled: [],
    cancelThrows: false,
    erased: [],
    cookieUrls: [],
    badge: [],
  };
}

resetState();
globalThis.chrome = {
  runtime: {
    id: 'test-extension',
    lastError: undefined,
    onInstalled: { addListener() {} },
    sendNativeMessage(host, message, callback) {
      state.sent.push(message);
      const reply = state.hostReply(message);
      if (reply === 'never') {
        return;
      }

      queueMicrotask(() => {
        if (reply instanceof Error) {
          chrome.runtime.lastError = { message: reply.message };
          callback(undefined);
          chrome.runtime.lastError = undefined;
        } else {
          callback(reply);
        }
      });
    },
  },
  storage: {
    local: {
      async get(key) {
        if (state.storageThrows) {
          throw new Error('storage broken');
        }

        return { [key]: state.storage[key] };
      },
      async set(values) {
        Object.assign(state.storage, values);
      },
    },
  },
  downloads: {
    onDeterminingFilename: { addListener: (listener) => { listeners.determine = listener; } },
    async cancel(id) {
      if (state.cancelThrows) {
        throw new Error('no such download');
      }

      state.cancelled.push(id);
    },
    async search({ id }) {
      return [{ id, state: 'interrupted' }];
    },
    async erase({ id }) {
      state.erased.push(id);
      return [id];
    },
  },
  contextMenus: {
    onClicked: { addListener: (listener) => { listeners.menu = listener; } },
    removeAll(callback) { callback(); },
    create() {},
  },
  cookies: {
    async getAll({ url }) {
      state.cookieUrls.push(url);
      return [{ name: 'sid', value: '1' }];
    },
  },
  action: {
    setBadgeBackgroundColor() {},
    setBadgeText({ text }) { state.badge.push(text); },
  },
  i18n: { getMessage: (key) => key },
};

await import('../background.js');

const flush = async (rounds = 20) => {
  for (let i = 0; i < rounds; i++) {
    await new Promise((resolve) => setImmediate(resolve));
  }
};

function zipItem(overrides = {}) {
  return {
    id: 7,
    url: 'https://example.com/get?id=1',
    finalUrl: 'https://example.com/files/setup.zip',
    filename: 'setup.zip',
    referrer: 'https://example.com/',
    totalBytes: 1000,
    fileSize: 1000,
    mime: 'application/zip',
    incognito: false,
    ...overrides,
  };
}

/** Fires onDeterminingFilename and returns how often suggest() was called, with which arguments. */
async function determine(item) {
  const suggestions = [];
  const keepsWaiting = listeners.determine(item, (...args) => suggestions.push(args));
  assert.equal(keepsWaiting, true);
  await flush();
  return suggestions;
}

beforeEach(async () => {
  await flush(); // Let the startup rules refresh of the import finish.
  resetState();
});

test('Colibri accepts: the browser download is cancelled, released once and erased', async () => {
  const suggestions = await determine(zipItem());

  assert.deepEqual(suggestions, [[]]);
  assert.deepEqual(state.cancelled, [7]);
  assert.deepEqual(state.erased, [7]);
  const add = state.sent.at(-1);
  assert.equal(add.type, 'add');
  assert.equal(add.url, 'https://example.com/get?id=1');
  assert.equal(add.cookies, 'sid=1');
  assert.deepEqual(state.cookieUrls, ['https://example.com/get?id=1']);
});

for (const [name, setup] of [
  ['Colibri refuses', () => { state.hostReply = () => ({ ok: false, error: 'no' }); }],
  ['the host is missing', () => { state.hostReply = () => new Error('Specified native messaging host not found.'); }],
  ['the host answers nothing usable', () => { state.hostReply = () => undefined; }],
  ['capture is switched off', () => { state.storage.enabled = false; }],
  ['storage fails', () => { state.storageThrows = true; }],
]) {
  test(`${name}: the browser keeps the download`, async () => {
    setup();

    const suggestions = await determine(zipItem());

    assert.deepEqual(suggestions, [[]]);
    assert.deepEqual(state.cancelled, []);
    assert.deepEqual(state.erased, []);
  });
}

test('downloads outside the rules are released without asking Colibri', async () => {
  for (const item of [zipItem({ filename: 'photo.png' }), zipItem({ url: 'blob:https://example.com/1' }), zipItem({ incognito: true })]) {
    state.sent = [];

    assert.deepEqual(await determine(item), [[]]);
    assert.deepEqual(state.sent, []);
  }

  assert.deepEqual(state.cancelled, []);
});

test('cookies are left out after a redirect to another host', async () => {
  await determine(zipItem({ url: 'https://evil.example/x.zip', finalUrl: 'https://bank.example/statement.zip' }));

  assert.equal(state.sent.at(-1).cookies, undefined);
  assert.deepEqual(state.cookieUrls, []);
});

test('a cancel that fails still releases the download exactly once', async () => {
  state.cancelThrows = true;

  assert.deepEqual(await determine(zipItem()), [[]]);
});

test('no answer within 20 s: the browser keeps the download', async (t) => {
  t.mock.timers.enable({ apis: ['setTimeout'] });
  state.hostReply = (message) => (message.type === 'add' ? 'never' : { ok: true });
  const suggestions = [];

  listeners.determine(zipItem(), (...args) => suggestions.push(args));
  await flush();
  assert.deepEqual(suggestions, []); // Still held while the host may answer.

  t.mock.timers.tick(20000);
  await flush();

  assert.deepEqual(suggestions, [[]]);
  assert.deepEqual(state.cancelled, []);
});

test('context menu sends the link; a failure shows the badge', async () => {
  listeners.menu({ menuItemId: 'colibri-download-link', linkUrl: 'https://cdn.other.net/a.zip', pageUrl: 'https://example.com/p?token=1' }, { incognito: false });
  await flush();

  const add = state.sent.at(-1);
  assert.equal(add.url, 'https://cdn.other.net/a.zip');
  assert.equal(add.referrer, 'https://example.com/');
  assert.deepEqual(state.badge, []);

  state.hostReply = () => ({ ok: false });
  mock.timers.enable({ apis: ['setTimeout'] });
  try {
    listeners.menu({ menuItemId: 'colibri-download-link', linkUrl: 'https://example.com/a.zip', pageUrl: 'https://example.com/' }, {});
    await flush();
    assert.deepEqual(state.badge, ['!']);
    mock.timers.tick(5000);
    assert.deepEqual(state.badge, ['!', '']);
  } finally {
    mock.timers.reset();
  }
});
