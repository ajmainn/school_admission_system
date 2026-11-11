// Required response fields from pages 3-4 of the assignment.
export type HoldStatus = 'HELD' | 'CONFIRMED' | 'EXPIRED' | 'CANCELLED';
export interface CreatedClass { id: string; name: string; totalSeats: number }
export interface ClassSeats extends CreatedClass {
  heldSeats: number;
  confirmedSeats: number;
  availableSeats: number;
}
export interface CreatedHold {
  holdId: string; classId: string; studentId: string; status: HoldStatus; expiresAt: string;
}
export interface HoldDetails {
  holdId: string; classId: string; status: HoldStatus; expiresAt: string;
}
export interface ConfirmedHold { holdId: string; status: HoldStatus }
export interface StudentHold extends HoldDetails { className: string }
export interface ApiError { error: string }
