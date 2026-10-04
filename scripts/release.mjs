// CI-only publication. Imports and `validate` are offline; `publish` requires a trusted Actions context.
import { readFileSync, createReadStream, statSync } from 'node:fs';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { parseVersion, compareVersions } from './version.mjs';
const repository = 'albertgmz/Colibri';
const apiRoot = `https://api.github.com/repos/${repository}`;
export function validateReleaseManifest(manifest, version) {
  parseVersion(version);
  const names = [`Colibri-${version}-win-x64-setup.exe`, `Colibri-${version}-win-x64-portable.zip`];
  if (manifest.format !== 1 || manifest.repository !== repository || manifest.version !== version || manifest.tag !== `v${version}` || !Array.isArray(manifest.assets) || manifest.assets.length !== 2)
    throw new Error('Release manifest identity/version is invalid.');
  const actual = new Set();
  for (const asset of manifest.assets) {
    if (!names.includes(asset.name) || actual.has(asset.name) || !Number.isSafeInteger(asset.size) || asset.size <= 0 || asset.size > 512 * 1024 * 1024 || !/^[a-f0-9]{64}$/.test(asset.sha256))
      throw new Error('Release asset name/hash/size is invalid.');
    actual.add(asset.name);
  }
  return manifest;
}
export function validateReleaseContext(context) {
  if (context.repository !== repository || context.eventRepository !== repository || context.private !== false || !['push', 'workflow_dispatch'].includes(context.event) ||
      !['refs/heads/v2', 'refs/heads/main'].includes(context.ref) || !/^[a-f0-9]{40}$/.test(context.sha))
    throw new Error('Publication requires this public repository, a v2/main branch push or dispatch, and an exact commit SHA.');
  return context.ref.slice('refs/heads/'.length);
}
async function hashFile(file) {
  const hash = createHash('sha256');
  for await (const chunk of createReadStream(file)) hash.update(chunk);
  return hash.digest('hex');
}
async function verifyLocal(directory, manifest) {
  for (const name of ['release-manifest.json', 'SHA256SUMS']) {
    const metadata = statSync(path.join(directory, name));
    if (!metadata.isFile() || metadata.size <= 0 || metadata.size > 1024 * 1024) throw new Error('Missing or invalid release metadata file.');
  }
  const checksums = readFileSync(path.join(directory, 'SHA256SUMS'), 'ascii').replace(/\r?\n$/, '').split(/\r?\n/).sort();
  const expected = manifest.assets.map(asset => `${asset.sha256}  ${asset.name}`).sort();
  if (JSON.stringify(checksums) !== JSON.stringify(expected)) throw new Error('SHA256SUMS must contain exactly the two manifest checksums and names.');
  for (const asset of manifest.assets) {
    const file = path.join(directory, asset.name);
    if (!statSync(file).isFile() || statSync(file).size !== asset.size || await hashFile(file) !== asset.sha256) throw new Error(`Local asset verification failed: ${asset.name}`);
  }
}
async function publish(directory, manifest) {
  const event = JSON.parse(readFileSync(process.env.GITHUB_EVENT_PATH, 'utf8'));
  const branch = validateReleaseContext({ repository: process.env.GITHUB_REPOSITORY, eventRepository: event.repository?.full_name, private: event.repository?.private,
    event: process.env.GITHUB_EVENT_NAME, ref: process.env.GITHUB_REF, sha: process.env.GITHUB_SHA });
  if (process.env.GITHUB_ACTIONS !== 'true' || !process.env.GITHUB_TOKEN) throw new Error('Publication requires Actions GITHUB_TOKEN.');
  const sha = process.env.GITHUB_SHA;
  async function request(endpoint, method = 'GET', body) {
    const response = await fetch(apiRoot + endpoint, { method, redirect: 'error', signal: AbortSignal.timeout(30000),
      headers: { Authorization: `Bearer ${process.env.GITHUB_TOKEN}`, Accept: 'application/vnd.github+json', 'X-GitHub-Api-Version': '2026-03-10', 'User-Agent': 'Colibri-release', ...(body ? { 'Content-Type': 'application/json' } : {}) },
      body: body ? JSON.stringify(body) : undefined });
    if (method === 'GET' && response.status === 404) return null;
    if (!response.ok) throw new Error(`GitHub ${method} ${endpoint} failed (${response.status}); no assets were replaced.`);
    return response.status === 204 ? null : response.json();
  }
  if ((await request(`/git/ref/heads/${branch}`))?.object?.sha !== sha) { console.log('Branch advanced; skipping stale release.'); return; }
  const tag = await request(`/git/ref/tags/${manifest.tag}`);
  if (tag && (tag.object?.type !== 'commit' || tag.object.sha !== sha)) throw new Error('Release tag already belongs to another commit or is not a lightweight CI tag.');
  let release = await request(`/releases/tags/${manifest.tag}`);
  // Drafts are not returned by the by-tag endpoint in every API context; enumerate for reliable retry.
  let highest = null;
  for (let page = 1; page <= 20; page++) {
    const releases = await request(`/releases?per_page=100&page=${page}`);
    if (!Array.isArray(releases)) throw new Error('Invalid release list.');
    for (const entry of releases) {
      if (entry.tag_name === manifest.tag) {
        if (release && release.id !== entry.id) throw new Error('Duplicate releases for the tag.');
        release = entry;
      }
      if (!entry.draft && !entry.prerelease) {
        const version = parseVersion(entry.tag_name.replace(/^v/, '')).text;
        if (highest === null || compareVersions(version, highest) > 0) highest = version;
      }
    }
    if (releases.length < 100) break;
    if (page === 20) throw new Error('Release history exceeds validation bound.');
  }
  if (highest && compareVersions(manifest.version, highest) < 0) throw new Error('Refusing to publish an older stable version.');
  if (!tag) {
    if (release) throw new Error('Release exists without its expected tag.');
    await request('/git/refs', 'POST', { ref: `refs/tags/${manifest.tag}`, sha });
  }
  if (!release) release = await request('/releases', 'POST', { tag_name: manifest.tag, target_commitish: sha,
    name: `Colibri ${manifest.version}`, draft: true, prerelease: false, generate_release_notes: true });
  if (release.prerelease) throw new Error('Expected a stable release.');
  const files = [...manifest.assets];
  for (const name of ['release-manifest.json', 'SHA256SUMS']) {
    const file = path.join(directory, name);
    if (!statSync(file).isFile() || statSync(file).size > 1024 * 1024) throw new Error('Invalid release metadata file.');
    files.push({ name, size: statSync(file).size, sha256: await hashFile(file) });
  }
  async function verifyRemote() {
    const remote = await request(`/releases/${release.id}/assets?per_page=100`);
    if (!Array.isArray(remote) || remote.some(a => !files.some(f => f.name === a.name))) throw new Error('Unexpected release assets; refusing mutation.');
    for (const file of files) {
      const matches = remote.filter(a => a.name === file.name);
      if (matches.length > 1) throw new Error('Duplicate release assets.');
      if (matches.length && (matches[0].state !== 'uploaded' || matches[0].size !== file.size || matches[0].digest !== `sha256:${file.sha256}`)) throw new Error(`Existing asset differs: ${file.name}; never clobber.`);
    }
    return remote;
  }
  let remote = await verifyRemote();
  if (!release.draft) {
    if (remote.length !== files.length) throw new Error('Existing published release is incomplete.');
    console.log(`Matching release already published; no changes. GitHub immutable: ${release.immutable === true}.`); return;
  }
  for (const file of files.filter(f => !remote.some(a => a.name === f.name))) {
    const upload = new URL(release.upload_url.replace(/\{.*$/, ''));
    if (upload.protocol !== 'https:' || upload.host !== 'uploads.github.com' || upload.pathname !== `/repos/${repository}/releases/${release.id}/assets`) throw new Error('Unexpected upload URL.');
    upload.searchParams.set('name', file.name);
    const response = await fetch(upload, { method: 'POST', redirect: 'error', duplex: 'half', signal: AbortSignal.timeout(300000),
      headers: { Authorization: `Bearer ${process.env.GITHUB_TOKEN}`, 'Content-Type': 'application/octet-stream', 'Content-Length': String(file.size), 'User-Agent': 'Colibri-release', 'X-GitHub-Api-Version': '2026-03-10' },
      body: createReadStream(path.join(directory, file.name)) });
    if (!response.ok) throw new Error(`Asset upload failed (${response.status}); draft retained for verified retry.`);
    await response.arrayBuffer();
  }
  remote = await verifyRemote();
  if (remote.length !== files.length) throw new Error('Release assets incomplete; draft retained.');
  if ((await request(`/git/ref/heads/${branch}`))?.object?.sha !== sha) throw new Error('Branch advanced during publication; draft retained.');
  const result = await request(`/releases/${release.id}`, 'PATCH', { draft: false, prerelease: false, make_latest: 'true' });
  console.log(`Published ${manifest.tag}. GitHub immutable: ${result.immutable === true}.`);
}
export async function main(args = process.argv.slice(2)) {
  const [mode, directory] = args;
  if (!['validate', 'publish'].includes(mode) || !directory) throw new Error('Usage: node scripts/release.mjs validate|publish <package-directory>');
  const version = parseVersion(readFileSync(new URL('../VERSION', import.meta.url), 'utf8')).text;
  const manifest = validateReleaseManifest(JSON.parse(readFileSync(path.join(directory, 'release-manifest.json'), 'utf8').replace(/^\uFEFF/, '')), version);
  await verifyLocal(directory, manifest);
  if (mode === 'publish') await publish(directory, manifest);
  else console.log(`Validated release assets for ${version}.`);
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  main().catch(error => { console.error(error.message); process.exitCode = 1; });
}
