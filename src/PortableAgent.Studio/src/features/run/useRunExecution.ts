import { useEffect, useState, useSyncExternalStore } from 'react';
import { portableAgentApi } from '../../api/portableAgentApi';
import { RunController } from './runController';
export function useRunExecution(provided?: RunController) {
  const [controller] = useState(() => provided ?? new RunController(portableAgentApi));
  const runs = useSyncExternalStore(controller.subscribe, controller.getSnapshot);
  useEffect(() => { controller.activate(); return () => controller.suspend(); }, [controller]);
  return { runs, controller };
}
