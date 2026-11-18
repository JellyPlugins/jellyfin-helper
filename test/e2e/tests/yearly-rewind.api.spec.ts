import { test, expect, request, type APIRequestContext } from '@playwright/test';
import { existsSync, readFileSync } from 'node:fs';
import { join } from 'node:path';
import { randomUUID } from 'node:crypto';
import { apiContext, normalUserContext, loadAuth, p, requireNormalUser } from '../setup/api-client.ts';
import { seedRewind, rewindPath, rewindDataDir, todayMonthDay, visibleRewindWindow } from '../setup/yearly-rewind.ts';

test.describe.serial('YearlyRewind personal frozen report', () => {
 let admin: APIRequestContext, user: APIRequestContext | null, original: any, fixture: ReturnType<typeof seedRewind>;
 const field = (data: any, name: string) => data[name] ?? data[name[0].toLowerCase() + name.slice(1)];
 test.beforeAll(async () => {
   const auth = loadAuth(); admin = await apiContext(auth); user = await normalUserContext(auth); requireNormalUser(user);
   original = await (await admin.get(p('Configuration'))).json();
   fixture = seedRewind(auth.normalUser!.userId);
   expect((await admin.put(p('Configuration'), {data: {...original, RecommendationsTaskMode: 'Deactivate', DiscoveryUserAccessEnabled: false,
     YearlyRewindEnabled: true, ...visibleRewindWindow() }})).ok()).toBeTruthy();
   await expect.poll(async () => (await user!.get(p('YearlyRewind/My'))).status()).toBe(200);
 });
 test.afterAll(async () => { if (original) await admin.put(p('Configuration'), {data: original}); await user?.dispose(); await admin?.dispose(); });

 test('normal users receive only their own frozen report, independent of Discovery', async () => {
   const response = await user!.get(p('YearlyRewind/My?userId=' + loadAuth().userId));
   expect(response.status()).toBe(200);
   expect(response.headers()['cache-control']).toContain('no-store');
   const report = await response.json();
   expect(field(report, 'UserId').replaceAll('-', '')).toBe(fixture.userId.replaceAll('-', ''));
   expect(field(report, 'SnapshotId').replaceAll('-', '')).toBe(fixture.snapshotId.replaceAll('-', ''));
   expect((await admin.get(p('Client/script'))).status()).toBe(200);
 });
 test('outside the UI window the same report remains stored and personally accessible', async () => {
   expect((await admin.put(p('Configuration'), {data: {...original, YearlyRewindEnabled: true, YearlyRewindStartDate: todayMonthDay(1), YearlyRewindEndDate: todayMonthDay(1) }})).ok()).toBeTruthy();
   const status = await (await user!.get(p('YearlyRewind/My/Status'))).json();
   expect(field(status, 'TabVisible')).toBe(false);
   const report = await (await user!.get(p('YearlyRewind/My'))).json();
   expect(field(report, 'SnapshotId').replaceAll('-', '')).toBe(fixture.snapshotId.replaceAll('-', ''));
   expect(JSON.parse(readFileSync(rewindPath(fixture.userId), 'utf8')).snapshotId).toBe(fixture.snapshotId);
 });
 test('disabling blocks access without deleting the report and anonymous access is rejected', async () => {
   expect((await admin.put(p('Configuration'), {data: {...original, YearlyRewindEnabled: false}})).ok()).toBeTruthy();
   expect((await user!.get(p('YearlyRewind/My'))).status()).toBe(403);
   expect(JSON.parse(readFileSync(rewindPath(fixture.userId), 'utf8')).snapshotId).toBe(fixture.snapshotId);
   const anonymous = await request.newContext({baseURL: loadAuth().baseUrl});
   expect((await anonymous.get(p('YearlyRewind/My'))).status()).toBe(401);
   await anonymous.dispose();
 });
 test('shared playback recording works with the annual page disabled and preserves its frozen report', async () => {
   const auth = loadAuth();
   const adminFixture = seedRewind(auth.userId);
   expect((await admin.put(p('Configuration'), {data: {...original, YearlyRewindEnabled: false,
     ...visibleRewindWindow() }})).ok()).toBeTruthy();
   const items = await (await admin.get('/Items', {params: {userId: auth.userId, IncludeItemTypes: 'Movie', Recursive: 'true', Fields: 'MediaSources'}})).json();
   const movie = items.Items.find((item: any) => item.RunTimeTicks > 0 && item.RunTimeTicks < 600000000);
   expect(movie, 'the generated library has a short real video for event collection').toBeTruthy();
   const historyFile = join(rewindDataDir(), 'activity-history', auth.userId.replaceAll('-', '') + '.json');
   const previous = existsSync(historyFile) ? JSON.parse(readFileSync(historyFile, 'utf8')) : {sessions: []};
   const priorSessions = new Set(previous.sessions.map((session: any) => session.id));
   const attemptStartedAt = Date.now();
   const playback = {ItemId: movie.Id, MediaSourceId: movie.Id, PlaySessionId: randomUUID(), CanSeek: true, PlayMethod: 'DirectPlay'};
   expect((await admin.post('/Sessions/Playing', {data: {...playback, PositionTicks: 0}})).ok()).toBeTruthy();
   await new Promise(resolve => setTimeout(resolve, 900));
   expect((await admin.post('/Sessions/Playing/Progress', {data: {...playback, PositionTicks: 9000000, IsPaused: false}})).ok()).toBeTruthy();
   await new Promise(resolve => setTimeout(resolve, 200));
   expect((await admin.post('/Sessions/Playing/Stopped', {data: {...playback, PositionTicks: 10000000}})).ok()).toBeTruthy();
   await expect.poll(() => {
     if (!existsSync(historyFile)) return false;
     const history = JSON.parse(readFileSync(historyFile, 'utf8'));
     return history.sessions.some((session: any) => !priorSessions.has(session.id) && history.metadata[session.metadataKey].itemId.replaceAll('-', '') === movie.Id.replaceAll('-', '')
       && session.qualifiedAtUtc && session.intervals.some((span: any) => Date.parse(span.startUtc) >= attemptStartedAt));
   }).toBe(true);
   expect((await admin.put(p('Configuration'), {data: {...original, YearlyRewindEnabled: true, ...visibleRewindWindow()}})).ok()).toBeTruthy();
   const stored = await (await admin.get(p('YearlyRewind/My'))).json();
   expect(field(stored, 'SnapshotId').replaceAll('-', '')).toBe(adminFixture.snapshotId.replaceAll('-', ''));
 });

});
