import { randomUUID } from 'node:crypto';
import { mkdirSync, writeFileSync, renameSync } from 'node:fs';
import { join } from 'node:path';
import { timelineDataDir } from './seed-timeline.ts';

export function rewindDataDir(): string {
  return timelineDataDir();
}

export function rewindPath(userId: string): string {
  return join(rewindDataDir(), 'yearly-rewind', 'reports', userId.replaceAll('-', '') + '.json');
}

export function todayMonthDay(offsetDays = 0): string {
  const date = new Date();
  date.setUTCDate(date.getUTCDate() + offsetDays);
  return date.toISOString().slice(5, 10);
}

export function seedRewind(userId: string) {
  const cutoff = new Date(); cutoff.setUTCHours(0, 0, 0, 0);
  const start = new Date(cutoff); start.setUTCFullYear(start.getUTCFullYear() - 1);
  const canonicalUserId = userId.replaceAll('-', '').replace(/^(........)(....)(....)(....)(............)$/, '$1-$2-$3-$4-$5');
  const report = { schemaVersion: 1, userId: canonicalUserId, snapshotId: randomUUID(), periodStartUtc: start.toISOString(), snapshotAtUtc: cutoff.toISOString(),
    generatedAtUtc: new Date().toISOString(), timeZoneId: 'UTC', dataState: 'Ready', activeDays: 3, items: [
      { itemId: randomUUID(), name: 'Rewind fixture film', itemType: 'Movie', activeSeconds: 7200, playCount: 1, genres: ['Drama'] }
    ], months: [{ startUtc: start.toISOString(), endUtc: cutoff.toISOString(), activeSeconds: 7200 }] };
  const path = rewindPath(userId);
  mkdirSync(join(rewindDataDir(), 'yearly-rewind', 'reports'), { recursive: true });
  writeFileSync(path + '.test-stage', JSON.stringify(report));
  renameSync(path + '.test-stage', path);
  return report;
}
