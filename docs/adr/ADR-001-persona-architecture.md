# ADR-001: Persona architecture for the unified drive-thru demo

- **Status:** **Accepted**, 2026-09-26T22:29:32-04:00, with Brian's decisions (design doc section 16). Proposed
  2026-09-25.
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
7. **One new Azure environment, two backends.** The azd env `azure-ai-drivethru` holds one Foundry resource and
   one ACA environment, with a Python and a .NET container app. Both serve the same frontend, and a header switch
   moves between their hostnames, keeping persona and model. There is no proxy on the audio path.
8. **Runtime theming.** A `PersonaProvider` applies theme tokens and manifest copy, replacing the hard-coded brand
   colors. The frontend gains persona, model and backend pickers. This is the approved exception to the "no
   frontend changes" rule.
9. **Repo and features.** The repo is renamed `AzureAIDriveThru`. Features work the same for every persona, and
   only brand logic differs. Dunkin's crew dashboard, CRM simulator and Azure Local edge stack, and the dead Azure
   Speech toggle, are not carried over.
10. **Retire the rest.** After Brian's parity sign-off:
    - Day 0: archive the sibling repos and stop the three old container apps;
    - Day 30: move the free Search service (and its indexes) into the new resource group, then delete every old
      resource group and Entra registration.
11. **Release gate.** `dev` merges to `main` only when the whole plan is green:
    - unit tests;
    - conformance for persona x backend x pipeline;
    - Playwright UX per persona x backend;
    - live Azure smoke per persona x backend x model;
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
- Quota must cover the new Foundry deployments while the old environments still hold theirs (#85).

## Alternatives considered

| Option | Why not |
| --- | --- |
| One deployment per brand | Three URLs and three always-on apps (six with C#), with no in-demo switch. Brian chose one deployment |
| Per-brand code plug-ins | Every rule would be written twice (Python and C#) and could drift |
| Keep an off-menu keyword fallback as data | Brian chose no off-menu ordering; the fallback was the source of the substring bugs |
| One hostname through a proxy app | An extra WebSocket hop on the audio path, and more parts to secure. Kept as the fallback if Brian wants one hostname |
| Mid-session persona or model switch | The voice locks after the first audio, and the order rules differ by brand. A new session is cleaner |
