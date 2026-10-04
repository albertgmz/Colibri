import test from 'node:test';
import assert from 'node:assert/strict';
import { validateReleaseManifest, validateReleaseContext, main } from './release.mjs';
import { createHash } from 'node:crypto';
import { mkdtempSync, readFileSync, writeFileSync, rmSync, renameSync } from 'node:fs';
import path from 'node:path';
import os from 'node:os';
const assets = ['setup.exe', 'portable.zip'].map(suffix => ({ name: `Colibri-2.0.1-win-x64-${suffix}`, size: 123, sha256: 'a'.repeat(64) }));
const manifest = { format: 1, repository: 'albertgmz/Colibri', version: '2.0.1', tag: 'v2.0.1', assets };
test('release manifest has exact versioned assets and bounded hashes/sizes', () => {
  assert.doesNotThrow(() => validateReleaseManifest(manifest, '2.0.1'));
  for (const value of [{ ...manifest, tag: 'v2.0.0' }, { ...manifest, repository: 'other/Colibri' },
    { ...manifest, assets: [assets[0], assets[0]] }, { ...manifest, assets: [{ ...assets[0], size: 0 }, assets[1]] },
    { ...manifest, assets: [{ ...assets[0], sha256: 'x'.repeat(64) }, assets[1]] }]) assert.throws(() => validateReleaseManifest(value, '2.0.1'));
});
test('offline GitHub fixture: complete checksums before publication, safe retry and conflict refusal', async () => {
  const directory = mkdtempSync(path.join(os.tmpdir(), 'colibri-release-test-'));
  const version = readFileSync(new URL('../VERSION', import.meta.url), 'utf8').trim();
  const files = ['setup.exe', 'portable.zip'].map(suffix => {
    const name = `Colibri-${version}-win-x64-${suffix}`, bytes = Buffer.from(`synthetic ${suffix}`);
    writeFileSync(path.join(directory, name), bytes);
    return { name, size: bytes.length, sha256: createHash('sha256').update(bytes).digest('hex') };
  });
  writeFileSync(path.join(directory, 'release-manifest.json'), JSON.stringify({ format: 1, repository: 'albertgmz/Colibri', version, tag: `v${version}`, assets: files }));
  writeFileSync(path.join(directory, 'SHA256SUMS'), files.map(f => `${f.sha256}  ${f.name}`).join('\n'));
  const eventPath = path.join(directory, 'event.json');
  writeFileSync(eventPath, JSON.stringify({ repository: { full_name: 'albertgmz/Colibri', private: false } }));
  const env = { GITHUB_ACTIONS: 'true', GITHUB_TOKEN: 'synthetic-not-a-secret', GITHUB_EVENT_PATH: eventPath,
    GITHUB_REPOSITORY: 'albertgmz/Colibri', GITHUB_EVENT_NAME: 'push', GITHUB_REF: 'refs/heads/v2', GITHUB_SHA: 'a'.repeat(40) };
  const previous = Object.fromEntries(Object.keys(env).map(key => [key, process.env[key]]));
  const originalFetch = globalThis.fetch;
  let tag = null, release = null, remote = [], mutations = 0;
  const result = (body, status = 200) => new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } });
  try {
    Object.assign(process.env, env);
    globalThis.fetch = async (url, options = {}) => {
      const parsed = new URL(url), method = options.method || 'GET', endpoint = parsed.pathname.replace('/repos/albertgmz/Colibri', '');
      if (method !== 'GET') mutations++;
      if (parsed.host === 'uploads.github.com') {
        assert.equal(release.draft, true);
        const chunks = []; for await (const chunk of options.body) chunks.push(chunk);
        const bytes = Buffer.concat(chunks);
        remote.push({ name: parsed.searchParams.get('name'), size: bytes.length, state: 'uploaded', digest: `sha256:${createHash('sha256').update(bytes).digest('hex')}` });
        return result({});
      }
      assert.equal(parsed.host, 'api.github.com');
      if (endpoint === '/git/ref/heads/v2') return result({ object: { sha: env.GITHUB_SHA } });
      if (endpoint.startsWith('/git/ref/tags/')) return tag ? result(tag) : result({}, 404);
      if (endpoint.startsWith('/releases/tags/')) return release ? result(release) : result({}, 404);
      if (endpoint === '/git/refs' && method === 'POST') { tag = { object: { type: 'commit', sha: env.GITHUB_SHA } }; return result(tag); }
      if (endpoint === '/releases' && method === 'GET') return result(release ? [release] : []);
      if (endpoint === '/releases' && method === 'POST') {
        release = { ...JSON.parse(options.body), id: 1, immutable: false, upload_url: 'https://uploads.github.com/repos/albertgmz/Colibri/releases/1/assets{?name,label}' };
        return result(release);
      }
      if (endpoint === '/releases/1/assets') return result(remote);
      if (endpoint === '/releases/1' && method === 'PATCH') {
        assert.equal(remote.length, 4);
        release = { ...release, ...JSON.parse(options.body), immutable: false }; return result(release);
      }
      throw new Error(`Unexpected offline API request ${method} ${url}`);
    };
    const checksumsPath = path.join(directory, 'SHA256SUMS');
    const validChecksums = readFileSync(checksumsPath, 'utf8');
    for (const invalid of ['', validChecksums.replace(files[0].sha256, 'b'.repeat(64)), validChecksums + '\n' + validChecksums]) {
      writeFileSync(checksumsPath, invalid);
      await assert.rejects(main(['publish', directory]));
      assert.equal(mutations, 0);
    }
    writeFileSync(checksumsPath, validChecksums);
    renameSync(checksumsPath, checksumsPath + '.saved');
    await assert.rejects(main(['publish', directory])); assert.equal(mutations, 0);
    renameSync(checksumsPath + '.saved', checksumsPath);
    await main(['publish', directory]);
    assert.equal(release.draft, false);
    assert.equal(release.immutable, false);
    const completeMutations = mutations;
    await main(['publish', directory]); assert.equal(mutations, completeMutations);
    remote[0].digest = 'sha256:' + 'b'.repeat(64);
    await assert.rejects(main(['publish', directory]), /Existing asset differs/);
    assert.equal(mutations, completeMutations);
    tag.object.sha = 'b'.repeat(40);
    await assert.rejects(main(['publish', directory]), /tag already belongs/);
    assert.equal(mutations, completeMutations);
  } finally {
    globalThis.fetch = originalFetch;
    for (const [key, value] of Object.entries(previous)) { if (value === undefined) delete process.env[key]; else process.env[key] = value; }
    assert.ok(path.resolve(directory).startsWith(path.resolve(os.tmpdir()) + path.sep));
    assert.ok(path.basename(directory).startsWith('colibri-release-test-'));
    rmSync(directory, { recursive: true, force: true });
  }
});
test('only trusted same-repository branch push/dispatch can publish', () => {
  const context = { repository: 'albertgmz/Colibri', private: false, event: 'push', ref: 'refs/heads/v2', sha: 'a'.repeat(40), eventRepository: 'albertgmz/Colibri' };
  assert.equal(validateReleaseContext(context), 'v2');
  for (const override of [{ private: true }, { event: 'pull_request' }, { ref: 'refs/heads/feature' }, { repository: 'fork/Colibri' }, { sha: 'main; echo nope' }, { eventRepository: 'fork/Colibri' }])
    assert.throws(() => validateReleaseContext({ ...context, ...override }));
});
