// Optional Phase 6 check: independent API processes share the development PostgreSQL DB.
// Leaves dummy records and stops only the extra container created by this script.
import assert from 'node:assert/strict';
import { execFileSync } from 'node:child_process';
import { randomUUID } from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { setTimeout as delay } from 'node:timers/promises';

const cwd = fileURLToPath(new URL('../', import.meta.url));
const docker = process.platform === 'win32' ? 'docker.exe' : 'docker';
const primary = process.env.API_BASE_URL || 'http://localhost:8080';
const name = `school-admission-phase6-${randomUUID().slice(0, 8)}`;
const student = 90000000000000000n + BigInt(Date.now());
let created = false;
const command = (...args) => execFileSync(docker, args, { cwd, encoding: 'utf8', timeout: 30000 }).trim();

async function createClass(seats, suffix) {
  const response = await fetch(`${primary}/classes`, { method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name: `Two-backend verification ${name} - ${suffix}`, totalSeats: seats }),
    signal: AbortSignal.timeout(15000) });
  assert.equal(response.status, 201);
  return response.json();
}

async function post(base, path, owner) {
  const response = await fetch(`${base}${path}`, { method: 'POST',
    headers: { 'X-Student-Id': owner.toString() }, signal: AbortSignal.timeout(30000) });
  return { status: response.status, body: await response.json() };
}

try {
  assert.equal((await fetch(`${primary}/health`)).status, 200, 'Start the main Compose services first.');
  command('compose', 'run', '--rm', '--no-deps', '-d', '--name', name, '-p', '127.0.0.1::8080', 'backend');
  created = true;
  const address = command('port', name, '8080/tcp').split('\n')[0].trim();
  const secondary = `http://${address}`;
  let ready = false;
  for (let attempt = 0; attempt < 60; attempt++) {
    try { ready = (await fetch(`${secondary}/health`, { signal: AbortSignal.timeout(1000) })).status === 200; }
    catch { /* Container can still be starting. */ }
    if (ready) break;
    await delay(250);
  }
  assert.ok(ready, 'Second backend did not become ready.');
  const bases = [primary, secondary];
  const capacityClass = await createClass(5, 'capacity');
  const holds = await Promise.all(Array.from({ length: 50 }, (_, i) =>
    post(bases[i % 2], `/classes/${capacityClass.id}/holds`, student + BigInt(i))));
  assert.equal(holds.filter(r => r.status === 201).length, 5);
  assert.equal(holds.filter(r => r.status === 409).length, 45);
  for (const base of bases) {
    const seats = await (await fetch(`${base}/classes/${capacityClass.id}`)).json();
    assert.equal(seats.heldSeats, 5);
    assert.equal(seats.confirmedSeats, 0);
    assert.equal(seats.availableSeats, 0);
  }
  console.log('PASS: 50 requests split across two backend processes -> 5 created, 45 conflicts; both report 5 held/0 available.');

  const duplicateClass = await createClass(50, 'duplicate');
  const duplicates = await Promise.all(Array.from({ length: 50 }, (_, i) =>
    post(bases[i % 2], `/classes/${duplicateClass.id}/holds`, student)));
  assert.equal(duplicates.filter(r => r.status === 201).length, 1);
  assert.equal(duplicates.filter(r => r.status === 409).length, 49);
  const hold = duplicates.find(r => r.status === 201).body;
  const confirmations = await Promise.all(Array.from({ length: 20 }, (_, i) =>
    post(bases[i % 2], `/holds/${hold.holdId}/confirm`, student)));
  assert.ok(confirmations.every(r => r.status === 200 && r.body.status === 'CONFIRMED'));
  for (const base of bases) {
    const seats = await (await fetch(`${base}/classes/${duplicateClass.id}`)).json();
    assert.equal(seats.heldSeats, 0);
    assert.equal(seats.confirmedSeats, 1);
    assert.equal(seats.availableSeats, 49);
  }
  console.log('PASS: 50 same-student requests -> 1 created, 49 conflicts; 20 confirmations -> one confirmed seat across both processes.');
} finally {
  if (created) {
    command('stop', '--time', '10', name);
    console.log('Stopped the temporary second backend; the original Compose services and named volume remain.');
  }
}
