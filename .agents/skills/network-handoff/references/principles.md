# Research basis and limits

Checked 2026-11-09. These are design references, not proof of any OpenClaw
implementation or a requirement to adopt the referenced technology.

| Source | Principle used here | Limit of the analogy |
|---|---|---|
| [Google AIP-151](https://google.aip.dev/151) and [Operations protocol](https://github.com/googleapis/googleapis/blob/master/google/longrunning/operations.proto) | Return a trackable operation for long work; separate start rejection from execution failure; cancellation is best effort and queryable; deleting interest does not cancel work | Do not require Google protobuf/service types, its example duration/retention, or new infrastructure |
| [Google AIP-155](https://google.aip.dev/155) | Request IDs support an explicit idempotency guarantee with a documented window | Optional IDs or stale current-state responses do not establish durable historical attribution |
| [Azure asynchronous request-reply](https://learn.microsoft.com/en-us/azure/architecture/patterns/asynchronous-request-reply) | Prompt acceptance, status resource, bounded polling and retry hints; validate before acceptance | HTTP 202 is not by itself proof of durable acceptance; no requirement for HTTP, queues or callbacks |
| [AWS safe idempotent APIs](https://aws.amazon.com/builders-library/making-retries-safe-with-idempotent-APIs/) | Caller-provided intent identity, semantically equivalent retry responses, parameter conflict and late-arrival handling | A generated identifier without receiver deduplication/persistence is not exactly-once execution |
| [AWS timeouts, retries and jitter](https://aws.amazon.com/builders-library/timeouts-retries-and-backoff-with-jitter/) | Finite measured limits, ambiguous effects after timeout, one retry point, bounded backoff/jitter | Do not copy regional percentile examples as unexplained local constants or treat all server errors as safe mutation retries |
| [gRPC deadlines](https://grpc.io/docs/guides/deadlines/) | Propagate remaining duration after elapsed work to avoid clock-skew assumptions; spawned work needs explicit cancellation handling | RPC cancellation does not prove that an independently accepted operation stopped or compensated |
| [etcd guarantees](https://etcd.io/docs/v3.5/learning/api_guarantees/) | Separate authoritative reads from delayed watches; revision/cursor/history define reconnect recovery; timeout can leave outcome uncertain | etcd's ordered/unique/resumable watch and durable KV guarantees belong to etcd, not arbitrary event buses; no etcd dependency is implied |
| [Kleppmann on fencing](https://martin.kleppmann.com/2016/02/08/how-to-do-distributed-locking.html) | Paused former owners can act after a client-side lease check; the resource receiving the effect must reject obsolete authority | Use the owner's actual serialized/CAS/fencing seam; a random generation ID alone is not an ordered fencing token or authorization |
| [Azure compensating transaction](https://learn.microsoft.com/en-us/azure/architecture/patterns/compensating-transaction) | Compensation is domain-specific, resumable and idempotent; restoring a snapshot can overwrite concurrent work | Do not import generic retry-on-failure or lease timing as a substitute for confirmed ownership |
| [Kubernetes probes](https://kubernetes.io/docs/concepts/workloads/pods/probes/) | Startup, liveness and readiness answer different questions; readiness can regress | Do not require Kubernetes or equate TCP probe success with owned listener, authenticated admission or approved capability |

The immutable terminal-outcome rule in this skill is a required design invariant
for operation records, separate from ongoing health. It is not a claim that all
referenced APIs provide immutable retained historical responses. Scope and
retention must be checked for each producer.
