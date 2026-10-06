# Python vs C# A/B report

**Run:** 2026-10-06 01:03-01:35 UTC against the production environment `azureaidrivethru-prod`
(eastus2). Both live container apps were on `dev` `2b46696` (the release candidate, including
#313 read-back and #327 canonical item names). Command:
`python scripts/ab_compare.py --reps 3 --models gpt-realtime-2.1-mini,gpt-realtime-2.1`
over every shipped persona: 36 sessions, 0 errors. **n = 3 per cell**, so treat p90 as roughly the
worst of three runs, not a stable tail estimate.

## Method

- Each session talks to the app the way the browser does: an Entra delegated token,
  `/api/auth/session`, `/realtime?persona=&model=&mode=`, the frontend's own `session.update`
  (server VAD), then the persona's recorded Demo Mode guest clips streamed as PCM16 24 kHz,
  followed by 1.2 s of trailing silence so server VAD ends the turn. Production rejects client
  text turns, so streamed audio is the only faithful path.
- **First audio** is measured from the end of the guest's speech to the first assistant audio
  delta. Frames are read concurrently while the trailing silence streams, so there is no
  measurement floor.
- **Turn** is measured from the end of the guest's speech until no new response starts for
  2.5 s. It includes tool-call rounds, Azure AI Search and the full spoken reply, **plus that
  fixed 2.5 s settle tail**, so compare turn times relative to each other, not as absolute
  response times.
- **Produced ticket** means the run ended with a priced, non-empty ticket and no error. It is
  not an accuracy check on its own.
- **Identical ticket** (parity) is the cross-backend correctness signal. Every rep on both
  backends must produce the same single ticket: items, sizes, quantities and total.
- **Session ready** is WebSocket connect to `extension.session_metadata` on an already-running
  app. It is not a container cold start.
- **CPU and memory** are `az monitor metrics` `UsageNanoCores` / `WorkingSetBytes`, averaged
  over the exact run window. The backends run one after another, so each average also includes
  that app's idle time while the other backend was being exercised.

## Summary

- **Ticket parity: 5 of 6 persona x model cells were identical across all 6 reps.** Every rep in
  every cell had the same items, sizes, quantities and total on both backends. The one "NO"
  cell differs only in the free-text guest customization the model wrote for a Dunkin regular
  coffee: once "(Cream and Sugar)" instead of the menu's documented "(Cream, Sugar)". Both
  backends store guest customizations verbatim by design, so this is model phrasing variance,
  not a backend difference. #325 / #327 already removed the larger source of variance (item
  names stored in the model's spelling); the previous run had 3 of 6 identical.
- **36/36 sessions produced a priced ticket**, with 0 errors and 0 timed-out turns.
- **`gpt-realtime-2.1-mini` shortened turn p50 by about 1.6 to 3.1 s** versus `gpt-realtime-2.1`
  on every persona and both backends. Its p90 was not uniformly better: McDonald's on C# had one
  slow mini run (19.5 s).
- **First-audio p50 was 1.4 to 2.5 s across all cells.** At n = 3 there was no consistent
  ordering between Python and C#, or between the two models.
- **Session ready was about 2.3 s** on both backends.
- **Resources were small on both** under this sequential load: C# averaged about 0.007 vCPU and
  222 MB working set, Python about 0.005 vCPU and 203 MB.
## Latency (p50 / p90, seconds)

| Persona | Backend | Model | First audio p50 | First audio p90 | Turn p50 | Turn p90 |
| --- | --- | --- | --- | --- | --- | --- |
| dunkin | dotnet | gpt-realtime-2.1 | 1.969 | 2.569 | 11.828 | 13.638 |
| dunkin | dotnet | gpt-realtime-2.1-mini | 1.766 | 2.751 | 8.719 | 10.378 |
| dunkin | python | gpt-realtime-2.1 | 1.781 | 1.932 | 10.828 | 13.153 |
| dunkin | python | gpt-realtime-2.1-mini | 2.360 | 3.775 | 8.844 | 9.832 |
| mcdonalds | dotnet | gpt-realtime-2.1 | 1.922 | 3.503 | 11.101 | 13.308 |
| mcdonalds | dotnet | gpt-realtime-2.1-mini | 1.797 | 2.435 | 9.469 | 19.497 |
| mcdonalds | python | gpt-realtime-2.1 | 1.383 | 1.589 | 10.860 | 11.659 |
| mcdonalds | python | gpt-realtime-2.1-mini | 1.837 | 2.183 | 8.602 | 11.761 |
| sonic | dotnet | gpt-realtime-2.1 | 2.515 | 2.703 | 11.312 | 15.969 |
| sonic | dotnet | gpt-realtime-2.1-mini | 1.829 | 2.472 | 8.407 | 11.625 |
| sonic | python | gpt-realtime-2.1 | 1.938 | 2.579 | 12.094 | 16.716 |
| sonic | python | gpt-realtime-2.1-mini | 1.750 | 2.066 | 8.969 | 11.931 |

## Python vs C# ticket parity (same spoken order)

| Persona | Model | Identical ticket | Python tickets | Dotnet tickets | Total |
| --- | --- | --- | --- | --- | --- |
| dunkin | gpt-realtime-2.1 | yes | 1 | 1 | $12.94 |
| dunkin | gpt-realtime-2.1-mini | NO | 1 | 2 | $12.94 |
| mcdonalds | gpt-realtime-2.1 | yes | 1 | 1 | $11.55 |
| mcdonalds | gpt-realtime-2.1-mini | yes | 1 | 1 | $11.55 |
| sonic | gpt-realtime-2.1 | yes | 1 | 1 | $11.55 |
| sonic | gpt-realtime-2.1-mini | yes | 1 | 1 | $11.55 |

## Correctness matrix

| Persona | Backend | Model | Reps | Produced ticket | Errors | Session ready p50 (s) |
| --- | --- | --- | --- | --- | --- | --- |
| dunkin | dotnet | gpt-realtime-2.1 | 3 | 3/3 | 0 | 2.390 |
| dunkin | dotnet | gpt-realtime-2.1-mini | 3 | 3/3 | 0 | 2.328 |
| dunkin | python | gpt-realtime-2.1 | 3 | 3/3 | 0 | 2.328 |
| dunkin | python | gpt-realtime-2.1-mini | 3 | 3/3 | 0 | 2.516 |
| mcdonalds | dotnet | gpt-realtime-2.1 | 3 | 3/3 | 0 | 2.265 |
| mcdonalds | dotnet | gpt-realtime-2.1-mini | 3 | 3/3 | 0 | 2.281 |
| mcdonalds | python | gpt-realtime-2.1 | 3 | 3/3 | 0 | 2.297 |
| mcdonalds | python | gpt-realtime-2.1-mini | 3 | 3/3 | 0 | 2.297 |
| sonic | dotnet | gpt-realtime-2.1 | 3 | 3/3 | 0 | 2.360 |
| sonic | dotnet | gpt-realtime-2.1-mini | 3 | 3/3 | 0 | 2.297 |
| sonic | python | gpt-realtime-2.1 | 3 | 3/3 | 0 | 2.281 |
| sonic | python | gpt-realtime-2.1-mini | 3 | 3/3 | 0 | 2.313 |

## Resource usage (run average)

Measured over the actual run window (2026-10-06T01:03:44Z to 2026-10-06T01:35:10Z). Backends are exercised one after another, never concurrently, so this window covers both.

| Backend | Avg CPU (nanocores) | Avg memory (bytes) |
| --- | --- | --- |
| dotnet | 7,058,203 | 221,870,336 |
| python | 4,789,774 | 203,363,712 |

