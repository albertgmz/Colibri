// Every text the user sees comes from _locales/en/messages.json, and every key used exists there.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readdirSync, readFileSync } from 'node:fs';

const read = (path) => readFileSync(new URL(path, import.meta.url), 'utf8');
const messages = JSON.parse(read('../_locales/en/messages.json'));
const manifest = JSON.parse(read('../manifest.json'));

test('the manifest takes its name, description and button title from the locale', () => {
  for (const text of [manifest.name, manifest.description, manifest.action.default_title]) {
    const key = /^__MSG_(\w+)__$/.exec(text)?.[1];
    assert.ok(key, `"${text}" is not a locale message`);
    assert.ok(messages[key], `missing message ${key}`);
  }
});

test('every message key used by the extension exists', () => {
  // Every script and page of the extension, tests excepted.
  const files = readdirSync(new URL('..', import.meta.url), { recursive: true })
    .map((f) => f.replaceAll('\\', '/'))
    .filter((f) => /\.(js|html)$/.test(f) && !f.startsWith('tests/'));
  const sources = files.map((f) => read(`../${f}`)).join('\n');
  const used = [
    ...[...sources.matchAll(/data-i18n="(\w+)"/g)].map((m) => m[1]),
    ...[...sources.matchAll(/getMessage\(['"](\w+)['"]\)/g)].map((m) => m[1]),
    // Keys kept in a table and passed to getMessage later, such as the popup's status messages.
    ...[...sources.matchAll(/:\s*['"]((?:popup|contextMenu|ext)\w+)['"]/g)].map((m) => m[1]),
  ];
  assert.ok(files.includes('popup/popup.js') && files.includes('background.js'));
  assert.ok(used.includes('popupStatusConnected') && used.includes('contextMenuDownload'));
  for (const key of used) {
    assert.ok(messages[key], `missing message ${key}`);
  }
});
