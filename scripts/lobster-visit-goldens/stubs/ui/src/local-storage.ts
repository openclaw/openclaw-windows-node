// The harness installs a fresh in-memory Storage per scenario.
export function getSafeLocalStorage(): Storage | null { return (globalThis as any).__storage ?? null; }
