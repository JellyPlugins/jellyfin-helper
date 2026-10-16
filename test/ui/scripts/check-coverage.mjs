// Merges the per-process istanbul reports from the Vitest run, prints a
// per-file table, writes lcov.info for Codecov, and fails below the mark.
import { mkdirSync, readdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const HERE = dirname(fileURLToPath(import.meta.url));
const UI_ROOT = join(HERE, '..');
const TMP = join(UI_ROOT, 'coverage', 'tmp');

const args = Object.fromEntries(
  process.argv.slice(2).map((a) => {
    const m = a.match(/^--([^=]+)=(.+)$/);
    return m ? [m[1], Number(m[2])] : [a.replace(/^--/, ''), true];
  }),
);
const LINE_MARK = args.lines ?? 10;
const BRANCH_MARK = args.branches ?? 8;

function mergeInto(merged, cov) {
  for (const [file, data] of Object.entries(cov)) {
    const m = (merged[file] ??= { s: {}, f: {}, b: {} });
    for (const [k, v] of Object.entries(data.s)) m.s[k] = (m.s[k] ?? 0) + v;
    for (const [k, v] of Object.entries(data.f)) m.f[k] = (m.f[k] ?? 0) + v;
    for (const [k, v] of Object.entries(data.b)) {
      m.b[k] = (m.b[k] ?? data.b[k].map(() => 0)).map((c, i) => c + v[i]);
    }
    m.statementMap ??= data.statementMap;
    m.fnMap ??= data.fnMap;
    m.branchMap ??= data.branchMap;
  }
  return merged;
}

function writeLcov(merged) {
  const lines = [];
  for (const [file, data] of Object.entries(merged)) {
    lines.push(`SF:${file}`);
    for (const fn of Object.values(data.fnMap)) lines.push(`FN:${fn.loc.start.line},${fn.name}`);
    lines.push(`FNF:${Object.keys(data.fnMap).length}`);
    lines.push(`FNH:${Object.values(data.f).filter((c) => c > 0).length}`);
    const byLine = new Map();
    for (const [id, count] of Object.entries(data.s)) {
      const line = data.statementMap[id].start.line;
      byLine.set(line, (byLine.get(line) ?? 0) + count);
    }
    for (const [line, count] of [...byLine.entries()].sort((a, b) => a[0] - b[0])) {
      lines.push(`DA:${line},${count}`);
    }
    lines.push(`LF:${byLine.size}`);
    lines.push(`LH:${[...byLine.values()].filter((c) => c > 0).length}`);
    let found = 0;
    let hit = 0;
    for (const [id, counts] of Object.entries(data.b)) {
      const loc = data.branchMap[id].loc.start.line;
      counts.forEach((c, i) => {
        found++;
        if (c > 0) hit++;
        lines.push(`BRDA:${loc},${id},${i},${c}`);
      });
    }
    lines.push(`BRF:${found}`);
    lines.push(`BRH:${hit}`);
    lines.push('end_of_record');
  }
  mkdirSync(join(UI_ROOT, 'coverage'), { recursive: true });
  writeFileSync(join(UI_ROOT, 'coverage', 'lcov.info'), lines.join('\n') + '\n');
}

let files = [];
try {
  files = readdirSync(TMP).filter((f) => f.endsWith('.json'));
} catch {
  files = [];
}
if (files.length === 0) {
  console.error('No coverage reports found. Run vitest first (the suite saves per-process reports).');
  process.exit(1);
}

const merged = {};
for (const f of files) mergeInto(merged, JSON.parse(readFileSync(join(TMP, f), 'utf8')));

let lineHit = 0;
let lineAll = 0;
let branchHit = 0;
let branchAll = 0;
for (const [file, data] of Object.entries(merged).sort()) {
  let lh = 0;
  let la = 0;
  let bh = 0;
  let ba = 0;
  for (const c of Object.values(data.s)) {
    la++;
    lineAll++;
    if (c > 0) {
      lh++;
      lineHit++;
    }
  }
  for (const counts of Object.values(data.b)) {
    for (const c of counts) {
      ba++;
      branchAll++;
      if (c > 0) {
        bh++;
        branchHit++;
      }
    }
  }
  console.log(
    `${file}: lines ${(la ? (lh / la) * 100 : 100).toFixed(1)}% (${lh}/${la})` +
      ` branches ${(ba ? (bh / ba) * 100 : 100).toFixed(1)}% (${bh}/${ba})`,
  );
}
const linePct = lineAll ? (lineHit / lineAll) * 100 : 100;
const branchPct = branchAll ? (branchHit / branchAll) * 100 : 100;
console.log(`TOTAL: lines ${linePct.toFixed(1)}% branches ${branchPct.toFixed(1)}%`);
writeLcov(merged);

let failed = false;
if (linePct < LINE_MARK) {
  console.error(`Line coverage ${linePct.toFixed(1)}% is below the ${LINE_MARK}% mark.`);
  failed = true;
}
if (branchPct < BRANCH_MARK) {
  console.error(`Branch coverage ${branchPct.toFixed(1)}% is below the ${BRANCH_MARK}% mark.`);
  failed = true;
}
process.exit(failed ? 1 : 0);
