import { afterAll, afterEach, beforeEach, describe, expect, test, vi } from 'vitest';
import { freshNativeTabsHelpers, loadNativeTabsBundle, saveCoverage, UiBridge } from '../src/loader.js';

afterAll(saveCoverage);

afterEach(() => {
  vi.clearAllTimers();
});

function setHash(hash: string) {
  window.history.replaceState(null, '', hash);
}

function layout(el: Element) {
  Object.defineProperty(el, 'offsetParent', { value: document.body, configurable: true });
  return el;
}

interface TabSetup {
  tabs: HTMLElement;
  slider: HTMLElement;
  root: HTMLElement;
  header: HTMLElement;
  selectedIndex: ReturnType<typeof vi.fn>;
}

function makeSelectedIndex(tabs: HTMLElement, initial = 0) {
  let current = initial;
  const fn: any = vi.fn((next?: number) => {
    if (typeof next === 'number') current = next;
    return current;
  });
  (tabs as any).selectedIndex = fn;
  return fn;
}

// Mirrors Jellyfin's Home tab strip: buttons are selected by position, so a
// button and a panel at the same data-index must exist for a tab to render.
function setupHome(opts: { foreignIndices?: number[]; hash?: string } = {}): TabSetup {
  setHash(opts.hash ?? '#/home');
  document.body.innerHTML = '';
  const tabs = document.createElement('div');
  tabs.setAttribute('is', 'emby-tabs');
  const slider = document.createElement('div');
  slider.className = 'emby-tabs-slider';
  tabs.appendChild(slider);
  const root = document.createElement('div');
  root.className = 'tabs-container';
  tabs.appendChild(root);
  document.body.appendChild(tabs);

  const indices = opts.foreignIndices ?? [0, 1];
  indices.forEach((index, position) => {
    const btn = document.createElement('button');
    btn.className = 'emby-tab-button';
    btn.dataset.index = String(index);
    btn.textContent = 'tab' + index;
    slider.appendChild(btn);
    const panel = document.createElement('div');
    panel.className = 'tabContent pageTabContent' + (position === 0 ? ' is-active' : '');
    panel.dataset.index = String(index);
    root.appendChild(panel);
  });
  layout(root);
  layout(slider);
  const header = document.createElement('div');
  header.className = 'headerRight';
  document.body.appendChild(header);
  layout(header);
  return { tabs, slider, root, header, selectedIndex: makeSelectedIndex(tabs) };
}

function addForeignTab(setup: TabSetup, index: number) {
  const btn = document.createElement('button');
  btn.className = 'emby-tab-button';
  btn.dataset.index = String(index);
  setup.slider.appendChild(btn);
  const panel = document.createElement('div');
  panel.className = 'tabContent pageTabContent';
  panel.dataset.index = String(index);
  setup.root.appendChild(panel);
  return { btn, panel };
}

function addEntry(ui: UiBridge, id = 'a', onMount = vi.fn()) {
  ui.register(id, 'Tab ' + id, onMount, 'explore');
  return onMount;
}

describe('native tabs: mounting', () => {
  let ui: UiBridge;

  beforeEach(() => {
    ui = freshNativeTabsHelpers();
    setHash('#/home');
    document.body.innerHTML = '';
  });

  test('isOnHomePage accepts home variants and rejects other routes', () => {
    for (const hash of ['', '#/home', '#/home.html', '#/home?tab=2', '#/home.html?tab=2']) {
      setHash(hash);
      expect(ui.isOnHomePage()).toBe(true);
    }
    setHash('#/nowhere');
    expect(ui.isOnHomePage()).toBe(false);
  });

  test('register ignores an empty id and a duplicate id', () => {
    setupHome();
    ui.register('', 'x', vi.fn());
    addEntry(ui, 'a');
    addEntry(ui, 'a');
    expect(ui.findEntry('')).toBeNull();
    expect(ui.entries).toHaveLength(1);
  });

  test('ensureInjected is a no-op without entries or off the home page', () => {
    const setup = setupHome();
    ui.ensureInjected();
    expect(setup.slider.querySelectorAll('.emby-tab-button')).toHaveLength(2);
    addEntry(ui, 'a');
    setHash('#/nowhere');
    ui.ensureInjected();
    expect(document.getElementById('jfh-tab-btn-a')).toBeNull();
  });

  test('ensureInjected is a no-op without a tab strip or panel root', () => {
    document.body.innerHTML = '';
    addEntry(ui, 'a');
    ui.ensureInjected();
    const slider = document.createElement('div');
    slider.className = 'emby-tabs-slider';
    document.body.appendChild(slider);
    ui.ensureInjected();
    expect(document.getElementById('jfh-tab-btn-a')).toBeNull();
  });

  test('ensureInjected appends a button and panel at the next index and calls onMount once', () => {
    const setup = setupHome();
    const onMount = addEntry(ui, 'a');
    ui.ensureInjected();
    const btn = document.getElementById('jfh-tab-btn-a')!;
    const panel = document.getElementById('jfh-tab-panel-a')!;
    expect(btn.dataset.index).toBe('2');
    expect(panel.dataset.index).toBe('2');
    expect(panel.parentElement).toBe(setup.root);
    expect(onMount).toHaveBeenCalledTimes(1);
    expect(onMount).toHaveBeenCalledWith(panel);

    ui.ensureInjected();
    expect(setup.slider.querySelectorAll('#jfh-tab-btn-a')).toHaveLength(1);
    expect(onMount).toHaveBeenCalledTimes(1);
  });

  test('a panel moved out of the root is re-appended and deactivated', () => {
    const setup = setupHome();
    addEntry(ui, 'a');
    ui.ensureInjected();
    const panel = document.getElementById('jfh-tab-panel-a')!;
    panel.classList.add('is-active');
    document.body.appendChild(panel);
    ui.ensureInjected();
    expect(panel.parentElement).toBe(setup.root);
    expect(panel.classList.contains('is-active')).toBe(false);
  });

  test('reserved slots fill the gaps below our first index', () => {
    const setup = setupHome({ foreignIndices: [0, 1, 4] });
    addEntry(ui, 'a');
    ui.ensureInjected();
    expect(document.getElementById('jfh-tab-btn-a')!.dataset.index).toBe('5');
    expect(setup.slider.querySelector('#jfh-tab-reserved-btn-2')).not.toBeNull();
    expect(setup.slider.querySelector('#jfh-tab-reserved-btn-3')).not.toBeNull();
    expect(setup.root.querySelector('#jfh-tab-reserved-panel-2')).not.toBeNull();
    expect(setup.root.querySelector('#jfh-tab-reserved-panel-3')).not.toBeNull();
    expect(setup.root.querySelector('#jfh-tab-reserved-panel-4')).toBeNull();
  });

  test('a reserved slot is removed once an entry claims its index', () => {
    const setup = setupHome();
    const reserved = document.createElement('div');
    reserved.id = 'jfh-tab-reserved-panel-2';
    reserved.className = 'tabContent pageTabContent';
    reserved.dataset.index = '2';
    setup.root.appendChild(reserved);
    addEntry(ui, 'a');
    ui.ensureInjected();
    expect(document.getElementById('jfh-tab-reserved-panel-2')).toBeNull();
    expect(document.getElementById('jfh-tab-panel-a')!.dataset.index).toBe('2');
  });

  test('reserved slots at or beyond the last index are dropped', () => {
    const setup = setupHome();
    const stale = document.createElement('div');
    stale.id = 'jfh-tab-reserved-panel-9';
    stale.className = 'tabContent pageTabContent';
    stale.dataset.index = '9';
    setup.root.appendChild(stale);
    addEntry(ui, 'a');
    ui.ensureInjected();
    expect(document.getElementById('jfh-tab-reserved-panel-9')).toBeNull();
  });

  test('a foreign header deep link shifts our first index past it', () => {
    const setup = setupHome();
    const link = document.createElement('a');
    link.setAttribute('href', '#/home?tab=5');
    document.body.appendChild(link);
    addEntry(ui, 'a');
    ui.ensureInjected();
    expect(document.getElementById('jfh-tab-btn-a')!.dataset.index).toBe('6');
    expect(setup.slider.querySelector('#jfh-tab-reserved-btn-2')).not.toBeNull();
  });

  test('a header link inside our own tab is ignored when computing the index', () => {
    setupHome();
    const own = document.createElement('div');
    own.id = 'jfh-tab-something';
    const link = document.createElement('a');
    link.setAttribute('href', '#/home?tab=7');
    own.appendChild(link);
    document.body.appendChild(own);
    addEntry(ui, 'a');
    ui.ensureInjected();
    expect(document.getElementById('jfh-tab-btn-a')!.dataset.index).toBe('2');
  });

  test('an unparsable foreign index is ignored', () => {
    const setup = setupHome();
    const btn = document.createElement('button');
    btn.className = 'emby-tab-button';
    btn.dataset.index = 'nope';
    setup.slider.appendChild(btn);
    addEntry(ui, 'a');
    ui.ensureInjected();
    expect(document.getElementById('jfh-tab-btn-a')!.dataset.index).toBe('2');
  });

  test('a relabel moves the selection when our active tab shifts', () => {
    const setup = setupHome();
    addEntry(ui, 'a');
    ui.ensureInjected();
    const btn = document.getElementById('jfh-tab-btn-a')!;
    btn.classList.add('emby-tab-button-active');
    (setup.tabs as any).selectedTabIndex = 2;
    addForeignTab(setup, 3);
    ui.ensureInjected();
    expect(btn.dataset.index).toBe('4');
    expect((setup.tabs as any).selectedTabIndex).toBe(4);
  });

  test('buttons are reordered into index order and the strip refreshes', () => {
    const setup = setupHome();
    (setup.tabs as any).refresh = vi.fn();
    addEntry(ui, 'a');
    addEntry(ui, 'b');
    ui.ensureInjected();
    const a = document.getElementById('jfh-tab-btn-a')!;
    const b = document.getElementById('jfh-tab-btn-b')!;
    expect((setup.tabs as any).refresh).toHaveBeenCalled();
    setup.slider.appendChild(a);
    setup.slider.appendChild(b);
    setup.slider.insertBefore(b, a);
    ui.ensureInjected();
    const order = Array.from(setup.slider.querySelectorAll('.emby-tab-button'))
      .filter((el) => el.id.startsWith('jfh-tab-btn-'))
      .map((el) => el.id);
    expect(order).toEqual(['jfh-tab-btn-a', 'jfh-tab-btn-b']);
  });

  test('alignSlots leaves the order untouched when a slot has no index', () => {
    const setup = setupHome();
    addEntry(ui, 'a');
    ui.ensureInjected();
    const broken = document.createElement('button');
    broken.className = 'emby-tab-button';
    setup.slider.appendChild(broken);
    expect(() => ui.ensureInjected()).not.toThrow();
  });

  test('getTabsRoot prefers the last laid-out root', () => {
    const setup = setupHome();
    const hiddenWrap = document.createElement('div');
    hiddenWrap.hidden = true;
    const hiddenRoot = document.createElement('div');
    const hiddenPanel = document.createElement('div');
    hiddenPanel.className = 'tabContent pageTabContent';
    hiddenPanel.dataset.index = '0';
    hiddenRoot.appendChild(hiddenPanel);
    hiddenWrap.appendChild(hiddenRoot);
    document.body.appendChild(hiddenWrap);
    layout(hiddenRoot);
    expect(ui.getTabsRoot()).toBe(setup.root);
  });

  test('isActiveTabContainer rejects disconnected, hidden, inactive, and unlaid elements', () => {
    expect(ui.isActiveTabContainer(document.createElement('div'))).toBe(false);
    const hiddenParent = document.createElement('div');
    hiddenParent.hidden = true;
    const child = document.createElement('div');
    hiddenParent.appendChild(child);
    document.body.appendChild(hiddenParent);
    expect(ui.isActiveTabContainer(child)).toBe(false);
    const classHidden = document.createElement('div');
    classHidden.className = 'hide';
    const inner = document.createElement('div');
    classHidden.appendChild(inner);
    document.body.appendChild(classHidden);
    expect(ui.isActiveTabContainer(inner)).toBe(false);
    const inactive = document.createElement('div');
    inactive.className = 'tabContent';
    const marker = document.createElement('div');
    inactive.appendChild(marker);
    document.body.appendChild(inactive);
    layout(marker);
    expect(ui.isActiveTabContainer(marker)).toBe(false);
    const active = document.createElement('div');
    active.className = 'tabContent is-active';
    const marker2 = document.createElement('div');
    active.appendChild(marker2);
    document.body.appendChild(active);
    expect(ui.isActiveTabContainer(marker2)).toBe(false);
    layout(marker2);
    expect(ui.isActiveTabContainer(marker2)).toBe(true);
  });
});

describe('native tabs: deep links', () => {
  let ui: UiBridge;

  beforeEach(() => {
    ui = freshNativeTabsHelpers();
    document.body.innerHTML = '';
  });

  test('deepLinkHash keeps the route and encodes the stable id', () => {
    setHash('#/home.html');
    ui.register('a b', 'Tab', vi.fn());
    expect(ui.deepLinkHash({ index: 3, id: 'a b' })).toBe('#/home.html?tab=3&jfhTab=a%20b');
  });

  test('hashParam decodes a value and tolerates malformed input', () => {
    setHash('#/home?jfhTab=a%20b');
    expect(ui.hashParam('jfhTab')).toBe('a b');
    setHash('#/home?jfhTab=%E0%A4%A');
    expect(ui.hashParam('jfhTab')).toBe('%E0%A4%A');
    setHash('#/home');
    expect(ui.hashParam('jfhTab')).toBeNull();
  });

  test('syncDeepLink rewrites a stale index to the stable id', () => {
    const setup = setupHome({ hash: '#/home?tab=9&jfhTab=a' });
    addEntry(ui, 'a');
    ui.ensureInjected();
    expect(window.location.hash).toBe('#/home?tab=2&jfhTab=a');
    expect(setup.selectedIndex).toHaveBeenCalledWith(2);
  });

  test('syncDeepLink ignores a missing or below-range tab index', () => {
    const setup = setupHome({ hash: '#/home?tab=x' });
    addEntry(ui, 'a');
    ui.ensureInjected();
    expect(setup.selectedIndex).not.toHaveBeenCalledWith(2);
    setHash('#/home?tab=1');
    ui.ensureInjected();
    expect(setup.selectedIndex).not.toHaveBeenCalledWith(1);
  });

  test('syncDeepLink does not override a hash the user picked', () => {
    const setup = setupHome({ hash: '#/home?tab=2&jfhTab=a' });
    addEntry(ui, 'a');
    ui.ensureInjected();
    setup.selectedIndex.mockClear();
    setup.tabs.dispatchEvent(new CustomEvent('beforetabchange', { detail: { selectedTabIndex: 0 } }));
    ui.ensureInjected();
    expect(setup.selectedIndex).not.toHaveBeenCalled();
  });

  test('syncDeepLink keeps an already applied selection without re-selecting', () => {
    const setup = setupHome({ hash: '#/home?tab=2&jfhTab=a' });
    addEntry(ui, 'a');
    ui.ensureInjected();
    const panel = document.getElementById('jfh-tab-panel-a')!;
    panel.classList.add('is-active');
    document.getElementById('jfh-tab-btn-a')!.classList.add('emby-tab-button-active');
    // One more pass records the applied selection now that the active classes exist.
    ui.ensureInjected();
    setup.selectedIndex.mockClear();
    ui.ensureInjected();
    // The getter is read for the guard; the setter must not run again.
    expect(setup.selectedIndex).not.toHaveBeenCalledWith(2);
    expect(panel.classList.contains('is-active')).toBe(true);
  });

  test('syncDeepLink clears stray active classes from other tabs', () => {
    const setup = setupHome({ hash: '#/home?tab=2&jfhTab=a' });
    addEntry(ui, 'a');
    ui.ensureInjected();
    const foreignBtn = setup.slider.querySelectorAll('.emby-tab-button')[0] as HTMLElement;
    foreignBtn.classList.add('emby-tab-button-active');
    setHash('#/home?tab=2&jfhTab=a');
    ui.ensureInjected();
    expect(foreignBtn.classList.contains('emby-tab-button-active')).toBe(false);
  });

  test('syncDeepLink returns when the tab strip has no selection function', () => {
    const setup = setupHome({ hash: '#/home?tab=2&jfhTab=a' });
    delete (setup.tabs as any).selectedIndex;
    addEntry(ui, 'a');
    expect(() => ui.ensureInjected()).not.toThrow();
  });

  test('watchUserTabChanges records a differing user pick and a recent input', () => {
    const setup = setupHome({ hash: '#/home?tab=2&jfhTab=a' });
    addEntry(ui, 'a');
    ui.ensureInjected();
    setup.tabs.dispatchEvent(new CustomEvent('beforetabchange', { detail: { selectedTabIndex: 0 } }));
    setup.selectedIndex.mockClear();
    ui.ensureInjected();
    expect(setup.selectedIndex).not.toHaveBeenCalled();

    setHash('#/home?tab=2&jfhTab=a');
    // A trusted strip input marks the click as recent even when the picked index matches the URL.
    ui.noteTabStripInput({ isTrusted: true, target: setup.tabs });
    setup.tabs.dispatchEvent(new CustomEvent('beforetabchange', { detail: { selectedTabIndex: 2 } }));
    setup.selectedIndex.mockClear();
    ui.ensureInjected();
    expect(setup.selectedIndex).not.toHaveBeenCalled();
  });

  test('watchUserTabChanges is idempotent and tolerates no strip', () => {
    const setup = setupHome();
    expect(() => ui.watchUserTabChanges(null)).not.toThrow();
    ui.watchUserTabChanges(setup.tabs);
    ui.watchUserTabChanges(setup.tabs);
    expect(() => setup.tabs.dispatchEvent(new CustomEvent('tabchange', { detail: { selectedTabIndex: 0 } }))).not.toThrow();
  });

  test('noteTabStripInput ignores untrusted and non-tab events', () => {
    const setup = setupHome();
    setup.tabs.dispatchEvent(new Event('pointerdown', { bubbles: true }));
    expect(() => ui.noteTabStripInput({ isTrusted: false, target: setup.tabs })).not.toThrow();
    expect(() => ui.noteTabStripInput({ isTrusted: true, target: document.body })).not.toThrow();
  });

  test('clearStraySelection guards on the root, index, selection, and element identity', () => {
    const setup = setupHome({ hash: '#/home?tab=2&jfhTab=a' });
    addEntry(ui, 'a');
    ui.ensureInjected();
    const btn = document.getElementById('jfh-tab-btn-a')!;
    const panel = document.getElementById('jfh-tab-panel-a')!;

    expect(() => ui.clearStraySelection(setup.tabs, 2)).not.toThrow();
    setHash('#/nowhere');
    ui.clearStraySelection(setup.tabs, 2);

    setHash('#/home');
    setup.selectedIndex = makeSelectedIndex(setup.tabs, 0);
    ui.clearStraySelection(setup.tabs, Number.NaN);
    ui.clearStraySelection(setup.tabs, 9);

    setup.selectedIndex = makeSelectedIndex(setup.tabs, 2);
    btn.classList.add('emby-tab-button-active');
    panel.classList.add('is-active');
    const strayPanel = setup.root.querySelectorAll('.tabContent')[0] as HTMLElement;
    const strayBtn = setup.slider.querySelectorAll('.emby-tab-button')[0] as HTMLElement;
    strayPanel.classList.add('is-active');
    strayBtn.classList.add('emby-tab-button-active');
    ui.clearStraySelection(setup.tabs, 2);
    expect(strayPanel.classList.contains('is-active')).toBe(false);
    expect(strayBtn.classList.contains('emby-tab-button-active')).toBe(false);
  });

  test('clearStraySelection leaves an index mismatch alone', () => {
    const setup = setupHome({ hash: '#/home?tab=2&jfhTab=a' });
    addEntry(ui, 'a');
    ui.ensureInjected();
    setup.selectedIndex = makeSelectedIndex(setup.tabs, 2);
    (setup.root.querySelectorAll('.tabContent')[2] as HTMLElement).dataset.index = '5';
    const stray = setup.root.querySelectorAll('.tabContent')[0] as HTMLElement;
    stray.classList.add('is-active');
    ui.clearStraySelection(setup.tabs, 2);
    expect(stray.classList.contains('is-active')).toBe(true);
  });
});

describe('native tabs: header fallback', () => {
  let ui: UiBridge;

  beforeEach(() => {
    ui = freshNativeTabsHelpers();
    document.body.innerHTML = '';
  });

  test('an invisible tab button gets a header link that navigates to the deep link', () => {
    const setup = setupHome();
    addEntry(ui, 'a');
    ui.ensureInjected();
    const link = document.getElementById('jfh-tab-link-a') as HTMLElement;
    expect(link).not.toBeNull();
    expect(link.parentElement!.id).toBe('jfh-native-tabs-group');
    expect(setup.header.querySelector('#jfh-native-tabs-separator')).not.toBeNull();
    link.click();
    expect(window.location.hash).toBe('#/home?tab=2&jfhTab=a');
  });

  test('a visible tab button removes a stale header link', () => {
    const setup = setupHome();
    addEntry(ui, 'a');
    ui.ensureInjected();
    const btn = document.getElementById('jfh-tab-btn-a')!;
    layout(btn);
    vi.advanceTimersByTime(1100);
    ui.ensureInjected();
    expect(document.getElementById('jfh-tab-link-a')).toBeNull();
    expect(document.getElementById('jfh-native-tabs-group')).toBeNull();
    expect(setup.header.children).toHaveLength(0);
  });

  test('an existing header link is not duplicated', () => {
    setupHome();
    addEntry(ui, 'a');
    ui.ensureInjected();
    ui.ensureInjected();
    expect(document.querySelectorAll('#jfh-tab-link-a')).toHaveLength(1);
  });

  test('no link is added without a header tray', () => {
    setupHome().header.remove();
    addEntry(ui, 'a');
    ui.ensureInjected();
    expect(document.getElementById('jfh-tab-link-a')).toBeNull();
  });

  test('the MUI toolbar tray is reused when it is not a button', () => {
    document.body.innerHTML = '';
    const toolbar = document.createElement('div');
    toolbar.className = 'MuiToolbar-root';
    const tray = document.createElement('div');
    tray.className = 'MuiBox-root';
    const userMenuBox = document.createElement('div');
    userMenuBox.className = 'MuiBox-root';
    const userButton = document.createElement('button');
    userButton.setAttribute('aria-controls', 'app-user-menu');
    userMenuBox.appendChild(userButton);
    toolbar.appendChild(tray);
    toolbar.appendChild(userMenuBox);
    document.body.appendChild(toolbar);
    setupHomeWithToolbar(toolbar);
    addEntry(ui, 'a');
    ui.ensureInjected();
    expect(tray.querySelector('#jfh-tab-link-a')).not.toBeNull();
  });

  test('a button tray triggers a dedicated header container before the user menu', () => {
    document.body.innerHTML = '';
    const toolbar = document.createElement('div');
    toolbar.className = 'MuiToolbar-root';
    const trayButton = document.createElement('button');
    const userMenuBox = document.createElement('div');
    userMenuBox.className = 'MuiBox-root';
    const userButton = document.createElement('button');
    userButton.setAttribute('aria-controls', 'app-user-menu');
    userMenuBox.appendChild(userButton);
    toolbar.appendChild(trayButton);
    toolbar.appendChild(userMenuBox);
    document.body.appendChild(toolbar);
    setupHomeWithToolbar(toolbar);
    addEntry(ui, 'a');
    ui.ensureInjected();
    const container = toolbar.querySelector('[data-jfh-header-fallback]') as HTMLElement;
    expect(container).not.toBeNull();
    expect(container.nextElementSibling).toBe(userMenuBox);
    expect(container.querySelector('#jfh-tab-link-a')).not.toBeNull();
  });

  test('a toolbar without a user menu appends the container', () => {
    document.body.innerHTML = '';
    const toolbar = document.createElement('div');
    toolbar.className = 'MuiToolbar-root';
    const appBar = document.createElement('div');
    appBar.className = 'MuiAppBar-root';
    appBar.appendChild(toolbar);
    document.body.appendChild(appBar);
    setupHomeWithToolbar(toolbar);
    addEntry(ui, 'a');
    ui.ensureInjected();
    const container = toolbar.querySelector('[data-jfh-header-fallback]') as HTMLElement;
    expect(container).not.toBeNull();
    expect(container.querySelector('#jfh-tab-link-a')).not.toBeNull();
  });
});

// Builds the tab strip without the legacy header so the MUI tray path runs.
function setupHomeWithToolbar(toolbar: HTMLElement) {
  setHash('#/home');
  const tabs = document.createElement('div');
  tabs.setAttribute('is', 'emby-tabs');
  const slider = document.createElement('div');
  slider.className = 'emby-tabs-slider';
  tabs.appendChild(slider);
  const root = document.createElement('div');
  const panel = document.createElement('div');
  panel.className = 'tabContent pageTabContent';
  panel.dataset.index = '0';
  root.appendChild(panel);
  tabs.appendChild(root);
  toolbar.parentElement!.appendChild(tabs);
  layout(root);
  layout(slider);
  layout(toolbar);
}

describe('native tabs: focus and unregister', () => {
  let ui: UiBridge;

  beforeEach(() => {
    ui = freshNativeTabsHelpers();
    document.body.innerHTML = '';
  });

  test('focus returns false for an unknown id', () => {
    setupHome();
    expect(ui.focus('missing')).toBe(false);
  });

  test('focus off home stores a pending focus applied once the strip mounts', () => {
    const setup = setupHome({ hash: '#/nowhere' });
    const onMount = addEntry(ui, 'a');
    const clickSpy = vi.spyOn(HTMLButtonElement.prototype, 'click');
    expect(ui.focus('a')).toBe(false);
    expect(onMount).not.toHaveBeenCalled();
    setHash('#/home');
    ui.ensureInjected();
    expect(onMount).toHaveBeenCalledTimes(1);
    // The pending focus clicked the freshly mounted button once.
    expect(clickSpy).toHaveBeenCalledTimes(1);
    expect(setup.slider.querySelector('#jfh-tab-btn-a')).not.toBeNull();
    clickSpy.mockRestore();
  });

  test('focus on home injects and clicks the tab immediately', () => {
    setupHome();
    addEntry(ui, 'a');
    expect(ui.focus('a')).toBe(true);
    const btn = document.getElementById('jfh-tab-btn-a') as HTMLElement;
    const clicked = vi.fn();
    btn.addEventListener('click', clicked);
    expect(ui.focus('a')).toBe(true);
    expect(clicked).toHaveBeenCalledTimes(1);
  });

  test('applyPendingFocus tolerates an entry without an index or a missing button', () => {
    setupHome();
    addEntry(ui, 'a');
    setHash('#/nowhere');
    ui.focus('a');
    ui.applyPendingFocus();
    setHash('#/home');
    ui.ensureInjected();
    document.getElementById('jfh-tab-btn-a')!.remove();
    setHash('#/nowhere');
    ui.focus('a');
    setHash('#/home');
    expect(() => ui.applyPendingFocus()).not.toThrow();
  });

  test('unregister removes the button, panel, header link, and group', () => {
    setupHome();
    addEntry(ui, 'a');
    ui.ensureInjected();
    expect(document.getElementById('jfh-tab-link-a')).not.toBeNull();
    ui.unregister('a');
    expect(document.getElementById('jfh-tab-btn-a')).toBeNull();
    expect(document.getElementById('jfh-tab-panel-a')).toBeNull();
    expect(document.getElementById('jfh-tab-link-a')).toBeNull();
    expect(document.getElementById('jfh-native-tabs-group')).toBeNull();
    expect(ui.findEntry('a')).toBeNull();
  });

  test('unregister clears a pending focus for the removed entry', () => {
    setupHome({ hash: '#/nowhere' });
    addEntry(ui, 'a');
    ui.focus('a');
    ui.unregister('a');
    setHash('#/home');
    expect(() => ui.applyPendingFocus()).not.toThrow();
  });

  test('unregister tolerates an unknown id', () => {
    setupHome();
    expect(() => ui.unregister('missing')).not.toThrow();
  });
});

describe('native tabs: shipped bundle', () => {
  afterEach(() => {
    delete (globalThis as any).JellyfinHelperTabs;
  });

  test('the bundle exposes the registry surface', () => {
    delete (globalThis as any).JellyfinHelperTabs;
    loadNativeTabsBundle();
    const registry = (globalThis as any).JellyfinHelperTabs;
    expect(typeof registry.register).toBe('function');
    expect(typeof registry.unregister).toBe('function');
    expect(typeof registry.focus).toBe('function');
  });

  test('a second load never replaces an existing registry', () => {
    const sentinel = { register: vi.fn(), unregister: vi.fn(), focus: vi.fn() };
    (globalThis as any).JellyfinHelperTabs = sentinel;
    loadNativeTabsBundle();
    expect((globalThis as any).JellyfinHelperTabs).toBe(sentinel);
  });
});

test('init wires observers and listeners without throwing', () => {
  const tabs = freshNativeTabsHelpers();
  expect(() => tabs.init()).not.toThrow();
});
