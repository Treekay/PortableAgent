import type { GraphProjection } from './graphProjection';
export function graphLayout(graph: GraphProjection) {
  const positions = new Map<string, { x: number; y: number }>();
  graph.columns.forEach((column, x) => column.forEach((id, y) => positions.set(id, { x: x * 310, y: y * 250 })));
  return positions;
}
