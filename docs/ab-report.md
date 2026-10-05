# Python vs C# A/B report

**Run:** 2026-10-05, production environment `azureaidrivethru-prod` (eastus2), both live container apps at `dev` `2ea69ec`.
**Harness:** `scripts/ab_compare.py --reps 3 --models gpt-realtime-2.1-mini,gpt-realtime-2.1` (all shipped personas). 36 sessions total, 0 errors.

## Method

- Each session talks to the app exactly like the browser: Entra delegated token, `/api/auth/session`, `/realtime?persona=&model=&mode=`, the frontend's own `session.update` (server VAD), then the persona's **recorded Demo Mode guest clips** streamed as PCM16 24 kHz with trailing silence. Production rejects client text turns, so audio is the only faithful path.
- **First audio** = end of the guest's speech to the first assistant audio delta. **Turn** = end of guest speech until no new response starts for 2.5 s (includes tool-call rounds, Azure AI Search, and the full spoken reply).
- **Correctness** = each run produced a priced ticket with no error, and Python and C# produced the *identical* ticket (items, sizes, quantities, total) for the same spoken order.
- **Cold start** = WebSocket connect to `extension.session_metadata` (session ready). **CPU/memory** = `az monitor metrics` `UsageNanoCores` / `WorkingSetBytes` averaged over the run window.

## Summary

- **Parity: 6/6 persona x model combinations produced identical tickets on both backends** ($12.94, $11.55, $11.55 by persona); **36/36 orders correct**.
- **Latency is model-bound, not backend-bound.** First-audio p50 is 1.5 to 2.3 s on both backends; Python and C# differences are within run-to-run noise at 3 reps.
- **`gpt-realtime-2.1-mini` (the default) shortens full turns** by roughly 1 to 2 s at p50 versus `gpt-realtime-2.1` on every persona and both backends, which is why it is the default for transactional ordering.
- **Session ready (cold start) about 2.3 s** on both backends.
- **Resources are small on both:** C# averaged about 0.0036 vCPU and 225 MB working set, Python about 0.0020 vCPU and 215 MB, under this load.
## Latency (p50 / p90, seconds)

| Persona | Backend | Model | First audio p50 | First audio p90 | Turn p50 | Turn p90 |
| --- | --- | --- | --- | --- | --- | --- |
| dunkin | dotnet | gpt-realtime-2.1 | 2.329 | 2.613 | 11.031 | 12.322 |
| dunkin | dotnet | gpt-realtime-2.1-mini | 1.782 | 2.122 | 8.828 | 12.144 |
| dunkin | python | gpt-realtime-2.1 | 1.578 | 1.831 | 10.047 | 10.891 |
| dunkin | python | gpt-realtime-2.1-mini | 1.797 | 2.387 | 8.969 | 9.894 |
| mcdonalds | dotnet | gpt-realtime-2.1 | 1.672 | 2.779 | 9.945 | 12.701 |
| mcdonalds | dotnet | gpt-realtime-2.1-mini | 1.820 | 2.367 | 8.875 | 11.317 |
| mcdonalds | python | gpt-realtime-2.1 | 1.547 | 2.671 | 10.305 | 10.795 |
| mcdonalds | python | gpt-realtime-2.1-mini | 1.812 | 3.131 | 8.429 | 11.939 |
| sonic | dotnet | gpt-realtime-2.1 | 2.015 | 2.629 | 10.296 | 17.009 |
| sonic | dotnet | gpt-realtime-2.1-mini | 1.703 | 2.394 | 8.875 | 11.987 |
| sonic | python | gpt-realtime-2.1 | 1.859 | 2.612 | 10.172 | 17.375 |
| sonic | python | gpt-realtime-2.1-mini | 1.953 | 2.728 | 7.391 | 13.184 |

## Python vs C# ticket parity (same spoken order)

| Persona | Model | Identical ticket | Total |
| --- | --- | --- | --- |
| dunkin | gpt-realtime-2.1 | yes | $12.94 |
| dunkin | gpt-realtime-2.1-mini | yes | $12.94 |
| mcdonalds | gpt-realtime-2.1 | yes | $11.55 |
| mcdonalds | gpt-realtime-2.1-mini | yes | $11.55 |
| sonic | gpt-realtime-2.1 | yes | $11.55 |
| sonic | gpt-realtime-2.1-mini | yes | $11.55 |

## Correctness matrix

| Persona | Backend | Model | Reps | Correct | Errors | Cold start p50 (s) |
| --- | --- | --- | --- | --- | --- | --- |
| dunkin | dotnet | gpt-realtime-2.1 | 3 | 3/3 | 0 | 2.312 |
| dunkin | dotnet | gpt-realtime-2.1-mini | 3 | 3/3 | 0 | 2.312 |
| dunkin | python | gpt-realtime-2.1 | 3 | 3/3 | 0 | 2.281 |
| dunkin | python | gpt-realtime-2.1-mini | 3 | 3/3 | 0 | 2.328 |
| mcdonalds | dotnet | gpt-realtime-2.1 | 3 | 3/3 | 0 | 2.375 |
| mcdonalds | dotnet | gpt-realtime-2.1-mini | 3 | 3/3 | 0 | 2.313 |
| mcdonalds | python | gpt-realtime-2.1 | 3 | 3/3 | 0 | 2.266 |
| mcdonalds | python | gpt-realtime-2.1-mini | 3 | 3/3 | 0 | 2.344 |
| sonic | dotnet | gpt-realtime-2.1 | 3 | 3/3 | 0 | 2.297 |
| sonic | dotnet | gpt-realtime-2.1-mini | 3 | 3/3 | 0 | 2.312 |
| sonic | python | gpt-realtime-2.1 | 3 | 3/3 | 0 | 2.360 |
| sonic | python | gpt-realtime-2.1-mini | 3 | 3/3 | 0 | 2.282 |

## Resource usage (run average)

| Backend | Avg CPU (nanocores) | Avg memory (bytes) |
| --- | --- | --- |
| dotnet | 3,558,388 | 225,449,028 |
| python | 2,019,469 | 215,046,827 |

