import { expect, test, type Page } from '@playwright/test';
import { writeFile } from 'node:fs/promises';

async function send(page: Page, message: string) {
  await page.getByLabel('Message', { exact: true }).fill(message);
  const post = page.waitForResponse(r => r.request().method() === 'POST' && new URL(r.url()).pathname === '/api/runs');
  await page.getByRole('button', { name: 'Send message' }).click();
  const response = await post; expect(response.status()).toBe(202);
  return await response.json() as { runId: string; runUrl: string };
}
async function navigate(page: Page, name: string) {
  await page.getByRole('navigation', { name: 'Studio views', exact: true }).getByRole('button', { name, exact: true }).click();
}

test('real Flight reject then approve; progressive proxied SSE, narrow approval and durable answer', async ({ page, request }, info) => {
  await page.addInitScript(() => {
    const Native = window.EventSource;
    (window as unknown as { sourceCount: number }).sourceCount = 0;
    window.EventSource = class extends Native { constructor(url: string | URL, config?: EventSourceInit) { super(url, config); (window as unknown as { sourceCount: number }).sourceCount++; } };
  });
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
  await page.getByLabel('Message', { exact: true }).fill('Preserved next draft');
  const sourceCount = await page.evaluate(() => (window as unknown as { sourceCount: number }).sourceCount);
  await navigate(page, 'Trace');
  // The first bound Run remains selected until the reader explicitly chooses another.
  await expect(page.getByLabel('Inspect Run')).toHaveValue(await page.getByLabel('Inspect Run').locator('option').first().getAttribute('value') as string);
  await page.getByLabel('Inspect Run').selectOption({ index: 1 });
  await expect(page.locator('.trace-row')).toHaveCount(9);
  await expect(page.getByRole('button', { name: /#9 .*ApprovalRequired/ })).toBeVisible();
  const selectedClientId = await page.getByLabel('Inspect Run').inputValue();
  await page.screenshot({ path: info.outputPath('trace-paused.png') });
  await navigate(page, 'Graph');
  await expect(page.locator('.react-flow__node')).toHaveCount(5);
  await expect(page.getByText('Waiting approval', { exact: true }).first()).toBeVisible();
  const initialNodeIds = await page.locator('.react-flow__node').evaluateAll(nodes => nodes.map(n => n.getAttribute('data-id')));
  await page.getByRole('button', { name: 'Fit graph', exact: true }).click();
  const approvalNode = page.locator('.react-flow__node').filter({ has: page.locator('.field-label', { hasText: /^Approval$/ }) });
  await approvalNode.click(); await expect(page.getByRole('complementary', { name: 'Selected node inspector' })).toContainText('ApprovalRequired');
  await expect(page.locator('.react-flow__node.draggable')).toHaveCount(0);
  const nodeTransform = await approvalNode.getAttribute('style');
  const box = await approvalNode.boundingBox();
  await page.mouse.move(box!.x + 25, box!.y + 25); await page.mouse.down(); await page.mouse.move(box!.x + 100, box!.y + 60, { steps: 4 }); await page.mouse.up();
  expect(await approvalNode.getAttribute('style')).toBe(nodeTransform);
  await page.keyboard.press('Delete'); await expect(page.locator('.react-flow__node')).toHaveCount(5);
  await page.screenshot({ path: info.outputPath('graph-paused.png') });
  await navigate(page, 'Tools');
  await expect(page.getByText('Persisted tool catalog snapshot', { exact: true })).toBeVisible();
  await expect(page.getByRole('article', { name: 'cancel_booking', exact: true })).toBeVisible();
  await expect(page.getByText('Snapshot status: AwaitingApproval · snapshotSequence: 9')).toBeVisible();
  await expect(page.getByLabel('Inspect Run')).toHaveValue(selectedClientId);
  expect(await page.evaluate(() => (window as unknown as { sourceCount: number }).sourceCount)).toBe(sourceCount);
  await navigate(page, 'Chat');
  await expect(page.getByLabel('Message', { exact: true })).toHaveValue('Preserved next draft');
  await expect(run.locator('summary')).toBeVisible();
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
  await navigate(page, 'Trace'); await expect(page.locator('.trace-row')).toHaveCount(20);
  await expect(page.getByLabel('Inspect Run')).toHaveValue(selectedClientId);
  await page.getByRole('button', { name: /#20 .*RunCompleted/ }).scrollIntoViewIfNeeded();
  await page.screenshot({ path: info.outputPath('trace-completed.png') });
  await navigate(page, 'Graph'); await expect(page.locator('.react-flow__node')).toHaveCount(10);
  const finalNodeIds = await page.locator('.react-flow__node').evaluateAll(nodes => nodes.map(n => n.getAttribute('data-id')));
  expect(initialNodeIds.every(id => finalNodeIds.includes(id))).toBe(true);
  await page.getByRole('button', { name: 'Fit graph', exact: true }).click();
  await page.screenshot({ path: info.outputPath('graph-completed.png') });
  await navigate(page, 'Tools');
  const tool = page.getByRole('article', { name: 'cancel_booking', exact: true });
  await expect(tool).toContainText('Execution starts observed: 1'); await expect(tool).toContainText('Execution completions observed: 1');
  await expect(tool).toContainText('Approved'); await expect(tool.getByText(/RequireApproval/)).toHaveCount(2);
  await tool.getByText('Input schema', { exact: true }).click(); await expect(tool.getByLabel('Input schema JSON')).toContainText('bookingId');
  await page.screenshot({ path: info.outputPath('tools-completed.png') });
  expect(await page.evaluate(() => (window as unknown as { sourceCount: number }).sourceCount)).toBe(sourceCount);
  const inspectionEvidence = JSON.stringify({ runId: approved.runId, sourceCount, selectedClientId,
    pausedEvents: before.length, completedEvents: after.length, initialNodeIds, finalNodeIds,
    catalog: await (await request.get(`/api/runs/${approved.runId}/tools`)).json(),
    observedUsage: await tool.innerText() }, null, 2);
  await writeFile(info.outputPath('phase7b-inspection-evidence.json'), inspectionEvidence);
  await info.attach('phase7b-inspection-evidence', { body: inspectionEvidence, contentType: 'application/json' });
  await page.setViewportSize({ width: 390, height: 844 });
  const mobile = page.getByRole('navigation', { name: 'Mobile Studio views' }); await expect(mobile).toBeVisible();
  for (const view of ['Trace', 'Graph', 'Tools']) {
    await mobile.getByRole('button', { name: view, exact: true }).click();
    if (view === 'Graph') await page.getByRole('button', { name: 'Fit graph', exact: true }).click();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.screenshot({ path: info.outputPath(`narrow-${view.toLowerCase()}.png`) });
  }
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
  await navigate(page, 'Trace'); await page.getByLabel('Inspect Run').selectOption({ index: 1 });
  await expect(page.getByRole('button', { name: /ToolCallProposed/ })).toBeVisible();
  await expect(page.getByRole('button', { name: /PolicyEvaluationCompleted.*Deny/ })).toBeVisible();
  await expect(page.getByRole('button', { name: /ToolExecutionStarted/ })).toHaveCount(0);
  await navigate(page, 'Graph'); await expect(page.locator('.graph-fact').filter({ hasText: 'Blocked by policy' })).toHaveCount(1);
  await navigate(page, 'Tools'); const deniedTool = page.getByRole('article', { name: 'create_staff_task', exact: true });
  await expect(deniedTool).toContainText('pet-mcp / create_staff_task'); await expect(deniedTool).toContainText('Deny'); await expect(deniedTool).toContainText('Execution starts observed: 0');
  await expect(page.getByRole('button', { name: /^(Approve|Reject|Execute|Test tool|Run tool)$/ })).toHaveCount(0);
  await page.screenshot({ path: info.outputPath('pet-tools-denial.png') });
  await page.reload(); await expect(page.getByRole('article')).toHaveCount(0);
});
