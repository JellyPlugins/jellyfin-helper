import { afterAll, afterEach, beforeEach, describe, expect, test, vi } from 'vitest';
import { ajaxRouter } from '../src/apiStub.js';
import { freshConfigPage, freshDiscoveryHelpers, saveCoverage, type UiBridge } from '../src/loader.js';

let page: UiBridge;
let ui: any;

beforeEach(() => {
  document.body.innerHTML = '<main class="jfh-ui-surface"><button id="opener">Open</button></main>';
  page = freshConfigPage();
  ui = (window as any).JellyfinHelperUi;
  document.dispatchEvent(new Event('pointerdown', { bubbles: true }));
  vi.stubGlobal('matchMedia', () => ({ matches: false }));
});

afterEach(() => {
  for (const overlay of document.querySelectorAll('[aria-modal="true"]')) {
    ui.closeDialog(overlay.parentElement, false);
  }
  vi.unstubAllGlobals();
  document.documentElement.lang = '';
});
afterAll(saveCoverage);

function dialog(id = 'dialog') {
  const opener = document.getElementById('opener') as HTMLButtonElement;
  opener.focus();
  const modal = page.createDialogOverlay(id, 'Confirm', '#00a4dc', 'Choose an action');
  const cancel = vi.fn(() => page.removeDialogById(id));
  modal.btnRow.appendChild(page.createDialogBtn('Cancel', 'cancel', cancel));
  modal.btnRow.appendChild(page.createDialogBtn('Delete', 'danger', vi.fn()));
  ui.openDialog(modal.overlay, modal.dialog);
  return { ...modal, opener, cancel };
}

describe('modal interaction', () => {
  test('cancellation can reenable the opener before deferred focus restoration', async () => {
    const opener = document.getElementById('opener') as HTMLButtonElement;
    opener.disabled = true;
    const modal = page.createDialogOverlay('disabled-dialog', 'Confirm', '#00a4dc', 'Choose');
    modal.btnRow.appendChild(page.createDialogBtn('Cancel', 'cancel', () => {
      ui.closeDialog(modal.overlay);
      opener.disabled = false;
    }));
    ui.openDialog(modal.overlay, modal.dialog, opener);
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
    await vi.advanceTimersByTimeAsync(0);
    expect(document.activeElement).toBe(opener);
  });
  test('pointer activation restores a button even when the browser did not focus it', () => {
    const opener = document.getElementById('opener')!;
    opener.dispatchEvent(new Event('pointerdown', { bubbles: true }));
    const modal = page.createDialogOverlay('pointer-dialog', 'Confirm', '#00a4dc', 'Choose');
    modal.btnRow.appendChild(page.createDialogBtn('Cancel', 'cancel', () => page.removeDialogById('pointer-dialog')));
    ui.openDialog(modal.overlay, modal.dialog);
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
    expect(document.activeElement).toBe(opener);
  });
  test('has a title, traps focus and cancels safely with Escape', () => {
    const modal = dialog();
    expect(modal.dialog.getAttribute('aria-labelledby')).toBe('dialog-title');
    expect(modal.dialog.getAttribute('role')).toBe('dialog');
    const buttons = modal.dialog.querySelectorAll('button');
    expect(document.activeElement).toBe(buttons[0]);
    buttons[1].focus();
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', bubbles: true, cancelable: true }));
    expect(document.activeElement).toBe(buttons[0]);
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Tab', shiftKey: true, bubbles: true, cancelable: true }));
    expect(document.activeElement).toBe(buttons[1]);
    modal.opener.focus();
    expect(modal.dialog.contains(document.activeElement)).toBe(true);
    document.dispatchEvent(new KeyboardEvent('keydown', { key: 'Escape', bubbles: true, cancelable: true }));
    expect(modal.cancel).toHaveBeenCalledOnce();
    expect(modal.overlay.isConnected).toBe(false);
    expect(document.activeElement).toBe(modal.opener);
    expect(modal.opener.closest('[inert]')).toBeNull();
  });

  test('restores existing inert and scroll state rather than resetting host state', () => {
    const aside = document.createElement('aside');
    aside.setAttribute('inert', '');
    document.body.appendChild(aside);
    document.body.style.overflow = 'clip';
    const modal = dialog();
    expect(document.body.style.overflow).toBe('hidden');
    ui.closeDialog(modal.overlay);
    expect(aside.hasAttribute('inert')).toBe(true);
    expect(document.body.style.overflow).toBe('clip');
    document.body.style.overflow = '';
  });

  test('a nested dialog returns focus to the still-open parent', () => {
    const first = dialog('first');
    const trigger = first.dialog.querySelector('button');
    const second = page.createDialogOverlay('second', 'Second', '#00a4dc', 'Nested');
    second.btnRow.appendChild(page.createDialogBtn('Cancel', 'cancel', () => ui.closeDialog(second.overlay)));
    ui.openDialog(second.overlay, second.dialog, trigger);
    expect(first.overlay.hasAttribute('inert')).toBe(true);
    ui.closeDialog(second.overlay);
    expect(first.overlay.hasAttribute('inert')).toBe(false);
    expect(document.activeElement).toBe(trigger);
    ui.closeDialog(first.overlay);
  });
});

describe('tabs and collapsed content', () => {
  test('animated options preserve immediate state when native animation is unavailable', () => {
    const trigger = document.getElementById('opener')!;
    const body = document.createElement('div');
    document.querySelector('main')!.appendChild(body);
    const opened = vi.fn(() => body.classList.add('open'));
    const closed = vi.fn();
    ui.setExpanded(body, true, trigger, { update: opened });
    expect(opened).toHaveBeenCalledOnce();
    expect(body.hidden).toBe(false);
    expect(ui.isExpanded(body)).toBe(true);
    ui.setExpanded(body, false, trigger, { update: () => body.classList.remove('open'), onClosed: closed });
    expect(body.hidden).toBe(true);
    expect(body.hasAttribute('inert')).toBe(true);
    expect(ui.isExpanded(body)).toBe(false);
    expect(closed).toHaveBeenCalledOnce();
  });
  test.each(['ltr', 'rtl'])('keyboard navigation wraps and respects %s direction without activating tabs', direction => {
    const list = document.createElement('div');
    list.style.direction = direction;
    list.innerHTML = '<button>One</button><button hidden>Hidden</button><button disabled>Disabled</button><button>Two</button><button>Three</button>';
    document.querySelector('main')!.appendChild(list);
    const buttons = list.querySelectorAll('button');
    ui.syncTabs(list, 'button', buttons[0], () => null);
    buttons[0].focus();
    const press = (key: string) => document.activeElement!.dispatchEvent(new KeyboardEvent('keydown', { key, bubbles: true, cancelable: true }));
    press('ArrowRight');
    expect(document.activeElement).toBe(direction === 'ltr' ? buttons[3] : buttons[4]);
    press('End');
    expect(document.activeElement).toBe(buttons[4]);
    press('ArrowRight');
    expect(document.activeElement).toBe(direction === 'ltr' ? buttons[0] : buttons[3]);
    press('Home');
    expect(document.activeElement).toBe(buttons[0]);
    press('ArrowLeft');
    expect(document.activeElement).toBe(direction === 'ltr' ? buttons[4] : buttons[3]);
    expect(buttons[0].getAttribute('aria-selected')).toBe('true');
    expect(buttons[3].getAttribute('aria-selected')).toBe('false');
    expect(buttons[4].getAttribute('aria-selected')).toBe('false');
  });
  test('formats large counts and old timestamps using the document language', () => {
    document.documentElement.lang = 'de';
    expect(page.formatCount(1234567)).toBe('1.234.567');
    expect(page.formatCount(Number.NaN)).toBe('0');
    expect(page.formatTimeAgo('invalid')).toBe('');
    const then = new Date(Date.now() - 8 * 86400000);
    expect(page.formatTimeAgo(then.toISOString())).toBe(new Intl.DateTimeFormat('de', { dateStyle: 'medium' }).format(then));
    document.documentElement.lang = '';
  });
  test('closed content is hidden and inert, and closing returns an internal focus', () => {
    const trigger = document.getElementById('opener')!;
    const body = document.createElement('div');
    body.innerHTML = '<input value="setting">';
    document.querySelector('main')!.appendChild(body);
    body.querySelector('input')!.focus();
    ui.setExpanded(body, false, trigger);
    expect(body.hidden).toBe(true);
    expect(body.hasAttribute('inert')).toBe(true);
    expect(document.activeElement).toBe(trigger);
    ui.setExpanded(body, true, trigger);
    expect(body.hidden).toBe(false);
    expect(body.hasAttribute('inert')).toBe(false);
    expect(trigger.getAttribute('aria-expanded')).toBe('true');
  });

  test('dashboard tabs expose selection and support manual keyboard activation', () => {
    document.body.innerHTML = '<main class="jfh-ui-surface">' + page.renderShell() + '</main>';
    page.initTabs();
    const first = document.querySelector('.tab-btn.active') as HTMLButtonElement;
    const next = document.querySelector('.tab-btn[data-tab="codecs"]') as HTMLButtonElement;
    first.focus();
    first.dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowRight', bubbles: true, cancelable: true }));
    expect(document.activeElement).toBe(next);
    expect(first.getAttribute('aria-selected')).toBe('true');
    next.click();
    expect(next.getAttribute('aria-selected')).toBe('true');
    expect(first.tabIndex).toBe(-1);
    expect(document.getElementById('tab-codecs')!.hidden).toBe(false);
    expect(document.getElementById('tab-overview')!.hidden).toBe(true);
    expect(document.getElementById('tab-codecs')!.getAttribute('aria-labelledby')).toBe(next.id);
  });

  test('tab handlers remain singular when initialized repeatedly', () => {
    document.body.innerHTML = page.renderShell();
    const next = document.querySelector('.tab-btn[data-tab="codecs"]') as HTMLButtonElement;
    const add = vi.spyOn(next, 'addEventListener');
    page.initTabs();
    page.initTabs();
    expect(add.mock.calls.filter(([name]) => name === 'click')).toHaveLength(1);
  });
});

describe('Discovery rendering', () => {
  function discovery() {
    ajaxRouter({});
    (globalThis as any).ApiClient.getCurrentUserId = () => 'u1';
    return freshDiscoveryHelpers();
  }
  const rec = (id: number, title = 'Movie') => ({ TmdbId: id, Title: title, MediaType: 'movie', PosterPath: '/poster.jpg', Score: 0.8 });

  test('ranking updates do not move a card while its action has focus', () => {
    const discoveryUi = discovery();
    const host = document.createElement('div');
    document.body.appendChild(host);
    discoveryUi.renderCards(host, { Recommendations: [rec(1), rec(2)] });
    const card = host.querySelector('.jfh-discovery-card')!;
    const action = card.querySelector('[data-jfh-action="request"]') as HTMLButtonElement;
    action.focus();
    discoveryUi.renderCards(host, { Recommendations: [rec(2), rec(1)] });
    expect(host.querySelector('.jfh-discovery-card')).toBe(card);
    expect(document.activeElement).toBe(action);
  });

  test('an unavailable Trakt source retires its tab controls', () => {
    const discoveryUi = discovery();
    const host = document.createElement('div');
    host.innerHTML = '<div class="jfh-discovery-tabs"><button>Unavailable source</button></div>';
    document.body.appendChild(host);
    discoveryUi.setCachedDiscoveryResult({ Recommendations: [rec(1)] });
    discoveryUi.renderShell(host, false);
    expect(host.querySelector('.jfh-discovery-tabs')).toBeNull();
    expect(host.querySelector('.jfh-discovery-card')).not.toBeNull();
  });

  test('a broken poster keeps its details control and replaces only the image', () => {
    const discoveryUi = discovery();
    const host = document.createElement('div');
    document.body.appendChild(host);
    discoveryUi.renderCards(host, { Recommendations: [rec(1)] });
    const details = host.querySelector('.jfh-discovery-details');
    host.querySelector('img')!.dispatchEvent(new Event('error'));
    expect(host.querySelector('img')).toBeNull();
    expect(host.querySelector('.jfh-discovery-no-poster')).not.toBeNull();
    expect(host.querySelector('.jfh-discovery-details')).toBe(details);
  });

  test('unchanged cards retain their DOM, focus and details state', () => {
    const discoveryUi = discovery();
    const host = document.createElement('div');
    document.body.appendChild(host);
    const payload = { Recommendations: [rec(1), rec(2)] };
    discoveryUi.renderCards(host, payload);
    const card = host.querySelector('.jfh-discovery-card')!;
    const button = card.querySelector('.jfh-discovery-details') as HTMLButtonElement;
    button.click();
    button.focus();
    discoveryUi.renderCards(host, structuredClone(payload));
    expect(host.querySelector('.jfh-discovery-card')).toBe(card);
    expect(document.activeElement).toBe(button);
    expect(button.getAttribute('aria-expanded')).toBe('true');
    expect(card.querySelector('.jfh-discovery-flip-back')!.hasAttribute('inert')).toBe(false);
    expect(card.querySelector('a')!.getAttribute('href')).toContain('themoviedb.org/movie/1');
  });

  test('updates one changed card without rebuilding its neighbour', () => {
    const discoveryUi = discovery();
    const host = document.createElement('div');
    document.body.appendChild(host);
    discoveryUi.renderCards(host, { Recommendations: [rec(1), rec(2)] });
    const cards = host.querySelectorAll('.jfh-discovery-card');
    const request = cards[0].querySelector('[data-jfh-action="request"]') as HTMLButtonElement;
    request.focus();
    discoveryUi.renderCards(host, { Recommendations: [rec(1, 'Updated title'), rec(2)] });
    expect(host.querySelectorAll('.jfh-discovery-card')[1]).toBe(cards[1]);
    expect(cards[0].textContent).toContain('Updated title');
    expect((document.activeElement as HTMLElement).dataset.jfhAction).toBe('request');
  });

  test('an open profile picker retains its trigger during refresh', () => {
    const discoveryUi = discovery();
    const host = document.createElement('div');
    document.body.appendChild(host);
    discoveryUi.renderCards(host, { Recommendations: [rec(1)] });
    const button = host.querySelector('[data-jfh-action="request"]')!;
    discoveryUi.showProfilePopup(1, 'movie', button, [{ ServerId: 1, ProfileId: 2, ProfileName: 'HD' }]);
    discoveryUi.renderCards(host, { Recommendations: [rec(1, 'New title')] });
    expect(host.querySelector('[data-jfh-action="request"]')).toBe(button);
    discoveryUi.closeDiscoveryPopup(button);
  });

  test('card removal moves focus to the next card without motion when requested', async () => {
    const discoveryUi = discovery();
    const host = document.createElement('div');
    document.body.appendChild(host);
    discoveryUi.renderCards(host, { Recommendations: [rec(1), rec(2)] });
    const cards = host.querySelectorAll('.jfh-discovery-card');
    (cards[0].querySelector('button') as HTMLButtonElement).focus();
    vi.stubGlobal('matchMedia', () => ({ matches: true }));
    const done = vi.fn();
    ui.removeCard(cards[0], done);
    await vi.advanceTimersByTimeAsync(0);
    expect(cards[0].isConnected).toBe(false);
    expect(cards[1].contains(document.activeElement)).toBe(true);
    expect(done).toHaveBeenCalledOnce();
  });
});

describe('toast interaction', () => {
  test('messages stack and remain readable while hovered or focused', async () => {
    const first = ui.showToast('First', { className: 'jfh-discovery-toast', duration: 1000, closeLabel: 'Close' });
    const second = ui.showToast('Second', { className: 'jfh-discovery-toast', duration: 1000, closeLabel: 'Close' });
    expect(first.parentElement).toBe(second.parentElement);
    first.dispatchEvent(new MouseEvent('mouseenter'));
    await vi.advanceTimersByTimeAsync(1300);
    expect(first.isConnected).toBe(true);
    expect(second.isConnected).toBe(false);
    first.querySelector('button').focus();
    first.dispatchEvent(new MouseEvent('mouseleave'));
    await vi.advanceTimersByTimeAsync(1500);
    expect(first.isConnected).toBe(true);
    first.querySelector('button').click();
    await vi.advanceTimersByTimeAsync(200);
    expect(first.isConnected).toBe(false);
  });

  test('a hidden page pauses the remaining toast lifetime', async () => {
    let hidden = false;
    const hiddenGetter = vi.spyOn(document, 'hidden', 'get').mockImplementation(() => hidden);
    const toast = ui.showToast('Error', { className: 'jfh-discovery-toast', duration: 1000, closeLabel: 'Close' });
    await vi.advanceTimersByTimeAsync(400);
    hidden = true;
    document.dispatchEvent(new Event('visibilitychange'));
    await vi.advanceTimersByTimeAsync(5000);
    expect(toast.isConnected).toBe(true);
    hidden = false;
    document.dispatchEvent(new Event('visibilitychange'));
    await vi.advanceTimersByTimeAsync(599);
    expect(toast.classList.contains('jfh-discovery-toast-visible')).toBe(true);
    await vi.advanceTimersByTimeAsync(201);
    expect(toast.isConnected).toBe(false);
    hiddenGetter.mockRestore();
  });
});
