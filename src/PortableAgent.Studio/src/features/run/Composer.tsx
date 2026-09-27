import { useRef } from 'react';
import { ArrowUp } from 'lucide-react';
export function Composer({ value, onChange, onSubmit, disabled, busy }: { value: string; onChange: (value: string) => void; onSubmit: () => void; disabled: boolean; busy: boolean }) {
  const composing = useRef(false);
  const cannotSend = disabled || !value.trim() || value.length > 8000;
  return <div className="composer-wrap"><form onSubmit={e => { e.preventDefault(); if (!cannotSend) onSubmit(); }}>
    <label className="sr-only" htmlFor="message">Message</label>
    <textarea id="message" placeholder="Give the agent a task…" value={value} maxLength={8000} rows={2}
      onChange={e => onChange(e.target.value)} onCompositionStart={() => { composing.current = true; }} onCompositionEnd={() => { composing.current = false; }}
      onKeyDown={e => { if (e.key === 'Enter' && !e.shiftKey && !e.nativeEvent.isComposing && !composing.current && e.keyCode !== 229) { e.preventDefault(); if (!cannotSend) onSubmit(); } }} />
    <div className="composer-actions"><span>{busy ? 'Finish the current run to send another task.' : 'Enter to send · Shift + Enter for a new line'}</span>
      <span className="character-count">{value.length.toLocaleString()} / 8,000</span>
      <button type="submit" className="send-button" aria-label="Send message" disabled={cannotSend}><ArrowUp size={19} /></button>
    </div>
  </form><p className="composer-note">Each message starts a new independent run. <span>History resets on refresh.</span></p></div>;
}
