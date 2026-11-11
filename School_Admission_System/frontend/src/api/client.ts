import type { ClassSeats, CreatedClass, CreatedHold, HoldDetails, ConfirmedHold, StudentHold } from '../types/api';

const baseUrl = (import.meta.env.VITE_API_BASE_URL || 'http://localhost:8080').replace(/\/$/, '');

export class ApiRequestError extends Error {
  constructor(public readonly status: number, message: string) { super(message); }
}

async function request<T>(path: string, init?: RequestInit, studentId?: string): Promise<T> {
  const headers = new Headers(init?.headers);
  headers.set('Accept', 'application/json');
  if (init?.body) headers.set('Content-Type', 'application/json');
  if (studentId) headers.set('X-Student-Id', studentId);
  let response: Response;
  try {
    response = await fetch(`${baseUrl}${path}`, { ...init, headers });
  } catch (error) {
    if (init?.signal?.aborted) throw error;
    throw new ApiRequestError(0, 'Cannot reach the server. Please try again.');
  }
  if (!response.ok) {
    const payload: { error?: string } | null = await response.json().catch(() => null);
    throw new ApiRequestError(response.status, payload?.error ?? `Request failed (${response.status}).`);
  }
  if (response.status === 204) return undefined as T;
  return response.json() as Promise<T>;
}

export const api = {
  createClass: (name: string, totalSeats: number) => request<CreatedClass>('/classes', { method: 'POST', body: JSON.stringify({ name, totalSeats }) }),
  listClasses: (signal?: AbortSignal) => request<ClassSeats[]>('/classes', { signal }),
  getClass: (classId: string, signal?: AbortSignal) => request<ClassSeats>(`/classes/${encodeURIComponent(classId)}`, { signal }),
  createHold: (classId: string, studentId: string) => request<CreatedHold>(`/classes/${encodeURIComponent(classId)}/holds`, { method: 'POST' }, studentId),
  getHold: (holdId: string, signal?: AbortSignal) => request<HoldDetails>(`/holds/${encodeURIComponent(holdId)}`, { signal }),
  confirmHold: (holdId: string, studentId: string) => request<ConfirmedHold>(`/holds/${encodeURIComponent(holdId)}/confirm`, { method: 'POST' }, studentId),
  cancelHold: (holdId: string, studentId: string) => request<void>(`/holds/${encodeURIComponent(holdId)}`, { method: 'DELETE' }, studentId),
  myHolds: (studentId: string, signal?: AbortSignal) => request<StudentHold[]>('/me/holds', { signal }, studentId),
};
