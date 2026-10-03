import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { test } from 'node:test';
import {
  baseName,
  buildAddMessage,
  connectionStatus,
  cookieHeader,
  cookiesAllowed,
  decideCapture,
  DEFAULT_RULES,
  downloadFileName,
  extensionOf,
  fileNameFromUrl,
  isCapturableUrl,
  knownSize,
  LIMITS,
  linkReferrer,
  matchesRules,
  rulesFromConfig,
  storedRulesOrDefault,
} from '../lib/capture.js';

const rules = { extensions: ['zip', 'exe', 'iso'], minSizeKiB: 100 };

function item(overrides = {}) {
  return {
    id: 1,
    url: 'https://example.com/files/setup.exe',
    finalUrl: 'https://cdn.example.com/files/setup.exe',
    filename: 'setup.exe',
    referrer: 'https://example.com/download',
    totalBytes: 5_000_000,
    fileSize: 5_000_000,
    mime: 'application/octet-stream',
    incognito: false,
    ...overrides,
  };
}

test('only http, https and ftp URLs with a host are captured', () => {
  assert.ok(isCapturableUrl('https://example.com/a.zip'));
  assert.ok(isCapturableUrl('http://example.com/a.zip'));
  assert.ok(isCapturableUrl('ftp://example.com/a.zip'));
  for (const url of ['blob:https://example.com/7e3c', 'data:application/zip;base64,AAAA', 'javascript:alert(1)',
    'file:///C:/a.zip', 'filesystem:https://example.com/temporary/a.zip', 'chrome://downloads', '', 'not a url', null]) {
    assert.equal(isCapturableUrl(url), false, String(url));
  }

  assert.equal(isCapturableUrl('https://example.com/' + 'a'.repeat(LIMITS.url)), false);
});

test('file names and extensions', () => {
  assert.equal(baseName('C:\\Users\\ana\\Downloads\\file.zip'), 'file.zip');
  assert.equal(baseName('/home/ana/file.tar.gz'), 'file.tar.gz');
  assert.equal(baseName('file.zip'), 'file.zip');
  assert.equal(extensionOf('Setup.EXE'), 'exe');
  assert.equal(extensionOf('archive.tar.gz'), 'gz');
  assert.equal(extensionOf('.bashrc'), '');
  assert.equal(extensionOf('README'), '');
  assert.equal(extensionOf('trailing.'), '');
  assert.equal(fileNameFromUrl('https://example.com/a/My%20File.zip?x=1#y'), 'My File.zip');
  assert.equal(fileNameFromUrl('https://example.com/'), '');
  assert.equal(fileNameFromUrl('https://example.com/bad%E0%A4%A.zip'), 'bad%E0%A4%A.zip');
  assert.equal(downloadFileName(item({ filename: '' })), 'setup.exe');
  assert.equal(downloadFileName(item({ filename: 'Real Name.iso' })), 'Real Name.iso');
});

test('unknown sizes are 0 or -1', () => {
  assert.equal(knownSize(item()), 5_000_000);
  assert.equal(knownSize(item({ totalBytes: 0, fileSize: 0 })), null);
  assert.equal(knownSize(item({ totalBytes: -1, fileSize: -1 })), null);
  assert.equal(knownSize(item({ totalBytes: -1, fileSize: 2048 })), 2048);
});

test('rules match by extension and minimum size; an unknown size passes', () => {
  assert.ok(matchesRules(rules, { fileName: 'a.ZIP', size: 100 * 1024 }));
  assert.equal(matchesRules(rules, { fileName: 'a.zip', size: 100 * 1024 - 1 }), false);
  assert.ok(matchesRules(rules, { fileName: 'a.zip', size: null }));
  assert.equal(matchesRules(rules, { fileName: 'a.png', size: 10_000_000 }), false);
  assert.equal(matchesRules(rules, { fileName: 'zip', size: null }), false);
});

test('capture decision covers every way to leave a download in the browser', () => {
  const base = { enabled: true, rules };
  assert.deepEqual(decideCapture({ ...base, item: item() }), { capture: true });
  assert.equal(decideCapture({ ...base, enabled: false, item: item() }).reason, 'disabled');
  assert.equal(decideCapture({ ...base, item: item({ incognito: true }) }).reason, 'incognito');
  assert.equal(decideCapture({ ...base, item: item({ url: 'blob:https://example.com/1', filename: 'a.zip' }) }).reason, 'scheme');
  assert.equal(decideCapture({ ...base, item: item({ url: 'data:application/zip;base64,AA', filename: 'a.zip' }) }).reason, 'scheme');
  assert.equal(decideCapture({ ...base, item: item({ filename: 'photo.png' }) }).reason, 'rules');
  assert.equal(decideCapture({ ...base, item: item({ totalBytes: 1000, fileSize: 1000 }) }).reason, 'rules');
  assert.ok(decideCapture({ ...base, item: item({ totalBytes: -1, fileSize: 0 }) }).capture);
});

test('cookies go along only when the download stays on their host', () => {
  assert.ok(cookiesAllowed('https://example.com/a.zip', undefined));
  assert.ok(cookiesAllowed('https://example.com/a.zip', 'https://example.com/a.zip'));
  assert.ok(cookiesAllowed('https://example.com/get?id=1', 'https://example.com/files/a.zip'));
  assert.equal(cookiesAllowed('https://evil.example/x.zip', 'https://bank.example/statement.zip'), false);
  assert.equal(cookiesAllowed('https://example.com/a.zip', 'https://example.com:8443/a.zip'), false);
  assert.equal(cookiesAllowed('https://example.com/a.zip', 'not a url'), false);
});

test('context menu referrer is reduced to the origin for another site', () => {
  assert.equal(linkReferrer('https://example.com/page?token=1', 'https://example.com/a.zip'), 'https://example.com/page?token=1');
  assert.equal(linkReferrer('https://example.com/page?token=1#x', 'https://cdn.other.net/a.zip'), 'https://example.com/');
  assert.equal(linkReferrer('data:text/html,hi', 'https://example.com/a.zip'), '');
  assert.equal(linkReferrer(undefined, 'https://example.com/a.zip'), '');
});

test('cookie header', () => {
  assert.equal(cookieHeader([{ name: 'a', value: 'b' }, { name: 'c', value: 'd' }]), 'a=b; c=d');
  assert.equal(cookieHeader([{ name: '', value: 'solo' }, { name: '', value: '' }]), 'solo');
  assert.equal(cookieHeader([]), '');
});

test('add message carries the download context', () => {
  const message = buildAddMessage({
    url: 'https://example.com/get?id=1',
    finalUrl: 'https://cdn.example.com/file.zip',
    fileName: 'C:\\Downloads\\file.zip',
    referrer: 'https://example.com/page',
    cookies: 'sid=1',
    userAgent: 'Mozilla/5.0',
    size: 123,
    mimeType: 'application/zip',
  });

  assert.deepEqual(message, {
    type: 'add',
    url: 'https://example.com/get?id=1',
    finalUrl: 'https://cdn.example.com/file.zip',
    fileName: 'file.zip',
    referrer: 'https://example.com/page',
    cookies: 'sid=1',
    userAgent: 'Mozilla/5.0',
    size: 123,
    mimeType: 'application/zip',
  });
});

test('add message leaves out unsafe or oversized optional fields', () => {
  const message = buildAddMessage({
    url: 'https://example.com/a.zip',
    finalUrl: 'blob:https://example.com/1',
    fileName: 'a'.repeat(LIMITS.fileName + 1),
    referrer: 'javascript:alert(1)',
    userAgent: 'UA\r\nX-Injected: 1',
    size: -1,
    mimeType: 'x'.repeat(LIMITS.mimeType + 1),
  });

  assert.deepEqual(message, { type: 'add', url: 'https://example.com/a.zip' });
  assert.deepEqual(buildAddMessage({ url: 'https://example.com/a.zip', finalUrl: 'https://example.com/a.zip', size: 1.5 }),
    { type: 'add', url: 'https://example.com/a.zip' });
});

test('add message is refused when the URL or the cookies would be refused by Colibri', () => {
  assert.equal(buildAddMessage({ url: 'data:text/plain,hi' }), null);
  assert.equal(buildAddMessage({ url: 'https://example.com/a.zip', cookies: 'a'.repeat(LIMITS.cookies + 1) }), null);
  assert.equal(buildAddMessage({ url: 'https://example.com/a.zip', cookies: 'a=b\nInjected: 1' }), null);
});

test('rules from the host answer are validated', () => {
  assert.deepEqual(rulesFromConfig({ ok: true, captureExtensions: ['zip', 'ISO'], minSizeKiB: 5 }), { extensions: ['zip', 'iso'], minSizeKiB: 5 });
  assert.deepEqual(rulesFromConfig({ ok: true, captureExtensions: [], minSizeKiB: 0 }), { extensions: [], minSizeKiB: 0 });
  for (const bad of [null, { ok: false }, { ok: true }, { ok: true, captureExtensions: 'zip', minSizeKiB: 0 },
    { ok: true, captureExtensions: ['z.ip'], minSizeKiB: 0 }, { ok: true, captureExtensions: [1], minSizeKiB: 0 },
    { ok: true, captureExtensions: ['zip'], minSizeKiB: -1 }, { ok: true, captureExtensions: ['zip'], minSizeKiB: 1.5 },
    { ok: true, captureExtensions: new Array(LIMITS.extensions + 1).fill('zip'), minSizeKiB: 0 }]) {
    assert.equal(rulesFromConfig(bad), null, JSON.stringify(bad));
  }
});

test('stored rules fall back to the defaults', () => {
  assert.deepEqual(storedRulesOrDefault(undefined), { extensions: [...DEFAULT_RULES.extensions], minSizeKiB: 0 });
  assert.deepEqual(storedRulesOrDefault({ extensions: 'bad' }).extensions, [...DEFAULT_RULES.extensions]);
  assert.deepEqual(storedRulesOrDefault({ extensions: ['iso'], minSizeKiB: 1 }), { extensions: ['iso'], minSizeKiB: 1 });
});

test('default rules are the defaults of Colibri settings', () => {
  // AppSettings.BrowserCaptureExtensions in Colibri.Core: the same list, in the same order.
  const source = readFileSync(new URL('../../src/Colibri.Core/Settings/AppSettings.cs', import.meta.url), 'utf8');
  const list = source.match(/BrowserCaptureExtensions \{ get; set; \} =\s*\[([^\]]*)\]/)[1];
  assert.deepEqual([...list.matchAll(/"([^"]+)"/g)].map((m) => m[1]), [...DEFAULT_RULES.extensions]);
  assert.match(source, /BrowserCaptureMinSizeKiB \{ get; set; \}\s*\n/);
});

test('popup connection status', () => {
  assert.equal(connectionStatus({ ok: true }), 'connected');
  assert.equal(connectionStatus({ ok: false, error: 'app-not-running' }), 'notRunning');
  assert.equal(connectionStatus({ ok: false, error: 'native', nativeError: 'Specified native messaging host not found.' }), 'hostMissing');
  assert.equal(connectionStatus({ ok: false, error: 'native', nativeError: 'Access to the specified native messaging host is forbidden.' }), 'hostMissing');
  assert.equal(connectionStatus({ ok: false, error: 'native', nativeError: 'Native host has exited.' }), 'error');
  assert.equal(connectionStatus({ ok: false, error: 'timeout' }), 'error');
});

test('manifest asks for exactly the permissions the extension uses', () => {
  const manifest = JSON.parse(readFileSync(new URL('../manifest.json', import.meta.url), 'utf8'));
  assert.equal(manifest.manifest_version, 3);
  assert.deepEqual([...manifest.permissions].sort(), ['contextMenus', 'cookies', 'downloads', 'nativeMessaging', 'storage']);
  assert.deepEqual(manifest.host_permissions, ['<all_urls>']);
  assert.equal(manifest.background.type, 'module');
});
