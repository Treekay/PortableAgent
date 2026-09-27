export function UserMessage({ text }: { text: string }) {
  return <div className="message user-message"><div className="message-label">YOU</div><p>{text}</p></div>;
}
