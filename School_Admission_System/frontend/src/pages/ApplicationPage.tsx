import { useCallback, useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { api, ApiRequestError } from '../api/client';
import { usePolling } from '../hooks/usePolling';
import { displayStatus, errorMessage, localTime, validStudentId } from '../lib/admission';

interface Props { studentId: string }
export function ApplicationPage({ studentId }: Props) {
  const { holdId } = useParams();
  const load = useCallback(async (signal: AbortSignal) => {
    if (!holdId) throw new Error('Application reference is missing.');
    const hold = await api.getHold(holdId, signal);
    const admissionClass = await api.getClass(hold.classId, signal);
    return { hold, className: admissionClass.name };
  }, [holdId]);
  const { data, error, loading, refresh, replaceData } = usePolling(load, 2000);
  const [now, setNow] = useState(Date.now());
  const [pending, setPending] = useState<'confirm' | 'cancel' | null>(null);
  const [actionError, setActionError] = useState('');
  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 1000);
    return () => clearInterval(timer);
  }, []);
  const status = data ? displayStatus(data.hold.status, data.hold.expiresAt, now) : null;
  const remaining = data ? Math.max(0, Math.ceil((Date.parse(data.hold.expiresAt) - now) / 1000)) : 0;
  async function act(action: 'confirm' | 'cancel') {
    if (!data || pending || !validStudentId(studentId) || displayStatus(data.hold.status, data.hold.expiresAt) !== 'HELD') return;
    setPending(action); setActionError('');
    try {
      if (action === 'confirm') await api.confirmHold(data.hold.holdId, studentId);
      else await api.cancelHold(data.hold.holdId, studentId);
      replaceData({ ...data, hold: { ...data.hold, status: action === 'confirm' ? 'CONFIRMED' : 'CANCELLED' } });
    } catch (error) {
      setActionError(errorMessage(error) + (error instanceof ApiRequestError && error.status === 404
        ? ' Check that the student ID matches the application owner.' : ''));
      refresh();
    } finally { setPending(null); }
  }
  return <section className="panel">
    <h1>Application details</h1>
    {loading && <p role="status">Loading application…</p>}
    {error && <p role="alert" className="error">{error} <button onClick={refresh}>Retry</button></p>}
    {data && <>
      <h2>{data.className}</h2>
      <p>Status: <strong className={`status status-${status}`} role="status">{status}</strong></p>
      <p className="reference">Application reference: {data.hold.holdId}</p>
      {status === 'HELD' && <>
        <p className="countdown">Time left: <strong>{Math.floor(remaining / 60)}:{String(remaining % 60).padStart(2, '0')}</strong></p>
        <p>Pay before {localTime(data.hold.expiresAt)}. Times are shown in your browser’s local timezone.</p>
        <p>Pay Fee simulates a successful payment; no payment details are needed.</p>
      </>}
      {status === 'CONFIRMED' && <p className="success">Admission confirmed. Your seat is reserved.</p>}
      {status === 'CANCELLED' && <p>This application was cancelled and its seat released.</p>}
      {status === 'EXPIRED' && <p className="warning">This unpaid hold has expired. You can apply again if a seat is available.</p>}
      {!validStudentId(studentId) && <p className="warning">Enter a valid 17-digit student ID to pay or cancel.</p>}
      <div className="actions">
        <button disabled={status !== 'HELD' || !validStudentId(studentId) || !!pending || !!error}
          onClick={() => void act('confirm')}>{pending === 'confirm' ? 'Confirming…' : 'Pay Fee'}</button>
        <button className="secondary" disabled={status !== 'HELD' || !validStudentId(studentId) || !!pending || !!error}
          onClick={() => void act('cancel')}>{pending === 'cancel' ? 'Cancelling…' : 'Cancel application'}</button>
      </div>
    </>}
    {actionError && <p role="alert" className="error">{actionError}</p>}
    <p><Link to="/">View classes</Link> · <Link to="/me/holds">My applications</Link></p>
  </section>;
}
