// Stands in for @openclaw/normalization-core, which the lobster modules use only for expectDefined.
export function expectDefined<T>(value: T | undefined | null, label: string): T { if (value === undefined || value === null) { throw new Error(label); } return value; }
