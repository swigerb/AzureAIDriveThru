# Persona architecture: one drive-thru app, three brands, two backends

- **Issue:** #19 (P1 design spike), including the design for #51. Part of epic #6.
- **Decision record:** [ADR-001](adr/ADR-001-persona-architecture.md); authentication:
  [ADR-002](adr/ADR-002-entra-authentication.md) (Proposed, section 18)
- **Status:** **Accepted**, 2026-09-26T22:29:32-04:00, with Brian's decisions (section 16),
  including the environment details of 22:52 (decision 11). Proposed 2026-09-25T19:10:59-04:00.
- **Author:** Rick (lead)
- **Repo:** this repo is being renamed `AzureAIDriveThru` (#69).

## 1. Summary

One app serves Sonic, McDonald's and Dunkin on Microsoft Foundry. It has a Python backend (the reference) and a
C# (.NET 11) backend. Each brand is a **persona pack**: a folder of data (`persona.json`, prompts,
`menuItems.json`, assets) that both backends load through the same contract. Brand rules are data, read by
shared engines. The one behavior data can't express (McDonald's meal-number lookup) is a named strategy that both
backends implement.

Brian's decisions (2026-09-26, section 16):

- **Environment:** one new Azure environment. All three personas are on one URL and are picked per session.
- **Menu:** no off-menu ordering. Floats are Sonic menu items: full price at happy hour, but they can fill the
  combo drink slot.
- **Happy hour:** only Sonic and Dunkin have one, and both announce it.
- **Features:** McDonald's local mode was kept as a persona-agnostic pipeline, then dropped entirely (issue #155,
  2026-09-28, reversing this decision -- see ADR-001 decision 7). Dunkin's crew dashboard, CRM simulator and edge
  stack are dropped.
- **Models:** model flexibility is a first-class feature. Realtime and chat models are selectable per deployment,
  persona and session (section 7).
- **Repo and cleanup:** the repo is renamed `AzureAIDriveThru`. After parity, the sibling repos are archived and
  every old environment is torn down at cutover. Only the old shared Search service and its three indexes are
  kept, for a 30-day grace.
- **New environment (section 10):** azd env `azureaidrivethru-prod`, resource group `rg-azureaidrivethru-prod`,
  eastus2, subscription `BrianSwiger-Microsoft-External-2026`. It has its own Foundry (Azure OpenAI) account and
  its own paid AI Search service with one index per persona, and zero dependency on `rg-sonic-demo`,
  `rg-mcd-demo` or `rg-dunkin-demo`.

P2 is 20 issues (#69 to #88) in milestone "P2 Unified demo (Python)" (section 13). The C# port (#12 to #18, #21)
then ports the unified app once and deploys into the same environment (section 14). `dev` merges to `main` only
after the full validation plan in section 12 is green.

## 2. Inputs and method

| Repo | Role | Branch and HEAD read | Working tree |
| --- | --- | --- | --- |
| `swigerb/SonicAIDriveThru` | Primary, reference | `dev` at `d720e16` (spike start); this branch is cut from `origin/dev` `0806b52`, which only adds a Scribe log commit | Clean apart from Scribe's `.squad` notes |
| `swigerb/McDonalds_AI_DriveThru` | Sibling, read-only | `dev` at `cb8cfb5` (Merge #5: scrub session.updated) | Clean |
| `swigerb/dunkin-chat-voice-assistant` | Sibling, read-only | `dev` at `efa6b64` (Merge #10: scrub session.updated) | Clean |

Method: a file-level diff of all tracked files, then a line-level diff of every shared backend, frontend, infra
and script file, then a direct read of each brand's rules (`menu_utils.py`, `order_state.py`, `tools.py`,
prompts, `config.yaml`, `menuItems.json`, `index.css`, `App.tsx`). Differences caused only by Sonic being ahead
on hardening (S1, S1.5) are not brand differences. They are listed once, in the last row of the inventory.

Live topology (from each repo's azd environment, 2026-09-25):
- **Resource groups:** three (`rg-sonic-demo`, `rg-mcd-demo`, `rg-dunkin-demo`), each with its own container app
  and its own Entra app registration.
- **Shared resources in `rg-sonic-demo`:**
  - the Azure OpenAI resource `cog-axgpampkq3yfa`, which the siblings reuse;
  - **one free Search service**, `gptkb-axgpampkq3yfa` (eastus, semantic ranker disabled), holding the three
    indexes (`sonic-menu-items`, `mcdonalds-menu-items`, `dunkin-menu-items`).
- **Deployments:** Sonic uses the `gpt-realtime-2.1` deployment; the siblings use `gpt-realtime-2.1-dz`.
- **Search limits:** the comment in `setup_search_index.py` notes that the free SKU allows one service with three
  indexes.

## 3. Difference inventory

Classes:

- **Shared code:** one code path for every persona.
- **Persona data:** lives in the persona pack.
- **Strategy:** named, closed-set behavior in shared code, selected by the pack. Both backends implement it.
- **Drop:** not carried into the unified app. It stays in the archived repo's history.

"Mc" is McDonald's. File references are to each repo's `dev` HEAD above.

### 3.1 Prompts and conversation text

| # | Item | Sonic | Mc | Dunkin | Class | Unified home |
| --- | --- | --- | --- | --- | --- | --- |
| 1 | System prompt | `prompts/sonic/system_prompt.yaml`, 22 sections incl. `SONIC_BRANDING_AND_SIZING`, `HAPPY_HOUR`, `COMBO_LOGIC` | `prompts/mcdonalds/system_prompt.yaml`, 253 lines, adds `COMBO_MEAL_SYNONYMS`, `EXTRA_VALUE_MEALS` | Inline Python string `DUNKIN_SYSTEM_PROMPT` in `app.py:31` (about 14 sentences) | Persona data | `personas/<id>/prompts/system_prompt.yaml` (Dunkin converted to YAML) |
| 2 | Greeting | `greeting.yaml`: "Welcome to Sonic Drive-In! ..."; a second hard-coded copy in `session_manager.py:181` | `greeting.yaml`: "Welcome to McDonald's! ..." | Hard-coded in `rtmt.py:260`: "Welcome to Dunkin! How may I help you today?" | Persona data | `prompts/greeting.yaml`; the code copies are removed |
| 3 | Role name in nudge and transcript replay | "carhop" (`session_manager.py:153`, `:455`) | "crew member" | "crew" (`rtmt.py:991`) | Persona data | `persona.json` `roleName` |
| 4 | Tool schemas (descriptions) | `tool_schemas.yaml` | `tool_schemas.yaml` allows only add/remove, and it wins over the inline schema in `tools.py:413` that has `modify` (`tools.py:685`). The prompt (`system_prompt.yaml:155`) still tells the model to call `modify`, so `modify` is dormant: a sibling bug | Inline in `tools.py:296`, telling the model that extras are separate items | Persona data (text); shared code (`modify`) | `prompts/tool_schemas.yaml`; `modify` offered only where the pack's schema lists it (McDonald's, fixed in #78) |
| 5 | Upsell hints, delta templates | `hints.yaml`, plus in-code fallbacks in `tools.py:533` | `hints.yaml` (McFlurry), plus `tools.py:596` | None | Persona data | `prompts/hints.yaml`; the in-code fallbacks are deleted |
| 6 | Error and refusal text | `error_messages.yaml` | `error_messages.yaml` | Inline strings in `tools.py:408` | Persona data | `prompts/error_messages.yaml` |
| 7 | Happy-hour banner in tool results | Hard-coded in `tools.py:547` and `:569` | "drinks and slushes are half-price!" (a Sonic leftover) at `tools.py:609` | None: Dunkin's happy hour is silent | Persona data | `persona.json` `pricing.happyHour.banner` |
| 8 | Missing-combo-part hint | "a side (fries or tots)", "a drink or slush" in `order_state.py:338` | "... to finish their meal" | n/a | Persona data | `persona.json` `bundles.missingPartText` |
| 9 | Local-model prompt | n/a | `local_system_prompt.yaml` (Phi-4) | n/a | Persona data | Never landed as a per-pack file: the persona-agnostic local pipeline this row fed (row 37, #81) was dropped entirely by #155 before shipping |

### 3.2 Menu, index and ingestion

| # | Item | Sonic | Mc | Dunkin | Class | Unified home |
| --- | --- | --- | --- | --- | --- | --- |
| 10 | Menu data | `menuItems.json`: 6 categories, 60 items (10 combos); fields `name, sizes, description, longDescription, origin, popularity, image`; raw export `sonic-menu-items.json` (3.4 MB) | 5 categories, 71 items (20 meals); adds `menuPeriod` (breakfast 20, lunch 32, allDay 19), `mealNumber`, `calories`, `allergens`; sources `mcdonalds-menu-items.json`, `offline_menu.json` | 5 categories, 16 items; adds `caffeineContent`, `brewingMethod`, `calories`, `availability`; `structured_menu_items`; 3 PDFs in `public/` | Persona data | `personas/<id>/menu/menuItems.json`; raw exports in `menu/source/` |
| 11 | Search index name | `sonic-menu-items` | `mcdonalds-menu-items` | Live: `dunkin-menu-items`; stale defaults: `coffee-chat` (`.env-sample`) and `voicerag-intvect` (`main.parameters.json`) | Persona data | `persona.json` `search.indexName` |
| 12 | Index schema | `id, category, name, description, longDescription, origin, caffeineContent, brewingMethod, popularity, sizes, embedding` | Narrower: `id, category, name, description, sizes, embedding` | Same as Sonic | Shared code | One superset schema. Adding fields is additive for the McD index |
| 13 | Search content fields | `description` | `description, longDescription, category` | `description, longDescription, category` | Persona data | `persona.json` `search.contentFields` (default `description`) |
| 14 | Generic ingestion | `setup_search_index.py` | Same, McD-flavored strings | Same, Dunkin-flavored strings | Shared code | One script with `--persona` (or it loops over `PERSONAS`) |
| 15 | Brand raw-export converters | `extract_production_items.py`, `update_menu_sizes.py`, `sonic_menu_ingestion_search.ipynb` | `build_mcdonalds_menu.py`, `mcdonalds_menu_ingestion_search.ipynb` | `ingest_menu_local.py` (ChromaDB edge) | Persona data prep | `scripts/personas/<id>/`. The Dunkin edge script is dropped with row 48 |
| 16 | PDF integrated vectorization | n/a | `menu_ingestion_search_pdf.ipynb`, `setup_intvect.*` | Same plus 3 PDFs | Drop | VoiceRAG template leftovers; no live index uses them |

### 3.3 Ordering rules

| # | Item | Sonic | Mc | Dunkin | Class | Unified home |
| --- | --- | --- | --- | --- | --- | --- |
| 17 | Sizes | Mini, Small, Medium, Large, Extra Large, **Route 44**; aliases `rt 44, rt44, 44, 44oz, route44, extralarge`; "RT 44" is always spoken "Route 44" (`order_state.py:361`, prompt `SONIC_BRANDING_AND_SIZING`) | Small, Medium, Large (default Medium); `Regular` on 2 items | small, medium, large (lowercase in data) | Persona data plus shared normalizer | `persona.json` `sizes`. Route 44 exists only in Sonic's pack |
| 18 | Bundle detection | `"combo" in name` | `"meal" in name or "combo" in name` (synonyms) | None | Persona data | Per-item `bundle`. A spoken synonym ("Big Mac combo") resolves through item `aliases`; an unknown name is rejected (decision 4) |
| 19 | Bundle slots | Side (only Tots or Groovy Fries) and drink, both chosen by the guest; a separately ordered side or drink is absorbed | Side **auto-filled** (`{size} Fries`, Medium if unsized; Hash Browns for breakfast meals); drink absorbed | n/a | Shared code plus persona data | One bundle engine; slot `fill: "absorb"` or `"autoFill"` per item |
| 20 | Standalone to bundle conversion | Adding "X Combo" removes a standalone X and carries its mods | Same for meals | n/a | Shared code | Bundle engine |
| 21 | Resize a bundle in place | No | `modify` action changes size and re-prefixes components (`order_state.py:319`), but the loaded schema never offers it (row 4) | n/a | Shared code | Engine supports it; exposed only when the pack's tool schema lists `modify` |
| 22 | Bundle components on the wire | Folded into `display` ("X w/ Tots & Drink") | `OrderItem.components: list[str]` | n/a | Shared code | Additive optional `components` field for every persona; the ticket renders it when present |
| 23 | Meal numbers | n/a | `MEAL_NUMBER_MAP` and a regex in `tools.py:94` rewrite "number 1" to "Big Mac Meal" before search. The same numbers are already in `menuItems.json` (`mealNumber`, `menuPeriod`) | n/a | Strategy plus persona data | `strategies.searchQueryRewrite: "meal_numbers"`, reading `mealNumber` from the menu |
| 24 | Combo-slot classification | Menu category plus name tables `_COMBO_SIDE_ITEMS`, `_SUNDAES`, `_TOTS_ALIASES`, and a word-bounded off-menu fallback (`menu_utils.py:293` to `:440`) | Substring keywords (`order_state.py:39`: "tea", "pepper", "coffee", "shake", "mcflurry" ...), so "Philly Cheesesteak" counts as a drink | n/a | Persona data (#51) | Per-item `comboSlot`. No keyword fallback: an off-menu item is rejected (decision 4, #73) |
| 25 | Spoken aliases | `_TOTS_ALIASES` (14 exact forms, #60) | None | None | Persona data | Per-item `aliases` |
| 26 | Extras | Keywords: flavor add-in, whipped cream, extra patty, extra cheese, add bacon. Allowed with drinks, slushes, shakes, burgers, combos; blocked on hot dogs and tots and sides. Plain-text refusal | Keywords: extra patty, extra cheese, add bacon. Allowed with burgers and sandwiches, chicken and McNuggets, combos; blocked on drinks, shakes, sides, desserts | Whipped cream $0.50, flavor swirl $0.75, extra espresso shot $1.00. Allowed with signature lattes and cold beverages; blocked on donuts and bakery and breakfast sandwiches. Structured JSON rejection with `suggested_calls`; splits "Latte with extra shot" into two calls | Persona data plus shared code | `persona.json` `extras`; one guard with structured rejection for every persona |
| 27 | Invalid modifiers | `INVALID_MODS` in `tools.py:106` | Similar table in `tools.py:230` | None | Persona data | `persona.json` `invalidModifiers` |
| 28 | Out-of-stock machines | Ice cream machine "down": shake, blast, sundae, ice cream (`tools.py:84`) | Same keywords (McFlurry is not in the list) | None | Persona data | `persona.json` `machines` plus per-item `requiresMachine` |
| 29 | Category keyword fallback (extras eligibility, upsell) | Substring (`menu_utils.py:258`): "tea" matches "steak" | Substring (`tools.py:214`) | Substring (`tools.py:_infer_category`) | Removed | Category comes only from the menu. Unknown items are rejected (decision 4, #73) |

### 3.4 Pricing

| # | Item | Sonic | Mc | Dunkin | Class | Unified home |
| --- | --- | --- | --- | --- | --- | --- |
| 30 | Happy hour | 14:00 to 16:00 store time, price x 0.5, slushes and fountain drinks only; shakes, Blasts, sundaes and floats full price (#39, decisions 43 to 46, #64); announced | 14:00 to 16:00, x 0.5 on anything the substring check calls a drink, including shakes and McFlurry; the prompt says "half-price drinks". **Unified: none** (decision 5) | 14:00 to 17:00, x 0.75 (25% off), signature lattes and cold beverages (`config.yaml` `happy_hour_categories`); silent today. **Unified: announced like Sonic** (decision 6) | Persona data | `pricing.happyHour` (or `null` for none) plus per-item `happyHourDiscounted` |
| 31 | Tax | 8% | 8% | 8% | Persona data | `pricing.taxRate` |
| 32 | Store timezone | `America/Chicago` | `America/Chicago` | `America/New_York` | Persona data | `store.timezone`; `STORE_TIMEZONE` stays as a global override for the conformance FixedClock profile |
| 33 | Money arithmetic | `Decimal`, `format_money`, `*Display` fields (#47) | float | float | Shared code | Sonic's |
| 34 | Quantity limits | 10 per item, 25 per order | Same | Same | Shared config | `config.yaml`; a persona may override |

### 3.5 Voice, audio, frontend

| # | Item | Sonic | Mc | Dunkin | Class | Unified home |
| --- | --- | --- | --- | --- | --- | --- |
| 35 | Default voice | `marin` (`manifest.yaml` still says `coral`; nothing reads it) | `marin` | `marin` | Persona data | `persona.json` `voice.default`. The picker list and labels are shared |
| 36 | Apology clips | `apology-<lang>.wav`, voiced as "a friendly Sonic Drive-In carhop" | `rate-limit-apology-<lang>.wav` | `apology-<lang>.wav`, "a friendly Dunkin' drive-thru crew member". Same four phrases in all three | Persona data (assets); shared script | `assets/audio/apology-<lang>.wav`; `generate_apology_clips.py --persona` |
| 37 | McD local mode | n/a | Phi-4 ONNX, Piper TTS, Whisper STT, `processor_router.py`; about 3,000 backend lines; UI toggle; `docker-compose.local.yml` | n/a | Shared code | Kept as the persona-agnostic `local` pipeline, off by default (decision 7, section 7, #81); then dropped entirely (decision 7 reversed, #155, 2026-09-28) |
| 38 | Azure Speech mode | Frontend toggle only | `azurespeech.py`, `azure_speech_gpt4o_mini.py`, but nothing imports them, and no backend registers `/azurespeech/*` | Same | Drop | Dead in all three; remove the toggle |
| 39 | Theme | `--brand-red 341 100% 45%`, `--brand-blue 208 52% 33%`, light/dark; Nunito Sans and Montserrat; **87 hard-coded hex values** in `App.tsx`, `order-summary.tsx`, `menu-panel.tsx` | `--brand-red 357 100% 43%`, dark `40 12% 14%`; 114 hex values in 5 files | `--brand-orange 28 100% 58%`, `--brand-pink 329 100% 45%`, cream, brown; Fredoka; 76 hex values | Persona data plus shared code | `persona.json` `ui.theme` tokens applied by `PersonaProvider` |
| 40 | Identity, copy, legal | Logo svg/png, title "Sonic Drive-In Voice Ordering", hero ("Carhop Pick"), ticket "Carhop ticket / Your Sonic Order", legal line naming Inspire Brands and Sonic Corp., `app.title` and `status.notRecordingMessage` in 4 locales | Logo, "McDonald's AI Drive-Thru", same keys | Logo, "Dunkin' Voice Crew", extra favicons, same keys | Persona data | `persona.json` `ui` block plus `assets/` |
| 41 | Menu panel | Imported at build time (`menu-panel.tsx:1`) | Adds a breakfast/lunch toggle filtering on `menuPeriod` | Categories only | Shared code plus persona data | Fetched at runtime; the daypart toggle is shown when the pack declares `dayparts` |
| 42 | Demo data, resume key | `dummyOrder.json`, `dummyTranscripts.json`; `sonic.resumeId` | Own demo data | Own demo data | Persona data; shared key | `assets/demo/`; key `drivethru.resumeId.<persona>` |

### 3.6 Config, infra and brand-only features

| # | Item | Sonic | Mc | Dunkin | Class | Unified home |
| --- | --- | --- | --- | --- | --- | --- |
| 43 | VAD and search tuning | VAD 0.5 / 200 ms; KNN 15, top 3 (perf audit) | Same | VAD 0.7 / 500 ms; KNN 50, top 5 | Shared config | Sonic's tuned values. The browser sends its own VAD anyway |
| 44 | Env vars | `AZURE_SEARCH_INDEX`, `STORE_TIMEZONE`, `SONIC_MENU_ITEMS_PATH` / `MENU_ITEMS_PATH` | Adds `LOCAL_MODE_*`, `AZURE_SEARCH_CONTENT_FIELDS` | Adds `USE_LOCAL_PIPELINE`, `CRM_DB_PATH`; `.env.template` for AKS, ACR, Key Vault | Shared code | Add `PERSONAS`, `DEFAULT_PERSONA`, `PERSONAS_DIR`, optional `SEARCH_INDEX_<ID>`, and `AZURE_AI_MODEL_DEPLOYMENTS` (section 7). `AZURE_SEARCH_INDEX` applies only when one persona is enabled. The menu-path vars are removed. `LOCAL_MODE_*` was kept as `LOCAL_RUNTIME_*` for the local pipeline, then removed with it (#155). The edge and CRM vars are dropped |
| 45 | Bicep, azd | `APP_SESSION_SECRET`, sticky ingress, EasyAuth, replicas 1 to 5, postdeploy smoke | Same, minus small ordering changes; `MCD_SKIP_REALTIME_SMOKE` | No `APP_SESSION_SECRET`, no replica bounds or health probe (hardening lag); `DUNKIN_SKIP_REALTIME_SMOKE` | Shared code | Sonic's, plus the persona env vars; one smoke that loops over personas |
| 46 | Crew dashboard and simulator | n/a | n/a | `/dashboard` WebSocket, `/simulator/demo`, `app/employee-dashboard` SPA served at `/crew/` (467 lines), `drive_thru/` simulator (595 lines), `session_manager` publishes orders to it | Drop (decision 7) | Not carried over (#86). The code stays at the archived repo's `final-standalone` tag |
| 47 | CRM | n/a | n/a | `crm/` (252 lines, SQLite), `crm_seed.json`, `seed_crm.py`. Only the simulator's fake guests use it; the voice agent never calls it | Drop (decision 7) | Not carried over (#86) |
| 48 | Azure Local edge | n/a | n/a | `Dockerfile.edge`, `requirements-edge.txt`, `rtmt_local.py` (667 lines), `k8s/` (5 files), `flux/` (22 files), `deploy-edge.*`, 2 docs | Drop (decision 7) | Not carried over (#86) |
| 49 | Internal protocol ids | `sonic_mt_` item-id prefix (#29 authorship), `sonic_*` event ids, logger `sonic-drive-in` | Own | Own | Shared code, **unchanged** | Never shown to guests; the #29 authorship contract depends on the prefix |
| 50 | Brand-rule tests | `test_combo_orders.py`, `test_menu_utils.py`, golden files, the conformance suite (about 420 scenarios) | `test_order_logic.py`, `test_menu_utils.py`, `test_extras_rules.py` | `test_happy_hour.py`, `test_extras_rules.py`, `test_update_order_result.py`, `test_crm.py` | Persona data (golden rows) | `tests/conformance/testdata/personas/<id>/`; the CRM and dashboard tests are dropped |
| 51 | Hardening lag (not brand) | S1 and S1.5 relay security, resume, rate limit, reasoning, exact money, conformance hooks | Partly ported; #6 open | Partly ported; #11 open | Shared code | Sonic's implementation is the base |

**Count:** 51 rows. 31 are persona data (some with a shared engine that reads them), 13 are shared code or
config (one of them, row 37, the local mode later dropped by #155), 1 is a strategy, 1 is removed (row 29, the
category keyword
fallback), and 5 are dropped:
- the PDF template (row 16);
- the dead Azure Speech toggle (row 38);
- Dunkin's dashboard, CRM and edge stack (rows 46 to 48).

## 4. The persona contract

### 4.1 Directory layout

```text
personas/
  persona.schema.json          JSON Schema for persona.json (both backends and CI validate against it)
  menu.schema.json             JSON Schema for menuItems.json, including the #51 fields
  sonic/
    persona.json               rules, strategies, UI manifest
    prompts/
      system_prompt.yaml       moved from app/backend/prompts/sonic/ (manifest.yaml folds into persona.json)
      greeting.yaml
      tool_schemas.yaml
      error_messages.yaml
      hints.yaml
      cascade_system_prompt.yaml optional override for chat models in the cascade pipeline (#82)
    menu/
      menuItems.json           the served menu and the index source (moved from app/frontend/src/data/)
      source/                  raw brand exports, e.g. sonic-menu-items.json
    assets/
      logo.svg  logo.png  favicon.ico
      audio/apology-en.wav  apology-es.wav  apology-fr.wav  apology-ja.wav
      demo/dummyOrder.json  demo/dummyTranscripts.json
  mcdonalds/  (same shape)
  dunkin/     (same shape)
scripts/personas/<id>/         brand raw-export converters (row 15)
tests/conformance/testdata/personas/<id>/
  golden-menu-categories.json  golden-order-pricing.json  golden-<feature>.json
```

`personas/` sits at the repo root because the Python backend, the C# backend, the frontend build, the
conformance suite and the scripts all read it. The Dockerfiles copy it to `/app/personas`.

### 4.2 `persona.json`

All money values are quoted decimal strings, so C# reads them as `decimal` without going through `double`
(the same convention as `golden-order-pricing.json`). Unknown fields are rejected. The Sonic pack, abridged:

```json
{
  "$schema": "../persona.schema.json",
  "schemaVersion": 1,
  "id": "sonic",
  "displayName": "Sonic Drive-In",
  "roleName": "carhop",
  "locales": { "default": "en", "supported": ["en", "es", "fr", "ja"] },
  "store": { "timezone": "America/Chicago" },
  "voice": { "default": "marin" },
  "search": { "indexName": "sonic-menu-items", "contentFields": ["description"] },

  "pricing": {
    "taxRate": "0.08",
    "happyHour": {
      "startHour": 14, "endHour": 16, "priceMultiplier": "0.5", "announce": true,
      "banner": "[HAPPY HOUR ACTIVE: slushes and fountain drinks are half-price; shakes, Blasts and sundaes are full price]"
    }
  },

  "sizes": {
    "canonical": { "mini": "Mini", "small": "Small", "medium": "Medium", "large": "Large",
                   "xl": "Extra Large", "route 44": "Route 44", "standard": "Standard" },
    "aliases":   { "s": "small", "m": "medium", "l": "large", "extralarge": "xl",
                   "rt 44": "route 44", "rt44": "route 44", "44": "route 44", "44oz": "route 44", "route44": "route 44" },
    "spokenAs":  { "RT 44": "Route 44", "RT44": "Route 44" },
    "hidden":    ["", "standard", "n/a", "na", "none", "n.a."],
    "default":   null
  },

  "bundles": {
    "nameMarkers": ["combo"],
    "convertStandalone": true,
    "missingPartText": { "sides": "a side (fries or tots)", "drinks": "a drink or slush" }
  },

  "extras": {
    "allowedBaseCategories": ["slushes & drinks", "shakes & ice cream", "burgers & sandwiches", "combos"],
    "blockedBaseCategories": ["hot dogs & tots"],
    "splitCombinedNames": false
  },

  "invalidModifiers": { "shake": ["lettuce", "tomato", "onion"], "slush": ["cheese", "bacon", "patty"] },
  "machines": {
    "ice_cream_machine": { "status": "down", "label": "Ice cream machine is being cleaned" },
    "slush_machine": { "status": "operational", "label": "Slush machine is down" }
  },

  "models": {
    "realtime": { "default": "gpt-realtime-2.1", "allowed": ["gpt-realtime-2.1", "gpt-realtime-mini"] },
    "cascade":  { "default": "gpt-5-mini", "allowed": ["gpt-5-mini", "phi-4"] }
  },

  "strategies": { "searchQueryRewrite": "none" },
  "features": { "dayparts": false },

  "ui": {
    "title": "Sonic Drive-In Voice Ordering",
    "theme": {
      "light": { "primary": "341 100% 45%", "secondary": "208 52% 33%", "background": "195 44% 96%", "foreground": "208 53% 20%" },
      "dark":  { "primary": "347 100% 71%" },
      "font":  { "family": "Nunito Sans", "importUrl": "https://fonts.googleapis.com/css2?family=Nunito+Sans:wght@400;600;700;800;900&display=swap" }
    },
    "assets": { "logo": "assets/logo.svg", "favicon": "assets/favicon.ico", "apologyClip": "assets/audio/apology-{lang}.wav" },
    "strings": {
      "en": { "app.title": "Sonic Voice Ordering", "status.notRecordingMessage": "Let's order from America's Drive-In!",
              "ticket.kicker": "Carhop ticket", "ticket.title": "Your Sonic Order", "menu.button": "View Sonic Menu" }
    },
    "hero": { "headline": "Sonic ordering powered by Microsoft Foundry", "callouts": [] },
    "legal": "Disclaimer: This project is a non-commercial demo application ... not affiliated with, endorsed, or sponsored by Inspire Brands, Inc. or Sonic Corp. ..."
  }
}
```

Field rules:

| Block | Rule |
| --- | --- |
| `sizes` | The shared normalizer replaces `SIZE_MAP` and `SIZE_ALIASES`. It uses the compact-key matching from `menu_utils.py:64` for every persona. `spokenAs` drives readback. Route 44 appears only in Sonic's pack. |
| `bundles` | Engine settings. Which items are bundles, and their slots, is per item (4.3). Spoken synonyms such as "combo" for "meal" resolve through item `aliases`. |
| `extras` | One guard for all personas. Extras are `isExtra` menu items. The guard checks that the order already has a base in an allowed category. A refusal is always the structured JSON result Dunkin uses today (`status: "rejected"`, `item_added: false`, optional `suggested_calls`), with the text from the pack's `error_messages.yaml`. |
| `pricing.happyHour` | Optional. `null` means the persona has no happy hour (McDonald's, decision 5). When present, `announce: true` adds the banner to tool results and turns on the `HAPPY_HOUR` prompt section (Sonic and Dunkin, decision 6). |
| No `offMenu` block | Removed by decision 4. An item that isn't on the menu (after `_menu_key` normalization and aliases) is rejected with `reason: "not_on_menu"`. There is no keyword fallback of any kind (section 6). |
| `models` | Which catalog models the persona allows, and its default per pipeline (section 7). The deployment decides which ones exist; the session picks one of the allowed. |
| `strategies` | A closed set. P2 has one slot, `searchQueryRewrite`, with the values `none` and `meal_numbers`. Adding a value needs both backends and a conformance scenario in the same PR series. |
| `ui` | Everything the browser needs before it connects. It is served by `/api/personas/<id>`. Prompts and rules are never served to the browser. |
| `ui.theme.{light,dark}.surface` | Optional shadcn-style UI slot palette (issue #117, `personaTheme.ts`'s `PersonaSurfaceTokens`/`PersonaSurfaceDarkTokens`): backs the shared shadcn tokens (`card`/`secondary`/`muted`/`accent`/`destructive`/`border`/`input`/`ring`/`chart-N`) that `index.css` previously hard-coded to one persona's palette. Every key is optional; a pack that omits `surface` gets the shared neutral defaults `index.css` falls back to. |

### 4.3 Per-item menu fields (#51)

`menuItems.json` keeps today's shape (`menuItems[].category`, `items[]` with `name`, `sizes[]`, `description`,
...). The new fields are optional and additive, and the defaults are the safe ones:

| Field | Type, default | Meaning | Replaces |
| --- | --- | --- | --- |
| `menuItems[].icon` | string, optional (category-level, not per-item) | The category's own emoji/icon (`menuItems.json`, `menu.schema.json`), rendered in `menu-panel.tsx`. A category that omits it falls back to the shared neutral `DEFAULT_CATEGORY_ICON` | `menu-panel.tsx`'s hardcoded category→icon map |
| `comboSlot` | `"sides" \| "drinks" \| "none"`, default `"none"` | Can this item fill a bundle's included side or drink slot? The values match `golden-menu-categories.json` exactly, so golden rows can be compared field by field | `_COMBO_SIDE_ITEMS`, `_SUNDAES`, `_COMBO_DRINK_CATEGORIES` and the category-derived rule |
| `happyHourDiscounted` | bool, default `false` | Does the happy-hour multiplier apply? Independent of `comboSlot` (PR #50 rule) | `_SHAKES_AND_BLASTS_HAPPY_HOUR_DISCOUNTED`, Dunkin's `happy_hour_categories`, McD's keyword check |
| `aliases` | string[], default `[]` | Exact spoken names that resolve to this item after `_menu_key` normalization | `_TOTS_ALIASES` |
| `bundle` | object, absent means not a bundle | `{ "slots": ["sides", "drinks"], "autoFill": { "sides": "{size} Fries" }, "defaultSize": "Medium" }` | `"combo" in name`, McD `_is_meal`, `_get_default_side`, `_BREAKFAST_KEYWORDS` |
| `requiresMachine` | string, optional | Key into `persona.json` `machines` | `_ICE_CREAM_MACHINE_KEYWORDS` for on-menu items |
| `isExtra` | bool, default `false` | This item is an extra (Dunkin's Whipped Cream, Flavor Swirl, Extra Espresso Shot) | Extras detection for on-menu extras |
| `menuPeriod`, `mealNumber` | existing McD fields, now in the schema | Daypart filter and meal-number lookup | McD `MEAL_NUMBER_MAP`, `BREAKFAST_MEAL_NUMBER_MAP` |

Sonic examples: `Tots` gets `comboSlot: "sides"` and the #60 aliases. `Hot Fudge Sundae` gets
`comboSlot: "none"` and `happyHourDiscounted: false`. `Cherry Limeade` gets `comboSlot: "drinks"` and
`happyHourDiscounted: true`. `Chocolate Classic Shake` gets `drinks` and `false` (Brian, #39).
`Cheeseburger Combo` gets `bundle.slots: ["sides", "drinks"]`. McDonald's examples: `Big Mac® Meal` gets
`bundle: { slots: ["sides", "drinks"], autoFill: { sides: "{size} Fries" }, defaultSize: "Medium" }`, and
`Egg McMuffin® Meal` gets `autoFill: { sides: "Hash Browns" }`.

Floats (#64, decision 3): `Root Beer Float`, `Coke Float` and `Dr Pepper Float` are not in Sonic's source export.
They become Sonic menu items with `comboSlot: "drinks"` and `happyHourDiscounted: false`, and their `origin`
records that they were added by decision (#72). Fountain drinks (Coca-Cola, Dr Pepper, Sprite, BARQ'S Root Beer
and their diet and zero variants) and priced add-ons are in the export, so #72 brings them onto the menu too.
Today a guest orders them through the keyword fallback.

One deliberate scope change: #60 limited the Tots aliases to the combo side slot. Here an alias resolves the
item for every lookup (category, happy hour, machines). For Tots the result is the same everywhere: the alias
now gives the category `hot dogs & tots` instead of the fallback's `sides`, both are in the blocked-extras set,
both trigger the same upsell hint, and Tots is never discounted. #71 pins this with a test.

### 4.4 How each backend loads a pack

| Step | Python (P2) | C# (S2 to S4) |
| --- | --- | --- |
| Find packs | `PERSONAS_DIR` (default `<repo>/personas`, `/app/personas` in the container); enable `PERSONAS` (comma list, default: every folder); `DEFAULT_PERSONA` must be in the list | Same env vars, bound to `PersonaOptions` |
| Parse and validate | Pydantic models with `extra="forbid"` for `persona.json` and `menuItems.json`; CI also validates against the JSON Schemas | `System.Text.Json` source-generated records with `JsonUnmappedMemberHandling.Disallow`; prompts through YamlDotNet (already planned in #12) |
| Build | Per persona: `MenuIndex` (normalized key and aliases to item), a `PromptLoader` pointed at `personas/<id>/prompts` (today's class, given a path instead of a brand), and one `SearchClient` per index. Once per process: the model catalog (section 7) | `PersonaCatalog` and `ModelCatalog` singletons; strategies as keyed services (`ISearchQueryRewriter`: `none`, `meal_numbers`); processors as keyed services (`realtime`, `cascade`) |
| Fail fast | Any invalid enabled pack stops startup with the file and field named | Same |
| Per session | `session_manager` stores `persona_id`, `model_id` and the pipeline; `tools.py` and `order_state.py` take the session's `Persona` instead of module globals | The `SessionActor` holds its `Persona` and model |
| Report | `/health` gains `personas: ["sonic", ...]` (additive) | Same |
| Hot reload | `DEV_MODE` keeps reloading prompts, per persona | Not required |

The normalization rules (`_menu_key`: paren groups, whitespace, lowercase invariant, `®`, `™`, curly
apostrophe) stay shared and stay documented in `tests/conformance/README.md`. They are not per persona.

## 5. Switching personas (decided)

### 5.1 The decision

Brian's decisions 1 and 2: the persona is **picked per session**, and **one deployment** serves all three
personas on a single URL. The per-deployment allow-list (`PERSONAS`, `DEFAULT_PERSONA`) stays as a capability,
not a plan: the one deployment enables `sonic,mcdonalds,dunkin` with `DEFAULT_PERSONA=sonic`.

| | Per deployment (`PERSONA=sonic`) | **Per session inside an allow-list (accepted)** |
| --- | --- | --- |
| Demo UX | Three URLs; switching means changing tabs | One URL; switching takes two clicks; brands can run side by side in two tabs |
| AI Search | One static index per app | A per-session client over the three indexes |
| Voice and model | Per-app defaults | Per-session, from the pack and the session's picks |
| Azure cost | Three always-on apps per backend (six with C#) | One app per backend (two with C#) |
| Code | Smallest change | The persona is threaded through the session, which C# needs anyway (DI) |

**Not supported:** switching persona or model mid-session. The realtime voice locks after the first audio, the
order rules differ by brand, and swapping instructions mid-conversation confuses the model. A switch always
starts a new session.

### 5.2 Wire contract (both backends, pinned by conformance)

| Surface | Contract |
| --- | --- |
| `GET /api/personas` | `{ "default": "sonic", "personas": [ { "id", "displayName", "logoUrl", "theme" } ], "backends": [ { "id": "python", "url" }, { "id": "dotnet", "url" } ] }`, enabled personas only; `backends` lists the deployed backends (section 10) |
| `GET /api/personas/{id}` | The pack's `ui` block, plus `roleName`, `voice.default`, `locales`, `features.dayparts`, `menuUrl`, and the selectable `models` per pipeline (section 7). 404 if not enabled |
| `GET /personas/{id}/assets/*`, `GET /personas/{id}/menu.json` | Static files from the pack, immutable caching (the existing compression and caching middleware) |
| `GET /realtime?persona={id}&model={id}` | Omitted persona: `DEFAULT_PERSONA`. Omitted model: the persona's default realtime model. Unknown or not enabled: **HTTP 404 before the WebSocket upgrade**, never a silent fallback. Both are fixed for the session |
| `extension.metadata` | Gains `persona`, `model` and `pipeline` (additive) |
| Resume | The held session remembers its persona and model. `extension.resume` from a socket opened with a different persona or model gets `extension.resume_rejected` with `reason: "persona_mismatch"` or `"model_mismatch"`, then a fresh session (the existing rejection path, then metadata) |
| Session token, Origin checks, authentication | Entra ID per section 18 (ADR-002, which replaces EasyAuth): every route above except `/personas/{id}/assets/*` branding files needs a bearer, and `/realtime` takes it as `?access_token`. The HMAC session token gains the caller's `oid`. The persona and model are not secrets and are not in the HMAC token |

## 6. Menu rules: #51, #64 and no off-menu ordering (decided)

**Decision 4: no off-menu ordering.** "If it's not on the menu in our source data, you cannot order it."

- **Classification comes only from the menu.** On-menu classification uses the per-item fields in 4.3.
  `menu_utils.py` keeps the shared normalization and the lookup engine, but no item names (#71).
- **Unknown items are rejected.** `update_order` `add` resolves the name through `_menu_key`, then the item's
  aliases, against the session persona's menu.
  - An unknown item gets the structured rejection `reason: "not_on_menu"`, and nothing is added.
  - A size the item doesn't list gets `reason: "size_not_available"`.
  - Parenthesized customizations on an on-menu item are still allowed and still validated.
  - Priced extras are menu items (`isExtra`).
  - **Tool result contract (both rejection reasons, PR #100 review).** `update_order` returns a JSON object,
    never plain text, so the C# port can mirror the same shape -- same keys, types and
    values, compared parsed:
    `{ "status": "rejected", "item_added": false, "reason": "not_on_menu" | "size_not_available", "item_name",
    "message" }`, plus `"available_sizes"` (the item's real display sizes) only for `size_not_available`. The
    `message` for `not_on_menu` also tells the model to search with the guest's words and offer the closest
    real item by its exact name, so a rejection still moves the order forward.
  - **`item_name` means something different per reason (#74 note).** For `not_on_menu`, `item_name` is the
    guest's own words -- the raw, unresolved `update_order` tool-call argument, since nothing on the menu
    matched it. For `size_not_available`, `item_name` is the real menu name (`menu_item["name"]`) -- the item
    itself DID resolve; only the requested size didn't. A client rendering these rejections (or a future
    persona's own copy) must not assume `item_name` is always menu-canonical.
  - **`machine_unavailable` (#77, add-time only).** An on-menu item whose per-item `requiresMachine` key
    points at a `persona.json` `machines.<key>` this store's *own* status currently reports `"down"` is
    rejected with the same shape: `{ "status": "rejected", "item_added": false, "reason":
    "machine_unavailable", "item_name", "message" }` -- `item_name` is the real menu name (the item DID
    resolve), and `message` is built from that same machine's own `label` (`persona.json` `machines.<key>`
    is `{ "status": "down" | "operational", "label": "<store-facing outage text>" }`, replacing the old
    bare-string status and the module-level, name-keyed `_MACHINE_OOS_LABELS` dict that only the default
    persona's two machines ever populated). `modify` never re-runs this check: resizing an item already in
    the order doesn't newly require the machine it already required when it was added, so a legitimate
    resize of an item that was fine at add time is never incorrectly blocked by a machine that went down
    afterward. `fryer` is dropped from every persona's `machines` block unless some item's own
    `requiresMachine` actually names it (none does today) -- an unused machine key is dead data, not a
    real gate.
  - **`extras_blocked_category` / `extras_no_base_item` (#77, shared extras engine, add-time only).** An
    `isExtra` item can only be added once the current order has a real base item whose category is in this
    persona's own `extras.allowedBaseCategories` and not in `extras.blockedBaseCategories` -- checked against
    every line already in the order, not just the last one added. Same shape again: `{ "status": "rejected",
    "item_added": false, "reason": "extras_blocked_category" | "extras_no_base_item", "item_name", "message"
    }`. `extras_blocked_category` fires when a real base item is present but its category is explicitly
    blocked (e.g. a side or a hot dog on its own); `extras_no_base_item` fires when there is no allowed base
    item in the order at all yet. This one engine (`menu.is_extra_item`, `menu.allowed_extra_categories`,
    `menu.blocked_extra_categories`) is the only place any persona's extras rule is enforced -- it is
    data-driven from that persona's own `persona.json`, not a second brand-specific implementation.
  - **`modify` (#77, McDonald's tool/prompt contract, `tool_schemas.yaml` opt-in only).** Re-prices an
    existing order line at a new size from the persona's own menu (never the tool call's own `price`,
    consistent with #104) and leaves that line's `components` untouched -- a bundle's absorbed sides/drinks
    are not re-picked on a resize. A `modify` for an item not currently in the order is rejected with the
    same shape, never reported as a success: `{ "status": "rejected", "item_added": false, "reason":
    "not_in_order", "item_name", "message" }`. `item_name` is the real menu name (the item resolved; it just
    isn't in the order), `message` is the persona's `item_not_in_order` error message, and the order is left
    unchanged. The check runs after the on-menu and size gates, so an off-menu or wrong-size `modify` still
    gets `not_on_menu` or `size_not_available`. This is the single, shared implementation both McDonald's
    (real "modify a Happy Meal to a large" flow) and
    any future pack's own `modify`-listing tool schema route through -- there is no persona-specific
    `modify` variant.
- **Every keyword fallback is removed** (combo slot, happy hour and category), along with the `offMenu` schema
  block (#73). That removes the whole class of substring bugs ("tea" in "steak") for every persona.
- **Menus must be complete.** Every brand's `menuItems.json` is completed from its source data before the
  fallback goes (#72 for Sonic; #78 and #79 for McDonald's and Dunkin).
  - Sonic's served menu lacks fountain drinks and add-ons that are in its production export.
  - Without them, a Sonic guest could no longer order a Coke.
- **Conformance changes on purpose** (#73). The off-menu rows flip from "absorbed, discounted or charged" to
  "rejected, order unchanged":
  - the off-menu fountain drink ("Dr Pepper Zero");
  - the spoken variants ("Cherry Slushes", "Slushie", "Milkshake");
  - the fountain and shake compounds;
  - steak versus tea;
  - the off-menu side-like item;
  - a size word inside the name ("Large Tater Tots").

  Scenarios that order an off-menu name only as a fixture (for example "Small Fries") switch to real items.
- **The golden table is checked, not generated.** `golden-menu-categories.json` stays hand-owned and is
  checked against each menu, so the golden file remains an independent oracle.
- **The unit price always comes from the menu, never the tool call (#104, decided).** Supersedes the
  old #28 N23 rule (trust the tool call's price verbatim). `update_order`'s `add` always charges
  `menu_utils.MenuCatalog.price_for`'s resolved per-size price, applied once in
  `order_state.py::handle_order_update`, the one choke point for both the realtime tool-call path and
  any direct caller (a test, an admin tool) building an order without going through `tools.py`'s
  on-menu gate; the C# port needs that same single choke point (#14). Applies uniformly to combos,
  extras, happy hour and resizes. The tool call's own `price` stays in the schema, described as
  "ignored," purely for backward compatibility, and is only ever used for a debug/warn mismatch log,
  never for charging. If `price_for` finds no menu record at all, this same branch falls back to the
  caller-supplied price (never a silent $0, and never a crash on a null/non-numeric one) instead;
  `app/backend/tests/test_menu_data_completeness.py` asserts every item/size in every real and
  fixture pack has a matching price, so an on-menu `add` should never actually reach that fallback.
  Acceptance proof: `test_tool_calling.py::test_add_wrong_tool_price_charges_menu_price` and
  `UpdateOrderAddRemoveModifyTests.Adding_an_item_with_a_wrong_tool_call_price_is_charged_the_menu_price`.

**#64, decided (decision 3).** Floats do not get the happy-hour price, and they can fill the combo drink slot.
They are real Sonic menu items (4.3, #72), not a keyword rule.

- **A pack refuses to start if two menu items collide on their lookup key, or an alias collides
  with another item's own key or another item's own alias (#128, decided).** `_menu_key`'s
  modifier-stripping (this section, `strip_modifiers`) is by design -- but that exact same
  stripping can silently collapse two differently-named items (e.g. several differently-priced
  "(N piece)" size variants of the same base name) into one lookup key, with whichever one loaded
  last winning and the others unreachable/mispriced. `menu_utils.validate_menu_key_collisions`
  (Python) and `MenuKeyValidator.ValidateNoCollisions` (C#) are the single implementation of this
  rule in each backend, run eagerly for every enabled persona at process startup (Python also runs
  it again whenever a `MenuCatalog` is built, so no code path can ever construct a
  degraded/last-write-wins catalog) -- same fail-fast convention, same "one contract, two backends"
  requirement as every other startup validation in this doc.

### 6.1 Required rejection-message keys, validated at startup (#125, decided)

Every structured rejection above (`not_on_menu`, `size_not_available`, `machine_unavailable`,
`extras_blocked_category`, `extras_no_base_item`, `not_in_order`) renders its `message` from the
persona's own `error_messages.yaml` via `prompt_loader.render_error(<key>, ...)`. This is the
canonical, single list of keys every persona pack's `error_messages.yaml` **must** define:

| Required key               | Rendered for reason (this section)                       |
|-----------------------------|------------------------------------------------------------|
| `generic_error`             | shared catch-all fallback (no dedicated reason yet)         |
| `item_not_on_menu`          | `not_on_menu`                                               |
| `size_not_available`        | `size_not_available`                                        |
| `item_not_in_order`         | `not_in_order` (`modify` target missing)                     |
| `machine_unavailable`       | `machine_unavailable`                                        |
| `extras_blocked_category`   | `extras_blocked_category`                                    |
| `extras_no_base_item`       | `extras_no_base_item`                                        |

**One list, two mirrors, kept in sync by a test in each language.** The list above is duplicated
verbatim, in the same order, as an ordered constant in both loaders:

- Python: `prompt_loader.REQUIRED_ERROR_MESSAGE_KEYS` (`app/backend/prompt_loader.py`).
- C#: `PromptLoader.RequiredErrorMessageKeys` (`app/backend-dotnet/src/Backend/Prompts/PromptLoader.cs`).

Rather than externalizing the list to a shared file (which would mean plumbing a new path through
both loaders' constructors and every test fixture's directory layout), each language's test suite
parses the *other* language's source file as plain text and asserts the two ordered lists are
equal:
`RequiredErrorMessageKeys_MatchPython` (`PromptLoaderTests.cs`) parses `prompt_loader.py`;
`test_python_and_dotnet_required_error_message_keys_are_equal`
(`RequiredErrorMessageKeysMatchDotnetTests` in `test_prompt_loading.py`) parses `PromptLoader.cs`.
A drift in either direction fails whichever suite runs.

**Both loaders fail fast at startup if a pack is missing any required key.** `PromptLoader`'s
constructor (C#) / `_load_all()` (Python) validates `error_messages.yaml` immediately after
loading it, before the pack is considered usable, and raises naming both the persona/brand and
every missing key (not just the first one) -- `PromptLoadException` in C#, `ValueError` in
Python. This applies to every persona a process constructs a loader for: in Python, `app.py`'s
`create_app()` builds one `PromptLoader` per enabled persona at startup (so a broken pack fails
the whole process before it serves traffic), plus `default_persona.py`'s lazily-cached default;
in C# today, `Program.cs` constructs one `PromptLoader` for the default persona only (multi-persona
prompt loading is a later wave, `docs/dotnet_mapping.md`).

Draft persona packs land the required keys themselves as part of their own PR (Dunkin's pack
already has all seven; a future McDonald's pack must add them too) -- this validation does not
touch `personas/dunkin/**` or `personas/mcdonalds/**`.

## 7. Model flexibility on Microsoft Foundry

Decision 8 makes model flexibility one of the demo's three themes, alongside Microsoft Foundry and persona
switching: "enable differing models, e.g. selectable realtime/chat models per deployment/persona."

### 7.1 Pipelines

A **pipeline** is how a session turns the guest's voice into the carhop's voice. All three pipelines share the
same tools, persona pack, order state, no-off-menu rule and browser wire contract. The frontend doesn't know
which one is running.

| Pipeline | Models | Where it runs | Status |
| --- | --- | --- | --- |
| `realtime` | A Foundry realtime model, speech to speech (today's `gpt-realtime-2.1`, plus alternatives such as a smaller realtime model) | Azure | Today's `rtmt.py`; selectable per session in #75 |
| `cascade` | Transcription (`gpt-4o-transcribe`), then a Foundry **chat** model with tool calling (OpenAI or non-OpenAI), then TTS (`gpt-4o-mini-tts`) -- all Azure OpenAI audio and chat models on the same Foundry AIServices account; **not** Azure Speech, which isn't used and has no SDK or resource in this pipeline | Azure | New in #82; parity with `realtime` below |

A third pipeline, `local` (Whisper, then Phi-4 mini (ONNX), then Piper, all on-device through a companion
runtime process -- McDonald's local mode, made persona-agnostic in #81), was kept off by default from P2 through
2026-09-28. **Issue #155 dropped it entirely** (reversing decision 7, ADR-001): the demo now runs on Microsoft
Foundry exclusively, so there is no on-device pipeline and no `LOCAL_RUNTIME_*` configuration surface. The
as-built design that pipeline had (7.6, below) is retained here only as a historical record of what #155 removed.

**Turn-taking parity.** The frontend can't tell pipelines apart, so `cascade` (Rick's PR #118 review item 6)
matches `realtime`'s observable behavior where the demo needs it:

| Behavior | `realtime` | `cascade` |
| --- | --- | --- |
| Greeting on connect: the persona's `greeting.yaml`, spoken, with the same `response.*` frames | yes | yes |
| Barge-in: a `speech_started` cancels the in-flight model call and speech; nothing more is sent for the cancelled turn | yes (upstream VAD) | yes (local VAD) |
| Upstream failure is never silent | rate limit: `extension.rate_limited` | a 429 from chat, transcription or TTS: the same `extension.rate_limited` path |
| Resume (grace hold, rehydration) | yes | deferred to #126 |
| Idle nudge | yes | deferred to #126 |
| Echo suppression after playback | yes | deferred to #126 |

Each "yes" on `cascade` has a conformance row (`tests/conformance/.../Scenarios/Cascade/`).

**Explicitly deferred, tracked in #126:** resume (a reconnect on `cascade` today gets a fresh session, with no
grace-hold/rehydration equivalent to `realtime`'s), the idle-nudge, and echo suppression (`cascade_processor.py`
doesn't import `audio_pipeline.py`'s `EchoSuppressor`, so there is no cooldown window after playback the way
`realtime` has). None of these are required for the P2 demo's happy path or the conformance rows above; each
needs its own design pass before landing on `cascade`.

### 7.2 Config shape

There are three layers, each owned by one team:

1. **Catalog (shared, in `app/backend/config.yaml`):** facts about each model, the same for every deployment.
   ```yaml
   models:
     catalog:
       - { id: gpt-realtime-2.1, pipeline: realtime, label: "GPT Realtime 2.1", reasoning: true }
       - { id: gpt-realtime-mini, pipeline: realtime, label: "GPT Realtime mini", reasoning: false }
       - { id: gpt-5-mini, pipeline: cascade, label: "GPT-5 mini", toolCalling: true }
       - { id: phi-4, pipeline: cascade, label: "Phi-4 (Foundry)", toolCalling: true }
     cascade:
       transcription: gpt-4o-transcribe
       tts: gpt-4o-mini-tts
   ```
   The ids above are examples. Unity fixes the real list in #75 and #82, after checking tool-calling support and
   regional availability.
2. **Deployment (infra, `AZURE_AI_MODEL_DEPLOYMENTS`):** a JSON map from catalog id to Foundry deployment name,
   emitted by Bicep from the deployments it actually created, for example
   `{"gpt-realtime-2.1":"gpt-realtime-2.1","gpt-5-mini":"gpt-5-mini"}`. A catalog entry with no deployment isn't
   selectable. The existing `AZURE_OPENAI_REALTIME_DEPLOYMENT` stays as the realtime default for back-compat.
3. **Persona (the pack's `models` block, 4.2):** which catalog models the persona allows, and its default per
   pipeline.

### 7.3 Where it's selectable

| Level | Decides | How |
| --- | --- | --- |
| Deployment | Which models exist | Bicep model deployments feed `AZURE_AI_MODEL_DEPLOYMENTS` |
| Persona | Which of those a brand allows, and its default | `models` in `persona.json` |
| Session | Which allowed model this conversation uses | The Settings model picker, sent as `?model=` at connect. Locked for the session |

Selectable = catalog ∩ deployment ∩ persona-allowed. The pipeline follows from the chosen model's `pipeline`.

### 7.4 How both backends honor it

- **Same files.** Both backends read the same `config.yaml` catalog, the same `AZURE_AI_MODEL_DEPLOYMENTS`, the
  same `persona.json`, and expose the same `/api/personas/{id}` model list and `?model=` contract.
- **Realtime.** The chosen deployment goes into the upstream URL (`/openai/v1/realtime?model=<deployment>`).
  Whether `reasoning` is sent is decided in this order: a runtime rejection latch (always wins, for the rest
  of the process); then the explicit `AZURE_OPENAI_REALTIME_REASONING_MODEL` switch (`true`/`false`, an
  operator-level override); then the bound model's catalog `reasoning` flag; the deployment-name heuristic is
  only the `auto` fallback, reached when the switch is unset and no model is bound yet.
- **Cascade.** Chat calls go through the Foundry resource's `/models` endpoint, the one surface that serves both
  Azure OpenAI and Foundry Models (non-OpenAI, for example Phi-4) deployments in the same chat-completions shape:
  - **SDK choice (Rick's #118 review, non-blocking item):** Python uses `azure-ai-inference`
    (`azure.ai.inference.aio.ChatCompletionsClient`, `1.0.0b9`, REST `2024-05-01-preview`), **not** the plain
    `openai` SDK -- the `openai` SDK's chat-completions client assumes an OpenAI-shaped deployment and doesn't
    target Foundry's model-agnostic `/models` route. C# follows the same choice for parity: `Azure.AI.Inference`
    (NuGet), not the OpenAI .NET SDK, so both backends can select a non-OpenAI catalog model (Phi-4, once
    qualified in #87) without a backend-specific code path.
  - Transcription and TTS are **Azure OpenAI audio models on the same AIServices account**
    (`gpt-4o-transcribe` through `/openai/v1/audio/transcriptions`, `gpt-4o-mini-tts` through
    `/openai/v1/audio/speech`) -- **not Azure Speech**; there is no Speech SDK or resource in this pipeline. The
    catalog's `cascade.transcription` and `cascade.tts` entries name the deployments.
  - Tool definitions come from the pack's `tool_schemas.yaml`, converted to chat-tool shape once.
- **Processor interface.** Both backends implement `realtime` and `cascade` behind one interface that
  emits the same browser frames (`response.audio.delta`, transcripts, `extension.*`), so conformance treats the
  pipeline as a dimension, not a different product.

### 7.5 Conformance impact

- **New contract scenarios:**
  - the model list;
  - `?model=` reaching the fake upstream as the chosen deployment;
  - an unknown or disallowed model getting a 404;
  - the model lock;
  - `model_mismatch` on resume;
  - `reasoning` sent only for reasoning models.
- **Cascade gets new harness fakes** (#82): `FakeChatCompletionsServer` (scripted tool calls, like
  `ResponseScript`), `FakeTranscription` and `FakeTts`. A representative ordering subset runs per persona on the
  cascade pipeline.

### 7.6 Local mode: as-built design (#81, removed by #155)

Section 7.1 sketched local mode as Python loading ONNX Runtime GenAI/Whisper/Piper in-process, mirroring the
sibling drive-thru project's own local mode as closely as possible. The as-built #81 design took a different,
deliberately lighter-weight shape once it came time to implement it as a *persona-agnostic* pipeline behind the
same `PipelineProcessor` interface as `cascade` (#82): a companion-process boundary (`LocalProcessor` talking to
an external HTTP runtime over a three-endpoint `/v1/transcribe` / `/v1/chat` / `/v1/speak` contract, never
importing ONNX Runtime, Whisper or Piper directly), gated on `LOCAL_RUNTIME_ENDPOINT` plus each persona's own
`models.local` allow-list, with the same tools/session-metadata/wire-protocol contract as `realtime`/`cascade`
and a C# harness fake (`FakeLocalRuntimeServer`) standing in for the companion process in conformance.

**Brian decided on 2026-09-28 that the demo runs on Microsoft Foundry exclusively** (issue #155, reversing
decision 7 / ADR-001): there is no local runtime, no ONNX, no `phi-4-mini-local`, and no on-device pipeline of
any kind. Every piece of the as-built design above -- `LocalProcessor`, `LocalRuntimeClient`,
`FakeLocalRuntimeServer`, the `local` catalog/persona/schema entries, and the `LOCAL_RUNTIME_*` environment
surface -- was deleted. This section is kept only as a historical record of what #81 built and #155 removed; it
does not describe anything present in the current codebase.

## 8. Conformance and test dimensions

The suite's dimensions become **persona x backend x pipeline**. Model is a sub-dimension of pipeline.

| Change | Detail |
| --- | --- |
| Personas under test | `CONFORMANCE_PERSONAS` (default: every pack). The harness launches the backend with `PERSONAS` set to the same list. In external mode the operator starts the backend with that same list |
| Backends | The CI matrix axis `backend: [python, dotnet]` (dotnet from S2, #12). The gate requires every leg |
| Fake search | `FakeSearchServer` loads every persona's menu and routes by index name |
| Data-driven scenarios | Ordering, pricing, happy hour and combo theories take the persona as a parameter and read `testdata/personas/<id>/golden-*.json` |
| Persona-specific scenarios | Tagged `[Trait("persona", "<id>")]`: Route 44 and combos (Sonic); meal auto-fill, `modify` and meal numbers (McDonald's); extras split and the announced 25% happy hour (Dunkin); "no happy hour ever" (McDonald's) |
| Not applicable versus missing | A scenario that needs a capability (`bundles`, `happyHour`, `extras`, `dayparts`) is skipped only when that capability is absent from the pack. `PersonaCoverageTests` fails if any (persona, capability) pair has no scenario |
| Pipelines and models | Realtime scenarios run for the default model; a model-selection subset runs for every enabled realtime model; a cascade subset runs per persona (7.5) |
| Transport, security, resume | Run once against the default persona, plus `PersonaSmokeTests` for every persona |
| No off-menu | The rejection rows from section 6 |
| Data checks | Every pack validates against the schemas; each golden table matches its menu fields |
| Brand guards | `test_rebrand_verification.py` and `locales.test.ts` are inverted: brand words may appear only in their own pack, no pack mentions another brand, and the cross-brand design docs are excluded |
| Money | Unchanged: `0.000001m` tolerance for Python, exact for C# |

## 9. Frontend changes

This is the explicit exception to the "no frontend changes" rule (epic #6). Owner: Morty (#80).

| # | Change |
| --- | --- |
| F1 | `PersonaProvider`: read `?persona=`, fetch `/api/personas/<id>`, set CSS variables and `data-persona`, set the title and favicon, and merge the pack's i18n strings over the shared locale files |
| F2 | Replace every hard-coded brand hex value (87 in Sonic) with theme tokens, light and dark. This can start immediately |
| F3 | Hero, callouts, legal line and ticket headings come from the manifest; rename `SonicApp` to `App` |
| F4 | The menu panel fetches `menuUrl` at runtime; show the daypart toggle when `features.dayparts` is set (McDonald's) |
| F5 | The ticket renders `components` for bundles |
| F6 | **Persona picker** in the header: all three personas on one URL. Switching starts a new session, and asks first if the ticket has items |
| F7 | Per-persona resume key (`drivethru.resumeId.<persona>`), apology clip, demo data, and default voice |
| F8 | The WebSocket URL becomes `/realtime?persona=<id>&model=<id>` |
| F9 | Remove the dead Azure Speech toggle (row 38) |
| F10 | **Model picker** in Settings, like the voice picker: grouped by pipeline, limited to `/api/personas/<id>` models, locked for the session |
| F11 | **Backend switch** in the header (Python or .NET): a link to the other backend's hostname that keeps `persona` and `model`. It's hidden until `backends` lists two (section 10) |
| F12 | **Local mode:** was to be shown in the model picker only when `/health` reported it available (off by default). Never shipped in the picker -- the `local` pipeline was dropped entirely before this landed (#155) |

Fonts: the pack's `importUrl` must be on `fonts.googleapis.com` (the loader enforces this).

## 10. The new Azure environment

Decisions 1, 10 and 11: one new environment, fully independent of the old ones. Every other environment is torn
down (section 11).

| Setting | Value |
| --- | --- |
| Subscription | `BrianSwiger-Microsoft-External-2026` (`44847a42-6b69-4e6c-b7e5-ce7140469dd6`), the one the current demos use |
| Region | `eastus2` for every resource except AI Search, which is in `eastus` (eastus2 had no Basic-SKU Search capacity at provision time, #87; see 10.2) |
| azd env | `azureaidrivethru-prod` |
| Resource group | `rg-azureaidrivethru-prod` |
| Dependencies | **None** on `rg-sonic-demo`, `rg-mcd-demo` or `rg-dunkin-demo`. The `*_REUSE_EXISTING` flags stay `false`; no app setting, role assignment or hook may name an old resource. #87 checks this before cutover |

### 10.1 One frontend, two backends: recommendation

| Option | Pros | Cons |
| --- | --- | --- |
| **A. Two container apps (Python and .NET) in one ACA environment. Each serves the same frontend build, and a header switch navigates between the two hostnames (recommended)** | No proxy on the audio path, so the S8 A/B measures the backends themselves. In-app Entra auth (section 18) and sticky sessions work per app. Independent scale and rollback. Trivial A/B | Two hostnames (one per backend, each with all three personas); the URL changes when you switch backend |
| B. A third "front" app that serves the SPA and proxies `/realtime` and `/api` to an internal backend chosen by a selector | One hostname | An extra WebSocket hop for every audio frame; the proxy must preserve affinity for resume; one more component to secure and scale |
| C. Azure Front Door with path routing | One hostname, managed | Added cost; auth and cookies per origin; overkill for a demo |

**Recommendation: A.** "All three personas on one URL" holds on each backend. The backend switch keeps persona
and model, and an Entra SSO session makes the hop seamless. Option A is accepted (section 17); B stays the
fallback if a single hostname is ever required.

### 10.2 Resources (`rg-azureaidrivethru-prod`, eastus2)

| Resource | Notes |
| --- | --- |
| Microsoft Foundry (Azure OpenAI) account and project | **Its own** account, never `cog-axgpampkq3yfa`. Deployments in 10.3 |
| AI Search | **Its own paid service** (Basic SKU). Provisioned in **East US**, not eastus2 like the rest of the environment -- eastus2 had no Basic-SKU Search capacity left at provision time (#87), so Brian approved splitting it out via the existing `searchServiceLocation`/`AZURE_SEARCH_SERVICE_LOCATION` param (already in `infra/main.bicep`, wired only to the Search module). The free slot is taken by the old shared `gptkb-axgpampkq3yfa`, and paid removes the 3-index cap. One index per persona: `sonic-menu-items`, `mcdonalds-menu-items`, `dunkin-menu-items`, ingested from each pack's `menu/menuItems.json` by the postprovision hook (#84). The identity gets data-plane roles only on this service |
| Storage | Its own account for ingestion, as today |
| ACA environment, Log Analytics, ACR, user-assigned identity | Its own, as today, with `AzureAIDriveThru` names |
| Container app `python` | One gunicorn worker, sticky ingress, `/health` probe, in-app Entra auth (`AUTH_MODE=Entra`, `ENTRA_*`, EasyAuth disabled; section 18), `APP_SESSION_SECRET`, `PERSONAS=sonic,mcdonalds,dunkin`, `DEFAULT_PERSONA=sonic`, `AZURE_AI_MODEL_DEPLOYMENTS` (#85, #87) |
| Container app `dotnet` | Same settings and the same Foundry account, Search and identity. Added by S7 (#17) |
| Entra | **One** new app registration, `AzureAIDriveThru` (single tenant, public SPA client, `access_as_user`, role `DriveThru.User`, assignment required), with SPA redirect URIs for both hostnames. Created by `Setup-EntraAuth.ps1` run as Brian (section 18). The three old registrations are deleted at cutover (#88) |

### 10.3 Model deployments and capacity

Quota for GlobalStandard `gpt-realtime-2.1` is subscription-wide, and only about 10 units are spare while the old
environments hold theirs (36 for Sonic plus 10 DataZoneStandard for the siblings). So the new environment starts
small and scales after cutover.

| Deployment | SKU | Stand-up capacity | After cutover | Notes |
| --- | --- | --- | --- | --- |
| `gpt-realtime-2.1` (realtime default) | GlobalStandard | **10 or less** | Scale up (for example to 40) once #88 deletes `cog-axgpampkq3yfa` | Bicep param `realtimeDeploymentCapacity`; scaling is a param change plus `azd provision`, then the smoke again |
| One alternative realtime model (for example `gpt-realtime-mini`) | GlobalStandard | Small | Unchanged | Separate quota bucket with headroom; proves the model picker live |
| `gpt-5-mini` (cascade chat, OpenAI) | GlobalStandard | **50** | Unchanged | Version `2025-08-07` (`2026-08-07` doesn't exist in eastus2, verified read-only). 1 unit is ~1K TPM; a cascade turn (~2.5K-token system prompt plus tool schemas and history) needs more than 1. `OpenAI.GlobalStandard.gpt-5-mini` usage was 170/1000 at review time, so 50 fits with headroom (#118 review item 3) |
| `Phi-4` (cascade chat, non-OpenAI) | GlobalStandard | **20** | Unchanged | Version `7` (`1` doesn't exist; versions 2-7 are listed). Catalog-only until Unity's live tool-calling qualification in #87 passes (the eastus2 listing shows only `chatCompletion`, not `assistants`/`agentsV2`) -- removed from every persona's `models.cascade.allowed` until then. `AIServices.GlobalStandard.Phi-4` usage was 0/1000 at review time |
| `gpt-4o-transcribe` (cascade transcription) | GlobalStandard | **10** | Unchanged | Version `2025-03-20`, confirmed listed (`audioTranscriptions`). `OpenAI.GlobalStandard.gpt-4o-transcribe` usage was 0/400 at review time |
| `gpt-4o-mini-tts` (cascade TTS) | GlobalStandard | **10** | Unchanged | Version `2025-03-20`, confirmed listed (`audioSpeech`); `2025-12-15` is also listed and may be preferred once Unity's live voice-quality check in #87 passes. `OpenAI.GlobalStandard.gpt-4o-mini-tts` usage was 0/600 at review time |
| `text-embedding-3-large` | Standard | 30 | Unchanged | Ingestion and query embeddings |

All four cascade figures (versions, SKUs and capacities) were verified read-only against eastus2
(`az cognitiveservices model list -l eastus2`) and quota headroom against the subscription's current usage
(`az cognitiveservices usage list -l eastus2`) for Rick's #118 review items 2 and 3; see `infra/model-deployments.json`
for the exact deployment entries.

DataZoneStandard is not used: the US data-zone pool for `gpt-realtime-2.1` is exhausted by the siblings. Live
smoke and the manual checklist run serially, so 10 units are enough for verification; CI never touches quota.

## 11. Repo rename, sibling archive and teardown

- **Rename (decision 8, #69):** `swigerb/SonicAIDriveThru` becomes `swigerb/AzureAIDriveThru` right after this
  PR merges. GitHub keeps the redirects. Internal protocol ids stay (row 49).
- **Siblings until parity:** the McDonald's and Dunkin repos get security fixes only (McDonald's #6, Dunkin #11).

**Teardown sequence (decisions 9, 10 and 11, #88).** Squanchy executes; Brian confirms right before each
destructive step (Day 0 and Day 30).

| When | Step |
| --- | --- |
| Stand-up (#85, #87) | Create `rg-azureaidrivethru-prod` and deploy the unified Python app with realtime capacity 10 or less. Ingest the three indexes into the new Search service. Run the live smoke and the zero-dependency check (section 12). The old environments keep running, untouched |
| Day 0: cutover (#88) | 1. Brian's parity sign-off: the parity checklist (every row in section 3 marked done or dropped) and the manual UX checklist, both on the new URL. 2. Archive the McDonald's and Dunkin repos: README redirect to the new URL with `?persona=`, a `final-standalone` tag, then GitHub archive. 3. Brian confirms the teardown. 4. Delete `rg-mcd-demo` and `rg-dunkin-demo` (whole groups). 5. In `rg-sonic-demo`, delete resource by resource, keeping only `gptkb-axgpampkq3yfa`: the container app, ACA environment, ACR, identity, storage (and its Event Grid system topic), Log Analytics, and `cog-axgpampkq3yfa` (its deployments first, including the orphaned `gpt-realtime-1.5`; then the account; then purge it so the quota is released). 6. Brian deletes the three old Entra app registrations. 7. Scale the new realtime deployment (10.3), re-provision, and run the live smoke again |
| Days 1 to 29 | Grace period. `rg-sonic-demo` holds only the old Search service and its three indexes (`sonic-menu-items`, `mcdonalds-menu-items`, `dunkin-menu-items`), kept as a data reference. No app uses them. Rollback for the demo itself is a previous revision of the new app |
| Day 30 | Brian confirms. Delete `gptkb-axgpampkq3yfa`, then the empty `rg-sonic-demo`. Verify that no `rg-*-demo` group remains in the subscription |
| Later | The C# app joins the same environment (S7, #17). It doesn't wait for, and doesn't block, the teardown |

Dunkin's Azure Local edge stack was never deployed, so its removal is code-only (#86).

## 12. End-to-end validation plan

`dev` merges to `main` only when every level below is green for the scope delivered (S8, #18).

| Level | What | Runs where | Gate |
| --- | --- | --- | --- |
| Unit | pytest (backend), vitest (frontend), xUnit (C# backend, from S2), harness unit tests; every new behavior mutation-checked | CI on every PR | `conformance-gate` plus the unit jobs |
| Functional conformance | The black-box suite: **persona x backend x pipeline**, with a model-selection subset (section 8) | CI on every PR (fakes only) | Every leg of the matrix |
| UX, automated | Playwright per persona **x backend**: theme, menu (daypart for McDonald's), one voice order through the fake upstream, persona switch, model switch, backend switch, resume after a dropped socket, rate-limit clip | CI (browser job) | Required |
| Live Azure smoke | `smoke_realtime.py --persona --model` (and a cascade smoke) **per persona x backend x enabled model**: session config accepted, tools registered, transcription echo, one tool call, and a Search query against that persona's index | azd `postdeploy` on `azureaidrivethru-prod`; again after the Day 0 scale-up | Deploy is red if any fails |
| Zero-dependency check | Every app setting, role assignment and hook output of the new environment resolves only to resources in `rg-azureaidrivethru-prod` (a script over `azd env get-values` and `az role assignment list`). Re-run after Day 0: the live smoke must still pass with the old AOAI deleted | #87, then #88 | Required before Brian's teardown confirmation |
| UX, manual checklist | Per persona on the live URL: greeting, a three-item order with the brand rule (Route 44; a meal with auto-filled fries; a latte with extras), happy hour announced on Sonic and Dunkin and absent on McDonald's (fixed-clock build or a live window), off-menu rejection, barge-in, resume, backend and model switch. Unity records it | New environment, before Day 0 and before release | Brian's sign-off |
| Live A/B (S8) | Scripted orders against both backends, per persona: first-audio latency, tool correctness, CPU and memory per session, cold start | New environment | S8 report |

## 13. P2 implementation breakdown (issues)

Milestone **"P2 Unified demo (Python)"**. Sizes: S up to 1 dev-day, M 2 to 3, L 3 to 5. The umbrella is #20.

| Issue | Work item | Owner | Size | Depends on |
| --- | --- | --- | --- | --- |
| #69 | P2-0 Rename the repo to AzureAIDriveThru | Squanchy | S | this PR |
| #70 | P2-1 Persona pack skeleton and loader (Sonic only, no behavior change) | Summer | M | this PR |
| #71 | P2-2 #51 per-item menu fields, data-driven classification (Sonic) | Summer | M | #70 |
| #72 | P2-3 Complete the Sonic menu from source data, incl. floats (#64) | Summer, Unity | M | #71 |
| #73 | P2-4 No off-menu ordering; remove the keyword fallback; update conformance | Summer, Birdperson | M | #72 |
| #74 | P2-5 Per-session persona binding (Python) | Summer | L | #73 |
| #75 | P2-6 Model catalog, processor interface, per-session realtime model selection | Summer, Squanchy, Unity | M | #74 |
| #76 | P2-7 Conformance: persona, model and pipeline dimensions; invert brand guards | Birdperson, Beth | L | #70 (start), #74, #75 |
| #77 | P2-8 Shared bundle and extras engines | Summer, Unity | M | #74 |
| #78 | P2-9 McDonald's pack (no happy hour, meals, meal numbers, `modify` fix) | Summer, Unity | L | #77 (#75) |
| #79 | P2-10 Dunkin pack (extras, announced 25% happy hour) | Summer, Unity | M | #77 |
| #80 | P2-11 Frontend: theming and persona, model and backend pickers | Morty | L | #74, #75 (F2 starts now) |
| #81 | P2-12 Local mode as a persona-agnostic pipeline (default cloud) | Summer, Unity | L | #75 |
| #82 | P2-13 Cascade pipeline with Foundry chat models | Summer, Unity, Birdperson | L | #75 |
| #83 | P2-14 Voices, greetings, clips, prompt review per persona | Unity | M | #75, #78, #79 |
| #84 | P2-15 Ingestion and Search per persona | Summer, Squanchy | M | #70 (script), #72, #78, #79; #85 (live run) |
| #85 | P2-16 New Azure environment on Foundry (own AOAI and paid Search), ready for both backends | Squanchy, Rick | L | this PR (skeleton); #75 (catalog) |
| #86 | P2-17 Scope record: dashboard, CRM and edge not carried over | Rick | S | #69 (both edit the README) |
| #87 | P2-18 Deploy the unified Python app; live smoke per persona and model | Squanchy, Unity | M | #80 to #85 |
| #88 | P2-19 Parity sign-off, sibling archive, teardown (Search kept 30 days) | Rick, Squanchy | M | #87 and Brian |

**Critical path:** #70 → #71 → #72 → #73 → #74 → #75 → #82 → #87 → #88. The brand packs (#77, #78, #79, #83,
#84) run beside the model work (#81, #82) after #74.

**Can start right away (once this PR merges):**
- #69, the rename;
- #70, the loader;
- #85, the environment skeleton;
- #86, the scope record (after #69);
- #76, harness groundwork (after #70);
- #80 F2, the token refactor.

**Mapping from the proposed plan:** P2-1 to P2-12 of the proposal became #70, #71, #74, #76, #77, #78, #79, #80,
#83, #84, #87 and #88. New from the decisions: #69, #72, #73, #75, #81, #82, #85 and #86.

## 14. The C# port

The C# port starts after P2 lands and ports the unified app once. It deploys into the **same** new environment.

| Issue | Scope update |
| --- | --- |
| #12 S2 skeleton | `PersonaCatalog` and `ModelCatalog` from `personas/` and `config.yaml`; `/api/personas` (with `models` and `backends`); the `?persona=&model=` binding with 404s; the processor interface skeleton; `docs/dotnet_mapping.md` covers `personas.py` and the model catalog |
| #13 S3 middle tier | The `realtime` processor with per-session persona and model (reasoning from the catalog); the `cascade` processor through the Foundry v1 chat endpoint with the same tools. (`local` was planned last per section 17's original resolved ask, then dropped entirely by #155 before the C# port reached it) |
| #14 S4 tools and orders | Data-driven engines: sizes, bundles, extras, per-persona happy hour (none for McDonald's), the `meal_numbers` strategy, and the **no-off-menu rejection**; golden files per persona match to the cent |
| #15 S5 sessions | Resume binds persona and model (`persona_mismatch`, `model_mismatch`) |
| #16 S6 tooling | Ingestion, clips and smoke with `--persona` (and `--model`); the brand raw-export converters |
| #17 S7 deployment | The `dotnet` container app in `rg-azureaidrivethru-prod`, beside `python`: the same Foundry deployments, Search, identity and Entra app (a second redirect URI); the backend switch goes live |
| #18 S8 A/B and release | The section 12 plan in full, per persona x backend x model; then `dev` to `main` |
| #21 parity | The persona x pipeline suite is green on C# |

## 15. Risks

| # | Risk | Mitigation |
| --- | --- | --- |
| R1 | The move from module globals to a per-session persona regresses hardened Sonic code | #70 and #71 are behavior-neutral behind the full suite; #72 to #74 are the only intentional changes |
| R2 | No off-menu ordering removes things guests order today (a Coke at Sonic) | #72 completes the menu from source data **before** #73 removes the fallback; the prompt offers the closest menu item |
| R3 | A sibling brand rule is lost or changed in the port | Golden files come from the siblings' own tests; parity checklist in #88 |
| R4 | Realtime quota is tight until cutover: about 10 spare GlobalStandard `gpt-realtime-2.1` units subscription-wide | Stand up at capacity 10 or less (10.3); CI uses fakes only; live checks run serially; scale after #88 deletes `cog-axgpampkq3yfa` |
| R5 | A cascade chat model's tool calling is weaker than the realtime model's | Unity qualifies each model in #82 with the ordering subset before it enters the catalog; per-persona `allowed` lists keep weak models off |
| R6 | The new environment silently depends on an old resource, so the Day 0 teardown breaks it | Own AOAI and own Search from day one; reuse flags off; the zero-dependency check and a post-teardown smoke (section 12) |
| R7 | Conformance run time grows with persona x backend x pipeline | Only data-driven theories multiply; shard by persona; the cascade and model subsets are representative, not full |
| R8 | Prompt or brand leakage (a McDonald's crew member says "carhop") | Inverted brand guards (#76); per-persona smoke; Unity's review (#83) |
| R9 | Trademark optics of three real brands on one URL | Per-pack legal line; the allow-list can hide any persona without a code change |
| R10 | *(Historical, moot since #155)* Local mode drifts because CI can't run it | Unit tests with mocked models; the manual checklist before each release. The `local` pipeline was dropped entirely by #155 (2026-09-28), so this risk no longer applies |

## 16. Decisions (Brian, 2026-09-26)

| Q | Question | Decision | Where |
| --- | --- | --- | --- |
| 1 | Switching model | Per-session picker; **one** deployment, in a **new** Azure environment | Sections 5, 10; #74, #85 |
| 2 | Personas on the main URL | All three on a single URL | Section 5; #74, #80 |
| 3 | #64 floats | Not happy-hour discounted; can fill the combo drink slot. Added as Sonic menu items (not in the source export) | Sections 4.3, 6; #72; #64 closed |
| 4 | Off-menu fallback | None. Not on the menu in the source data means it can't be ordered. The keyword fallback is removed and conformance changes accordingly | Section 6; #72, #73 |
| 5 | McDonald's happy hour | McDonald's has none; only Sonic and Dunkin. Keep the per-persona enable/disable | 4.2 `pricing.happyHour: null`; #78 |
| 6 | Dunkin happy hour | Announced like Sonic | #79 |
| 7 | Brand-only features | Keep McDonald's local mode, default cloud, as a persona-agnostic pipeline. Remove the Dunkin crew dashboard, CRM simulator and Azure Local edge stack. Demo features work the same across personas except brand-specific logic. **Reversed 2026-09-28 (#155):** the local mode half of this decision is dropped -- the demo runs on Microsoft Foundry exclusively (realtime and cascade only); the Dunkin drop stands unchanged | Rows 37, 46 to 48; #81, #86, #155 |
| 8 | Repo name | `AzureAIDriveThru`. The focus is Microsoft Foundry, Azure, persona switching and model flexibility | Sections 7, 11; #69, #75, #82 |
| 9 | Sibling cutover | After parity: archive both repos, delete their container apps, keep the Search indexes 30 days | Section 11; #88 |
| 10 | Deploy target | Stand up a new environment and tear down all others | Sections 10, 11; #85, #88 |
| 11 | New environment details (22:52) | Subscription `BrianSwiger-Microsoft-External-2026`, eastus2, azd env `azureaidrivethru-prod`, group `rg-azureaidrivethru-prod`; its own AOAI/Foundry and its own paid Search (one index per persona); no dependency on the old groups; realtime capacity 10 or less until cutover. Teardown at cutover, confirmed by Brian right before: delete `rg-mcd-demo` and `rg-dunkin-demo`; delete everything in `rg-sonic-demo` (including the old AOAI and `gpt-realtime-1.5`) except the old Search service and its three indexes, which are deleted after 30 days | Sections 10, 11; #85, #87, #88 |

## 17. Resolved asks

Every earlier ask is closed with the default below (lead call, 2026-09-26). Nothing is waiting on Brian before P2
starts; he confirms only the two destructive teardown steps in #88.

1. **Float sizes and prices:** Mini, Small, Medium and Large at the Classic Shake prices (3.39, 4.19, 4.69, 5.69)
   (#72).
2. **Entra:** Squanchy creates the one new app registration with Brian's signed-in account during #85, with
   redirect URIs for both backend hostnames. The three old registrations are deleted at cutover (#88).
   **Superseded 2026-09-27 by ADR-002 (section 18):** in-app Entra ID following Retail Pulse, not EasyAuth. The
   registration is created by `Setup-EntraAuth.ps1`, run as Brian.
3. **C# local mode:** ported, last in the C# track (#13). **Superseded 2026-09-28 (#155):** local mode was
   dropped entirely before the C# port reached it; nothing local-mode-related is ported to C#.
4. **Two backend hostnames:** accepted (section 10.1, option A). Option B stays the fallback.

## 18. Authentication: Entra ID following Retail Pulse (ADR-002)

**Status:** Proposed, 2026-09-27 ([ADR-002](adr/ADR-002-entra-authentication.md)). It supersedes EasyAuth
everywhere in this document. The reference is Retail Pulse (`swigerb/retail-pulse`: ADR-005,
`docs/authentication-entra.md`, `docs/authentication-matrix.md`, `scripts/Setup-EntraAuth.ps1`,
`scripts/Verify-EntraAuth.ps1`, `scripts/Verify-ProductionAuth.ps1`). It's deployed in the same subscription and
tenant, so everything below is already proven there, except where noted as a deliberate difference.

**Why now:** staging (`capps-backend-pwvzk3t22wttm`) was public with no authentication, so anyone could spend our
realtime quota. It's locked (ingress disabled, min replicas 0) until this ships; the undo commands are on #85.

### 18.1 App registration

| Setting | Value | Note |
| --- | --- | --- |
| Display name | `AzureAIDriveThru` | Tagged `AzureAIDriveThruManaged`. The Setup script never adopts an app by name |
| Audience | Single tenant (`AzureADMyOrg`) | The subscription's tenant, the same as Retail Pulse |
| Client type | Public SPA client, auth code + PKCE | **No** client secret, password or certificate credential |
| App ID URI | `api://{clientId}` | |
| Delegated scope | `access_as_user` (enabled) | Exposed as `api://{clientId}/access_as_user` |
| Access token version | `requestedAccessTokenVersion = 2` | **Tighter than Retail Pulse:** we accept only the v2 issuer, not the v1 `sts.windows.net` form |
| App role | `DriveThru.User`, member type User, enabled | Required on every protected route |
| Service principal | `appRoleAssignmentRequired = true` | An unassigned user is stopped **by Entra at sign-in** (`AADSTS50105`, possibly on Entra's own error page) and never gets a token. Our 403 screen (18.6) covers a different case: a token without the role or the scope |
| SPA redirect URIs | `https://<python-fqdn>`, `https://<dotnet-fqdn>` (with #17), `http://localhost:8000`, `http://localhost:5173` | Bare origins, SPA platform only. **Tighter than Retail Pulse:** no Web platform redirect URIs |
| Pre-authorized client | Azure CLI (`04b07795-8ddb-461a-bbee-02f9e1bf7b46`) on `access_as_user` | Allows `az account get-access-token --scope api://{clientId}/access_as_user` for the headless checks in 18.11 |
| Initial assignment | The person who runs Setup (Brian) | Others are added with `-AssignUserUpn` or in Enterprise applications, Users and groups |

Demo viewers outside the tenant are invited as B2B guests and assigned the role. No multi-tenant registration.

### 18.2 Route matrix

The rule is **deny by default**. Anonymous access is an allow-list, and any new route is protected unless it's
added to that list on purpose. Each backend has a unit test that walks its route table, like Retail Pulse's
`EndpointAuthorizationCoverageTests`.

| Route | Access | Why |
| --- | --- | --- |
| `GET /` (SPA shell) and the SPA bundle (the static route) | Anonymous | The sign-in gate has to load before sign-in. The bundle holds only public ids |
| `GET /health` | Anonymous | The ACA probe can't send a bearer. Retail Pulse does the same |
| `GET /personas/{id}/assets/*` for `.svg .png .jpg .webp .ico .wav .mp3` | Anonymous (**public branding**) | Loaded by `<img>`, `<audio>`, the favicon and CSS, which can't carry a bearer. See below |
| `GET /personas/{id}/assets/*` for any other type (today `demo/*.json`) | Entra | Fetched with `fetch()`, so it can carry the bearer. Deny by default |
| `GET /personas/{id}/menu.json` | Entra | Fetched with `fetch()` |
| `GET /api/personas`, `GET /api/personas/{id}` | Entra | `/api/*` has no exceptions. The sign-in screen is neutral product branding, so it needs no persona data |
| `GET /api/auth/session` | Entra | Mints the layered session token for the caller (18.3) |
| `GET /realtime` (WebSocket) | Entra via `?access_token`, plus the session token | 18.3 |
| Anything else | Entra, or 404 | Deny by default |

**Public branding assets, not signed URLs or a cookie.** This is the simplest safe option.
- The files are logos, favicons and pre-recorded apology clips. The logos are the brands' public marks, and a clip
  is one generic sentence.
- The existing guard already limits the route to enabled packs and to real files under the pack's `assets/`
  directory (segment checks, symlink-resolved containment).
- We add an extension allow-list for anonymous access, and a persona-loader check that nothing else lives under
  `assets/` except `demo/*.json`, which is protected.
- Signed URLs would need signing on both backends and change the URL on every mint, which breaks the immutable
  `?v=` caching.
- A cookie would bring back cookie auth and CSRF handling for public files.
- Fetching assets as blobs with a bearer can't cover the favicon or CSS, and it delays the rate-limit apology clip,
  which has to play instantly.

### 18.3 WebSocket and the session token

Browsers can't set headers on a WebSocket, so `/realtime` reads the Entra access token from `?access_token`. That
query parameter is honored on `/realtime` **only**: on any other path it's ignored, so a token there still gets
401. This matches Retail Pulse's `/hubs` rule.

**The HMAC session token is kept and layered**, not replaced:

| Step | Behavior |
| --- | --- |
| Mint | `GET /api/auth/session` is protected like every `/api/*` route. Its token gains an `oid` claim, the caller's Entra object id. The TTL stays 900 s |
| Require | In Entra mode `require_session_token` is forced on; `config.yaml` can't turn it off. In Development pass-through it follows `config.yaml`, as today (default off) |
| Bind | `/realtime` rejects with 401 unless the session token is valid, has an `oid`, and that `oid` equals the Entra token's `oid` |
| Frontend | It fetches a fresh session token, with the bearer, before **every** connect, including reconnects and resumes. That fixes today's fetch-once-per-mount, which would fail a reconnect after 15 minutes once the token is required |

**Check order** on `/realtime`, identical on both backends:
1. Entra (401 or 403). It runs in middleware before the handler, so an unauthenticated caller learns nothing about
   personas, models or origins.
2. Origin (403).
3. Session token (401).
4. The concurrency limit, then the persona and model 404s, unchanged.

This makes a bad Origin plus no token a 401 on both backends, which conformance pins.

**Why keep and layer rather than replace:**
- **Contract stability.** Both backends already implement `/api/auth/session` and `?token` (the C# port has
  `SessionTokenService` and `RealtimeAuthGate`), and the suite pins them. Replacing them in the middle of the C#
  port changes both backends and the suite for no gain. Layering costs about 20 lines per backend.
- **Development mode.** With Entra unconfigured, the HMAC token and the Origin check remain the local guard, as
  today.
- **One identity per session.** Both credentials name the same `oid`, so logs and any later per-user limit have a
  single identity to key on.

**The honest limit:** the layer isn't a second factor against a stolen Entra token, because that token can mint a
session token. The mitigations for a token in a URL are these:
- the query-string token is read only on `/realtime`;
- tokens are never logged (18.4);
- the session token is short-lived.

If a security review wants more, the upgrade path is a single-use session ticket as the **only** `/realtime`
credential, so the Entra token never appears in a URL (ADR-002 alternatives).

A socket is authenticated at the upgrade. A session that outlives its access token keeps running, as in
Retail Pulse; every reconnect presents a fresh token. Resume stays keyed on the resume id, as Brian decided on
2026-09-22 (no principal binding). This design doesn't change that.

### 18.4 Token validation (parity contract)

| Check | Value (both backends) |
| --- | --- |
| Algorithm | RS256 only. `none` and HS* are rejected |
| Signing keys | The tenant JWKS, from OIDC discovery at `{ENTRA_INSTANCE}{tenant}/v2.0/.well-known/openid-configuration`. Fetched lazily (never at startup, so an Entra outage can't crash-loop the app), cached for 24 h, and re-fetched on an unknown `kid` at most once every 5 minutes |
| Issuer | Exactly `{ENTRA_INSTANCE}{tenant}/v2.0`, which is `https://login.microsoftonline.com/{tenant}/v2.0` in Azure |
| Tenant | `tid` equals `ENTRA_TENANT_ID`. Redundant with the issuer, and checked on purpose |
| Audience | `{clientId}` or `api://{clientId}` |
| Lifetime | `exp` and `nbf` required, with a 5-minute clock skew (the ASP.NET default and Retail Pulse's value, set explicitly on both backends) |
| Role | `roles` contains `ENTRA_APP_ROLE` (`DriveThru.User`) |
| Scope | `scp`, a space-delimited list, contains `ENTRA_API_SCOPE` (`access_as_user`). A token without `scp` (app-only) is rejected; Retail Pulse's `AllowAppOnlyTokens` opt-in is not ported |
| Failure | 401 with `WWW-Authenticate: Bearer` and `{"error":"unauthorized"}` for a missing, malformed, badly signed, expired, wrong-issuer or wrong-audience token. 403 with `{"error":"forbidden"}` for a valid token without the role or scope |
| Principal | `oid`, `tid` and `name` exposed to handlers. `request["principal"]` in Python, `HttpContext.User` in C# |
| Logging | Never log a token. Access logs record the path **without** the query string. In Python that's an access logger class that records the matched route template, on both run paths (below); a log **format** can't do it. In C#, `Microsoft.AspNetCore.Hosting.Diagnostics` is raised to Warning, because its Information "Request starting" line includes the query string. The existing `?token=` HMAC leak is fixed by the same change |

**Python access logging (both run paths).** A gunicorn format fix doesn't work. The app runs
`aiohttp.GunicornWebWorker`, whose `_get_valid_log_format` raises `ValueError` on any gunicorn `%(name)s`
directive (aiohttp 3.14.3 `worker.py`), so `--access-logformat '%(U)s'` would crash the worker at boot. aiohttp's
own format has no path-only directive: `%r` is the full request line, query string included, and the default
format (`%a %t "%r" ...`) is what leaks `?token=` today. So:
- **`PathOnlyAccessLogger(aiohttp.abc.AbstractAccessLogger)`** in a new `app/backend/access_log.py`. It logs the
  method, the **matched route's template** (`request.match_info.route.resource.canonical`, for example
  `/personas/{persona_id}/menu.json`, or `<unmatched>` when no route matched), the status and the elapsed time.
  Never `path_qs`, `rel_url`, `url` or `raw_path`, and not `request.path` either: it's percent-decoded, so a
  hand-crafted `/realtime%3Faccess_token=X` would log as `/realtime?access_token=X`. The template also keeps
  free-form paths out of the log.
- **One helper, `access_log_kwargs()`,** returns `{"access_log_class": PathOnlyAccessLogger}`. Both run paths
  spread it, so neither can drift. Each path keeps its own timeouts.
- **gunicorn:** the worker accepts an async factory that returns a `web.AppRunner`, and then uses that runner
  as-is. A new async factory `create_runner()` in `app.py` awaits `create_app()` and returns
  `web.AppRunner(app, logger=logging.getLogger("gunicorn.error"), access_log=logging.getLogger("gunicorn.access"),
  keepalive_timeout=65, shutdown_timeout=28.5, **access_log_kwargs())`. aiohttp's server errors go to gunicorn's
  error log on purpose, as they do when the worker builds the runner itself. The Dockerfile CMD
  targets `app:create_runner`. `create_app()` keeps returning a `web.Application`, so the tests and
  `python app.py` don't change shape. Because the worker then ignores gunicorn's `--keep-alive` and its derived
  shutdown timeout, the runner carries those values itself (65 s keep-alive and a 28.5 s shutdown, the worker's
  95% of the 30 s graceful timeout). The CMD drops `--keep-alive`, which would be silently ignored, and keeps
  `--graceful-timeout`, which the arbiter still uses. The CMD never sets `--access-logformat`.
- **A startup failure must stop gunicorn.** `create_app()` fails fast with `sys.exit(1)` (missing env vars,
  persona packs, catalogs, the production guard, and now the 18.5 mode checks). Under `GunicornWebWorker` that
  doesn't stop the process: `SystemExit` bypasses the arbiter's boot-failure path (gunicorn 23 `arbiter.py`), which
  halts the master with exit code 3 only when the worker raises an `Exception` before it has booted. With
  `SystemExit` the worker exits 1 and the master respawns it, about 100 boots in 8 seconds, with the port accepting
  TCP and HTTP hanging. That still fails closed, but it floods Log Analytics and makes "fails fast" untrue in the
  container. So `create_runner()` wraps `await create_app()` in `try` / `except SystemExit as exc:` and
  `raise RuntimeError("startup failed") from exc`. gunicorn logs `Worker failed to boot.` once and the master
  exits 3. `python app.py` is unchanged and still exits non-zero.
- **`python app.py`:** `web.run_app(create_app(), host=..., port=..., shutdown_timeout=..., keepalive_timeout=...,
  **access_log_kwargs())`, with the timeouts from `config.yaml` `connection`, as today.
- **Tests:** a unit test sends `/realtime?access_token=x&token=y` and `/realtime%3Faccess_token=x` through the
  logger and asserts neither value appears; a test asserts `create_runner()` returns a runner with
  `PathOnlyAccessLogger`; a test asserts `create_runner()` raises an `Exception`, not `SystemExit`, when
  `create_app()` exits; a test asserts the `__main__` path passes `access_log_kwargs()` to `run_app`; and a
  Dockerfile test asserts the CMD targets `app:create_runner` and contains no `%(` and no `--access-logformat`.
  Conformance rows 14 and 15 run `python app.py`, not gunicorn, so they don't cover the deployed path by themselves.
- **CI image-boot check** (new work: today's Docker job replaces the CMD with `sh -c`, so gunicorn has never booted
  in CI). It runs the built image with its **default CMD**:
  - **positive:** non-Production and unconfigured (Development pass-through), with placeholder values for the four
    `_REQUIRED_ENV_VARS` (without them `create_app()` exits and there's nothing to probe). Wait, with a timeout,
    for 200 from `/health`, then request `/realtime?access_token=CANARY&token=CANARY` and `/health?token=CANARY`.
    `docker logs` must contain the `/health` access line and must not contain `CANARY`;
  - **negative:** the same image with `RUNNING_IN_PRODUCTION=true` and no `AUTH_MODE` exits non-zero within
    30 seconds, with exactly one `Booting worker` line in its log.

**Python:** `PyJWT[crypto]` (PyJWT plus `cryptography`) with `jwt.PyJWKClient`. Its synchronous fetch runs through
`asyncio.to_thread`, so the event loop never blocks on a cache miss. It all lives in one new module,
`app/backend/entra_auth.py`, holding the settings, the validator and the aiohttp middleware. We rejected `msal` and
`azure-identity` because they acquire tokens and don't validate incoming ones.

**C#:** `Microsoft.AspNetCore.Authentication.JwtBearer` with explicit `TokenValidationParameters`:
- `ValidAlgorithms = RS256`, `MapInboundClaims = false`, and `RoleClaimType = roles`;
- the role and scope enforced in an authorization policy used as both `DefaultPolicy` and `FallbackPolicy`;
- `.AllowAnonymous()` only on the 18.2 allow-list, where the SPA static-files middleware is anonymous by
  position;
- `OnMessageReceived` reading `access_token` only when the path is `/realtime`.

We don't use Microsoft.Identity.Web: its `AzureAd` conventions and issuer handling make parity with PyJWT and
the fake issuer harder. `menu.json` and asset JSON must be endpoints, never served by the static-files
middleware, or they would bypass the policy.

### 18.5 Modes and the configuration contract

The mode is resolved at startup, the same way on both backends. There's no auto-detection of a provider. "Fail fast"
means the process exits non-zero before it listens: under gunicorn that's a worker boot failure that stops the
master (exit 3), never a respawn loop (18.4).

| `AUTH_MODE` | Ids configured | Environment | Result |
| --- | --- | --- | --- |
| `Entra` | Valid | Any | Entra enforced |
| `Entra` | Missing or placeholder | Any | **Fail fast** |
| unset | Valid | Not Production | Entra enforced. Configured always means enforced, which is what lets conformance test real validation black-box |
| unset | Partial, placeholder or non-GUID | Any | **Fail fast** (the same rule as the frontend, 18.6) |
| unset | None | Not Production | Development pass-through, with a synthetic identity (`oid` `00000000-0000-0000-0000-000000000001`, the role and the scope) and a loud startup warning |
| `Development` | None | Not Production | Development pass-through, the same as the row above |
| `Development` | Any id set (`ENTRA_TENANT_ID` or `ENTRA_CLIENT_ID`, valid or not) | Any | **Fail fast.** Pass-through happens only when Entra is unconfigured, so a configured process can never skip validation |
| unset or `Development` | Any | **Production** | **Fail fast** |
| anything else | Any | Any | **Fail fast** |

Explicit `Development` exists only so that a launcher can state its intent, mirroring the frontend's
`VITE_AUTH_MODE=Development` (18.6). It never overrides configured ids: an id set with `Development` is treated as
a misconfiguration, not as a request to bypass Entra.

"Production" means `RUNNING_IN_PRODUCTION=true` for Python and `ASPNETCORE_ENVIRONMENT=Production` for C#. Both are
pinned on the container apps.

Both backends read the **same flat environment names**, not Retail Pulse's `MicrosoftEntra__*`, so one Bicep env
block serves both apps:

| Name | Default | Notes |
| --- | --- | --- |
| `AUTH_MODE` | none | `Entra` or `Development`. Pinned to `Entra` in Azure |
| `ENTRA_TENANT_ID` | none | A GUID. The Bicep default is the subscription's tenant |
| `ENTRA_CLIENT_ID` | none | A GUID, from the Setup output |
| `ENTRA_API_SCOPE` | `access_as_user` | |
| `ENTRA_APP_ROLE` | `DriveThru.User` | |
| `ENTRA_INSTANCE` | `https://login.microsoftonline.com/` | Must be `https://`, except plain `http://` to a loopback host (`127.0.0.1`, `::1`, `localhost`), which the harness's HTTP-only fake issuer needs (fakes stay HTTP, #23). Never set in Azure, which `Verify-ProductionAuth.ps1` checks |
| `APP_SESSION_SECRET` | none | Already a container app secret for Python. **Required** in Production Entra mode (a fail fast, not today's warning), and added to the dotnet app |

We don't port Retail Pulse's separate `Security__RequireAuth` knob. Retail Pulse has it for a legacy disabled
mode. Our resolver can't express "disabled" outside Development, so a third knob would only be a new way to
misconfigure. In Azure, "require auth" is `AUTH_MODE=Entra` plus the production flag, and anything else won't
boot.

**Why only two modes:** Retail Pulse's provider-neutral contract (Entra, GitHub BFF, Anonymous) exists because
Retail Pulse built real second providers. We have one audience (Brian and invited viewers in his tenant) and no
second provider on the roadmap. Porting the contract would double the auth surface on two backends and in the
suite. The resolver is still explicit (an unknown mode fails), so adding a mode later is additive.

### 18.6 Frontend (MSAL)

Ported from Retail Pulse `src/RetailPulse.Web/src/auth/`, trimmed to Entra. New `app/frontend/src/auth/`:

| Piece | Behavior |
| --- | --- |
| `authConfig.ts` | Builds the MSAL config from `VITE_ENTRA_TENANT_ID`, `VITE_ENTRA_CLIENT_ID` and `VITE_ENTRA_API_SCOPE` (default `access_as_user`). The scope is `api://{clientId}/access_as_user`; the authority is `https://login.microsoftonline.com/{tenant}`; the redirect URI is `window.location.origin`; `navigateToLoginRequestUrl` is on, so `?persona=&model=` survive sign-in. The cache is `sessionStorage`, and PII logging is off |
| Mode and fail-closed | **A pass-through bundle needs an explicit `VITE_AUTH_MODE=Development`.** The CI, harness and Playwright builds set it. Every other build fails closed. The resolver, used the same way by `vite.config.ts` (build guard and marker) and by `authConfig.ts` (runtime): **`Entra`** with valid ids builds an Entra bundle; `Entra` with a missing, placeholder or non-GUID id fails. **`Development`** with no ids builds a pass-through bundle; `Development` with any id set fails (the mirror of the backend rule in 18.5). **No mode:** valid ids build an Entra bundle (configured means enforced, as on the backend); with no ids a production build (`vite build`) fails, and only the dev server (`import.meta.env.DEV`, `npm run dev`) passes through, as in Retail Pulse's `authMode.ts`. Any partial or placeholder id fails in every mode. **An unknown mode** fails. The guard and the marker read the values Vite actually bakes in, through `loadEnv(mode, root, "VITE_")` (so `.env`, `.env.local` and `.env.[mode]` files count, not only `process.env`). At runtime, a bundle that somehow reaches a browser misconfigured renders a configuration-error screen and makes no API or WebSocket call. **Deliberate difference from Retail Pulse:** RP decides pass-through on `import.meta.env.DEV`; we also allow it in a production build, but only with the explicit mode, because the harness and Playwright serve built bundles |
| Auth-mode marker | The build writes `<meta name="drivethru-auth-mode" content="Entra">` (or `Development`) into `index.html`, from the same `loadEnv` values as the guard, which `Verify-ProductionAuth.ps1` reads |
| `AuthGate` | Wraps `PersonaProvider` and `App`, so nothing persona-related loads before sign-in. The sign-in screen uses neutral product branding. An unauthenticated load **starts `loginRedirect` automatically**, which the Entra SSO cookie makes silent on the second hostname. **No-loop rule:** the automatic redirect happens only on a clean load. After an explicit sign-out, a 403, or a **failed redirect** it shows a manual **Sign in** button instead. A failed redirect is `handleRedirectPromise` throwing because Entra returned an error (the user cancelled, `consent_required` ("Need admin approval", 18.13 step 3), or `AADSTS50105` (not assigned)), or `loginRedirect` itself rejecting (for example `user_cancelled` when the visitor presses Back from the Entra page). The gate then shows Entra's error code with a short explanation (never the raw message, which can carry the user's UPN) and the **Sign in** button, never retries `loginRedirect` on its own, and never shows the "deployment misconfigured" screen, which is only for a bad build. A 403 (a token without the role or the scope) shows "not authorized: ask Brian to assign you **DriveThru.User**". An unassigned user usually never reaches our screens: Entra stops them at sign-in with `AADSTS50105`, often on Entra's own page (18.1) |
| `authorizedFetch` | Adds the bearer only to the explicit protected same-origin paths: `/api/**`, `/personas/{id}/menu.json`, and `/personas/{id}/assets/**` except the anonymous branding types (18.2). Never to any other request, and never cross-origin (the direct-AOAI debug mode keeps its own key). A 401 forces a token refresh and one retry, then goes back to sign-in; a 403 goes to access denied |
| WebSocket | Before each connect: acquire the token silently, then fetch `/api/auth/session` with it, then open `/realtime?persona=&model=&token=<hmac>&access_token=<entra>`. The same happens on reconnect and resume (18.3) |
| Removed | The legacy `context/auth-context.tsx` (`VITE_AUTH_URL`, `VITE_AUTH_ENABLED`) and its `.env` defaults |

### 18.7 Two backend hostnames

- **One registration** with one SPA redirect URI per origin (18.1). `Setup-EntraAuth.ps1 -FromAzdEnv` reads
  `BACKEND_URI` and `BACKEND_DOTNET_URI`, so adding the dotnet host in #17 means re-running Setup.
- **MSAL state per host.** MSAL's cache is per origin, so the other hostname starts with an empty cache. The gate's
  automatic `loginRedirect` goes to Entra as a top-level navigation, which uses the first-party SSO cookie. It
  returns without a prompt and without depending on third-party cookies (unlike `ssoSilent` in an iframe).
  Persona and model survive in the URL.
- **No CORS.** Each host serves its own SPA, every API and WebSocket call is same-origin, and the backend switch is
  a navigation. `security.allowed_origins` stays empty, and the Origin check stays same-origin.

### 18.8 Infra and the image build

| Change | Detail |
| --- | --- |
| Bicep params | `entraTenantId` (default `tenant().tenantId`), `entraClientId`, `entraApiScope`, `entraAppRole`, mapped in `main.parameters.json` from the azd env `ENTRA_TENANT_ID`, `ENTRA_CLIENT_ID`, `ENTRA_API_SCOPE` and `ENTRA_APP_ROLE` |
| Ingress switch | `backendIngressEnabled` (bool, **default `true`**) in `main.bicep`, from the azd env `BACKEND_INGRESS_ENABLED` (`"${BACKEND_INGRESS_ENABLED=true}"` in `main.parameters.json`, so an unset variable means on), passed to the Python app's `ingressEnabled` (the module param already exists). The dotnet app gets `backendDotnetIngressEnabled` from `BACKEND_DOTNET_INGRESS_ENABLED` with #17. It's what makes the 18.10 rollout dark until verified. The default keeps every other environment unchanged |
| Python app env | `AUTH_MODE=Entra`, the four `ENTRA_*` values, and `RUNNING_IN_PRODUCTION=true` (already pinned) |
| Dotnet app env (#17) | The same, plus `ASPNETCORE_ENVIRONMENT=Production` and the `APP_SESSION_SECRET` secret (the same param as Python) |
| EasyAuth removed | Delete `enableAuth`, `authClientId`, `authClientSecret`, the `aad-client-secret` secret plumbing, the `containerAppAuth` module and `core/security/container-app-auth.bicep`. `DEPLOY.md`'s EasyAuth section is replaced |
| postprovision | New `scripts/postprovision_auth.ps1` and `.sh`, run before `write_env`: `az containerapp auth update --enabled false` for every container app in the environment, and removal of a leftover `aad-client-secret`. Idempotent, and it fails the hook on error, as in Retail Pulse |
| SPA build values | `ARG VITE_AUTH_MODE=Entra` plus `ARG VITE_ENTRA_TENANT_ID VITE_ENTRA_CLIENT_ID VITE_ENTRA_API_SCOPE` in the **build stage only** of `app/Dockerfile` (and `Dockerfile.dotnet` from #17). **The mode defaults to `Entra`,** so an image build that loses its build args (the remote-build risk below) fails the Vite guard instead of shipping a Development bundle. `azure.yaml` `docker.buildArgs` sets `VITE_AUTH_MODE=Entra` and `VITE_ENTRA_*=${ENTRA_*}` from the azd env, for both services. `scripts/docker-build.sh` passes the same `--build-arg`s from the environment. The CI Docker job (`conformance.yml`) passes `--build-arg VITE_AUTH_MODE=Development` explicitly, and every CI `npm run build` step sets `VITE_AUTH_MODE=Development`. The Dockerfile's `.env` generation and `.dockerignore`'s `!app/frontend/.env` exception are removed, so build args are the only path in. These are public identifiers; the old "avoid ARG" comment is rewritten to say so |
| Contract test | A pytest guard (next to `test_azd_service_wiring.py`) asserting: both apps pin `AUTH_MODE=Entra` and their production flag; no app sets `ENTRA_INSTANCE`; no EasyAuth module or secret remains; `buildArgs` carry the four `VITE_*` values for every service; each Dockerfile declares `ARG VITE_AUTH_MODE=Entra`; `backendIngressEnabled` defaults to `true`, is mapped from `BACKEND_INGRESS_ENABLED`, and is wired to the Python app's `ingressEnabled` (and the dotnet app's with #17) |

The azd remote build (`remoteBuild: true`) must pass `buildArgs` through to ACR. Squanchy verifies this on the
first deploy. If it doesn't, that service sets `remoteBuild: false`, which is a one-line fallback.

### 18.9 Scripts (ported, non-secret)

| Script | What it does |
| --- | --- |
| `scripts/Setup-EntraAuth.ps1` | Creates or reconciles the registration in 18.1 through `az rest` against Graph, with the caller's delegated token. Preview by default; nothing is written without `-Apply`. Create-only by display name: it fails on a name collision and never adopts by name. Reconcile needs `-ClientId` or `-AppObjectId`, plus ownership and the managed tag. Sets the v2 token version, the scope, the role, SPA-only redirect URIs (`-FrontendOrigin`, `-RedirectUri`, `-FromAzdEnv`) and the Azure CLI pre-authorization; sets `appRoleAssignmentRequired`; and assigns the caller (or `-AssignUserUpn`). With `-FromAzdEnv` it fails on an empty `BACKEND_URI` (the azd env value is blank while ingress is off, 18.10), rather than reconciling the redirect URIs without it. Creates no secrets, reads no `.env` file, and prints only ids plus the `azd env set` lines |
| `scripts/Verify-EntraAuth.ps1` | Read-only check of the registration: single tenant; no password or key credentials; `api://{clientId}`; the scope and role present and enabled; v2 tokens; at least one SPA redirect URI and no Web ones; `appRoleAssignmentRequired`. Non-zero exit on any gap |
| `scripts/Verify-ProductionAuth.ps1` | Read-only live posture for **each** deployed app. **Env pins:** `AUTH_MODE=Entra`; the production flag; the `ENTRA_*` values matching the expected ids (printed redacted); no `ENTRA_INSTANCE`. **Active revisions:** lists every revision with `properties.active == true`, not only the app template. Each must run the expected image (`-ExpectedImage`, default the azd env `SERVICE_<NAME>_IMAGE_NAME`) and carry the same env pins; any active revision on another image or without `AUTH_MODE=Entra` fails, and zero active revisions fails. **EasyAuth:** passes only on an observed `platform.enabled == false` (an unknown state is a failure) and no `aad-client-secret`. **Anonymous probes:** `/` is 200 and carries the `Entra` marker; `/health` is 200; a branding asset is 200; `/api/personas`, `/api/auth/session`, `menu.json` and asset JSON are 401; `/api/personas?access_token=<synthetic>` is 401; the `/realtime` upgrade is 401 with no token and with a synthetic token. **Registration:** delegates to `Verify-EntraAuth.ps1`. **`-RevisionsOnly`:** runs only the env-pin, active-revision and EasyAuth checks, through `az`, with no HTTP probes, so it works while ingress is off (18.10 step 4a); it also fails if ingress is enabled, since it's the dark check. **Optional `-Authenticated`:** gets a delegated token through `az account get-access-token` and expects 200 from `/api/personas`, never printing the token |

Pester or pytest source-scan tests pin the safety properties, like Retail Pulse's `SetupEntraAuthScriptContractTests`:
writes only under `-Apply`, no credential creation, no `.env` reads, no token output.

### 18.10 Rollout and unlock order

**Today, `azd provision` re-enables external ingress and sets min replicas to 1.** And the apps use
`activeRevisionsMode: Single` (`infra/core/host/container-app.bicep`): ACA keeps the previous revision active until
a new one is ready, and keeps it if the new one never becomes ready. So "the new image fails fast" doesn't fail
closed by itself. It leaves the **old, unauthenticated revision** active, and if the provisioned revision also
fails (a bad id, a missing secret), a provision that turns ingress on makes that old revision public (min
replicas 0 is no protection: HTTP scaling wakes it). Deactivating revisions by hand doesn't fix this reliably:
it isn't documented to work in Single mode while the latest revision isn't ready, switching to Multiple mode
changes a second production setting mid-rollout, and ingress would still come on before any post-provision check.

**So enabling ingress is the last step, and it's a separate provision.** The modules already have the switch:
`ingressEnabled` in `container-app.bicep`, passed through by `container-app-upsert.bicep`. `main.bicep` exposes it
as `backendIngressEnabled`, mapped in `main.parameters.json` from the azd env `BACKEND_INGRESS_ENABLED`, **defaulting
to `true`** so no other environment changes (18.8). The revision mode never changes.

1. The Python backend, frontend and infra changes merge to `dev`.
2. Brian runs Setup (18.13), then `azd env set` the printed ids.
3. `azd deploy` while ingress is still disabled. The new image in Production without `AUTH_MODE` fails fast (under
   gunicorn that's a clean exit, 18.4). **That is expected:** `azd deploy` may report the revision as failed or
   unhealthy. Don't "fix" it by provisioning with ingress on.
4. **Dark provision:** `azd env set BACKEND_INGRESS_ENABLED false`, then `azd provision`. This pins
   `AUTH_MODE=Entra` and the ids and sets min replicas to 1 with ingress still off. Postprovision disables EasyAuth.
   The new revision starts, and its `/health` probe decides readiness while nothing is public. In Single mode ACA
   retires the old revisions on its own once the new one is ready.
   - **4a. Check while dark:** `Verify-ProductionAuth.ps1 -RevisionsOnly` (18.9). It must show exactly one active
     revision, on the new image, with `AUTH_MODE=Entra`, healthy and running. If an old revision is still active,
     the new one isn't ready: read its logs and fix it while dark. Don't touch the revision mode.
   - There's no manual deactivation and no `set-mode` step. Listing revisions by hand
     (`az containerapp revision list -n <app> -g rg-azureaidrivethru-prod --query "[?properties.active]"`) is an
     optional cross-check, never a gate.
5. **Public provision:** `azd env set BACKEND_INGRESS_ENABLED true`, then `azd provision`. Ingress is app
   configuration, not revision template, so this creates no new revision. Run `Verify-ProductionAuth.ps1` (and
   `-Authenticated`) at once. It repeats the active-revision check. On any failure, run
   `az containerapp ingress disable` (or set the variable back to `false` and provision), then fix it.
6. Brian signs in on the live URL, and the lock note on #85 is closed out.

**The rule:** never run `azd provision` or `azd up` against `azureaidrivethru-prod` with `BACKEND_INGRESS_ENABLED`
true (or unset) until 4a has passed with ingress off.

**Side effects of the dark provision:**
- With ingress off, the module's `uri` output is empty (`container-app.bicep`), so `BACKEND_URI` in the azd env is
  blank until step 5. Setup already ran at step 2. Nothing else reads it today (the postprovision and postdeploy
  hooks don't), and any new hook that reads it must tolerate an empty value. `Setup-EntraAuth.ps1 -FromAzdEnv`
  **fails** on an empty `BACKEND_URI` instead of reconciling the redirect URIs without it.
- There's no HTTP scale rule while ingress is off. Min replicas 1 keeps the revision running for the probe.

The dotnet app (#17) goes live only after the C# parity work, with the same steps and verification (its own
`BACKEND_DOTNET_INGRESS_ENABLED`, wired the same way).

### 18.11 Conformance

**Harness:** Birdperson's new `FakeEntraIssuer` in `Conformance.Fakes`, an HTTP loopback server that serves:
- `/{tenant}/v2.0/.well-known/openid-configuration`, with the issuer `http://127.0.0.1:{port}/{tenant}/v2.0`;
- a JWKS with one published RSA key;
- a second, unpublished key for the bad-signature row;
- a `Mint(claims overrides)` helper.

**Fixtures:** the harness runs backends as Production (`RUNNING_IN_PRODUCTION=true`,
`ASPNETCORE_ENVIRONMENT=Production`), and Production without Entra now fails fast. So:
- The **default fixture runs in Entra mode** against the fake issuer (`ENTRA_INSTANCE` pointing at the loopback
  address).
- The harness HTTP client and `RealtimeBrowserClient` attach a valid token by default: the header for REST, and
  `?access_token` plus the HMAC `?token` for `/realtime`. Every existing scenario then exercises the authenticated
  path.
- A `DevelopmentPassThrough` fixture (non-Production, unconfigured) runs the mode rows and the Playwright UX runs.

A backend that doesn't enforce auth yet ignores the extra env and tokens, so the harness can land first.

**Rows,** run on both backends. Each runs on REST (`/api/personas`, `/api/auth/session`, `menu.json`) and on
`/realtime` unless noted:

| # | Case | Expected |
| --- | --- | --- |
| 1 | No token | 401 with `WWW-Authenticate: Bearer`; on `/realtime`, 401 before the upgrade |
| 2 | Wrong tenant (issuer and `tid` of another tenant, signed with the published key) | 401 |
| 3 | Wrong audience | 401 |
| 4 | Missing role | 403 |
| 5 | Missing scope; app-only token (roles, no `scp`) | 403 |
| 6 | Expired past the skew (`exp` = now minus 10 min) | 401 |
| 6b | Expired inside the skew (`exp` = now minus 1 min) | Accepted (pins the 5-minute skew on both backends) |
| 7 | Bad signature (unpublished key); `alg: none`; HS256 | 401 |
| 8 | Valid | 200. On `/realtime`, the socket opens and `extension.metadata` arrives |
| 9 | A valid token as `?access_token` on a REST path | 401 (the query token is read on `/realtime` only) |
| 10 | `/realtime` with a valid Entra token and no session token; with a session token minted for another `oid`; with both matching | 401; 401; opens |
| 11 | Bad Origin and no token on `/realtime` | 401 (the Entra check runs first on both backends) |
| 12 | Anonymous allow-list: `/`, `/health`, `/personas/sonic/assets/logo.svg`, an apology clip | 200 with no token. `/personas/sonic/assets/demo/dummyOrder.json` is 401 |
| 13 | Unknown persona on `/realtime` with no token | 401, not 404 |
| 14 | Logging: after rows 8 and 10, the captured backend output contains neither token | Pass. The harness runs `python app.py`, not gunicorn, so the deployed gunicorn path is pinned by the unit and Dockerfile tests in 18.4 |
| 15 | Modes (launch-and-exit rows): Production and unconfigured; Production with `AUTH_MODE=Development`; `AUTH_MODE=Development` with `ENTRA_TENANT_ID` and `ENTRA_CLIENT_ID` set, not Production; `AUTH_MODE=Development` with only one id set, not Production; unknown `AUTH_MODE`; `ENTRA_INSTANCE=http://` to a non-loopback host; Entra with a placeholder client id | The process exits non-zero before listening. The harness runs `python app.py`; the gunicorn image is covered by #144's CI boot check (18.4) |
| 16 | Development pass-through (non-Production, unconfigured, with `AUTH_MODE` unset and with `AUTH_MODE=Development`) | 200 with no token; `/realtime` opens |

Rows are turned on per backend when that backend's auth lands: by the Python issue for `python`, and by the C#
issue for `dotnet`, through the existing `DotnetPlaceholderPolicy` pattern. The gate then requires both legs.

### 18.12 Work breakdown

| Issue | Work | Owner | Milestone | Depends on |
| --- | --- | --- | --- | --- |
| #143 | Harness `FakeEntraIssuer`, Entra-mode default fixture, token-attaching clients, rows 1 to 16 | Birdperson | P2 | This ADR |
| #144 | Python: `entra_auth.py`, route matrix, `/realtime` check order, layered session token, modes (explicit `Development` with ids fails fast), route-template access logger on both run paths, `create_runner()` turning a startup exit into a clean gunicorn halt, CI image-boot check | Unity | P2 | This ADR; #143 harness part lands first |
| #145 | Frontend: MSAL `AuthGate` (no retry after a failed redirect), `authorizedFetch` on the protected path list, per-connect WebSocket tokens, fail-closed config (explicit `Development` for pass-through bundles, `loadEnv`), marker, removal of legacy auth | Morty | P2 | This ADR. Mocked MSAL, so it can start now |
| #146 | Infra: Bicep pins, `backendIngressEnabled` switch, EasyAuth removal, postprovision, build args (`VITE_AUTH_MODE=Entra` default), Setup and Verify scripts (active-revision check, `-RevisionsOnly`), contract test, `DEPLOY.md`, then the rollout in 18.10 (dark provision, check, then enable ingress) | Squanchy | P2 | This ADR for the scripts; #144 and #145 for the rollout |
| #147 | C#: JwtBearer parity, fallback policy, layered session token, check order, modes (explicit `Development` with ids fails fast), log level, dotnet rows on | Beth | S5 | #13, #143; it gates #17 |

**Order:** #143 harness first, then #144. Meanwhile #145 and the #146 scripts proceed. Brian runs Setup once the #146
scripts merge. Then the #146 rollout (18.10) unlocks staging. #147 follows #13 and must land before #17 makes
the dotnet app public.

### 18.13 What Brian does

1. Review and accept this ADR (the PR).
2. When the Setup script merges, from the repo root with the azd env selected:
   `az login --tenant <tenant-id>`, then `./scripts/Setup-EntraAuth.ps1 -TenantId <tenant-id> -FromAzdEnv`
   (preview), then the same command with `-Apply`. It creates the registration and assigns him `DriveThru.User`.
   He then runs the `azd env set` lines it prints. Squanchy can drive it, but it has to run as Brian, because it
   creates an app registration in his tenant.
3. Only if the first sign-in shows "Need admin approval": grant admin consent for `AzureAIDriveThru` once
   (Enterprise applications, Permissions), or ask the tenant admin. Retail Pulse, in the same tenant, is the
   precedent. The gate shows Entra's error with a **Sign in** button and doesn't retry by itself (18.6), so press
   it after consent is granted.
4. Assign `DriveThru.User` to anyone else who should use the demo. Someone who isn't assigned is stopped by Entra
   at sign-in with `AADSTS50105`, not by the app.
