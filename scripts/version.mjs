import { execFileSync } from 'node:child_process';
import { readFileSync, writeFileSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

export const BASELINE = '6587c041b56dd8a2a7d9edebd307543b6971b972';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
export function parseVersion(raw) {
  if (!/^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(?:\r?\n)?$/.test(raw))
    throw new Error('VERSION must contain one canonical major.minor.patch value.');
  const text = raw.replace(/\r?\n$/, '');
  const parts = text.split('.').map(Number);
  if (parts.some(n => n > 65534)) throw new Error('VERSION components must be between 0 and 65534.');
  return { text, parts };
}
export function compareVersions(a, b) {
  const aa = parseVersion(a).parts, bb = parseVersion(b).parts;
  for (let i = 0; i < 3; i++) if (aa[i] !== bb[i]) return aa[i] - bb[i];
  return 0;
}
export function validateCandidate(value, parents, used) {
  const version = parseVersion(value).text;
  if (!parents.length && version !== '2.0.0') throw new Error('The migration commit must introduce VERSION 2.0.0.');
  for (const parent of parents) if (compareVersions(version, parent) <= 0)
    throw new Error(`VERSION ${version} must exceed parent/current version ${parent}; bump and stage VERSION.`);
  if (used.includes(version)) throw new Error(`VERSION ${version} was already used by another commit or tag.`);
}
export function validateCommitVersions(commits, introduced, tags = []) {
  const bySha = new Map(commits.map(c => [c.sha, c]));
  const used = new Map();
  for (const commit of commits) if (commit.version !== null) {
    const version = parseVersion(commit.version).text;
    const prior = used.get(version);
    if (prior && prior !== commit.sha && (introduced.has(prior) || introduced.has(commit.sha)))
      throw new Error(`Duplicate VERSION ${version}: ${prior} and ${commit.sha}.`);
    used.set(version, commit.sha);
  }
  for (const commit of commits) if (introduced.has(commit.sha)) {
    if (commit.version === null) throw new Error(`Commit ${commit.sha} has no VERSION.`);
    const parents = commit.parents.map(p => bySha.get(p)?.version).filter(v => v != null);
    validateCandidate(commit.version, parents, []);
    for (const tag of tags) if (tag.version === parseVersion(commit.version).text && tag.sha !== commit.sha)
      throw new Error(`Tag v${tag.version} belongs to ${tag.sha}, not ${commit.sha}.`);
  }
}
function git(...args) { return execFileSync('git', args, { cwd: root, encoding: 'utf8', stdio: ['pipe', 'pipe', 'pipe'] }).trim(); }
function at(ref) {
  try { return execFileSync('git', ['show', `${ref}:VERSION`], { cwd: root, encoding: 'utf8', stdio: ['pipe', 'pipe', 'pipe'] }); } catch { return null; }
}
function history(extra = []) {
  const rows = git('rev-list', '--parents', '--all', ...extra, `^${BASELINE}`).split('\n').filter(Boolean);
  return rows.map(row => { const [sha, ...parents] = row.split(' '); return { sha, parents, version: at(sha) }; });
}
function tags() {
  return git('tag', '--list', 'v*').split('\n').filter(Boolean).flatMap(tag => {
    try { return [{ version: parseVersion(tag.slice(1)).text, sha: git('rev-parse', `${tag}^{commit}`) }]; }
    catch { throw new Error(`Invalid release tag ${tag}.`); }
  });
}
function introduced(from, to) {
  return new Set(git('rev-list', to, `^${BASELINE}`, ...(from && !/^0+$/.test(from) ? [`^${from}`] : [])).split('\n').filter(Boolean));
}
function range(from, to, requireAncestor = true) {
  if (requireAncestor && from && !/^0+$/.test(from)) {
    try { git('merge-base', '--is-ancestor', from, to); }
    catch { throw new Error('Version validation refuses rewritten history; rebase before publishing a new branch.'); }
  }
  validateCommitVersions(history([to]), introduced(from, to), tags());
}
function staged() {
  const value = at('');
  if (value === null) throw new Error('Stage VERSION before committing.');
  const refs = ['HEAD'];
  const mergeFile = path.resolve(root, git('rev-parse', '--git-path', 'MERGE_HEAD'));
  if (existsSync(mergeFile)) refs.push(...readFileSync(mergeFile, 'utf8').trim().split('\n'));
  if (at('HEAD') === null && git('rev-parse', 'HEAD') !== BASELINE)
    throw new Error('Missing VERSION after the migration baseline; restore a valid increasing version.');
  const parents = refs.map(at).filter(v => v !== null);
  validateCandidate(value, parents, [...history().filter(c => c.version !== null).map(c => parseVersion(c.version).text), ...tags().map(t => t.version)]);
}
export function main(args = process.argv.slice(2)) {
  const [mode, from, to] = args;
  if (mode === 'staged') staged();
  else if (mode === 'range') range(from, to || 'HEAD');
  else if (mode === 'pr-range') range(git('merge-base', from, to), to, false);
  else if (mode === 'pre-push') {
    for (const row of readFileSync(0, 'utf8').split('\n').filter(Boolean)) {
      const [, local, , remote] = row.split(/\s+/);
      if (!/^0+$/.test(local)) range(remote, local);
      else throw new Error('Deleting a remote branch/tag requires explicit maintainer review; version hook refuses.');
    }
  } else if (mode === 'read') console.log(parseVersion(readFileSync(path.join(root, 'VERSION'), 'utf8')).text);
  else if (mode === 'bump') {
    const current = parseVersion(readFileSync(path.join(root, 'VERSION'), 'utf8'));
    const next = parseVersion(from);
    if (compareVersions(next.text, current.text) <= 0) throw new Error('Bump must exceed the working VERSION.');
    writeFileSync(path.join(root, 'VERSION'), next.text + '\n');
    console.log(`VERSION is ${next.text}; inspect and stage it with your change.`);
  } else throw new Error('Usage: node scripts/version.mjs staged|range <before> <after>|pre-push|read|bump <version>');
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { main(); } catch (error) { console.error(error.message); process.exitCode = 1; }
}
