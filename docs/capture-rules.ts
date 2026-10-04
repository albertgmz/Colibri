// App-owned browser evaluation. Copy through the capture-policy generator; do not edit sibling copies.
import { categoryForFileName, captureCatalog, fileExtension } from './capture-catalog';
export type CaptureAction = 'capture' | 'ask' | 'browser';
export interface CapturePolicy { categories: Record<string, CaptureAction>; extensions: Record<string, CaptureAction> }
export function validPolicy(value: unknown): value is CapturePolicy {
  if (!value || typeof value !== 'object') return false;
  const p = value as CapturePolicy;
  return ['categories', 'extensions'].every(key => {
    const map = p[key as keyof CapturePolicy];
    return !!map && typeof map === 'object' && !Array.isArray(map) && Object.keys(map).length <= 256 &&
      Object.entries(map).every(([k, v]) => (key === 'categories' ? captureCatalog.categories.some(c => c.id === k) : /^[a-z0-9]{1,16}$/.test(k)) &&
        ['capture', 'ask', 'browser'].includes(v));
  });
}
export function normalizedExclusion(text: string): string | null {
  if (text.length > 512 || /[\u0000-\u001f?#]/.test(text)) return null;
  const colon = text.indexOf(':'); const kind = text.slice(0, colon).toLowerCase(); let value = text.slice(colon + 1);
  if (kind === 'type') {
    value = value.replace(/^\./, '').toLowerCase(); if (!/^[a-z0-9]{1,16}$/.test(value)) return null;
  } else if (['host', 'domain', 'path'].includes(kind)) {
    const slash = value.indexOf('/'); const host = slash < 0 ? value : value.slice(0, slash);
    if (!/^[a-z0-9.-]{1,253}$/i.test(host) || host.startsWith('.') || host.includes('..') || host.split('.').some(s => !s || s.startsWith('-') || s.endsWith('-'))) return null;
    if (kind === 'path' ? slash < 0 || value.includes('*') : slash >= 0) return null;
    value = host.toLowerCase() + (slash < 0 ? '' : value.slice(slash));
  } else return null;
  return kind + ':' + value;
}
export function exclusionMatches(rule: string, url: string, fileName = ''): boolean {
  const normalized = normalizedExclusion(rule); if (!normalized) return false;
  try {
    const u = new URL(url); const colon = normalized.indexOf(':'); const kind = normalized.slice(0, colon); const value = normalized.slice(colon + 1);
    if (kind === 'type') return fileExtension(fileName) === value;
    if (kind === 'host') return u.hostname.toLowerCase() === value;
    if (kind === 'domain') return u.hostname.toLowerCase() === value || u.hostname.toLowerCase().endsWith('.' + value);
    const slash = value.indexOf('/'); const path = value.slice(slash);
    return u.hostname.toLowerCase() === value.slice(0, slash) && (u.pathname === path || u.pathname.startsWith(path.replace(/\/$/, '') + '/'));
  } catch { return false; }
}
export function actionForFile(policy: CapturePolicy, fileName: string): CaptureAction {
  const extension = fileExtension(fileName);
  return policy.extensions[extension] ?? policy.categories[categoryForFileName(fileName)] ?? 'ask';
}
