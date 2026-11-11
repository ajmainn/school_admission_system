interface Props { value: string; onChange: (value: string) => void }

export function StudentIdInput({ value, onChange }: Props) {
  const isValid = /^\d{17}$/.test(value);
  return (
    <section className="panel">
      <label htmlFor="student-id"><strong>Student birth registration number (17 digits)</strong></label>
      <input id="student-id" inputMode="numeric" autoComplete="off" value={value}
        aria-describedby="student-id-help" aria-invalid={value.length > 0 && !isValid}
        onChange={(event) => onChange(event.target.value)}
        placeholder="Enter 17-digit number" />
      <small id="student-id-help">{value.length === 0 ? 'Required for student actions; no login. Use a dummy ID for this demo.' : isValid ? 'Format looks valid. This number identifies the student; it is not a login.' : 'Enter exactly 17 digits (0–9).'}</small>
    </section>
  );
}
