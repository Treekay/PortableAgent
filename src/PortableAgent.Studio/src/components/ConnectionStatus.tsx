export function ConnectionStatus({ state }: { state: 'loading' | 'ready' | 'error' }) {
  return <span className={`connection ${state}`}><span className="connection-dot" aria-hidden="true" />
    {state === 'loading' ? 'Checking API' : state === 'error' ? 'API unavailable' : 'Agent list loaded'}
  </span>;
}
