import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import path from 'node:path';
import { existsSync, readdirSync, chmodSync, readFileSync, writeFileSync } from 'node:fs';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const git = (...args) => execFileSync('git', args, { cwd: root, encoding: 'utf8' }).trim();
let existing;
try { existing = git('config', '--get', 'core.hooksPath'); } catch { /* unset */ }
if (existing && existing !== '.githooks') {
  console.error(`Existing core.hooksPath is ${existing}; refusing to replace another hook configuration.`);
  process.exitCode = 1;
} else {
  const defaultHooks = path.resolve(root, git('rev-parse', '--git-path', 'hooks'));
  if (!existing && existsSync(defaultHooks) && readdirSync(defaultHooks).some(name => !name.endsWith('.sample'))) {
    console.error('Existing local hooks found; refusing to disable them. Integrate the version checks manually.');
    process.exitCode = 1;
  } else {
  for (const name of ['pre-commit', 'pre-merge-commit', 'pre-push']) {
    const file = path.join(root, '.githooks', name);
    writeFileSync(file, readFileSync(file, 'utf8').replace(/\r\n/g, '\n'));
    chmodSync(file, 0o755);
  }
  git('config', '--local', 'core.hooksPath', '.githooks');
  console.log('Version hooks enabled for this checkout. Every commit must increase staged VERSION.');
  }
}
