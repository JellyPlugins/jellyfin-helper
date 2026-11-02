import { afterAll, afterEach, beforeEach, expect, test, vi } from 'vitest';
import { loadClientLifecycleBundle, saveCoverage } from '../src/loader.js';

afterAll(saveCoverage);

beforeEach(() => {
  delete (window as any).JellyfinHelperLifecycle;
  window.history.replaceState(null, '', '#/login');
  document.body.innerHTML = '';
});

afterEach(() => {
  (window as any).JellyfinHelperLifecycle?.dispose();
  delete (window as any).JellyfinHelperLifecycle;
  vi.clearAllTimers();
  vi.restoreAllMocks();
});

function hostClient(userId = '', token = '') {
  return {
    userId, token,
    getCurrentUserId() { return this.userId; },
    accessToken() { return this.token; },
    setAuthenticationInfo(accessToken: string, user: string) { this.token = accessToken; this.userId = user; return 'host-result'; },
  };
}

test('credential transitions notify before installation and preserve the host result', () => {
  const client = hostClient('a', 'first');
  (globalThis as any).ApiClient = client;
  loadClientLifecycleBundle();
  const seen: string[] = [];
  const before = () => seen.push('before:' + client.userId);
  const after = () => seen.push('after:' + client.userId);
  document.addEventListener('jfh:session-changing', before);
  document.addEventListener('jfh:session-changed', after);
  try {
    expect(client.setAuthenticationInfo('second', 'b')).toBe('host-result');
    expect(seen).toEqual(['before:a', 'after:b']);
    seen.length = 0;
    client.setAuthenticationInfo('second', 'b');
    expect(seen).toEqual([]);
    client.setAuthenticationInfo('', '');
    expect(seen).toEqual(['before:b', 'after:']);
  } finally {
    document.removeEventListener('jfh:session-changing', before);
    document.removeEventListener('jfh:session-changed', after);
  }
});

test('history notifications follow real URL changes and do not duplicate native events', () => {
  (globalThis as any).ApiClient = hostClient();
  loadClientLifecycleBundle();
  const notify = vi.fn();
  document.addEventListener('jfh:navigation', notify);
  try {
    window.history.pushState({ value: 1 }, '', '#/home');
    expect(window.history.state).toEqual({ value: 1 });
    expect(notify).toHaveBeenCalledTimes(1);
    window.dispatchEvent(new Event('hashchange'));
    window.dispatchEvent(new Event('popstate'));
    window.history.replaceState({ value: 2 }, '', '#/home');
    expect(notify).toHaveBeenCalledTimes(1);
    window.history.replaceState(null, '', '#/login.html');
    expect(notify).toHaveBeenCalledTimes(2);
  } finally {
    document.removeEventListener('jfh:navigation', notify);
  }
});

test('client replacement is adopted without rewriting a shared prototype', () => {
  const prototype = hostClient();
  const original = prototype.setAuthenticationInfo;
  const first = Object.create(prototype);
  (globalThis as any).ApiClient = first;
  loadClientLifecycleBundle();
  expect(prototype.setAuthenticationInfo).toBe(original);
  const replacement = Object.create(prototype);
  (globalThis as any).ApiClient = replacement;
  (window as any).JellyfinHelperLifecycle.refresh();
  expect(replacement.setAuthenticationInfo('new', 'b')).toBe('host-result');
  expect(replacement.userId).toBe('b');
  (window as any).JellyfinHelperLifecycle.dispose();
  expect(Object.hasOwn(first, 'setAuthenticationInfo')).toBe(false);
  expect(Object.hasOwn(replacement, 'setAuthenticationInfo')).toBe(false);
});

test('a client that copies an existing authentication hook does not receive another wrapper', () => {
  const first = hostClient('a', 'first');
  (globalThis as any).ApiClient = first;
  loadClientLifecycleBundle();
  const replacement = { ...first };
  const copied = replacement.setAuthenticationInfo;
  (globalThis as any).ApiClient = replacement;
  (window as any).JellyfinHelperLifecycle.refresh();
  expect(replacement.setAuthenticationInfo).toBe(copied);
  const before = vi.fn();
  document.addEventListener('jfh:session-changing', before);
  try {
    replacement.setAuthenticationInfo('second', 'b');
    expect(before).toHaveBeenCalledTimes(1);
    expect(replacement.userId).toBe('b');
  } finally {
    document.removeEventListener('jfh:session-changing', before);
  }
});

test('immutable clients and unavailable startup identity do not prevent navigation', () => {
  const client = Object.preventExtensions(Object.create(hostClient()));
  (globalThis as any).ApiClient = client;
  expect(loadClientLifecycleBundle).not.toThrow();
  expect(() => window.history.pushState(null, '', '#/home')).not.toThrow();
  (globalThis as any).ApiClient = { getCurrentUserId: () => { throw new Error('Not ready'); } };
  expect(() => (window as any).JellyfinHelperLifecycle.refresh()).not.toThrow();
});

test('cleanup preserves host hooks installed later and double loading is inert', () => {
  const client = hostClient();
  (globalThis as any).ApiClient = client;
  loadClientLifecycleBundle();
  const lifecycle = (window as any).JellyfinHelperLifecycle;
  const auth = client.setAuthenticationInfo;
  const push = window.history.pushState;
  loadClientLifecycleBundle();
  expect((window as any).JellyfinHelperLifecycle).toBe(lifecycle);
  expect(client.setAuthenticationInfo).toBe(auth);
  const laterAuth = vi.fn(() => 'later');
  client.setAuthenticationInfo = laterAuth;
  lifecycle.dispose();
  expect(client.setAuthenticationInfo).toBe(laterAuth);
  expect(() => push.call(window.history, null, '', '#/home')).not.toThrow();
});
