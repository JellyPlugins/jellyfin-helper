// Shared ApiClient stub + microtask flush. Several suites need the exact same
// router, so it lives here instead of being copy-pasted per file.

export type AjaxCall = { url: string; body?: unknown; method?: string };

/**
 * Installs a global ApiClient whose `ajax` resolves the first route whose key is a
 * substring of the request URL, rejects URLs matching `failures`, and records every
 * call. Request bodies are parsed back from the JSON string `apiPost` sends, so
 * assertions can match the shape the server actually receives.
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
      for (const [key, value] of Object.entries(routes)) {
        if (String(opts.url).includes(key)) return Promise.resolve(value);
      }
      return Promise.resolve({});
    },
  };
  return calls;
}

/** Drains pending microtasks so chained promise callbacks have settled. */
export async function flush(ticks = 10): Promise<void> {
  for (let i = 0; i < ticks; i++) await Promise.resolve();
}

