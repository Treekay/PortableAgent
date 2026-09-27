import { expect, test, type Page } from '@playwright/test';
import { writeFile } from 'node:fs/promises';

async function send(page: Page, message: string) {
  await page.getByLabel('Message', { exact: true }).fill(message);
  const post = page.waitForResponse(r => r.request().method() === 'POST' && new URL(r.url()).pathname === '/api/runs');
  await page.getByRole('button', { name: 'Send message' }).click();
  const response = await post; expect(response.status()).toBe(202);
  return await response.json() as { runId: string; runUrl: string };
}

test('real Flight reject then approve; progressive proxied SSE, narrow approval and durable answer', async ({ page, request }, info) => {
  const streamUrls: string[] = [];
  page.on('response', response => { if (response.headers()['content-type']?.includes('text/event-stream')) streamUrls.push(response.url()); });
  await page.goto('/'); await page.getByLabel('Agent', { exact: true }).selectOption('flight');
  await page.screenshot({ path: info.outputPath('desktop-empty.png') });
  const rejected = await send(page, 'Cancel my booking.');
  await expect(page.getByRole('button', { name: 'Reject', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Reject', exact: true }).click();
  await expect(page.getByText('Understood. I did not cancel the booking.')).toBeVisible();
  const rejectedState = await (await request.get(rejected.runUrl)).json();
  expect(rejectedState.status).toBe('Completed'); expect(rejectedState.toolCalls).toBe(0);

  const approved = await send(page, 'Cancel my booking.');
  const run = page.getByRole('article', { name: 'Run 2', exact: true });
  await expect(run.getByRole('button', { name: 'Approve', exact: true })).toBeVisible();
  await expect(run.getByLabel('Frozen approval arguments')).toHaveText('{\n  "bookingId": "NZ123"\n}');
  await expect(page.getByLabel('Agent', { exact: true })).toBeDisabled();
  await run.locator('summary').click();
  await expect(run.locator('.raw-event code').filter({ hasText: '9 · ApprovalRequired' })).toBeVisible();
  await expect(run.locator('.raw-event code').filter({ hasText: 'RunCompleted' })).toHaveCount(0);
  // The GET still says paused, while the browser has already received nine named SSE events.
  const paused = await (await request.get(approved.runUrl)).json();
  expect(paused.status).toBe('AwaitingApproval'); expect(paused.finalText).toBeNull();
  expect(streamUrls.some(url => url.startsWith('http://localhost:5173/api/') && url.includes(approved.runId))).toBe(true);
  const before = await run.locator('.raw-event code').allTextContents();
  await run.locator('summary').click();
  await run.getByRole('button', { name: 'Approve', exact: true }).scrollIntoViewIfNeeded();
  await page.screenshot({ path: info.outputPath('desktop-approval.png') });
  await page.setViewportSize({ width: 390, height: 844 });
  await run.getByRole('button', { name: 'Approve', exact: true }).scrollIntoViewIfNeeded();
  await expect(run.getByRole('button', { name: 'Approve', exact: true })).toBeInViewport();
  await expect(run.getByRole('button', { name: 'Reject', exact: true })).toBeInViewport();
  expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
  const button = await run.getByRole('button', { name: 'Approve', exact: true }).boundingBox();
  const composer = await page.locator('.composer-dock').boundingBox();
  expect(button!.y + button!.height).toBeLessThanOrEqual(composer!.y);
  await page.screenshot({ path: info.outputPath('narrow-approval.png') });
  await run.getByRole('button', { name: 'Approve', exact: true }).click();
  await expect(run.getByText('Booking NZ123 has been cancelled.')).toBeVisible();
  const toggle = run.getByRole('button', { name: /Completed.*2 model turns.*1 tool call/ });
  await expect(toggle).toHaveAttribute('aria-expanded', 'false');
  await page.setViewportSize({ width: 1440, height: 1000 });
  await page.screenshot({ path: info.outputPath('desktop-completed.png') });
  await toggle.click(); await run.locator('summary').click();
  for (const type of ['ApprovalResolved', 'RunResumed', 'ToolExecutionCompleted', 'RunCompleted'])
    await expect(run.locator('.raw-event code').filter({ hasText: type })).toHaveCount(1);
  const after = await run.locator('.raw-event code').allTextContents();
  const final = await (await request.get(approved.runUrl)).json();
  expect(final.finalText).toBe('Booking NZ123 has been cancelled.'); expect(final.toolCalls).toBe(1);
  const evidence = JSON.stringify({ streamUrls, rejected: rejectedState, beforeApproval: { runId: approved.runId, status: paused.status, events: before }, afterApproval: { events: after, final } }, null, 2);
  await writeFile(info.outputPath('proxied-sse-evidence.json'), evidence);
  await info.attach('proxied-sse-evidence', { body: evidence, contentType: 'application/json' });
});

test('real Pet read and policy denial remain separate independent Runs', async ({ page, request }, info) => {
  await page.goto('/'); await page.getByLabel('Agent', { exact: true }).selectOption('pet');
  const read = await send(page, 'Has Cooper eaten today?');
  await expect(page.getByText('Yes. Cooper was fed at 08:00.')).toBeVisible();
  const denied = await send(page, 'Ask the staff to give Cooper some fresh water.');
  const run = page.getByRole('article', { name: 'Run 2', exact: true });
  await expect(run.getByText('The staff task was not created because the operation was not permitted.')).toBeVisible();
  await run.getByRole('button', { name: /Completed/ }).click();
  await expect(run.getByText('Deny', { exact: true })).toBeVisible(); await expect(run.getByText('Blocked by policy')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Approve', exact: true })).toHaveCount(0);
  const state = await (await request.get(denied.runUrl)).json(); expect(state.toolCalls).toBe(0);
  expect(read.runId).not.toBe(denied.runId);
  await page.screenshot({ path: info.outputPath('pet-denial.png') });
  await page.reload(); await expect(page.getByRole('article')).toHaveCount(0);
});
