// Proof-only: timestamp settlement when it happens, not when a later await observes it.
export const recordCompletion = (promise, clock = () => performance.now()) =>
  promise.then(value => ({ value, completedAt: clock() }));
