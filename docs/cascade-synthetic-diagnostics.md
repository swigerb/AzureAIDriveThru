# Scoped synthetic cascade diagnostics

The C# backend has an optional diagnostic capture for investigating an assistant
speaking stage directions. It does not change model messages, tools, history,
completion selection, audio or retry behavior. Python and realtime are unchanged.
This is diagnostic evidence, not a voice fix or acceptance pass.

Capture is off by default. Enabling requires all four server configuration values:

| Environment variable | Required value |
| --- | --- |
| `CascadeDiagnostics__Enabled` | Exact `true` |
| `CascadeDiagnostics__OperatorObjectId` | Existing authenticated test operator's Entra object ID |
| `CascadeDiagnostics__RunMarker` | Fresh random GUID in 32-character `N` format |
| `CascadeDiagnostics__ExpiresUtc` | UTC timestamp no more than 15 minutes in the future |

The already authenticated `/realtime` WebSocket must also explicitly send the
matching `diagnostic_run` query parameter, with `persona=tacobell` and a cascade
model. The existing Entra principal's `oid` must match; pass-through development
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

## Evidence and privacy

Structured `Synthetic cascade diagnostic` console events contain a synthetic
session ID, response ID and stage. They record ordered roles, original content
lengths, sanitized assistant content, tool names and consistently hashed tool-call
IDs, final choice content/finish reason, and transcript/TTS/completion/cancellation
stages. Request history is capped at 64 messages and trimmed from the oldest end
to approximately 12,000 serialized characters; retained/original counts expose
truncation. Each assistant text is capped at 1,200 characters and each capture at
160 events and its expiry. The claim event counts toward this limit.

No auth headers, keys, operator IDs, run markers, raw guest/system/tool text,
tool arguments, check-in fields or tool-result JSON are copied. Tool content
lengths, not tool content, expose the boundary; this cannot prove the wording
of a hidden tool result. Assistant echoes of demo codes, long opaque tokens,
URLs, emails, long numbers and credential-labelled values are redacted.
Pattern redaction is not a general-purpose PII detector: use only the approved
fictional synthetic script and never enter real personal data or secrets.

Inspect only the exact synthetic session's logs in its bounded time window.
Preserve the unmodified failing audio/transcript and distinguish a model choice
from application emission. Diagnostic success alone is not product acceptance.
