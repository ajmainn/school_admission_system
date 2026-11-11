import { Link, NavLink, Route, Routes } from 'react-router-dom';
import { useState } from 'react';
import { StudentIdInput } from './components/StudentIdInput';
import { ClassesPage } from './pages/ClassesPage';
import { CreateClassPage } from './pages/CreateClassPage';
import { ApplicationPage } from './pages/ApplicationPage';
import { MyApplicationsPage } from './pages/MyApplicationsPage';

const KEY = 'school-admission-demo-student-id';

export default function App() {
  const [studentId, setStudentId] = useState(() => {
    try { return localStorage.getItem(KEY) ?? ''; } catch { return ''; }
  });
  const [storageError, setStorageError] = useState('');
  function updateStudentId(next: string) {
    setStudentId(next);
    try { localStorage.setItem(KEY, next); setStorageError(''); }
    catch { setStorageError('Browser storage is unavailable. Enter the student ID again after refreshing.'); }
  }
  return (
    <main className="container">
      <header>
        <Link to="/" className="brand">School Admission System</Link>
        <span className="muted">School admissions · demo</span>
      </header>
      <StudentIdInput value={studentId} onChange={updateStudentId} />
      {storageError && <p role="alert" className="warning">{storageError}</p>}
      <nav aria-label="Main navigation">
        <NavLink to="/" end>Classes</NavLink>
        <NavLink to="/admin/classes/new">Create class (admin)</NavLink>
        <NavLink to="/me/holds">My applications</NavLink>
      </nav>
      <Routes>
        <Route path="/" element={<ClassesPage studentId={studentId} />} />
        <Route path="/admin/classes/new" element={<CreateClassPage />} />
        <Route path="/holds/:holdId" element={<ApplicationPage key={studentId} studentId={studentId} />} />
        <Route path="/me/holds" element={<MyApplicationsPage key={studentId} studentId={studentId} />} />
        <Route path="*" element={<section className="panel"><h1>Page not found</h1><Link to="/">View classes</Link></section>} />
      </Routes>
    </main>
  );
}
