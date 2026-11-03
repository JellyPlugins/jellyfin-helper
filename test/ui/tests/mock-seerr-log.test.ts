import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { runInNewContext } from 'node:vm';
import { describe, expect, test, vi } from 'vitest';
import { REPO_ROOT } from '../src/loader.js';

// Execute the actual mock module with networking stubbed, not a duplicate sanitizer.
function mockLogger() {
  const output = vi.fn();
  let handler: (req: any, res: any) => Promise<void>;
  const scope: Record<string, any> = {
    http: { createServer: (callback: typeof handler) => { handler = callback; return { listen: () => {} }; } },
    process: { env: {} },
    console: { log: output },
    URL,
  };
  const source = readFileSync(join(REPO_ROOT, 'test/e2e/mocks/seerr-server.js'), 'utf8');
  runInNewContext(source.replace("import http from 'node:http';", ''), scope);
  async function request(url: string, method = 'GET', key?: string) {
    const response = { status: 0, body: '' };
    const res = {
      writeHead: (status: number) => { response.status = status; },
      end: (body: string) => { response.body = body; },
    };
    await handler({ url, method, headers: key ? { 'x-api-key': key } : {} }, res);
    return response;
  }
  return { log: scope.log, output, request };
}

describe('mock Seerr request logging', () => {
  test('ordinary requests retain the diagnostic format', () => {
    const { log, output } = mockLogger();
    log('GET', '/api/v1/request?skip=50', 500);
    expect(output).toHaveBeenCalledWith('[mock-seerr] GET /api/v1/request?skip=50 -> 500');
  });

  test.each(['\r', '\n', '\0', '\t', '\x1b', '\x7f', '\x85', '\u2028', '\u2029'])(
    'control characters cannot forge records or terminal commands: %j', (control) => {
      const { log, output } = mockLogger();
      log('GET' + control + 'FORGED', '/path' + control + 'FORGED', 404);
      expect(output).toHaveBeenCalledTimes(1);
      const line = output.mock.calls[0][0] as string;
      expect(line).not.toContain(control);
      expect(line).toContain('\\u' + control.charCodeAt(0).toString(16).padStart(4, '0'));
      expect(line.endsWith(' -> 404')).toBe(true);
    },
  );

  test('the actual HTTP dispatcher logs an adversarial method as one escaped record', async () => {
    const { request, output } = mockLogger();
    const response = await request('/not-found?query=normal', 'GET\u2028FORGED', 'test-key');
    expect(response.status).toBe(404);
    expect(output).toHaveBeenCalledWith('[mock-seerr] GET\\u2028FORGED /not-found?query=normal -> 404');
  });

  test('health stays unauthenticated while API authentication and 404 dispatch stay intact', async () => {
    const { request } = mockLogger();
    expect((await request('/health')).status).toBe(200);
    expect((await request('/api/v1/request')).status).toBe(401);
    expect((await request('/api/v1/request', 'GET', '*****')).status).toBe(401);
    expect((await request('/not-found', 'GET', 'test-key')).status).toBe(404);
  });

  test('request-list pagination, forced page-two failure and reset retain their state transitions', async () => {
    const { request } = mockLogger();
    const first = JSON.parse((await request('/api/v1/request?skip=0&take=50', 'GET', 'test-key')).body);
    expect(first.results.length).toBeGreaterThan(0);
    expect(first.pageInfo.pages).toBe(1);
    expect((await request('/force-fail-page2')).status).toBe(200);
    const inflated = JSON.parse((await request('/api/v1/request?skip=0&take=50', 'GET', 'test-key')).body);
    expect(inflated.pageInfo.results).toBeGreaterThan(50);
    expect((await request('/api/v1/request?skip=50', 'GET', 'test-key')).status).toBe(500);
    const calls = JSON.parse((await request('/list-calls')).body).calls;
    expect(calls).toEqual([{ skip: 0, status: 200 }, { skip: 50, status: 500 }]);
    await request('/reset');
    expect((await request('/api/v1/request?skip=50', 'GET', 'test-key')).status).toBe(200);
    expect(JSON.parse((await request('/count')).body).count).toBe(first.results.length);
  });
});
