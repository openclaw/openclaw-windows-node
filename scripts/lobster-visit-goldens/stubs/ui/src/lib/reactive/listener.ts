// Records document listeners so the harness can raise visibilitychange and pointermove itself.
type Registry = Array<{ type: string; handler: (event: unknown) => void }>;
export function listen(_target: unknown, type: string, handler: (event: any) => void, _options?: unknown): () => void {
  const registry = ((globalThis as any).__listeners ??= []) as Registry;
  const entry = { type, handler };
  registry.push(entry);
  return () => { const index = registry.indexOf(entry); if (index >= 0) registry.splice(index, 1); };
}
