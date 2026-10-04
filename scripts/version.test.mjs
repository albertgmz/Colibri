import test from 'node:test';
import assert from 'node:assert/strict';
import { parseVersion, compareVersions, validateCommitVersions, validateCandidate } from './version.mjs';
import { execFileSync, spawnSync } from 'node:child_process';
import { mkdtempSync, mkdirSync, readFileSync, writeFileSync, rmSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';

test('canonical bounded three-part versions', () => {
  assert.equal(parseVersion('2.0.0\n').text, '2.0.0');
  for (const value of ['02.0.0', '2.0', '2.0.0-beta', '2.0.65535', ' 2.0.0', '2.0.0\n\n'])
    assert.throws(() => parseVersion(value));
  assert.ok(compareVersions('2.0.10', '2.0.9') > 0);
});
test('real Git: root commit, staged bytes, intermediate commits, tags, divergent PR, merge parents', () => {
  const directory = mkdtempSync(path.join(os.tmpdir(), 'colibri-version-test-'));
  const git = (...args) => execFileSync('git', args, { cwd: directory, encoding: 'utf8', stdio: 'pipe' }).trim();
  const set = value => { writeFileSync(path.join(directory, 'VERSION'), value); git('add', 'VERSION'); };
  const commit = message => { git('commit', '--no-verify', '-m', message); return git('rev-parse', 'HEAD'); };
  try {
    git('init', '-b', 'main'); git('config', 'user.name', 'Fixture'); git('config', 'user.email', 'fixture@example.invalid');
    git('config', 'commit.gpgsign', 'false');
    git('config', 'core.hooksPath', path.join(directory, 'no-hooks'));
    mkdirSync(path.join(directory, 'scripts'));
    writeFileSync(path.join(directory, 'scripts/version.mjs'), readFileSync(new URL('./version.mjs', import.meta.url), 'utf8'));
    const run = (args, input) => spawnSync(process.execPath, ['scripts/version.mjs', ...args], { cwd: directory, encoding: 'utf8', input });
    const zeros = '0'.repeat(40);
    // First commit in an empty repository: no HEAD, any valid version.
    set('0.5.0\n'); assert.equal(run(['staged']).status, 0); const first = commit('root version');
    assert.equal(run(['range', zeros, 'HEAD']).status, 0);
    assert.equal(run(['pre-push'], `refs/heads/main ${first} refs/heads/main ${zeros}\n`).status, 0);
    set('0.5.0\n'); assert.notEqual(run(['staged']).status, 0);
    set('0.4.9\n'); assert.notEqual(run(['staged']).status, 0);
    set(' 0.5.1\n'); assert.notEqual(run(['staged']).status, 0);
    set('0.5.1\n'); writeFileSync(path.join(directory, 'VERSION'), '0.5.0\n'); assert.equal(run(['staged']).status, 0);
    const second = commit('staged version');
    writeFileSync(path.join(directory, 'note'), 'note'); git('add', 'note'); commit('unchanged intermediate');
    set('0.5.2\n'); commit('valid tip'); assert.notEqual(run(['range', first, 'HEAD']).status, 0);
    assert.notEqual(run(['range', zeros, 'HEAD']).status, 0);
    git('checkout', '-b', 'feature', first); set('0.5.3\n'); const feature = commit('feature');
    assert.equal(run(['pr-range', second, feature]).status, 0);
    git('checkout', '-b', 'missing', feature); git('rm', '-q', 'VERSION'); commit('drops VERSION');
    assert.notEqual(run(['range', feature, 'HEAD']).status, 0);
    assert.notEqual(run(['staged']).status, 0);
    git('checkout', 'feature');
    git('tag', 'v0.5.4', first); set('0.5.4\n'); assert.notEqual(run(['staged']).status, 0);
    git('tag', '-d', 'v0.5.4');
    const mergeFile = path.join(directory, git('rev-parse', '--git-path', 'MERGE_HEAD'));
    writeFileSync(mergeFile, second + '\n' + feature + '\n');
    set('0.5.3\n'); assert.notEqual(run(['staged']).status, 0);
    set('0.5.5\n'); assert.equal(run(['staged']).status, 0);
    rmSync(mergeFile);
    // An orphan branch starts another root commit, which may use any unused valid version.
    git('checkout', '--orphan', 'fresh'); set('0.1.0\n'); assert.equal(run(['staged']).status, 0);
    const fresh = commit('fresh root');
    assert.equal(run(['range', zeros, fresh]).status, 0);
    assert.notEqual(run(['pre-push'], `refs/heads/fresh ${fresh} refs/heads/feature ${feature}\n`).status, 0);
  } finally {
    assert.ok(path.resolve(directory).startsWith(path.resolve(os.tmpdir()) + path.sep));
    assert.ok(path.basename(directory).startsWith('colibri-version-test-'));
    rmSync(directory, { recursive: true, force: true });
  }
});
test('every introduced commit increases, including every merge parent', () => {
  const commits = [{ sha: 'a', version: '0.5.0', parents: [] },
    { sha: 'b', version: '2.0.1', parents: ['a'] },
    { sha: 'c', version: '2.0.2', parents: ['b'] }];
  assert.doesNotThrow(() => validateCommitVersions(commits, new Set(['a', 'b', 'c'])));
  assert.throws(() => validateCommitVersions([...commits, { sha: 'd', version: '2.0.2', parents: ['b', 'c'] }], new Set(['d'])));
  assert.throws(() => validateCommitVersions([...commits, { sha: 'd', version: '0.4.0', parents: ['c'] }], new Set(['d'])));
  assert.throws(() => validateCommitVersions([...commits, { sha: 'd', version: null, parents: ['c'] }], new Set(['d'])));
  assert.throws(() => validateCommitVersions([{ sha: 'a', version: null, parents: [] }, { sha: 'b', version: '1.0.0', parents: ['a'] }], new Set(['b'])));
});
test('duplicate branch versions fail but the same commit is not a duplicate', () => {
  const commits = [{ sha: 'a', version: '2.0.0', parents: [] },
    { sha: 'b', version: '2.0.1', parents: ['a'] },
    { sha: 'c', version: '2.0.1', parents: ['a'] }];
  assert.throws(() => validateCommitVersions(commits, new Set(['c'])));
  assert.doesNotThrow(() => validateCommitVersions(commits.slice(0, 2), new Set(['b'])));
});
test('root and staged candidates enforce unused increasing version', () => {
  assert.doesNotThrow(() => validateCandidate('0.5.0', [], []));
  assert.doesNotThrow(() => validateCandidate('2.0.1', [], []));
  assert.throws(() => validateCandidate('0.5.0', [], ['0.5.0']));
  assert.throws(() => validateCandidate('1.0.0', [null], []));
  assert.throws(() => validateCandidate('2.0.0', ['2.0.0'], []));
  assert.throws(() => validateCandidate('2.0.2', ['2.0.1'], ['2.0.2']));
  assert.doesNotThrow(() => validateCandidate('2.0.3', ['2.0.1', '2.0.2'], []));
});
