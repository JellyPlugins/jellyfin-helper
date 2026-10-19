// Shared ApiClient stub + microtask flush. Several suites need the exact same
// router, so it lives here instead of being copy-pasted per file.

export type AjaxCall = { url: string; body?: unknown; method?: string };

/**
 * Resolves a request URL against a route table: an exact URL key wins over a
 * substring key, then insertion order decides among substring matches. Sharing
 * this ordering keeps identical route tables from behaving differently per suite.
 */
export function matchRoute(url: string, routes: Record<string, unknown>): unknown {
  if (Object.prototype.hasOwnProperty.call(routes, url)) {
    return routes[url];
  }
  for (const [key, value] of Object.entries(routes)) {
    if (url.includes(key)) return value;
  }
  return {};
}

/**
 * Installs a global ApiClient whose `ajax` resolves the best-matching route (see
 * matchRoute), rejects URLs matching `failures`, and records every call. Request
 * bodies are parsed back from the JSON string `apiPost` sends, so assertions can
 * match the shape the server actually receives.
 */
export function ajaxRouter(routes: Record<string, unknown>, failures: string[] = []): AjaxCall[] {
  const calls: AjaxCall[] = [];
  (globalThis as any).ApiClient = {
    getUrl: (p: string) => p,
    getCurrentUserId: () => '',
    accessToken: () => 't',
    ajax: (opts: any) => {
      let body: unknown;
      try {
        body = typeof opts.data === 'string' ? JSON.parse(opts.data) : opts.data;
      } catch {
        body = opts.data;
      }
      calls.push({ url: String(opts.url), body, method: opts.type });
      if (failures.some((f) => String(opts.url).includes(f))) return Promise.reject(new Error('down'));
      return Promise.resolve(matchRoute(String(opts.url), routes));
    },
  };
  return calls;
}

/** Drains pending microtasks so chained promise callbacks have settled. */
export async function flush(ticks = 10): Promise<void> {
  for (let i = 0; i < ticks; i++) await Promise.resolve();
}

