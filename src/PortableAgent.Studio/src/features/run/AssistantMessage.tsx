import { Box } from 'lucide-react';
export function AssistantMessage({ text }: { text: string }) {
  return <div className="message assistant-message"><div className="message-label"><Box size={14} aria-hidden="true" /> ASSISTANT</div><p>{text}</p></div>;
}
