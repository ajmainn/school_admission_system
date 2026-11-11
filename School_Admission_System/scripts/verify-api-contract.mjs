// Final audit against the running development API; leaves one named dummy class/history.
import assert from 'node:assert/strict';
const base = process.env.API_BASE_URL || 'http://localhost:8080';
const number = 90000000000000000n + BigInt(Date.now());
const owner = number.toString();
const second = (number + 1n).toString();
const third = (number + 2n).toString();
const fields = (body, expected) => assert.deepEqual(Object.keys(body).sort(), expected.split(',').sort());
const seatFields = 'id,name,totalSeats,heldSeats,confirmedSeats,availableSeats';
const deadline = value => { assert.match(value, /Z$/); assert.ok(Number.isFinite(Date.parse(value))); };

async function request(method, path, status, student, body) {
  const headers = { Origin: 'http://localhost:3000' };
  if (student !== undefined) headers['X-Student-Id'] = student;
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const response = await fetch(`${base}${path}`, { method, headers,
    body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(15000) });
  const text = await response.text();
  assert.equal(response.status, status, `${method} ${path}: ${text}`);
  assert.equal(response.headers.get('access-control-allow-origin'), 'http://localhost:3000');
  if (status === 204) { assert.equal(text, ''); return; }
  return JSON.parse(text);
}

const created = await request('POST', '/classes', 201, undefined,
  { name: `Final API verification ${Date.now()} - Morning Shift`, totalSeats: 2 });
fields(created, 'id,name,totalSeats');
assert.match(created.id, /^c_[0-9a-f]{32}$/);
const listed = await request('GET', '/classes', 200);
assert.ok(Array.isArray(listed));
for (const item of listed) fields(item, seatFields);
assert.ok(listed.some(item => item.id === created.id));
fields(await request('GET', `/classes/${created.id}`, 200), seatFields);

const held = await request('POST', `/classes/${created.id}/holds`, 201, owner);
fields(held, 'holdId,classId,studentId,status,expiresAt');
assert.match(held.holdId, /^h_[0-9a-f]{32}$/);
assert.equal(held.studentId, owner);
assert.equal(held.status, 'HELD');
deadline(held.expiresAt);
const details = await request('GET', `/holds/${held.holdId}`, 200);
fields(details, 'holdId,classId,status,expiresAt');
assert.equal(details.status, 'HELD');
deadline(details.expiresAt);
fields(await request('POST', `/holds/${held.holdId}/confirm`, 200, owner), 'holdId,status');
assert.equal((await request('POST', `/holds/${held.holdId}/confirm`, 200, owner)).status, 'CONFIRMED');
const cancellable = await request('POST', `/classes/${created.id}/holds`, 201, second);
assert.ok((await request('POST', `/classes/${created.id}/holds`, 409, third)).error);
assert.ok((await request('DELETE', `/holds/${cancellable.holdId}`, 404, third)).error);
await request('DELETE', `/holds/${cancellable.holdId}`, 204, second);
assert.equal((await request('GET', `/holds/${cancellable.holdId}`, 200)).status, 'CANCELLED');
const history = await request('GET', '/me/holds', 200, owner);
assert.ok(Array.isArray(history));
for (const item of history) { fields(item, 'holdId,classId,className,status,expiresAt'); deadline(item.expiresAt); }
assert.equal(history.filter(item => item.classId === created.id).length, 1);
assert.equal(history.find(item => item.classId === created.id).status, 'CONFIRMED');
const seats = await request('GET', `/classes/${created.id}`, 200);
assert.equal(seats.heldSeats, 0);
assert.equal(seats.confirmedSeats, 1);
assert.equal(seats.availableSeats, 1);
assert.ok((await request('POST', `/classes/${created.id}/holds`, 409, owner)).error);
assert.ok((await request('GET', '/me/holds', 400, '123')).error);
console.log('PASS: all 8 API methods/paths, success codes, exact JSON fields, UTC Z timestamps, empty 204, CORS, ownership/capacity/duplicate errors, and retry counts.');
console.log(`Dummy audit fixture: ${created.id}.`);
