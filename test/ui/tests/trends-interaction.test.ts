import { afterAll, afterEach, beforeEach, describe, expect, test, vi } from 'vitest';
import { freshConfigPage, saveCoverage } from '../src/loader.js';

const ui = freshConfigPage();

afterAll(saveCoverage);

// Dense daily series spanning ~400 days so the initial level is monthly and
// zooming in crosses granularity thresholds. Growth is monotonic so dynamic
// Y-scaling and the diff panel have real deltas to render.
function denseTimeline(days = 400) {
  const dataPoints = [];
  const start = Date.UTC(2024, 0, 1);
  for (let i = 0; i < days; i++) {
    dataPoints.push({
      date: new Date(start + i * 86400000).toISOString(),
      cumulativeSize: (i + 1) * 1024 * 1024 * 1024,
      cumulativeFileCount: i + 1,
    });
  }
  return { granularity: 'daily', dataPoints, totalDirectoriesScanned: days, earliestFileDate: dataPoints[0].date };
}

// jsdom reports a zero-sized rect for every element, which collapses the
// pixel-to-time math to degenerate edges. Give the chart SVG a realistic box so
// clientXToTime / panByPixels / tooltip positioning produce observable output.
function stubSvgGeometry(container: HTMLElement) {
  const svg = container.querySelector('svg') as SVGElement;
  const rect = { left: 0, top: 0, width: 880, height: 240, right: 880, bottom: 240, x: 0, y: 0, toJSON() { return {}; } };
  svg.getBoundingClientRect = () => rect as DOMRect;
  const chart = container.querySelector('.trend-chart') as HTMLElement;
  chart.getBoundingClientRect = () => rect as DOMRect;
  return svg;
}

function routeTimeline(timeline: unknown) {
  (globalThis as any).ApiClient = {
    getUrl: (p: string) => p,
    getCurrentUserId: () => '',
    accessToken: () => 't',
    ajax: () => Promise.resolve(timeline),
  };
}

async function flush() {
  for (let i = 0; i < 10; i++) await Promise.resolve();
}

// Drives the real production entry point so attachTrendInteraction runs: loadTrendData
// fetches, renders into #trendChartContainer, then wires the interaction handlers over
// the live chartState. attachTrendInteraction is not on the test bridge, so gestures are
// driven through dispatched DOM events and effects are asserted via the mutated SVG/DOM.
async function mount(timeline: unknown) {
  routeTimeline(timeline);
  document.body.innerHTML = '<div id="trendChartContainer"></div>';
  const container = document.getElementById('trendChartContainer')!;
  ui.loadTrendData(false);
  await flush();
  const svg = stubSvgGeometry(container);
  return { container, svg };
}

// Snapshots the observable render state so a test can prove a gesture changed the window.
function snapshot(container: HTMLElement) {
  return {
    level: container.querySelector('.trend-meta-level')!.textContent,
    polyline: container.querySelector('.trend-line')!.getAttribute('points'),
    firstLabel: container.querySelector('.trend-xlabels text')?.textContent ?? '',
  };
}

beforeEach(() => {
  (window as any).requestAnimationFrame = undefined;
});

afterEach(() => {
  vi.clearAllTimers();
});

describe('attachTrendInteraction wiring', () => {
  test('a real timeline renders an interactive svg with a polyline', async () => {
    const { container } = await mount(denseTimeline());
    expect(container.querySelector('svg')).not.toBeNull();
    expect(container.querySelector('.trend-line')!.getAttribute('points')).toBeTruthy();
    expect(container.querySelector('.trend-meta-level')!.textContent).toBeTruthy();
  });
});

describe('wheel zoom narrows the window', () => {
  test('pinch zoom-in changes the rendered polyline', async () => {
    const { container, svg } = await mount(denseTimeline());
    const before = snapshot(container);
    svg.dispatchEvent(new WheelEvent('wheel', { deltaY: -120, ctrlKey: true, clientX: 440, bubbles: true, cancelable: true }));
    vi.advanceTimersByTime(32); // flush the rAF-fallback setTimeout redraw
    const after = snapshot(container);
    expect(after.polyline).not.toBe(before.polyline);
  });

  test('a tiny trackpad swipe without pinch is ignored', async () => {
    const { container, svg } = await mount(denseTimeline());
    const before = snapshot(container);
    svg.dispatchEvent(new WheelEvent('wheel', { deltaY: 10, bubbles: true, cancelable: true }));
    vi.advanceTimersByTime(32);
    expect(snapshot(container).polyline).toBe(before.polyline);
  });

  test('wheel at the full-domain edge is left to the browser (not prevented)', async () => {
    const { svg } = await mount(denseTimeline());
    // Already at full domain; a zoom-out notch hits the limit and is not consumed.
    const ev = new WheelEvent('wheel', { deltaY: 200, bubbles: true, cancelable: true });
    svg.dispatchEvent(ev);
    vi.advanceTimersByTime(32);
    expect(ev.defaultPrevented).toBe(false);
  });

  test('a zoom-in notch is consumed (preventDefault)', async () => {
    const { svg } = await mount(denseTimeline());
    const ev = new WheelEvent('wheel', { deltaY: -120, ctrlKey: true, clientX: 440, bubbles: true, cancelable: true });
    svg.dispatchEvent(ev);
    expect(ev.defaultPrevented).toBe(true);
  });

  test('deep zoom refines the granularity level toward daily', async () => {
    const { container, svg } = await mount(denseTimeline(1200));
    const levelBefore = container.querySelector('.trend-meta-level')!.textContent;
    // Several strong zoom-in notches collapse the window from months toward days.
    for (let i = 0; i < 8; i++) {
      svg.dispatchEvent(new WheelEvent('wheel', { deltaY: -400, ctrlKey: true, clientX: 440, bubbles: true, cancelable: true }));
      vi.advanceTimersByTime(32);
    }
    const levelAfter = container.querySelector('.trend-meta-level')!.textContent;
    const order = ['daily', 'weekly', 'monthly', 'yearly'];
    expect(order.indexOf(levelAfter!)).toBeLessThanOrEqual(order.indexOf(levelBefore!));
  });
});

describe('drag pan shifts the window', () => {
  test('dragging past the threshold changes the polyline', async () => {
    const { container, svg } = await mount(denseTimeline());
    // Zoom in first so there is room to pan without clamping to the domain edges.
    svg.dispatchEvent(new WheelEvent('wheel', { deltaY: -240, ctrlKey: true, clientX: 440, bubbles: true, cancelable: true }));
    vi.advanceTimersByTime(32);
    const before = snapshot(container);

    const chart = container.querySelector('.trend-chart') as HTMLElement;
    chart.dispatchEvent(new MouseEvent('mousedown', { button: 0, clientX: 400, bubbles: true }));
    // Move more than the 4px dead-zone to engage panning.
    window.dispatchEvent(new MouseEvent('mousemove', { clientX: 250, buttons: 1, bubbles: true }));
    vi.advanceTimersByTime(32);
    window.dispatchEvent(new MouseEvent('mouseup', { bubbles: true }));

    expect(snapshot(container).polyline).not.toBe(before.polyline);
  });

  test('a right-button mousedown does not start a pan', async () => {
    const { container, svg } = await mount(denseTimeline());
    svg.dispatchEvent(new WheelEvent('wheel', { deltaY: -240, ctrlKey: true, clientX: 440, bubbles: true, cancelable: true }));
    vi.advanceTimersByTime(32);
    const before = snapshot(container);
    const chart = container.querySelector('.trend-chart') as HTMLElement;
    chart.dispatchEvent(new MouseEvent('mousedown', { button: 2, clientX: 400, bubbles: true }));
    window.dispatchEvent(new MouseEvent('mousemove', { clientX: 200, buttons: 2, bubbles: true }));
    window.dispatchEvent(new MouseEvent('mouseup', { bubbles: true }));
    expect(snapshot(container).polyline).toBe(before.polyline);
  });

  test('a sub-threshold mouse move does not pan', async () => {
    const { container, svg } = await mount(denseTimeline());
    svg.dispatchEvent(new WheelEvent('wheel', { deltaY: -240, ctrlKey: true, clientX: 440, bubbles: true, cancelable: true }));
    vi.advanceTimersByTime(32);
    const before = snapshot(container);
    const chart = container.querySelector('.trend-chart') as HTMLElement;
    chart.dispatchEvent(new MouseEvent('mousedown', { button: 0, clientX: 400, bubbles: true }));
    window.dispatchEvent(new MouseEvent('mousemove', { clientX: 402, buttons: 1, bubbles: true }));
    window.dispatchEvent(new MouseEvent('mouseup', { bubbles: true }));
    expect(snapshot(container).polyline).toBe(before.polyline);
  });
});

describe('hover shows the tooltip and diff panel', () => {
  test('mousemove populates the tooltip and the then-vs-now diff', async () => {
    const { container, svg } = await mount(denseTimeline());
    // buttons:0 so this reads as a hover, not a drag.
    svg.dispatchEvent(new MouseEvent('mousemove', { clientX: 300, buttons: 0, bubbles: true }));

    const tooltip = container.querySelector('.trend-tooltip')!;
    expect(tooltip.classList.contains('visible')).toBe(true);
    expect(container.querySelector('.tt-date')!.textContent).toBeTruthy();
    expect(container.querySelector('.tt-size')!.textContent).toMatch(/B|KB|MB|GB|TB/);

    const diffPanel = container.querySelector('.trend-diff-panel')!;
    expect(diffPanel.classList.contains('visible')).toBe(true);
    // "now" is the latest point; hovering an earlier point yields a positive size delta.
    const diffSize = container.querySelector('.trend-diff-size')!;
    expect(diffSize.classList.contains('diff-up')).toBe(true);
    expect(diffSize.textContent).toContain('+');
  });

  test('mouseleave hides the tooltip and diff panel', async () => {
    const { container, svg } = await mount(denseTimeline());
    svg.dispatchEvent(new MouseEvent('mousemove', { clientX: 300, buttons: 0, bubbles: true }));
    expect(container.querySelector('.trend-tooltip')!.classList.contains('visible')).toBe(true);

    svg.dispatchEvent(new MouseEvent('mouseleave', { bubbles: true }));
    expect(container.querySelector('.trend-tooltip')!.classList.contains('visible')).toBe(false);
    expect(container.querySelector('.trend-diff-panel')!.classList.contains('visible')).toBe(false);
  });

  test('a hover while a button is held (drag) does not show the tooltip', async () => {
    const { container, svg } = await mount(denseTimeline());
    svg.dispatchEvent(new MouseEvent('mousemove', { clientX: 300, buttons: 1, bubbles: true }));
    expect(container.querySelector('.trend-tooltip')!.classList.contains('visible')).toBe(false);
  });
});

describe('touch gestures', () => {
  function touchEvent(type: string, points: Array<{ x: number; y: number }>, changed?: Array<{ x: number; y: number }>) {
    const make = (p: { x: number; y: number }) => ({ clientX: p.x, clientY: p.y });
    const ev: any = new Event(type, { bubbles: true, cancelable: true });
    ev.touches = points.map(make);
    ev.changedTouches = (changed ?? points).map(make);
    return ev;
  }

  test('a short stationary tap shows the tooltip', async () => {
    const { container } = await mount(denseTimeline());
    const chart = container.querySelector('.trend-chart') as HTMLElement;
    chart.dispatchEvent(touchEvent('touchstart', [{ x: 300, y: 100 }]));
    // Same position, end immediately: within the tap time/move thresholds.
    chart.dispatchEvent(touchEvent('touchend', [], [{ x: 300, y: 100 }]));
    expect(container.querySelector('.trend-tooltip')!.classList.contains('visible')).toBe(true);
  });

  test('a horizontal single-finger drag pans the window', async () => {
    const { container, svg } = await mount(denseTimeline());
    svg.dispatchEvent(new WheelEvent('wheel', { deltaY: -240, ctrlKey: true, clientX: 440, bubbles: true, cancelable: true }));
    vi.advanceTimersByTime(32);
    const before = snapshot(container);

    const chart = container.querySelector('.trend-chart') as HTMLElement;
    chart.dispatchEvent(touchEvent('touchstart', [{ x: 400, y: 100 }]));
    // Clearly horizontal move beyond the 8px gate engages pan mode.
    chart.dispatchEvent(touchEvent('touchmove', [{ x: 250, y: 102 }]));
    vi.advanceTimersByTime(32);
    chart.dispatchEvent(touchEvent('touchend', [], [{ x: 250, y: 102 }]));
    expect(snapshot(container).polyline).not.toBe(before.polyline);
  });

  test('a two-finger pinch spread changes the polyline', async () => {
    const { container } = await mount(denseTimeline());
    const before = snapshot(container);
    const chart = container.querySelector('.trend-chart') as HTMLElement;
    chart.dispatchEvent(touchEvent('touchstart', [{ x: 400, y: 100 }, { x: 480, y: 100 }]));
    // Fingers spread apart: dist grows, raw < 1, zoom in.
    chart.dispatchEvent(touchEvent('touchmove', [{ x: 300, y: 100 }, { x: 580, y: 100 }]));
    vi.advanceTimersByTime(32);
    expect(snapshot(container).polyline).not.toBe(before.polyline);
  });

  test('a vertical swipe scrolls and does not pan', async () => {
    const { container } = await mount(denseTimeline());
    const before = snapshot(container);
    const chart = container.querySelector('.trend-chart') as HTMLElement;
    chart.dispatchEvent(touchEvent('touchstart', [{ x: 400, y: 100 }]));
    chart.dispatchEvent(touchEvent('touchmove', [{ x: 402, y: 160 }]));
    vi.advanceTimersByTime(32);
    chart.dispatchEvent(touchEvent('touchend', [], [{ x: 402, y: 160 }]));
    expect(snapshot(container).polyline).toBe(before.polyline);
  });

  test('touchcancel hides the tooltip', async () => {
    const { container } = await mount(denseTimeline());
    const chart = container.querySelector('.trend-chart') as HTMLElement;
    chart.dispatchEvent(touchEvent('touchstart', [{ x: 300, y: 100 }]));
    chart.dispatchEvent(touchEvent('touchend', [], [{ x: 300, y: 100 }]));
    expect(container.querySelector('.trend-tooltip')!.classList.contains('visible')).toBe(true);
    chart.dispatchEvent(touchEvent('touchcancel', []));
    expect(container.querySelector('.trend-tooltip')!.classList.contains('visible')).toBe(false);
  });

  test('synthetic compat-mouse after a tap does not immediately hide the tooltip', async () => {
    const { container, svg } = await mount(denseTimeline());
    const chart = container.querySelector('.trend-chart') as HTMLElement;
    chart.dispatchEvent(touchEvent('touchstart', [{ x: 300, y: 100 }]));
    chart.dispatchEvent(touchEvent('touchend', [], [{ x: 300, y: 100 }]));
    // The browser fires a trailing synthetic mouseleave; recentlyTouched() must suppress it.
    svg.dispatchEvent(new MouseEvent('mouseleave', { bubbles: true }));
    expect(container.querySelector('.trend-tooltip')!.classList.contains('visible')).toBe(true);
  });
});

describe('redraw mutates the SVG in place', () => {
  test('zooming rebuilds grid, dots and labels without replacing the svg node', async () => {
    const { container, svg } = await mount(denseTimeline(800));
    const sameNode = container.querySelector('svg');
    svg.dispatchEvent(new WheelEvent('wheel', { deltaY: -300, ctrlKey: true, clientX: 440, bubbles: true, cancelable: true }));
    vi.advanceTimersByTime(32);
    // The SVG element is mutated, never swapped, so the reference stays identical.
    expect(container.querySelector('svg')).toBe(sameNode);
    // After a deep zoom the grid still has tick lines and the polyline has points.
    expect(container.querySelectorAll('.trend-grid line').length).toBeGreaterThan(0);
    expect(container.querySelector('.trend-line')!.getAttribute('points')).toBeTruthy();
  });

  test('hover works after a redraw because the svg node is stable', async () => {
    const { container, svg } = await mount(denseTimeline());
    svg.dispatchEvent(new WheelEvent('wheel', { deltaY: -200, ctrlKey: true, clientX: 440, bubbles: true, cancelable: true }));
    vi.advanceTimersByTime(32);
    // The same svg element still carries the hover listeners after the in-place redraw.
    svg.dispatchEvent(new MouseEvent('mousemove', { clientX: 300, buttons: 0, bubbles: true }));
    expect(container.querySelector('.trend-tooltip')!.classList.contains('visible')).toBe(true);
  });
});
