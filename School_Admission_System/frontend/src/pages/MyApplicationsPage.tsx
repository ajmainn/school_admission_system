import { useCallback } from 'react';
import { Link } from 'react-router-dom';
import { api } from '../api/client';
import { usePolling } from '../hooks/usePolling';
import { displayStatus, localTime, validStudentId } from '../lib/admission';

interface Props { studentId: string }
export function MyApplicationsPage({ studentId }: Props) {
  const valid = validStudentId(studentId);
  const load = useCallback((signal: AbortSignal) => api.myHolds(studentId, signal), [studentId]);
  const { data: holds, error, loading, refresh } = usePolling(load, 5000, valid);
  return <section className="panel">
    <h1>My applications</h1>
    <p className="muted">Your application history refreshes every 5 seconds.</p>
    {!valid && <p className="warning">Enter a valid 17-digit student ID above to view applications.</p>}
    {loading && <p role="status">Loading applications…</p>}
    {error && <p role="alert" className="error">{error} <button onClick={refresh}>Retry</button></p>}
    {holds?.length === 0 && <p>No applications for this student yet. <Link to="/">Browse classes</Link>.</p>}
    {!!holds?.length && <ul className="applications">{holds.map(hold => {
      const status = displayStatus(hold.status, hold.expiresAt);
      return <li key={hold.holdId}>
        <div><Link to={`/holds/${hold.holdId}`}>{hold.className}</Link>
          <small>{status === 'HELD' ? 'Pay before' : 'Original hold deadline:'} {localTime(hold.expiresAt)}</small></div>
        <span className={`status status-${status}`}>{status}</span>
      </li>;
    })}</ul>}
  </section>;
}
