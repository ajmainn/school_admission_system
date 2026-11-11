import { useState, type FormEvent } from 'react';
import { Link } from 'react-router-dom';
import { api } from '../api/client';
import { errorMessage } from '../lib/admission';
import type { CreatedClass } from '../types/api';

export function CreateClassPage() {
  const [name, setName] = useState('');
  const [seats, setSeats] = useState('');
  const [pending, setPending] = useState(false);
  const [error, setError] = useState('');
  const [created, setCreated] = useState<CreatedClass | null>(null);
  async function submit(event: FormEvent) {
    event.preventDefault();
    if (pending) return;
    setError(''); setCreated(null);
    const totalSeats = Number(seats);
    if (!name.trim() || name.trim().length > 200) { setError('Class name must contain 1 to 200 characters.'); return; }
    if (!Number.isInteger(totalSeats) || totalSeats < 1 || totalSeats > 2147483647) {
      setError('Total seats must be a whole number from 1 to 2147483647.'); return;
    }
    setPending(true);
    try { setCreated(await api.createClass(name.trim(), totalSeats)); setName(''); setSeats(''); }
    catch (error) { setError(errorMessage(error)); }
    finally { setPending(false); }
  }
  return <section className="panel">
    <h1>Create class (admin)</h1>
    <p>Create a separate class for each shift, for example “Class 6 - Morning Shift”.</p>
    <form onSubmit={submit} noValidate>
      <fieldset disabled={pending}>
        <label htmlFor="class-name">Class name</label>
        <input id="class-name" value={name} onChange={e => setName(e.target.value)} required />
        <label htmlFor="total-seats">Total seats</label>
        <input id="total-seats" type="number" min="1" max="2147483647" step="1" value={seats} onChange={e => setSeats(e.target.value)} required />
        <button type="submit">{pending ? 'Creating…' : 'Create class'}</button>
      </fieldset>
    </form>
    {error && <p role="alert" className="error">{error}</p>}
    {created && <p role="status" className="success">Created {created.name} with {created.totalSeats} seats. <Link to="/">View classes</Link></p>}
  </section>;
}
