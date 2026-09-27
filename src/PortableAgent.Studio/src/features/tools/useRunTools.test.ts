import { expect, it, vi } from 'vitest';
import { ToolsQueryCache } from './useRunTools';
import { catalog } from '../../test/inspectionFixtures';
import { deferred, runId } from '../../test/fixtures';
import type { RunToolsDto } from '../../api/contracts';
it('single-flights, coalesces refresh and refuses an older snapshot', async () => {
  const pending = deferred<RunToolsDto>(), getter = vi.fn().mockReturnValueOnce(pending.promise).mockResolvedValue(catalog({ snapshotSequence: 9, status: 'Running' }));
  const cache = new ToolsQueryCache(getter); cache.activate();
  const first = cache.refresh(runId); void cache.refresh(runId); void cache.refresh(runId); expect(getter).toHaveBeenCalledTimes(1);
  pending.resolve(catalog()); await first; await Promise.resolve(); expect(getter).toHaveBeenCalledTimes(2); expect(cache.get(runId).data?.snapshotSequence).toBe(20); cache.suspend();
});
it('caches per run and checkpoint without querying on every event', async () => {
  const getter = vi.fn().mockResolvedValue(catalog()), cache = new ToolsQueryCache(getter); cache.activate();
  cache.ensure(runId, 'Completed:20'); await Promise.resolve(); cache.ensure(runId, 'Completed:20'); expect(getter).toHaveBeenCalledTimes(1);
  cache.ensure(runId, 'Completed:21'); await Promise.resolve(); expect(getter).toHaveBeenCalledTimes(2); cache.suspend();
});
it('late responses cannot overwrite a different selected Run or a replacement request', async () => {
  const old = deferred<RunToolsDto>(), getter = vi.fn().mockReturnValueOnce(old.promise).mockResolvedValue(catalog({ runId: 'second' }));
  const cache = new ToolsQueryCache(getter); cache.activate(); const first = cache.refresh(runId); await cache.refresh('second');
  old.resolve(catalog()); await first; expect(cache.get('second').data?.runId).toBe('second');
  const stale = deferred<RunToolsDto>(); getter.mockReturnValueOnce(stale.promise); const abandoned = cache.refresh(runId); cache.suspend(); cache.activate();
  getter.mockResolvedValue(catalog({ snapshotSequence: 30 })); await cache.refresh(runId); stale.resolve(catalog()); await abandoned;
  expect(cache.get(runId).data?.snapshotSequence).toBe(30); cache.suspend();
});
it('query errors preserve the cached snapshot and do not mutate Runtime state', async () => {
  const getter = vi.fn().mockResolvedValue(catalog()), cache = new ToolsQueryCache(getter); cache.activate(); await cache.refresh(runId);
  getter.mockRejectedValue(new Error('offline')); await cache.refresh(runId); expect(cache.get(runId)).toMatchObject({ state: 'error', error: 'offline', data: { snapshotSequence: 20 } }); cache.suspend();
});
