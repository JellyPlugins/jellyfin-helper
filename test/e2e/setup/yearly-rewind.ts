import { randomUUID } from 'node:crypto';
import { mkdirSync, writeFileSync, renameSync } from 'node:fs';
import { join } from 'node:path';
import { timelineDataDir } from './seed-timeline.js';

export function rewindDataDir(): string {
  return timelineDataDir();
}

export function rewindPath(userId: string): string {
  return join(rewindDataDir(), 'yearly-rewind', 'reports', userId.replaceAll('-', '') + '.json');
}

export function todayMonthDay(offsetDays = 0, now = new Date()): string {
  const date = new Date(now);
  date.setUTCDate(date.getUTCDate() + offsetDays);
  if (date.getUTCMonth() === 1 && date.getUTCDate() === 29) date.setUTCDate(date.getUTCDate() + (offsetDays < 0 ? -1 : 1));
  return date.toISOString().slice(5, 10);
}

export function visibleRewindWindow(now = new Date()) {
  const leapDay = now.getUTCMonth() === 1 && now.getUTCDate() === 29;
  return {YearlyRewindStartDate: leapDay ? '02-28' : todayMonthDay(0, now),
    YearlyRewindEndDate: leapDay ? '03-01' : todayMonthDay(0, now)};
}

export function seedRewind(userId: string) {
  const cutoff = new Date(); cutoff.setUTCHours(0, 0, 0, 0);
  if (cutoff.getUTCMonth() === 1 && cutoff.getUTCDate() === 29) cutoff.setUTCDate(28);
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
