# ADR-001: Persona architecture for the unified drive-thru demo

- **Status:** **Accepted**, 2026-09-26T22:29:32-04:00, with Brian's decisions (design doc section 16),
  including the environment details of 22:52 (decision 11). Proposed 2026-09-25.
  **Amended 2026-09-28** (issue #155): decision 7 (local mode) is reversed -- see the note under decision 6
  below.
- **Issues:** #19 (P1 spike), including the design for #51. Part of epic #6. Implemented by #69 to #88 (P2),
  then #12 to #18 and #21 (C#).
- **Deciders:** Brian Swiger (owner), Rick (lead)
- **Full design:** [`docs/persona-architecture.md`](../persona-architecture.md)

## Context

We ran three forks of the same VoiceRAG drive-thru demo: Sonic (this repo), McDonald's and Dunkin. They shared
about 90% of their code, but each brand's rules lived in hard-coded Python (name-keyed tables and substring
keywords), Dunkin's prompt was an inline string, and brand colors were hard-coded in React. Sonic is far ahead on
hardening (S1, S1.5).

Brian's goal is one app on Microsoft Foundry:
- a Python backend (the reference) and a C# (.NET 11) backend;
- one shared conformance suite keeping them in sync;
- persona switching between Sonic, McDonald's and Dunkin, with every brand's rules intact;
- model flexibility.

The design doc diffs all three repos and classifies every difference (51 rows). Almost everything is data. Only
one behavior (McDonald's meal-number lookup) needs a named strategy.

## Decision

1. **Data-first persona packs.** Each brand is `personas/<id>/`: `persona.json` (rules, strategies, models, UI
   manifest; schema-validated), `prompts/*.yaml`, `menu/menuItems.json` with the #51 per-item fields, and
   `assets/`. Adding a brand means adding a folder.
2. **One contract, two backends.** Python and C# load the same files, fail fast on an invalid pack, and expose
   the same HTTP and WebSocket contract. Shared engines read the pack. A closed set of named strategies covers
   what data can't: one in P2, `searchQueryRewrite: "meal_numbers"`.
3. **Per-session persona, one deployment.** The browser opens `/realtime?persona=<id>&model=<id>`. Both values
   are fixed for the session, including resume. The one deployment enables all three personas on a single URL,
   with `PERSONAS` kept as an allow-list.
4. **No off-menu ordering.** Anything not on the persona's menu, as built from its source data, is rejected
   (`not_on_menu`).
   - Every keyword fallback is removed.
   - Menus are completed from source data first, including Sonic's fountain drinks and add-ons.
   - Floats are added to Sonic's menu: full price at happy hour, but they can fill the combo drink slot (#64).
   - Golden tables are checked against the data, never generated from it.
5. **Per-persona happy hour.** Sonic and Dunkin have one, and both announce it. McDonald's has none
   (`pricing.happyHour: null`). The switch stays per persona.
6. **Model flexibility on Microsoft Foundry.** Three pipelines sit behind one browser contract:
   - `realtime`: Foundry realtime models;
   - `cascade`: Foundry transcription, then a Foundry chat model with tools (OpenAI or not), then TTS;
   - `local`: McDonald's on-device mode, now persona-agnostic and off by default.

   A shared catalog lists the models, Bicep maps each to the deployment that exists, the persona allows a subset,
   and the session picks one.

   **Amended 2026-09-28 (decision 7 reversed, issue #155): local mode is dropped entirely.** Brian decided the
   demo runs on Microsoft Foundry exclusively -- there is no local runtime, no ONNX, and no `phi-4-mini-local`.
   Only two pipelines remain, `realtime` and `cascade`. The `local` pipeline, its catalog/persona/schema entries,
   its processor and companion-runtime client, its conformance fakes and rows, and every `LOCAL_RUNTIME_*`
   setting were removed. See `docs/persona-architecture.md` section 7.6 for the as-built design this reversal
   removed, and #155 for the full removal.
7. **One new, independent Azure environment, two backends.** Subscription `BrianSwiger-Microsoft-External-2026`,
   eastus2 (every resource except AI Search, which is in eastus because eastus2 had no Basic-SKU Search capacity
   at provision time, #87), azd env `azureaidrivethru-prod`, resource group `rg-azureaidrivethru-prod`. It has
   its own Foundry (Azure OpenAI) account, its own paid AI Search service with one index per persona, and one ACA
   environment with a Python and a .NET container app. It has zero dependency on the old resource groups.
   Realtime starts at GlobalStandard capacity 10 or less (the spare quota) and scales after cutover. Both apps
   serve the same frontend, and a header switch moves between their hostnames, keeping persona and model. There
   is no proxy on the audio path.
8. **Runtime theming.** A `PersonaProvider` applies theme tokens and manifest copy, replacing the hard-coded brand
   colors. The frontend gains persona, model and backend pickers. This is the approved exception to the "no
   frontend changes" rule.
9. **Repo and features.** The repo is renamed `AzureAIDriveThru`. Features work the same for every persona, and
   only brand logic differs. Dunkin's crew dashboard, CRM simulator and Azure Local edge stack, and the dead Azure
   Speech toggle, are not carried over.
10. **Retire the rest.** After Brian's parity sign-off, with his confirmation right before each destructive step:
    - Day 0 (cutover): archive the sibling repos; delete `rg-mcd-demo` and `rg-dunkin-demo`; delete everything in
      `rg-sonic-demo` (apps, ACR, identity, storage, Log Analytics, and the old shared AOAI including the orphaned
      `gpt-realtime-1.5`) except the old shared Search service and its three indexes; delete the old Entra
      registrations; scale the new realtime deployment.
    - Day 30: delete the old Search service and the empty `rg-sonic-demo`.
    - Dunkin's edge stack was never deployed, so its removal is code-only.
11. **Release gate.** `dev` merges to `main` only when the whole plan is green:
    - unit tests;
    - conformance for persona x backend x pipeline;
    - Playwright UX per persona x backend;
    - live Azure smoke per persona x backend x model, plus the zero-dependency check;
    - Brian's manual UX sign-off.

## Consequences

**Good**
- One codebase, one suite, one environment. A fourth brand is a folder plus golden files.
- The C# port is done once, persona- and model-aware from the start, with no name tables to transcribe.
- No off-menu ordering removes a whole class of substring pricing bugs.
- The demo can show the same brand on different Foundry models, live.

**Costs and risks**
- The Python module globals become per-session objects. This is done behind the existing suite
  (#70, #71, #74).
- No off-menu ordering flips about a dozen conformance rows and requires complete menus first (#72, #73).
- The cascade pipeline needs new harness fakes, and each chat model must qualify on tool calling (#82).
- The suite's run time grows with persona x backend x pipeline. It is sharded if needed.
- Realtime quota is tight until cutover (about 10 spare GlobalStandard units), so the new environment starts
  small and scales only after the old AOAI is deleted (#85, #88).
- A paid Search service costs more than the old free one; it is the price of independence and of more than
  three indexes.

## Alternatives considered

| Option | Why not |
| --- | --- |
| One deployment per brand | Three URLs and three always-on apps (six with C#), with no in-demo switch. Brian chose one deployment |
| Per-brand code plug-ins | Every rule would be written twice (Python and C#) and could drift |
| Keep an off-menu keyword fallback as data | Brian chose no off-menu ordering; the fallback was the source of the substring bugs |
| One hostname through a proxy app | An extra WebSocket hop on the audio path, and more parts to secure. Kept as the fallback if Brian wants one hostname |
| Reuse the old shared AOAI and free Search in the new environment | Couples the new environment to `rg-sonic-demo`, which is being torn down. Brian chose full independence |
| Mid-session persona or model switch | The voice locks after the first audio, and the order rules differ by brand. A new session is cleaner |
