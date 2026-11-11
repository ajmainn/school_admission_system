import { useCallback, useState } from 'react';
import { Link, useNavigate } from 'react-router-dom';
import { api } from '../api/client';
import { usePolling } from '../hooks/usePolling';
import { errorMessage, validStudentId } from '../lib/admission';

interface Props { studentId: string }
export function ClassesPage({ studentId }: Props) {
  const navigate = useNavigate();
  const load = useCallback((signal: AbortSignal) => api.listClasses(signal), []);
  const { data: classes, error, loading, refresh } = usePolling(load, 5000);
  const [pending, setPending] = useState<string | null>(null);
  const [actionError, setActionError] = useState('');
  async function apply(classId: string) {
    if (pending || !validStudentId(studentId)) return;
    setPending(classId); setActionError('');
    try { const hold = await api.createHold(classId, studentId); navigate(`/holds/${hold.holdId}`); }
    catch (error) { setActionError(errorMessage(error)); refresh(); }
    finally { setPending(null); }
  }
  return <section className="panel">
    <h1>Class list</h1>
    <p className="muted">Seat counts refresh every 5 seconds. Applying temporarily holds one seat until payment.</p>
    {!validStudentId(studentId) && <p className="warning">Enter a valid 17-digit student ID above to apply.</p>}
    {loading && <p role="status">Loading classes…</p>}
    {error && <p role="alert" className="error">{error} Counts may be out of date. <button onClick={refresh}>Retry</button></p>}
    {actionError && <p role="alert" className="error">{actionError} <Link to="/me/holds">Check my applications</Link></p>}
    {classes?.length === 0 && <p>No classes yet. <Link to="/admin/classes/new">Create the first class</Link>.</p>}
    {!!classes?.length && <><p className="mobile-hint muted">Scroll across the table to see seat counts and Apply.</p>
    <div className="table-scroll" tabIndex={0} role="region" aria-label="Class seat availability"><table>
      <caption className="sr-only">Classes and current seat availability</caption>
      <thead><tr><th>Class / shift</th><th>Total</th><th>Held</th><th>Confirmed</th><th>Available</th><th>Application</th></tr></thead>
      <tbody>{classes.map(item => <tr key={item.id}>
        <th scope="row">{item.name}</th><td>{item.totalSeats}</td><td>{item.heldSeats}</td>
        <td>{item.confirmedSeats}</td><td>{item.availableSeats}</td>
        <td><button disabled={!validStudentId(studentId) || item.availableSeats <= 0 || !!pending || !!error}
          onClick={() => void apply(item.id)}>{pending === item.id ? 'Applying…' : item.availableSeats <= 0 ? 'Full' : 'Apply'}</button></td>
      </tr>)}</tbody>
    </table></div></>}
  </section>;
}
