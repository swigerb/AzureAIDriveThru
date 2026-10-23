# Scoped synthetic model diagnostics

The C# backend has an optional diagnostic capture for investigating an assistant
speaking stage directions. Python cascade and C# realtime also support bounded
structural exchange capture. Capture does not change model messages, tools, history,
completion selection, audio or retry behavior.
This is diagnostic evidence, not a voice fix or acceptance pass.

Capture is off by default. Enabling requires all four server configuration values:

| Environment variable | Required value |
| --- | --- |
| `CascadeDiagnostics__Enabled` | Exact `true` |
| `CascadeDiagnostics__OperatorObjectId` | Existing authenticated test operator's Entra object ID |
| `CascadeDiagnostics__RunMarker` | Fresh random GUID in 32-character `N` format |
| `CascadeDiagnostics__ExpiresUtc` | UTC timestamp no more than 15 minutes in the future |

Use the same four suffixes under `RealtimeDiagnostics__` for C# realtime.
The cascade prefix applies to both backends; Python realtime has no capture.
These switches are independent and disabled by default. No permissions, identities,
model deployments or hosting changes are required.

The already authenticated `/realtime` WebSocket must also explicitly send the
matching `diagnostic_run` query parameter, with `persona=tacobell` and the explicitly
selected pipeline's model. The existing Entra principal's `oid` must match; pass-through development
authentication cannot enable capture. Existing Origin/session-token/Entra checks
remain mandatory. Invalid or expired enabled configuration and rejected opt-ins
produce fixed warnings without printing configuration, identity or marker values.

Only the first matching connection **per process** can claim capture. This is
**not globally durable across replicas or process restarts**. A repeated matching
opt-in on another process could capture another explicitly opted-in operator
session. Use the approved harness with exactly one WebSocket, never a shared
marker or automatic reconnect. Do not change scale or add permissions to run it.
Disable/remove these settings immediately after the one authorized diagnostic
attempt and verify disarm before any further use. Deployment and paid starts
require separate authorization and reviewed-source preflight.
Capture is disabled if resume changes the original server session binding.
Python only claims fresh sessions (resumed sessions are excluded); the C# realtime
observer stops when it sees a browser resume request, even if that resume fails.

## Evidence and privacy

Structured `Synthetic cascade diagnostic` console events contain a synthetic
session ID, response ID and stage. They record ordered roles, original content
lengths, sanitized assistant content, tool names and consistently hashed tool-call
IDs, final choice content/finish reason, and transcript/TTS/completion/cancellation
stages. Request history is capped at 64 messages and trimmed from the oldest end
to approximately 12,000 serialized characters; retained/original counts expose
truncation. Each assistant text is capped at 1,200 characters and each capture at
160 events and its expiry. The claim event counts toward this limit.
If diagnostic sanitization times out, capture stops with a fixed warning that
contains no input text; the chat request/response and guest turn continue unchanged.
Model, HTTP and cancellation failures are not swallowed. Turn correlation follows
the existing serialized barge-in chain: a
cancelled turn finishes before the next turn starts and assigns its response ID.
The real interruption/next-turn regression checks both diagnostic and emitted IDs.

No auth headers, keys, operator IDs, run markers, raw guest/system/tool text,
tool arguments, check-in fields or tool-result JSON are copied. Tool content
lengths, not tool content, expose the boundary; this cannot prove the wording
of a hidden tool result. Assistant echoes of demo codes, long opaque tokens,
URLs, emails, long numbers and credential-labelled values are redacted.
Pattern redaction is not a general-purpose PII detector: use only the approved
fictional synthetic script and never enter real personal data or secrets.

### Additional boundary coverage

C# realtime observes actual serialized upstream sends and complete receives,
including fragmented text frames, without modifying them. Outbound `session.update`
and `response.create` include instruction SHA256/length, tool names/schema hashes,
tool-choice and explicit input/tool presence. Inbound `session.updated` records the
acknowledged instruction hash/length and tools, not just what was sent. An absent
or omitted echoed field remains absent evidence, not proof it applied. Compare
hashes against the same-source bound loader and composed greeting override using
UTF-8 SHA256 (uppercase hex); schema hashes use the emitted JSON serialization.
`response.created`, output-item completion, transcript completion and `response.done`
record hashed response/item/call IDs, provider phase if present, sanitized assistant
content and status. Commentary and final remain audible; nothing is suppressed.
Audio bytes, input transcription and raw tool output are never logged.
Frames exceeding128KiB are omitted; capture events exceeding16,000 serialized
characters stop capture with a fixed warning. Frame assembly uses short locks
without awaits; socket ownership remains with the existing caller.

Python cascade observes each actual SDK completion invocation and its returned
choice, including tool rounds, scoped greeting and retry invocations. Ordered roles,
content lengths, system-policy hashes, sanitized assistant history/choices,
tool names/schema hashes, paired call-ID hashes, request index and finish reason
identify the earliest visible wrong choice. Guest commit and cancellation stages
are correlated to the existing response ID; original guest/tool content has no
hash because predictable-content hashes can disclose excluded values. This cannot
verify exact guest wording or private tool wording from diagnostics alone. The
browser's authoritative snapshot does not prove identical model-facing tool text.
Compare guest timing with the independently retained synthetic browser evidence.
System hashes exclude plaintext and use UTF-8 SHA256; Python schema hashes use
sorted compact JSON of the SDK tool definition. No new tool-state parser or raw
reward-state projection is introduced.

Python uses bounded linear token sanitization rather than backtracking regex.
Sanitization/type failures stop capture with a fixed warning; the original SDK
request, returned completion and cancellation continue. Both new paths cap captures
at160events/expiry and16,000serialized characters per event. A truncated/omitted
boundary is an evidence gap, never success. C# retains its100ms regex timeout.
All redaction remains imperfect for free-form names or other novel personal data:
the exact authenticated operator must use fictional synthetic inputs only.

Inspect only the exact synthetic session's logs in its bounded time window.
Preserve the unmodified failing audio/transcript and distinguish a model choice
from application emission. Diagnostic success alone is not product acceptance.
