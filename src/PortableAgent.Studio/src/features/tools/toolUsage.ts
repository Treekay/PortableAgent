import type { ToolCatalogEntryDto } from '../../api/contracts';
import type { CallInspection, RunInspection } from '../inspection/inspectionTypes';
import { field } from '../inspection/inspectionTypes';
export interface ToolUsage { call: CallInspection; association: 'Trusted identity' | 'ModelName match only' }
export function toolUsage(tools: ToolCatalogEntryDto[], inspection: RunInspection) {
  const entries = tools.map(tool => ({ tool, observations: [] as ToolUsage[] }));
  const unassociated: { call: CallInspection; reason: string }[] = [];
  for (const call of inspection.calls) {
    if (call.ambiguous) { unassociated.push({ call, reason: 'Duplicate CallId proposals make association ambiguous.' }); continue; }
    const identities = [...new Set(call.executionStarts.map(o => field(o.event, 'toolId')).filter((id): id is string => !!id))];
    const names = [...new Set(call.events.map(o => o.toolName).filter((name): name is string => !!name))];
    const matches = identities.length
      ? entries.filter(e => identities.length === 1 && `${e.tool.toolId.sourceId}/${e.tool.toolId.name}` === identities[0])
      : entries.filter(e => names.length === 1 && e.tool.modelName === names[0]);
    if (matches.length === 1) matches[0].observations.push({ call, association: identities.length ? 'Trusted identity' : 'ModelName match only' });
    else unassociated.push({ call, reason: 'Historical observation cannot be uniquely associated with this persisted catalog.' });
  }
  return { entries, unassociated };
}
