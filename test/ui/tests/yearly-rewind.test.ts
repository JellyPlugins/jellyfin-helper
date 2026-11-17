import { afterAll, afterEach, beforeEach, expect, test, vi } from 'vitest';
import { loadYearlyRewindBundle, freshConfigPage, saveCoverage } from '../src/loader.js';

afterAll(saveCoverage);
let mount: (panel: HTMLElement) => void;
let root: HTMLElement;
let enabled = true;
let visible = true;
let id = 'snapshot-a';
let user = 'a';
let available = true;
let api: any;
let extra: Record<string, any> = {};
const report = () => ({ SnapshotId: id, PeriodStartUtc: '2025-12-15T00:00:00Z', SnapshotAtUtc: '2026-12-15T00:00:00Z', TimeZoneId: 'UTC',
 DataState: 'Ready', ActiveDays: 2, FirstItemId: '12345678123412341234123456781234',
 Items: [{ ItemId: '12345678123412341234123456781234', Name: '<script>bad</script>', ItemType: 'Movie', ActiveSeconds: 3600, Genres: ['Drama'] },
 {ItemId: 'episode', Name: 'Episode', ItemType: 'Episode', SeriesId: 'series', SeriesName: 'Series', ActiveSeconds: 600, Genres: [] }],
 Months: [{ StartUtc: '2025-12-15T00:00:00Z', EndUtc: '2026-01-15T00:00:00Z', ActiveSeconds: 4200 }], ...extra });
const flush = async () => { for (let i = 0; i < 12; i++) await Promise.resolve(); };

beforeEach(() => {
 vi.useFakeTimers();
 enabled = true; visible = true; available = true; id = 'snapshot-a'; user = 'a'; extra = {};
 root = document.createElement('section'); document.body.replaceChildren(root);
 document.documentElement.lang = 'en';
 delete (window as any).JellyfinHelperRewind;
 (window as any).JellyfinHelperTabs = { register: vi.fn((_id, _title, callback) => { mount = callback; callback(root); }), unregister: vi.fn(), updateTitle: vi.fn() };
 api = { getCurrentUserId: () => user, accessToken: () => user ? 'token-' + user : '', serverAddress: () => 'https://test.example', getUrl: (path: string) => { if (!path) throw new Error('Url name cannot be empty'); return path; },
 ajax: vi.fn(({ url }) => Promise.resolve(url.startsWith('/Items/') ? {Id: '12345678123412341234123456781234'} : url.includes('Translations') ? {} : url.includes('Status') ? { Enabled: enabled, TabVisible: visible, Available: available, SnapshotId: id, SnapshotAtUtc: id === 'snapshot-b' ? '2027-12-15T00:00:00Z' : '2026-12-15T00:00:00Z', TimeZoneId: 'UTC' } : report())) };
 (globalThis as any).ApiClient = api;
});
afterEach(() => { (window as any).JellyfinHelperRewind?.dispose(); delete (window as any).JellyfinHelperRewind; vi.useRealTimers(); });

test('renders frozen numbers and months without top-title lists or title lookups', async () => {
 loadYearlyRewindBundle(); await flush();
 expect(root.textContent).toContain('Your year in review 2026');
 expect(root.textContent).not.toContain('<script>bad</script>');
 expect(root.querySelector('script')).toBeNull();
 expect(root.querySelectorAll('.jfh-rewind-stat')).toHaveLength(5);
 expect(root.querySelectorAll('.jfh-rewind-chart')).toHaveLength(1);
 expect(root.querySelector('[role="listitem"]')?.getAttribute('aria-label')).toBe('December 2025: 1.2 h');
 expect(root.querySelector('.jfh-rewind-bar')?.getAttribute('style')).toContain('height: 100%');
 expect(root.querySelector('progress')).toBeNull();
 expect(root.querySelector('.jfh-rewind-top-titles')).toBeNull();
 expect(root.textContent).not.toContain('Your top films');
 expect(root.textContent).not.toContain('Your top series');
 expect(api.ajax.mock.calls.some(([args]) => args.url.startsWith('/Items/'))).toBe(false);
 expect(root.querySelector('.jfh-rewind-heading .jfh-rewind-period')?.textContent).toBe('(12/15/2025 – 12/14/2026)');
 expect(root.textContent).not.toContain('Your first title');
 expect(root.textContent).not.toContain('Your most active period');
 expect((window as any).JellyfinHelperTabs.register.mock.calls[0][1]).toBe('Year in review 2026');
 expect(root.querySelector('img')).toBeNull();
});

test('periodic availability probes reuse the snapshot and hide the tab without deleting data', async () => {
 loadYearlyRewindBundle(); await flush();
 await vi.advanceTimersByTimeAsync(60000); await flush();
 expect(api.ajax.mock.calls.filter(([args]) => args.url.endsWith('/My'))).toHaveLength(1);
 visible = false;
 await vi.advanceTimersByTimeAsync(60000); await flush();
 expect((window as any).JellyfinHelperTabs.unregister).toHaveBeenCalledWith('yearly-rewind');
 expect(root.childNodes).toHaveLength(0);
 expect(api.ajax.mock.calls.every(([args]) => args.type === 'GET')).toBe(true);
});

test('new annual snapshot is loaded but a disabled feature is never mounted', async () => {
 enabled = false; loadYearlyRewindBundle(); await flush();
 expect((window as any).JellyfinHelperTabs.register).not.toHaveBeenCalled();
 enabled = true; await vi.advanceTimersByTimeAsync(60000); await flush();
 id = 'snapshot-b'; await vi.advanceTimersByTimeAsync(60000); await flush();
 expect(api.ajax.mock.calls.filter(([args]) => args.url.endsWith('/My'))).toHaveLength(2);
 expect((window as any).JellyfinHelperTabs.updateTitle).toHaveBeenCalledWith('yearly-rewind', 'Year in review 2027');
});

test('logout clears immediately and rejects a late response from the previous user', async () => {
 let resolve: (value: unknown) => void;
 api.ajax.mockImplementation(({url}) => url.endsWith('/My') ? new Promise(done => { resolve = done; }) : Promise.resolve(url.includes('Status') ? {Enabled: true, TabVisible: true, Available: true} : {}));
 loadYearlyRewindBundle(); await flush();
 document.dispatchEvent(new Event('jfh:session-changing')); user = '';
 resolve!(report()); await flush();
 expect(root.childNodes).toHaveLength(0);
});

test('handles missing history and retries errors without unsafe markup', async () => {
 let fail = true;
 api.ajax.mockImplementation(({url}) => url.endsWith('/My') ? fail ? Promise.reject({status: 503}) : Promise.resolve({...report(), DataState: 'NotCollected'}) : Promise.resolve(url.includes('Status') ? {Enabled: true, TabVisible: true, Available: true} : {}));
 loadYearlyRewindBundle(); await flush();
 expect(root.textContent).toContain('Try again'); fail = false;
 root.querySelector('button')!.click(); await flush();
 expect(root.textContent).toContain('Playback was not recorded');
 expect(root.querySelector('.jfh-rewind-stats')).toBeNull();
});

test('uses supported Jellyfin document locale and keeps unknown locales in English', async () => {
 document.documentElement.lang = 'de-DE'; loadYearlyRewindBundle(); await flush();
 expect(api.ajax.mock.calls[0][0].url).toContain('lang=de');
 (window as any).JellyfinHelperRewind.dispose(); delete (window as any).JellyfinHelperRewind;
 document.documentElement.lang = 'xx'; loadYearlyRewindBundle(); await flush();
 expect(api.ajax.mock.calls.some(([args]) => args.url.includes('lang=en'))).toBe(true);
});

test('uses the translated German heading and tab title with the snapshot year', async () => {
 document.documentElement.lang = 'de';
 const fallback = api.ajax.getMockImplementation();
 api.ajax.mockImplementation(args => args.url.includes('Translations') ? Promise.resolve({'rewind.title': 'Dein Jahresrückblick', 'rewind.tabTitle': 'Jahresrückblick'}) : fallback(args));
 loadYearlyRewindBundle(); await flush();
 expect(root.querySelector('h2')?.textContent).toBe('Dein Jahresrückblick 2026 (15.12.2025 – 14.12.2026)');
 expect((window as any).JellyfinHelperTabs.register.mock.calls[0][1]).toBe('Jahresrückblick 2026');
});

function insights() {
 return {HourlySeconds: Array.from({length: 24}, (_, index) => index === 20 ? 3600 : index === 8 ? 600 : 0),
  WeekdaySeconds: [0, 0, 0, 4200, 0, 0, 0], LongestStreakDays: 2, LongestStreakStart: '2026-06-03', LongestStreakEnd: '2026-06-04'};
}
function comparison() {
 return {PeriodStartUtc: '2024-12-15T00:00:00Z', SnapshotAtUtc: '2025-12-15T00:00:00Z', TimeZoneId: 'UTC', DataState: 'Ready',
 ActiveSeconds: 7200, MovieCount: 2, EpisodeCount: 0, SeriesCount: 0, ActiveDays: 4, Genres: ['Adventure'], TopGenre: 'Adventure'};
}

test('renders two accessible pie charts using time shares and unknown genre metadata', async () => {
 loadYearlyRewindBundle(); await flush();
 expect(root.querySelectorAll('.jfh-rewind-pie')).toHaveLength(2);
 expect(root.querySelector('.jfh-rewind-mix svg')?.getAttribute('aria-label')).toContain('Films: 85.7%');
 expect(root.querySelector('.jfh-rewind-mix svg')?.getAttribute('aria-label')).toContain('Series: 14.3%');
 expect(root.querySelector('.jfh-rewind-genres')?.textContent).toContain('Distinct genres: 1');
 expect(root.querySelector('.jfh-rewind-genres')?.textContent).toContain('Unknown genre');
 expect(root.querySelector('.jfh-rewind-genres svg')?.getAttribute('aria-label')).toContain('Drama: 85.7%');
});

test('monthly genre selection uses frozen monthly data without fetching or changing the report', async () => {
 extra.Months = [{StartUtc: '2025-12-15T00:00:00Z', EndUtc: '2026-01-15T00:00:00Z', ActiveSeconds: 4200,
 Genres: [{Name: 'Drama', ActiveSeconds: 3600}, {Name: null, ActiveSeconds: 600}]},
 {StartUtc: '2026-01-15T00:00:00Z', EndUtc: '2026-02-15T00:00:00Z', ActiveSeconds: 0, Genres: []}];
 loadYearlyRewindBundle(); await flush();
 const calls = api.ajax.mock.calls.length;
 const select = root.querySelector('.jfh-rewind-select') as HTMLSelectElement;
 expect(select.options).toHaveLength(3);
 select.value = '1'; select.dispatchEvent(new Event('change'));
 expect(root.querySelector('.jfh-rewind-genres svg')).toBeNull();
 expect(root.querySelector('.jfh-rewind-genres')?.textContent).toContain('No qualifying playback');
 select.value = 'all'; select.dispatchEvent(new Event('change'));
 expect(root.querySelectorAll('.jfh-rewind-pie')).toHaveLength(2);
 expect(api.ajax.mock.calls).toHaveLength(calls);
});

test('shows local viewing habits, calendar streak dates and only numeric previous-period differences', async () => {
 extra = {Insights: insights(), Comparison: comparison(), TimeZoneId: 'Pacific/Honolulu'};
 loadYearlyRewindBundle(); await flush();
 expect(root.querySelector('.jfh-rewind-patterns')?.textContent).toContain('Thursday');
 expect(root.querySelector('.jfh-rewind-patterns')?.textContent).toContain('Evening (18:00–24:00)');
 expect(root.querySelector('.jfh-rewind-patterns')?.textContent).toContain('Time zone: Pacific/Honolulu');
 expect(root.querySelector('.jfh-rewind-streak')?.textContent).toContain('2 days');
 expect(root.querySelector('.jfh-rewind-streak')?.textContent).toContain('06/03/2026 – 06/04/2026');
 expect(root.querySelectorAll('.jfh-rewind-comparison tbody tr')).toHaveLength(5);
 expect(root.querySelector('.jfh-rewind-comparison')?.textContent).toContain('-41.7%');
 expect(root.querySelector('.jfh-rewind-comparison')?.textContent).toContain('Genres absent from the previous report: Drama');
});

test('legacy snapshots explain missing insights and comparison without fabricating historical facts', async () => {
 loadYearlyRewindBundle(); await flush();
 expect(root.querySelector('.jfh-rewind-patterns')?.textContent).toContain('not included');
 expect(root.querySelector('.jfh-rewind-streak-length')).toBeNull();
 expect(root.querySelector('.jfh-rewind-comparison')?.textContent).toContain('No report for the preceding period');
 expect(root.querySelector('.jfh-rewind-comparison table')).toBeNull();
 expect(root.querySelector('.jfh-rewind-select')).toBeNull();
});

test('zero baselines avoid infinite percentages and incomplete collection is explained', async () => {
 extra.Comparison = {...comparison(), ActiveSeconds: 0, MovieCount: 0, ActiveDays: 0, DataState: 'Partial'};
 loadYearlyRewindBundle(); await flush();
 const table = root.querySelector('.jfh-rewind-comparison table')!;
 expect(table.textContent).not.toMatch(/NaN|Infinity|%/);
 expect(root.querySelector('.jfh-rewind-comparison .jfh-rewind-notice')?.textContent).toContain('partially recorded');
});

test('single-category and empty pies remain finite and safe for hostile genre labels', async () => {
 extra.Items = [{ItemId: 'deleted', ItemType: 'Movie', Name: 'Title', ActiveSeconds: 3600, Genres: ['<img src=x onerror=alert(1)>']}];
 loadYearlyRewindBundle(); await flush();
 expect(root.querySelectorAll('.jfh-rewind-pie circle')).toHaveLength(2);
 expect(root.querySelector('.jfh-rewind-genres img')).toBeNull();
 expect(root.querySelector('.jfh-rewind-genres')?.textContent).toContain('<img src=x onerror=alert(1)>');
 (window as any).JellyfinHelperRewind.dispose(); delete (window as any).JellyfinHelperRewind;
 extra.Items = []; loadYearlyRewindBundle(); await flush();
 expect(root.querySelector('.jfh-rewind-pie')).toBeNull();
 expect(root.textContent).not.toContain('NaN');
});

test('genre pie splits duplicate tags once and groups the long tail without losing time', async () => {
 extra.Items = Array.from({length: 9}, (_, index) => ({ItemId: 'deleted-' + index, ItemType: 'Movie', Name: 'Movie ' + index, ActiveSeconds: 3600,
  Genres: ['Genre ' + index, 'Genre ' + index, ' Shared ']}));
 loadYearlyRewindBundle(); await flush();
 const genres = root.querySelector('.jfh-rewind-genres')!;
 expect(genres.textContent).toContain('Distinct genres: 10');
 expect(genres.querySelectorAll('.jfh-rewind-pie-legend li')).toHaveLength(6);
 expect(genres.querySelector('svg')?.getAttribute('aria-label')).toContain('Shared: 50% · 4.5 h');
 expect(genres.querySelector('svg')?.getAttribute('aria-label')).toContain('Other genres: 27.8% · 2.5 h');
 expect(root.querySelector('.jfh-rewind-mix svg')?.getAttribute('aria-label')).toContain('Films: 100% · 9 h');
});

test('reconstructed activity is approximate and does not invent viewing habits or active days', async () => {
 extra = {DataState: 'Partial', ReconstructedItemCount: 1, ActiveDays: 0,
  Items: [{ItemId: 'reconstructed', Name: 'Imported title', ItemType: 'Movie', ActiveSeconds: 3600, EstimatedSeconds: 3600, HasReconstructedActivity: true, Genres: ['Drama']}],
  Months: [{StartUtc: '2025-12-15T00:00:00Z', EndUtc: '2026-01-15T00:00:00Z', ActiveSeconds: 3600, EstimatedSeconds: 3600}],
  Insights: {HourlySeconds: Array(24).fill(0), WeekdaySeconds: Array(7).fill(0), LongestStreakDays: 0}};
 loadYearlyRewindBundle(); await flush();
 expect(root.textContent).toContain('Includes 1 titles reconstructed from Jellyfin data');
 expect(root.textContent).toContain('≈ 1');
 expect(root.textContent).toContain('Measured active days');
 expect(root.textContent).toContain('These details require measured playback');
 expect(root.querySelector('.jfh-rewind-estimated-bar')).not.toBeNull();
 expect(root.querySelector('[role="listitem"]')?.getAttribute('aria-label')).toContain('1 h estimated');
 expect(root.textContent).not.toContain('Some playback data is missing');
});

test('unknown reconstructed duration stays unavailable instead of showing zero hours', async () => {
 extra = {DataState: 'Partial', ReconstructedItemCount: 1, ActiveDays: 0,
  Items: [{ItemId: 'unknown', Name: 'Imported title', ItemType: 'Movie', ActiveSeconds: 0, EstimatedSeconds: 0, HasReconstructedActivity: true, Genres: []}], Months: [],
  Comparison: {PeriodStartUtc: '2024-12-15T00:00:00Z', SnapshotAtUtc: '2025-12-15T00:00:00Z', TimeZoneId: 'UTC', ActiveSeconds: 0, EstimatedSeconds: 0, ReconstructedItemCount: 1}};
 loadYearlyRewindBundle(); await flush();
 expect(root.querySelector('.jfh-rewind-stat')?.textContent).toContain('—');
 expect(root.textContent).toContain('No watch duration is available');
 expect(root.textContent).not.toContain('Imported title');
 expect(root.querySelector('.jfh-rewind-estimated-bar')).toBeNull();
 const hours = root.querySelector('.jfh-rewind-comparison-table tbody tr');
 expect(Array.from(hours!.querySelectorAll('td'), cell => cell.textContent)).toEqual(['—', '—', '—']);
});
