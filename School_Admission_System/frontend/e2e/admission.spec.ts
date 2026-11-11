import { test, expect, type Page } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';

const student = () => (90000000000000000n + BigInt(Date.now())).toString();
const uniqueName = (suffix: string) => `UI verification ${Date.now()} - ${suffix}`;
const rowFor = (page: Page, name: string) => page.getByRole('row').filter({ has: page.getByRole('rowheader', { name, exact: true }) });

async function createClass(page: Page, name: string, seats = 1) {
  await page.getByRole('link', { name: 'Create class (admin)' }).click();
  await page.getByLabel('Class name', { exact: true }).fill(name);
  await page.getByLabel('Total seats', { exact: true }).fill(String(seats));
  const response = page.waitForResponse(r => r.url().endsWith('/classes') && r.request().method() === 'POST');
  await page.getByRole('button', { name: 'Create class', exact: true }).click();
  await expect(page.getByRole('status')).toContainText(`Created ${name}`);
  await page.getByRole('link', { name: 'View classes', exact: true }).click();
  return (await response).json() as Promise<{ id: string }>;
}

test('admin/student flow, ownership, duplicate conflict, cancellation and refresh recovery', async ({ page }) => {
  const errors: string[] = [];
  page.on('pageerror', error => errors.push(error.message));
  await page.goto('/');
  const id = student();
  await page.getByLabel('Student birth registration number').fill('123');
  const name = uniqueName('Morning Shift');
  await createClass(page, name, 2);
  const row = rowFor(page, name);
  await expect(row.getByRole('button', { name: 'Apply', exact: true })).toBeDisabled();
  await page.getByLabel('Student birth registration number').fill(id);
  await row.getByRole('button', { name: 'Apply', exact: true }).click();
  await expect(page).toHaveURL(/\/holds\/h_[0-9a-f]{32}$/);
  await expect(page.locator('.status')).toHaveText('HELD');
  await expect(page.getByText('Time left:')).toBeVisible();
  const countdown = await page.locator('.countdown').innerText();
  await expect(page.locator('.countdown')).not.toHaveText(countdown);
  await page.getByLabel('Student birth registration number').fill((BigInt(id) + 1n).toString());
  await page.getByRole('button', { name: 'Pay Fee', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('application owner');
  await page.getByLabel('Student birth registration number').fill(id);
  await page.getByRole('button', { name: 'Pay Fee', exact: true }).click();
  await expect(page.locator('.status')).toHaveText('CONFIRMED');
  await expect(page.getByRole('button', { name: 'Pay Fee', exact: true })).toBeDisabled();
  await page.reload();
  await expect(page.getByLabel('Student birth registration number')).toHaveValue(id);
  await expect(page.locator('.status')).toHaveText('CONFIRMED');
  await page.getByRole('link', { name: 'View classes', exact: true }).click();
  await rowFor(page, name).getByRole('button', { name: 'Apply', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('already has an active application');

  const cancelledName = uniqueName('Day Shift');
  await createClass(page, cancelledName);
  await rowFor(page, cancelledName).getByRole('button', { name: 'Apply', exact: true }).click();
  await page.getByRole('button', { name: 'Cancel application', exact: true }).click();
  await expect(page.locator('.status')).toHaveText('CANCELLED');
  await page.getByRole('link', { name: 'My applications', exact: true }).first().click();
  await expect(page.locator('.applications li')).toHaveCount(2);
  await expect(page.locator('.applications')).toContainText('CONFIRMED');
  await expect(page.locator('.applications')).toContainText('CANCELLED');
  await page.reload();
  await expect(page.locator('.applications li')).toHaveCount(2);
  await page.getByLabel('Student birth registration number').fill((BigInt(id) + 2n).toString());
  await expect(page.getByText('No applications for this student yet.')).toBeVisible();
  await expect(page.locator('.applications li')).toHaveCount(0);
  expect(errors).toEqual([]);
});

test('seat polling, automatic expiry and reapplication without reloading', async ({ page, context }) => {
  await page.goto('/');
  await page.getByLabel('Student birth registration number').fill(student());
  const name = uniqueName('Expiry Shift');
  await createClass(page, name);
  const observer = await context.newPage();
  await observer.goto('/');
  await expect(rowFor(observer, name).getByRole('button', { name: 'Apply', exact: true })).toBeEnabled();
  await rowFor(page, name).getByRole('button', { name: 'Apply', exact: true }).click();
  await expect(page.locator('.status')).toHaveText('HELD');
  await expect(rowFor(observer, name).getByRole('button', { name: 'Full', exact: true })).toBeDisabled();
  const id = page.url().split('/').pop()!;
  expect(id).toMatch(/^h_[0-9a-f]{32}$/);
  // Make ONLY this test's newly-created hold overdue; the real backend/worker handles it.
  // No new production endpoint or short-duration production configuration is required.
  execFileSync(process.platform === 'win32' ? 'docker.exe' : 'docker',
    ['compose', 'exec', '-T', 'db', 'sh', '-c', 'psql -v ON_ERROR_STOP=1 -U "$POSTGRES_USER" -d "$POSTGRES_DB"'],
    { cwd: fileURLToPath(new URL('../../', import.meta.url)),
      input: `UPDATE "Holds" SET "ExpiresAtUtc" = now() - interval '1 second' WHERE "Id" = '${id}';`,
      timeout: 10000 });
  await expect(page.locator('.status')).toHaveText('EXPIRED');
  await expect(page.getByRole('button', { name: 'Pay Fee', exact: true })).toBeDisabled();
  await expect(page.getByRole('button', { name: 'Cancel application', exact: true })).toBeDisabled();
  await expect(rowFor(observer, name).getByRole('button', { name: 'Apply', exact: true })).toBeEnabled();
  await page.getByRole('link', { name: 'My applications', exact: true }).first().click();
  await expect(page.locator('.applications')).toContainText('EXPIRED');
  await page.getByRole('link', { name: 'Classes', exact: true }).click();
  await rowFor(page, name).getByRole('button', { name: 'Apply', exact: true }).click();
  await expect(page.locator('.status')).toHaveText('HELD');
  await page.getByRole('button', { name: 'Cancel application', exact: true }).click();
  await expect(page.locator('.status')).toHaveText('CANCELLED');
  await observer.close();
});

test('capacity lost to another student produces a conflict and refreshed counts', async ({ page, request }) => {
  await page.goto('/');
  const id = student();
  await page.getByLabel('Student birth registration number').fill(id);
  const name = uniqueName('Capacity Shift');
  const created = await createClass(page, name);
  const competing = await request.post(`http://localhost:8080/classes/${created.id}/holds`,
    { headers: { 'X-Student-Id': (BigInt(id) + 1n).toString() } });
  expect(competing.status()).toBe(201);
  await rowFor(page, name).getByRole('button', { name: 'Apply', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('No seats are available');
  await expect(rowFor(page, name).getByRole('button', { name: 'Full', exact: true })).toBeDisabled();
  const hold = await competing.json();
  expect((await request.delete(`http://localhost:8080/holds/${hold.holdId}`,
    { headers: { 'X-Student-Id': (BigInt(id) + 1n).toString() } })).status()).toBe(204);
});

test('validation, network outage recovery and missing application errors', async ({ page }) => {
  await page.goto('/admin/classes/new');
  await page.getByLabel('Class name', { exact: true }).fill('   ');
  await page.getByLabel('Total seats', { exact: true }).fill('0');
  await page.getByRole('button', { name: 'Create class', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Class name must contain');
  await page.getByLabel('Class name', { exact: true }).fill('Validation class');
  await page.getByLabel('Total seats', { exact: true }).fill('1.5');
  await page.getByRole('button', { name: 'Create class', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('whole number');
  await page.route('**/classes', route => route.abort());
  await page.getByRole('link', { name: 'Classes', exact: true }).click();
  await expect(page.getByRole('alert')).toContainText('Cannot reach the server');
  await page.unroute('**/classes');
  await page.getByRole('button', { name: 'Retry', exact: true }).click();
  await expect(page.getByRole('table')).toBeVisible();
  await expect(page.getByRole('alert')).toHaveCount(0);
  await page.goto('/holds/h_00000000000000000000000000000000');
  await expect(page.getByRole('alert')).toContainText('Hold not found');
  await expect(page.getByRole('button', { name: 'Pay Fee', exact: true })).toHaveCount(0);
});

test('mobile layout remains usable', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await page.goto('/');
  await expect(page.getByRole('table')).toBeVisible();
  const overflow = await page.evaluate(() => document.documentElement.scrollWidth > window.innerWidth);
  expect(overflow).toBe(false);
  await page.screenshot({ path: 'test-results/mobile-classes.png', fullPage: true });
});
