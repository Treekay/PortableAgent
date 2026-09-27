import { useState } from 'react';
export function ToolSchema({ schema }: { schema: unknown }) {
  const [copy, setCopy] = useState('Copy schema');
  const json = JSON.stringify(schema, null, 2);
  return <details className="tool-schema"><summary>Input schema</summary><pre aria-label="Input schema JSON">{json}</pre>
    <button onClick={() => { void navigator.clipboard.writeText(json).then(() => setCopy('Copied')).catch(() => setCopy('Copy unavailable')); }}>{copy}</button>
  </details>;
}
