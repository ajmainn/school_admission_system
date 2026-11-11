import type { HoldStatus } from '../types/api';

export const validStudentId = (value: string) => /^[0-9]{17}$/.test(value);
export const errorMessage = (error: unknown) => error instanceof Error ? error.message : 'Something went wrong. Please try again.';
export const displayStatus = (status: HoldStatus, expiresAt: string, now = Date.now()): HoldStatus =>
  status === 'HELD' && Date.parse(expiresAt) <= now ? 'EXPIRED' : status;
export const localTime = (value: string) => new Date(value).toLocaleString();
