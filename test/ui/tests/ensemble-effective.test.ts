import { afterAll, beforeEach, describe, expect, test, vi } from 'vitest';
import { ajaxRouter, flush } from '../src/apiStub.js';
import { freshConfigPage, saveCoverage, UiBridge } from '../src/loader.js';

afterAll(saveCoverage);

describe('selected-user effective ensemble diagnostics', () => {
  let ui: UiBridge;
  let host: HTMLElement;

  beforeEach(() => {
    ui = freshConfigPage();
    document.body.innerHTML = '<div id="recsEnsembleFootnote"></div>';
    host = document.getElementById('recsEnsembleFootnote')!;
  });

  const shares = (host: HTMLElement) => Array.from(host.querySelectorAll('.recs-ensemble-blend strong'), el => el.textContent);

  test('Frozen suppresses all ML, including a stored Neural budget', () => {
    ui.renderEnsembleDiagnostics(host, { Available: true, Alpha: 0.3, NeuralBeta: 0.4, NeuralEnabled: true, QualityGateFrozen: true });
    expect(shares(host)).toEqual(['100%', '0%', 'off']);
  });

  test('backend effective factors, not stored targets, describe this user', () => {
    ui.renderEnsembleDiagnostics(host, { Available: true, Alpha: 0.5, AlphaOffset: 0.12, EffectiveAlpha: 0.62,
      NeuralBeta: 0.2, EffectiveNeuralBeta: 0.2, NeuralEnabled: true });
    expect(shares(host)).toEqual(['38%', '49.6%', '12.4%']);
  });

  test('a model without Neural reallocates its entire ML budget to Learned', () => {
    ui.renderEnsembleDiagnostics(host, { Available: true, Alpha: 0.5, NeuralBeta: 0.4, NeuralEnabled: false });
    expect(shares(host)).toEqual(['50%', '50%', 'off']);
  });

  test('rounded composition totals 100, not 101 percent', () => {
    ui.renderEnsembleDiagnostics(host, { Available: true, Alpha: 0.6667, NeuralBeta: 0.5, NeuralEnabled: true });
    const values = shares(host).map(value => Number.parseFloat(value!));
    expect(values).toEqual([33.33, 33.34, 33.33]);
    expect(values.reduce((a, b) => a + b, 0)).toBeCloseTo(100, 10);
  });

  test('small participating Neural shares are not mislabeled off', () => {
    ui.renderEnsembleDiagnostics(host, { Available: true, Alpha: 0.5, NeuralBeta: 0.0002, NeuralEnabled: true });
    expect(shares(host)).toEqual(['50%', '49.99%', '0.01%']);
  });

  test('a changed selection clears the previous user while awaiting a response', async () => {
    const pending: ((data: unknown) => void)[] = [];
    ajaxRouter({});
    (globalThis as any).ApiClient.ajax = () => new Promise(resolve => pending.push(resolve));
    ui.loadEnsembleDiagnostics('first');
    pending[0]({ Available: true, Alpha: 0.7, IsPerUser: true, UserName: 'First' });
    await flush();
    expect(host.textContent).toContain('First');
    ui.loadEnsembleDiagnostics('second');
    expect(host.textContent).not.toContain('First');
  });

  test('an older same-user response cannot poison the latest cached snapshot', async () => {
    const pending: ((data: unknown) => void)[] = [];
    ajaxRouter({});
    (globalThis as any).ApiClient.ajax = () => new Promise(resolve => pending.push(resolve));
    ui.loadEnsembleDiagnostics('user');
    ui.loadEnsembleDiagnostics('user');
    pending[1]({ Available: true, Alpha: 0.7 });
    await flush();
    pending[0]({ Available: true, Alpha: 0.2 });
    await flush();
    ui.loadEnsembleDiagnostics('user');
    expect(shares(host)).toEqual(['30%', '70%', 'off']);
  });

  test('cached values expire after 30 seconds even for the same user', async () => {
    const calls = ajaxRouter({ 'Diagnostics/Ensemble': { Available: true, Alpha: 0.5 } });
    ui.loadEnsembleDiagnostics('user');
    await flush();
    vi.advanceTimersByTime(30_001);
    ui.loadEnsembleDiagnostics('user');
    await flush();
    expect(calls).toHaveLength(2);
  });

  test('explicit recommendations reload invalidates diagnostics immediately', async () => {
    const calls = ajaxRouter({ 'Diagnostics/Ensemble': { Available: true, Alpha: 0.5 }, '/Recommendations': [] });
    const content = document.createElement('div');
    content.id = 'recsContent';
    document.body.appendChild(content);
    ui.loadEnsembleDiagnostics('user');
    await flush();
    ui.loadRecommendations();
    await flush();
    ui.loadEnsembleDiagnostics('user');
    await flush();
    expect(calls.filter(call => call.url.includes('Diagnostics/Ensemble'))).toHaveLength(2);
  });

  test('the diagnostics cache remains bounded with many users', async () => {
    const calls = ajaxRouter({ 'Diagnostics/Ensemble': { Available: false } });
    for (let i = 0; i < 129; i++) {
      ui.loadEnsembleDiagnostics('user-' + i);
      await flush();
    }
    ui.loadEnsembleDiagnostics('user-128');
    await flush();
    expect(calls).toHaveLength(129);
    ui.loadEnsembleDiagnostics('user-0');
    await flush();
    expect(calls).toHaveLength(130);
  });

  test.each([NaN, Infinity, -Infinity, 'invalid', null])('invalid factors never render nonfinite percentages: %s', factor => {
    ui.renderEnsembleDiagnostics(host, { Available: true, Alpha: factor, EffectiveAlpha: factor, NeuralBeta: factor, NeuralEnabled: true });
    expect(shares(host)).toEqual(['100%', '0%', 'off']);
    expect(host.textContent).not.toMatch(/NaN|Infinity/);
  });

  test('a sub-precision positive Neural share is visibly distinguished from off', () => {
    ui.renderEnsembleDiagnostics(host, { Available: true, Alpha: 0.5, NeuralBeta: 1e-8, NeuralEnabled: true });
    expect(shares(host)).toEqual(['50%', '50%', '<0.01%']);
    expect(host.querySelector('strong')!.innerHTML).not.toContain('<0.01%');
  });

  test('user names and unknown trends are text, never executable markup', () => {
    ui.renderEnsembleDiagnostics(host, { Available: true, Alpha: 0.5, IsPerUser: true,
      UserName: '<img src=x onerror=alert(1)>', Trend: '<script>throw 1</script>' });
    expect(host.querySelector('img,script')).toBeNull();
    expect(host.textContent).toContain('<img src=x onerror=alert(1)>');
  });

  test('the visible footnote stays concise and names scoped global-fallback users too', () => {
    ui.renderEnsembleDiagnostics(host, { Available: true, Alpha: .3, QualityGateFrozen: true,
      IsPerUser: false, UserName: 'User $& X', TrainingExampleCount: 40 });
    expect(host.querySelector('.recs-ensemble-label')!.textContent).toBe('Recommendation strategy for User $& X — Global model:');
    expect(host.querySelector('.recs-ensemble-details')).toBeNull();
    expect(host.textContent).not.toContain('Current scoring mix');
    expect(shares(host)).toEqual(['100%', '0%', 'off']);
    expect(host.querySelector('.recs-ensemble-blend')!.getAttribute('title')).toContain('Stored ML target: 30%');
  });

  test('personal model scope is explicit rather than inferred from its percentage', () => {
    ui.renderEnsembleDiagnostics(host, { Available: true, Alpha: .3, QualityGateFrozen: true,
      IsPerUser: true, UserName: 'User X', TrainingExampleCount: 503 });
    expect(host.querySelector('.recs-ensemble-label')!.textContent).toBe('Recommendation strategy for User X — Personal model:');
    expect(shares(host)).toEqual(['100%', '0%', 'off']);
  });

  test('cold-start priors show the selected user’s own operative composition and explicit inherited scope', () => {
    ui.renderEnsembleDiagnostics(host, { Available: true, Alpha: .75, EffectiveAlpha: .3, EffectiveNeuralBeta: .4,
      NeuralEnabled: true, IsPerUser: false, UserName: 'Cold user', TrainingExampleCount: 0, ValidationLevel: 'GlobalPrior' });
    expect(shares(host)).toEqual(['70%', '18%', '12%']);
    expect(host.querySelector('.recs-ensemble-label')!.textContent).toBe('Recommendation strategy for Cold user — Global priors, personal mixture:');
    expect(host.querySelector('.recs-ensemble-status')!.textContent).toContain('Starting mixture from global priors');
    expect(host.textContent).not.toContain('frozen');
  });

  test.each([
    ['Provisional', 'Provisional — ranking not yet verified'],
    ['Verified', 'Independently verified'],
  ])('personal %s status does not manufacture a frozen or verified claim', (ValidationLevel, expected) => {
    ui.renderEnsembleDiagnostics(host, { Available: true, EffectiveAlpha: .65, EffectiveNeuralBeta: .12,
      NeuralEnabled: true, IsPerUser: true, UserName: 'Warm user', TrainingExampleCount: 72,
      Trend: 'InsufficientData', ValidationLevel });
    expect(shares(host)).toEqual(['35%', '57.2%', '7.8%']);
    expect(host.querySelector('.recs-ensemble-status')!.textContent).toContain(expected);
    expect(host.querySelector('.recs-ensemble-label')!.textContent).toContain('Warm user — Personal model:');
  });

  test('actual withdrawal overrides stale provisional metadata and suppresses all ML', () => {
    ui.renderEnsembleDiagnostics(host, { Available: true, Alpha: .7, EffectiveAlpha: 0, EffectiveNeuralBeta: .4,
      NeuralEnabled: true, QualityGateFrozen: true, ValidationLevel: 'Provisional' });
    expect(shares(host)).toEqual(['100%', '0%', 'off']);
    expect(host.querySelector('.recs-ensemble-status')!.textContent).toContain('frozen');
  });
});
