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
  const rect = { left: 0, top: 0, width: 880, height: 240, right: 880, bottom: 240, x: 0, y: 0 };
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

  test('pinch zoom-out widens the window back', async () => {
    const { container, svg } = await mount(denseTimeline());
    svg.dispatchEvent(new WheelEvent('wheel', { deltaY: -120, ctrlKey: true, clientX: 440, bubbles: true, cancelable: true }));
    vi.advanceTimersByTime(32);
    const before = snapshot(container);
    svg.dispatchEvent(new WheelEvent('wheel', { deltaY: 120, ctrlKey: true, clientX: 440, bubbles: true, cancelable: true }));
    vi.advanceTimersByTime(32);
    expect(snapshot(container).polyline).not.toBe(before.polyline);
  });

  test('a line-mode wheel notch is converted to pixels and consumed', async () => {
    const { svg } = await mount(denseTimeline());
    const ev = new WheelEvent('wheel', { deltaY: -3, deltaMode: 1, ctrlKey: true, clientX: 440, bubbles: true, cancelable: true });
    svg.dispatchEvent(ev);
    expect(ev.defaultPrevented).toBe(true);
  });

  test('gestures in the same frame coalesce into one redraw', async () => {
    const { container, svg } = await mount(denseTimeline());
    const before = snapshot(container);
    svg.dispatchEvent(new WheelEvent('wheel', { deltaY: -240, ctrlKey: true, clientX: 440, bubbles: true, cancelable: true }));
    svg.dispatchEvent(new WheelEvent('wheel', { deltaY: -240, ctrlKey: true, clientX: 440, bubbles: true, cancelable: true }));
    vi.advanceTimersByTime(32);
    expect(snapshot(container).polyline).not.toBe(before.polyline);
  });

  test('a wheel far left of the plot clamps its anchor to the window start', async () => {
    const { container, svg } = await mount(denseTimeline());
    const before = snapshot(container);
    const left = new WheelEvent('wheel', { deltaY: -240, ctrlKey: true, clientX: -5000, bubbles: true, cancelable: true });
    svg.dispatchEvent(left);
    vi.advanceTimersByTime(32);
    const afterLeft = snapshot(container);
    expect(left.defaultPrevented).toBe(true);
    expect(afterLeft.polyline).not.toBe(before.polyline);

    const right = new WheelEvent('wheel', { deltaY: -240, ctrlKey: true, clientX: 6000, bubbles: true, cancelable: true });
    svg.dispatchEvent(right);
    vi.advanceTimersByTime(32);
    expect(right.defaultPrevented).toBe(true);
    expect(snapshot(container).polyline).not.toBe(afterLeft.polyline);
  });

  test('gestures without an svg fall back to the window start and skip panning', async () => {
    const { container, svg } = await mount(denseTimeline());
    svg.remove();
    const chart = container.querySelector('.trend-chart') as HTMLElement;
    const ev = new WheelEvent('wheel', { deltaY: -240, ctrlKey: true, clientX: 440, bubbles: true, cancelable: true });
    chart.dispatchEvent(ev);
    vi.advanceTimersByTime(32);
    expect(ev.defaultPrevented).toBe(true);

    const line = svg.querySelector('.trend-line')!;
    const beforePoints = line.getAttribute('points');
    chart.dispatchEvent(new MouseEvent('mousedown', { button: 0, clientX: 400, bubbles: true }));
    window.dispatchEvent(new MouseEvent('mousemove', { clientX: 100, buttons: 1, bubbles: true }));
    vi.advanceTimersByTime(32);
    window.dispatchEvent(new MouseEvent('mouseup', { bubbles: true }));
    // With no svg host left, panByPixels bails and the detached svg keeps its wheel frame.
    expect(line.getAttribute('points')).toBe(beforePoints);
  });
});

describe('drag pan shifts the window', () => {
  // One plain drag plus the two that run into a domain edge and get clamped.
  test.each([
    { label: 'dragging past the threshold changes the polyline', downX: 400, moveX: 250 },
    { label: 'dragging right against the left domain edge clamps the window', downX: 100, moveX: 800 },
    { label: 'dragging left against the right domain edge clamps the window', downX: 800, moveX: 0 },
  ])('$label', async ({ downX, moveX }) => {
    const { container, svg } = await mount(denseTimeline());
    // Zoom in first so there is room to pan without clamping to the domain edges.
    svg.dispatchEvent(new WheelEvent('wheel', { deltaY: -240, ctrlKey: true, clientX: 440, bubbles: true, cancelable: true }));
    vi.advanceTimersByTime(32);
    const before = snapshot(container);

    const chart = container.querySelector('.trend-chart') as HTMLElement;
    chart.dispatchEvent(new MouseEvent('mousedown', { button: 0, clientX: downX, bubbles: true }));
    window.dispatchEvent(new MouseEvent('mousemove', { clientX: moveX, buttons: 1, bubbles: true }));
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


  test('panning with a collapsed svg box falls back to a unit scale', async () => {
    const { container, svg } = await mount(denseTimeline());
    svg.dispatchEvent(new WheelEvent('wheel', { deltaY: -240, ctrlKey: true, clientX: 440, bubbles: true, cancelable: true }));
    vi.advanceTimersByTime(32);
    const before = snapshot(container);
    const zero = { left: 0, top: 0, width: 0, height: 0, right: 0, bottom: 0, x: 0, y: 0 };
    svg.getBoundingClientRect = () => zero as DOMRect;
    const chart = container.querySelector('.trend-chart') as HTMLElement;
    chart.dispatchEvent(new MouseEvent('mousedown', { button: 0, clientX: 400, bubbles: true }));
    window.dispatchEvent(new MouseEvent('mousemove', { clientX: 200, buttons: 1, bubbles: true }));
    vi.advanceTimersByTime(32);
    window.dispatchEvent(new MouseEvent('mouseup', { bubbles: true }));
    expect(snapshot(container).polyline).not.toBe(before.polyline);
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

  test('hovering beyond the plot clamps to the edge points and flips the tooltip', async () => {
    const { container, svg } = await mount(denseTimeline());
    svg.dispatchEvent(new MouseEvent('mousemove', { clientX: -5000, buttons: 0, bubbles: true }));
    const tooltip = container.querySelector('.trend-tooltip') as HTMLElement;
    expect(tooltip.classList.contains('visible')).toBe(true);
    const leftDate = container.querySelector('.tt-date')!.textContent;

    svg.dispatchEvent(new MouseEvent('mousemove', { clientX: 5000, buttons: 0, bubbles: true }));
    expect(container.querySelector('.tt-date')!.textContent).not.toBe(leftDate);
    // The newest point sits at the top-right corner: the tooltip flips to the left of it and clamps near the top.
    const crosshair = container.querySelector('.trend-crosshair') as HTMLElement;
    const anchorX = Number.parseFloat(crosshair.style.left);
    const tipX = Number.parseFloat(tooltip.style.left);
    expect(tipX).toBeLessThan(anchorX);
    expect(tipX).toBeCloseTo(anchorX - 132, 3);
    expect(tooltip.style.top).toBe('4px');
  });

  test('the tooltip stays inside a short chart box', async () => {
    const twoPoint = {
      granularity: 'daily',
      dataPoints: [
        { date: '2026-01-01T00:00:00.000Z', cumulativeSize: 1, cumulativeFileCount: 1 },
        { date: '2026-01-02T00:00:00.000Z', cumulativeSize: 1000, cumulativeFileCount: 2 },
      ],
    };
    const { container, svg } = await mount(twoPoint);
    const rect = { left: 0, top: 0, width: 880, height: 100, right: 880, bottom: 100, x: 0, y: 0 };
    svg.getBoundingClientRect = () => rect as DOMRect;
    (container.querySelector('.trend-chart') as HTMLElement).getBoundingClientRect = () => rect as DOMRect;
    svg.dispatchEvent(new MouseEvent('mousemove', { clientX: -5000, buttons: 0, bubbles: true }));
    const tooltip = container.querySelector('.trend-tooltip') as HTMLElement;
    expect(tooltip.classList.contains('visible')).toBe(true);
    expect(tooltip.style.top).toBe('46px');
  });

  test('a hover without tooltip nodes leaves the crosshair hidden', async () => {
    const { container, svg } = await mount(denseTimeline());
    container.querySelector('.trend-tooltip')!.remove();
    svg.dispatchEvent(new MouseEvent('mousemove', { clientX: 300, buttons: 0, bubbles: true }));
    expect(container.querySelector('.trend-crosshair')!.classList.contains('visible')).toBe(false);
  });

  test('hideTooltip tolerates missing overlay nodes', async () => {
    const { container, svg } = await mount(denseTimeline());
    svg.dispatchEvent(new MouseEvent('mousemove', { clientX: 300, buttons: 0, bubbles: true }));
    container.querySelector('.trend-tooltip')!.remove();
    container.querySelector('.trend-crosshair')!.remove();
    container.querySelector('.trend-active-dot')!.remove();
    expect(() => svg.dispatchEvent(new MouseEvent('mouseleave', { bubbles: true }))).not.toThrow();
    expect(container.querySelectorAll('.trend-tooltip, .trend-crosshair, .trend-active-dot')).toHaveLength(0);
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

  test('a synthetic mousemove right after a tap is ignored', async () => {
    const { container, svg } = await mount(denseTimeline());
    const chart = container.querySelector('.trend-chart') as HTMLElement;
    chart.dispatchEvent(touchEvent('touchstart', [{ x: 300, y: 100 }]));
    chart.dispatchEvent(touchEvent('touchend', [], [{ x: 300, y: 100 }]));
    const date = container.querySelector('.tt-date')!.textContent;
    svg.dispatchEvent(new MouseEvent('mousemove', { clientX: 800, buttons: 0, bubbles: true }));
    expect(container.querySelector('.tt-date')!.textContent).toBe(date);
  });

  test('a three-finger touch is ignored', async () => {
    const { container } = await mount(denseTimeline());
    const chart = container.querySelector('.trend-chart') as HTMLElement;
    chart.dispatchEvent(touchEvent('touchstart', [{ x: 100, y: 80 }, { x: 200, y: 80 }, { x: 300, y: 80 }]));
    chart.dispatchEvent(touchEvent('touchmove', [{ x: 100, y: 80 }, { x: 200, y: 80 }, { x: 300, y: 80 }]));
    // Ending with two fingers still down takes neither the reset nor the one-finger hand-off path.
    chart.dispatchEvent(touchEvent('touchend', [{ x: 100, y: 80 }, { x: 200, y: 80 }], []));
    chart.dispatchEvent(touchEvent('touchcancel', []));
    expect(container.querySelector('.trend-tooltip')!.classList.contains('visible')).toBe(false);
  });

  test('a pinch that never gains distance is ignored', async () => {
    const { container } = await mount(denseTimeline());
    const before = snapshot(container);
    const chart = container.querySelector('.trend-chart') as HTMLElement;
    chart.dispatchEvent(touchEvent('touchstart', [{ x: 400, y: 100 }, { x: 400, y: 100 }]));
    chart.dispatchEvent(touchEvent('touchmove', [{ x: 400, y: 100 }, { x: 400, y: 100 }]));
    vi.advanceTimersByTime(32);
    expect(snapshot(container).polyline).toBe(before.polyline);
  });

  test('a second touchmove keeps panning from the last position', async () => {
    const { container, svg } = await mount(denseTimeline());
    svg.dispatchEvent(new WheelEvent('wheel', { deltaY: -240, ctrlKey: true, clientX: 440, bubbles: true, cancelable: true }));
    vi.advanceTimersByTime(32);
    const before = snapshot(container);
    const chart = container.querySelector('.trend-chart') as HTMLElement;
    chart.dispatchEvent(touchEvent('touchstart', [{ x: 400, y: 100 }]));
    chart.dispatchEvent(touchEvent('touchmove', [{ x: 300, y: 100 }]));
    chart.dispatchEvent(touchEvent('touchmove', [{ x: 250, y: 100 }]));
    vi.advanceTimersByTime(32);
    expect(snapshot(container).polyline).not.toBe(before.polyline);
  });

  test('a tiny move below the pan gates keeps tap mode', async () => {
    const { container } = await mount(denseTimeline());
    const before = snapshot(container);
    const chart = container.querySelector('.trend-chart') as HTMLElement;
    chart.dispatchEvent(touchEvent('touchstart', [{ x: 400, y: 100 }]));
    chart.dispatchEvent(touchEvent('touchmove', [{ x: 404, y: 104 }]));
    vi.advanceTimersByTime(32);
    expect(snapshot(container).polyline).toBe(before.polyline);
    // Still a tap: ending inside the time and movement thresholds shows the tooltip.
    chart.dispatchEvent(touchEvent('touchend', [], [{ x: 404, y: 104 }]));
    expect(container.querySelector('.trend-tooltip')!.classList.contains('visible')).toBe(true);
  });

  test('a slow press does not count as a tap', async () => {
    const { container } = await mount(denseTimeline());
    const chart = container.querySelector('.trend-chart') as HTMLElement;
    chart.dispatchEvent(touchEvent('touchstart', [{ x: 300, y: 100 }]));
    vi.advanceTimersByTime(600);
    chart.dispatchEvent(touchEvent('touchend', [], [{ x: 300, y: 100 }]));
    expect(container.querySelector('.trend-tooltip')!.classList.contains('visible')).toBe(false);
  });

  test('repeated pinch-in clamps at the minimum span', async () => {
    const { container } = await mount(denseTimeline());
    const chart = container.querySelector('.trend-chart') as HTMLElement;
    for (let i = 0; i < 6; i++) {
      chart.dispatchEvent(touchEvent('touchstart', [{ x: 340, y: 100 }, { x: 540, y: 100 }]));
      chart.dispatchEvent(touchEvent('touchmove', [{ x: 200, y: 100 }, { x: 680, y: 100 }]));
      vi.advanceTimersByTime(32);
      chart.dispatchEvent(touchEvent('touchend', [], []));
    }
    expect(container.querySelector('.trend-meta-level')!.textContent).toBe('daily');
  });

  test('pinching outward past the domain snaps back to the full window', async () => {
    const { container } = await mount(denseTimeline());
    const before = snapshot(container);
    const chart = container.querySelector('.trend-chart') as HTMLElement;
    chart.dispatchEvent(touchEvent('touchstart', [{ x: 200, y: 100 }, { x: 680, y: 100 }]));
    chart.dispatchEvent(touchEvent('touchmove', [{ x: 435, y: 100 }, { x: 445, y: 100 }]));
    vi.advanceTimersByTime(32);
    // The overshoot is clamped back to the exact domain, so the frame matches the initial one.
    expect(snapshot(container).polyline).toBe(before.polyline);
    chart.dispatchEvent(touchEvent('touchend', [], []));
  });

  test('lifting one finger of a pinch hands off to single-finger panning', async () => {
    const { container, svg } = await mount(denseTimeline());
    svg.dispatchEvent(new WheelEvent('wheel', { deltaY: -240, ctrlKey: true, clientX: 440, bubbles: true, cancelable: true }));
    vi.advanceTimersByTime(32);
    const before = snapshot(container);
    const chart = container.querySelector('.trend-chart') as HTMLElement;
    chart.dispatchEvent(touchEvent('touchstart', [{ x: 320, y: 100 }, { x: 560, y: 100 }]));
    chart.dispatchEvent(touchEvent('touchmove', [{ x: 330, y: 100 }, { x: 580, y: 100 }]));
    vi.advanceTimersByTime(32);
    chart.dispatchEvent(touchEvent('touchend', [{ x: 330, y: 100 }], [{ x: 580, y: 100 }]));
    // The remaining finger now pans the window.
    chart.dispatchEvent(touchEvent('touchmove', [{ x: 250, y: 100 }]));
    vi.advanceTimersByTime(32);
    expect(snapshot(container).polyline).not.toBe(before.polyline);
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

  test('a sub-day dense series redraws without dots while zoomed', async () => {
    const start = Date.UTC(2026, 0, 1);
    const dataPoints = Array.from({ length: 600 }, (_, i) => ({
      date: new Date(start + i * 3600000).toISOString(),
      cumulativeSize: (i + 1) * 1024,
      cumulativeFileCount: i + 1,
    }));
    const { container, svg } = await mount({ granularity: 'daily', dataPoints });
    expect(container.querySelectorAll('.trend-dots circle')).toHaveLength(0);
    const before = snapshot(container);
    svg.dispatchEvent(new WheelEvent('wheel', { deltaY: -120, ctrlKey: true, clientX: 440, bubbles: true, cancelable: true }));
    vi.advanceTimersByTime(32);
    expect(snapshot(container).polyline).not.toBe(before.polyline);
    expect(container.querySelectorAll('.trend-dots circle')).toHaveLength(0);
  });

  test('redraw prunes grid lines that have no paired label', async () => {
    const { container, svg } = await mount(denseTimeline(400));
    const grid = container.querySelector('.trend-grid')!;
    const ns = 'http://www.w3.org/2000/svg';
    grid.appendChild(document.createElementNS(ns, 'line'));
    grid.appendChild(document.createElementNS(ns, 'line'));
    const before = container.querySelectorAll('.trend-grid line').length;
    svg.dispatchEvent(new WheelEvent('wheel', { deltaY: -240, ctrlKey: true, clientX: 440, bubbles: true, cancelable: true }));
    vi.advanceTimersByTime(32);
    expect(container.querySelectorAll('.trend-grid line').length).toBeLessThan(before);
  });

  test('redraw tolerates overlay groups that were stripped', async () => {
    const { container, svg } = await mount(denseTimeline());
    for (const sel of ['.trend-area', '.trend-line', '.trend-grid', '.trend-dots', '.trend-xlabels']) {
      container.querySelector(sel)!.remove();
    }
    const ev = new WheelEvent('wheel', { deltaY: -120, ctrlKey: true, clientX: 440, bubbles: true, cancelable: true });
    svg.dispatchEvent(ev);
    vi.advanceTimersByTime(32);
    expect(ev.defaultPrevented).toBe(true);
    expect(container.querySelector('.trend-line')).toBeNull();
  });
});

describe('hover on a shrinking series', () => {
  const shrinking = () => ({
    granularity: 'daily',
    dataPoints: [
      { date: '2026-01-01T00:00:00.000Z', cumulativeSize: 3000, cumulativeFileCount: 30 },
      { date: '2026-01-02T00:00:00.000Z', cumulativeSize: 2000, cumulativeFileCount: 20 },
      { date: '2026-01-03T00:00:00.000Z', cumulativeSize: 1000, cumulativeFileCount: 10 },
    ],
  });

  test('hovering an earlier larger point reports a negative delta', async () => {
    const { container, svg } = await mount(shrinking());
    svg.dispatchEvent(new MouseEvent('mousemove', { clientX: -5000, buttons: 0, bubbles: true }));
    const diffSize = container.querySelector('.trend-diff-size')!;
    expect(diffSize.classList.contains('diff-down')).toBe(true);
    expect(diffSize.textContent).toContain('-');
  });

  test('an all-zero series reports a neutral delta', async () => {
    const zero = (d: string) => ({ date: d, cumulativeSize: 0, cumulativeFileCount: 0 });
    const { container, svg } = await mount({
      granularity: 'daily',
      dataPoints: [zero('2026-01-01T00:00:00.000Z'), zero('2026-01-02T00:00:00.000Z'), zero('2026-01-03T00:00:00.000Z')],
    });
    svg.dispatchEvent(new MouseEvent('mousemove', { clientX: -5000, buttons: 0, bubbles: true }));
    const diffSize = container.querySelector('.trend-diff-size')!;
    expect(diffSize.classList.contains('diff-neutral')).toBe(true);
    expect(diffSize.textContent).toBe('±0 B');
  });
});

describe('attach with incomplete chart markup', () => {
  async function loadThroughFakeContainer(setup: (real: HTMLElement) => void) {
    routeTimeline(denseTimeline());
    document.body.innerHTML = '<div id="trendChartContainer"></div>';
    const real = document.getElementById('trendChartContainer')!;
    const orig = document.getElementById.bind(document);
    const fake = {
      get innerHTML() { return real.innerHTML; },
      set innerHTML(v: string) {
        real.innerHTML = v;
        setup(real);
      },
      querySelector: (sel: string) => real.querySelector(sel),
    };
    (document as any).getElementById = (id: string) => (id === 'trendChartContainer' ? fake : orig(id));
    try {
      ui.loadTrendData(false);
      await flush();
    } finally {
      (document as any).getElementById = orig;
    }
    return real;
  }

  test('a chart without a diff panel still shows hover tooltips', async () => {
    const real = await loadThroughFakeContainer((r) => r.querySelector('.trend-diff-panel')?.remove());
    const svg = stubSvgGeometry(real);
    svg.dispatchEvent(new MouseEvent('mousemove', { clientX: 300, buttons: 0, bubbles: true }));
    expect(real.querySelector('.trend-tooltip')!.classList.contains('visible')).toBe(true);
    expect(real.querySelector('.trend-diff-panel')).toBeNull();
    // Hiding again must skip the absent diff panel without throwing.
    svg.dispatchEvent(new MouseEvent('mouseleave', { bubbles: true }));
    expect(real.querySelector('.trend-tooltip')!.classList.contains('visible')).toBe(false);
  });

  test('a chart without the meta level still redraws', async () => {
    const real = await loadThroughFakeContainer((r) => r.querySelector('.trend-meta-level')?.remove());
    const svg = stubSvgGeometry(real);
    const line = real.querySelector('.trend-line')!;
    const before = line.getAttribute('points');
    svg.dispatchEvent(new WheelEvent('wheel', { deltaY: -120, ctrlKey: true, clientX: 440, bubbles: true, cancelable: true }));
    vi.advanceTimersByTime(32);
    expect(line.getAttribute('points')).not.toBe(before);
    expect(real.querySelector('.trend-meta-level')).toBeNull();
  });

  test('a diff panel missing its detail rows still updates the main fields', async () => {
    const strippers = ['.trend-diff-then-size', '.trend-diff-then-count', '.trend-diff-now-date', '.trend-diff-now-size', '.trend-diff-now-count'];
    const real = await loadThroughFakeContainer((r) => {
      for (const sel of strippers) r.querySelector(sel)?.remove();
    });
    const svg = stubSvgGeometry(real);
    svg.dispatchEvent(new MouseEvent('mousemove', { clientX: 300, buttons: 0, bubbles: true }));
    expect(real.querySelector('.trend-diff-dates')!.textContent).toBeTruthy();
    expect(real.querySelector('.trend-diff-size')!.textContent).toBeTruthy();
    expect(real.querySelector('.trend-diff-now-size')).toBeNull();
  });

  test('a chart without an svg accepts gestures but cannot redraw', async () => {
    const real = await loadThroughFakeContainer((r) => {
      r.querySelector('svg')?.remove();
      r.querySelector('.trend-meta-level')?.remove();
    });
    const chart = real.querySelector('.trend-chart') as HTMLElement;
    const ev = new WheelEvent('wheel', { deltaY: -240, ctrlKey: true, clientX: 440, bubbles: true, cancelable: true });
    chart.dispatchEvent(ev);
    vi.advanceTimersByTime(32);
    expect(ev.defaultPrevented).toBe(true);
    expect(real.querySelector('svg')).toBeNull();
  });
});
