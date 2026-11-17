import { readFileSync } from 'node:fs';
import { join } from 'node:path';
import { runInNewContext } from 'node:vm';
import { afterAll, beforeEach, describe, expect, test, vi } from 'vitest';
import { freshConfigPage, REPO_ROOT, saveCoverage, type UiBridge } from '../src/loader.js';

afterAll(saveCoverage);

function demo() {
  const source = readFileSync(join(REPO_ROOT, 'docs/mock/mock-data.js'), 'utf8');
  return runInNewContext(source + '\n({ ApiClient, MOCK_ENSEMBLE_DIAGNOSTICS, MOCK_ENSEMBLE_DIAGNOSTICS_PER_USER, _uid1, _uid2 });', {
    window: { fetch: vi.fn() }, structuredClone, console, setTimeout: (callback: () => void) => callback(),
  });
}

describe('GitHub Pages demo ensemble diagnostics', () => {
  let ui: UiBridge;
  let host: HTMLElement;

  beforeEach(() => {
    ui = freshConfigPage();
    host = document.createElement('div');
  });

  test('all demo models use the canonical 41-feature v6 catalogue and consistent operating shares', () => {
    const mock = demo();
    const documentation = readFileSync(join(REPO_ROOT, 'docs/RECOMMENDATION_FEATURES.md'), 'utf8');
    const names = Array.from(documentation.matchAll(/^\| \d+ \| `([^`]+)`/gm), match => match[1]);
    expect(names).toHaveLength(41);
    for (const data of [mock.MOCK_ENSEMBLE_DIAGNOSTICS, ...Object.values(mock.MOCK_ENSEMBLE_DIAGNOSTICS_PER_USER) as any[]]) {
      expect(data.NeuralModelVersion).toBe(6);
      expect(data.NeuralInputFeatureCount).toBe(41);
      expect(data.NeuralFeatures).toEqual(names);
      expect(data.AlphaMin).toBe(.3);
      expect(data.AlphaMax).toBe(.75);
      expect(data.EffectiveNeuralBeta).toBeLessThanOrEqual(.4);
      expect(data.HeuristicShare).toBeCloseTo(1 - data.EffectiveAlpha, 12);
      expect(data.LearnedShare).toBeCloseTo(data.EffectiveAlpha * (1 - data.EffectiveNeuralBeta), 12);
      expect(data.NeuralShare).toBeCloseTo(data.EffectiveAlpha * data.EffectiveNeuralBeta, 12);
      expect(data.HeuristicShare + data.LearnedShare + data.NeuralShare).toBeCloseTo(1, 12);
    }
  });

  test.each([
    ['global', ['38%', '44.64%', '17.36%'], 'Global model:', 'Independently verified'],
    ['alice', ['32%', '46.92%', '21.08%'], 'Alice — Personal model:', 'Provisional — ranking not yet verified'],
    ['bob', ['70%', '24%', '6%'], 'Bob — Global priors, personal mixture:', 'Starting mixture from global priors'],
  ])('the admin display renders %s demo metadata and evidence status through the shipped mock route', async (scope, shares, label, status) => {
    const mock = demo();
    const user = scope === 'alice' ? mock._uid1 : mock._uid2;
    const data = await mock.ApiClient.ajax({ url: 'mock://JellyfinHelper/Recommendations/Diagnostics/Ensemble' + (scope === 'global' ? '' : '?userId=' + user) });
    ui.renderEnsembleDiagnostics(host, data);
    expect(Array.from(host.querySelectorAll('.recs-ensemble-blend strong'), el => el.textContent)).toEqual(shares);
    expect(host.querySelector('.recs-ensemble-label')!.textContent).toContain(label);
    expect(host.querySelector('.recs-ensemble-status')!.textContent).toContain(status);
    const title = host.querySelector('.recs-ensemble-blend')!.getAttribute('title')!;
    expect(title).toContain('Neural model v6: 41 features');
    expect(title).toContain('ContentFitAffinity, ContentFitSupport, AudienceMismatch');
    if (scope !== 'global') {
      expect(data.AcceptanceTitleCount).toBe(0);
      expect(data.CandidateValidationLoss).toBeNull();
    }
  });

  test('demo route responses detach feature lists and personal evidence from subsequent callers', async () => {
    const mock = demo();
    const url = 'mock://JellyfinHelper/Recommendations/Diagnostics/Ensemble?userId=' + mock._uid1;
    const first = await mock.ApiClient.ajax({ url });
    first.NeuralFeatures[0] = 'changed';
    first.ValidationLevel = 'Verified';
    const next = await mock.ApiClient.ajax({ url });
    expect(next.NeuralFeatures[0]).toBe('GenreSimilarity');
    expect(next.ValidationLevel).toBe('Provisional');
    expect(mock.MOCK_ENSEMBLE_DIAGNOSTICS.NeuralFeatures[0]).toBe('GenreSimilarity');
    expect(mock.MOCK_ENSEMBLE_DIAGNOSTICS_PER_USER[mock._uid2].NeuralFeatures[0]).toBe('GenreSimilarity');
  });
});
