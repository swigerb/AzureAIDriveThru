# Persona architecture: one drive-thru app, three brands, two backends

- **Issue:** #19 (P1 design spike), including the design for #51. Part of epic #6.
- **Decision record:** [ADR-001](adr/ADR-001-persona-architecture.md)
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
- **Features:** McDonald's local mode stays as a persona-agnostic option that defaults to cloud. Dunkin's crew
  dashboard, CRM simulator and edge stack are dropped.
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
| 9 | Local-model prompt | n/a | `local_system_prompt.yaml` (Phi-4) | n/a | Persona data | `prompts/local_system_prompt.yaml` per pack, for the persona-agnostic local pipeline (row 37, #81) |

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
| 37 | McD local mode | n/a | Phi-4 ONNX, Piper TTS, Whisper STT, `processor_router.py`; about 3,000 backend lines; UI toggle; `docker-compose.local.yml` | n/a | Shared code | Kept as the persona-agnostic `local` pipeline, off by default (decision 7, section 7, #81) |
| 38 | Azure Speech mode | Frontend toggle only | `azurespeech.py`, `azure_speech_gpt4o_mini.py`, but nothing imports them, and no backend registers `/azurespeech/*` | Same | Drop | Dead in all three; remove the toggle |
| 39 | Theme | `--brand-red 341 100% 45%`, `--brand-blue 208 52% 33%`, light/dark; Nunito Sans and Montserrat; **87 hard-coded hex values** in `App.tsx`, `order-summary.tsx`, `menu-panel.tsx` | `--brand-red 357 100% 43%`, dark `40 12% 14%`; 114 hex values in 5 files | `--brand-orange 28 100% 58%`, `--brand-pink 329 100% 45%`, cream, brown; Fredoka; 76 hex values | Persona data plus shared code | `persona.json` `ui.theme` tokens applied by `PersonaProvider` |
| 40 | Identity, copy, legal | Logo svg/png, title "Sonic Drive-In Voice Ordering", hero ("Carhop Pick"), ticket "Carhop ticket / Your Sonic Order", legal line naming Inspire Brands and Sonic Corp., `app.title` and `status.notRecordingMessage` in 4 locales | Logo, "McDonald's AI Drive-Thru", same keys | Logo, "Dunkin' Voice Crew", extra favicons, same keys | Persona data | `persona.json` `ui` block plus `assets/` |
| 41 | Menu panel | Imported at build time (`menu-panel.tsx:1`) | Adds a breakfast/lunch toggle filtering on `menuPeriod` | Categories only | Shared code plus persona data | Fetched at runtime; the daypart toggle is shown when the pack declares `dayparts` |
| 42 | Demo data, resume key | `dummyOrder.json`, `dummyTranscripts.json`; `sonic.resumeId` | Own demo data | Own demo data | Persona data; shared key | `assets/demo/`; key `drivethru.resumeId.<persona>` |

### 3.6 Config, infra and brand-only features

| # | Item | Sonic | Mc | Dunkin | Class | Unified home |
| --- | --- | --- | --- | --- | --- | --- |
| 43 | VAD and search tuning | VAD 0.5 / 200 ms; KNN 15, top 3 (perf audit) | Same | VAD 0.7 / 500 ms; KNN 50, top 5 | Shared config | Sonic's tuned values. The browser sends its own VAD anyway |
| 44 | Env vars | `AZURE_SEARCH_INDEX`, `STORE_TIMEZONE`, `SONIC_MENU_ITEMS_PATH` / `MENU_ITEMS_PATH` | Adds `LOCAL_MODE_*`, `AZURE_SEARCH_CONTENT_FIELDS` | Adds `USE_LOCAL_PIPELINE`, `CRM_DB_PATH`; `.env.template` for AKS, ACR, Key Vault | Shared code | Add `PERSONAS`, `DEFAULT_PERSONA`, `PERSONAS_DIR`, optional `SEARCH_INDEX_<ID>`, and `AZURE_AI_MODEL_DEPLOYMENTS` (section 7). `AZURE_SEARCH_INDEX` applies only when one persona is enabled. The menu-path vars are removed. `LOCAL_MODE_*` stays for the local pipeline. The edge and CRM vars are dropped |
| 45 | Bicep, azd | `APP_SESSION_SECRET`, sticky ingress, EasyAuth, replicas 1 to 5, postdeploy smoke | Same, minus small ordering changes; `MCD_SKIP_REALTIME_SMOKE` | No `APP_SESSION_SECRET`, no replica bounds or health probe (hardening lag); `DUNKIN_SKIP_REALTIME_SMOKE` | Shared code | Sonic's, plus the persona env vars; one smoke that loops over personas |
| 46 | Crew dashboard and simulator | n/a | n/a | `/dashboard` WebSocket, `/simulator/demo`, `app/employee-dashboard` SPA served at `/crew/` (467 lines), `drive_thru/` simulator (595 lines), `session_manager` publishes orders to it | Drop (decision 7) | Not carried over (#86). The code stays at the archived repo's `final-standalone` tag |
| 47 | CRM | n/a | n/a | `crm/` (252 lines, SQLite), `crm_seed.json`, `seed_crm.py`. Only the simulator's fake guests use it; the voice agent never calls it | Drop (decision 7) | Not carried over (#86) |
| 48 | Azure Local edge | n/a | n/a | `Dockerfile.edge`, `requirements-edge.txt`, `rtmt_local.py` (667 lines), `k8s/` (5 files), `flux/` (22 files), `deploy-edge.*`, 2 docs | Drop (decision 7) | Not carried over (#86) |
| 49 | Internal protocol ids | `sonic_mt_` item-id prefix (#29 authorship), `sonic_*` event ids, logger `sonic-drive-in` | Own | Own | Shared code, **unchanged** | Never shown to guests; the #29 authorship contract depends on the prefix |
| 50 | Brand-rule tests | `test_combo_orders.py`, `test_menu_utils.py`, golden files, the conformance suite (about 420 scenarios) | `test_order_logic.py`, `test_menu_utils.py`, `test_extras_rules.py` | `test_happy_hour.py`, `test_extras_rules.py`, `test_update_order_result.py`, `test_crm.py` | Persona data (golden rows) | `tests/conformance/testdata/personas/<id>/`; the CRM and dashboard tests are dropped |
| 51 | Hardening lag (not brand) | S1 and S1.5 relay security, resume, rate limit, reasoning, exact money, conformance hooks | Partly ported; #6 open | Partly ported; #11 open | Shared code | Sonic's implementation is the base |

**Count:** 51 rows. 31 are persona data (some with a shared engine that reads them), 13 are shared code or
config (one of them, row 37, the kept local mode), 1 is a strategy, 1 is removed (row 29, the category keyword
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
      local_system_prompt.yaml   short prompt for the on-device local pipeline (section 7, #81)
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
    "cascade":  { "default": "gpt-5-mini", "allowed": ["gpt-5-mini", "phi-4"] },
    "local":    { "default": "phi-4-mini-local", "allowed": ["phi-4-mini-local"] }
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
    "hero": { "headline": "Sonic ordering powered by Azure conversation intelligence", "callouts": [] },
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

### 4.3 Per-item menu fields (#51)

`menuItems.json` keeps today's shape (`menuItems[].category`, `items[]` with `name`, `sizes[]`, `description`,
...). The new fields are optional and additive, and the defaults are the safe ones:

| Field | Type, default | Meaning | Replaces |
| --- | --- | --- | --- |
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
| Build | Per persona: `MenuIndex` (normalized key and aliases to item), a `PromptLoader` pointed at `personas/<id>/prompts` (today's class, given a path instead of a brand), and one `SearchClient` per index. Once per process: the model catalog (section 7) | `PersonaCatalog` and `ModelCatalog` singletons; strategies as keyed services (`ISearchQueryRewriter`: `none`, `meal_numbers`); processors as keyed services (`realtime`, `cascade`, `local`) |
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
| `GET /api/personas/{id}` | The pack's `ui` block, plus `voice.default`, `locales`, `features.dayparts`, `menuUrl`, and the selectable `models` per pipeline (section 7). 404 if not enabled |
| `GET /personas/{id}/assets/*`, `GET /personas/{id}/menu.json` | Static files from the pack, immutable caching (the existing compression and caching middleware) |
| `GET /realtime?persona={id}&model={id}` | Omitted persona: `DEFAULT_PERSONA`. Omitted model: the persona's default realtime model. Unknown or not enabled: **HTTP 404 before the WebSocket upgrade**, never a silent fallback. Both are fixed for the session |
| `extension.metadata` | Gains `persona`, `model` and `pipeline` (additive) |
| Resume | The held session remembers its persona and model. `extension.resume` from a socket opened with a different persona or model gets `extension.resume_rejected` with `reason: "persona_mismatch"` or `"model_mismatch"`, then a fresh session (the existing rejection path, then metadata) |
| Session token, Origin checks, EasyAuth | Unchanged. The persona and model are not secrets and are not in the HMAC token |

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
| `local` | Whisper, then Phi-4 mini (ONNX), then Piper, all on-device (McDonald's local mode, now persona-agnostic) | Developer machine; off by default | Ported in #81 |

**Cascade turn-taking parity (Rick's PR #118 review item 6/6).** The frontend can't tell pipelines apart, so
`cascade` matches `realtime`'s observable behavior for:

- **Greeting on connect** -- the persona's `greeting.yaml`, spoken through TTS, with the same `response.*` frames.
- **Barge-in** -- a local `speech_started` cancels the in-flight chat call and TTS stream; no further
  `response.audio.delta` is sent for the cancelled turn.
- **Rate limit** -- a 429 from chat, transcription or TTS goes down the same rate-limit notice path the frontend
  already handles for `realtime` (`extension.rate_limited`), not a silent failed turn.

Each has a conformance row (`tests/conformance/.../Scenarios/Cascade/CascadeConformanceTests.cs`).

**Explicitly deferred, tracked in #126:** resume (a reconnect on `cascade` today gets a fresh session, with no
grace-hold/rehydration equivalent to `realtime`'s), the idle-nudge, and echo suppression (`cascade_processor.py`
never imports `audio_pipeline.py`'s `EchoSuppressor`, so there is no cooldown window after TTS playback the way
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
       - { id: phi-4-mini-local, pipeline: local, label: "Phi-4 mini (on device)", runtime: onnx }
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
Local mode is selectable only when `/health` reports it available (models present on the machine).

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
- **Local.** Python runs ONNX Runtime GenAI, Whisper and Piper (ported from McDonald's). C# parity for local mode
  was resolved in section 17: port it, last in the C# track.
- **Processor interface.** Both backends implement `realtime`, `cascade` and `local` behind one interface that
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
- **Local mode is not in CI conformance,** because the models are too large. It's covered by unit tests with
  mocked models, plus the manual UX checklist.

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
| F12 | **Local mode:** shown in the model picker only when `/health` reports it available. It is off by default |

Fonts: the pack's `importUrl` must be on `fonts.googleapis.com` (the loader enforces this).

## 10. The new Azure environment

Decisions 1, 10 and 11: one new environment, fully independent of the old ones. Every other environment is torn
down (section 11).

| Setting | Value |
| --- | --- |
| Subscription | `BrianSwiger-Microsoft-External-2026` (`44847a42-6b69-4e6c-b7e5-ce7140469dd6`), the one the current demos use |
| Region | `eastus2` (all resources, including Search) |
| azd env | `azureaidrivethru-prod` |
| Resource group | `rg-azureaidrivethru-prod` |
| Dependencies | **None** on `rg-sonic-demo`, `rg-mcd-demo` or `rg-dunkin-demo`. The `*_REUSE_EXISTING` flags stay `false`; no app setting, role assignment or hook may name an old resource. #87 checks this before cutover |

### 10.1 One frontend, two backends: recommendation

| Option | Pros | Cons |
| --- | --- | --- |
| **A. Two container apps (Python and .NET) in one ACA environment. Each serves the same frontend build, and a header switch navigates between the two hostnames (recommended)** | No proxy on the audio path, so the S8 A/B measures the backends themselves. EasyAuth and sticky sessions work per app as they do today. Independent scale and rollback. Trivial A/B | Two hostnames (one per backend, each with all three personas); the URL changes when you switch backend |
| B. A third "front" app that serves the SPA and proxies `/realtime` and `/api` to an internal backend chosen by a selector | One hostname | An extra WebSocket hop for every audio frame; the proxy must preserve affinity for resume; one more component to secure and scale |
| C. Azure Front Door with path routing | One hostname, managed | Added cost; EasyAuth and cookies per origin; overkill for a demo |

**Recommendation: A.** "All three personas on one URL" holds on each backend. The backend switch keeps persona
and model, and an Entra SSO session makes the hop seamless. Option A is accepted (section 17); B stays the
fallback if a single hostname is ever required.

### 10.2 Resources (`rg-azureaidrivethru-prod`, eastus2)

| Resource | Notes |
| --- | --- |
| Microsoft Foundry (Azure OpenAI) account and project | **Its own** account, never `cog-axgpampkq3yfa`. Deployments in 10.3 |
| AI Search | **Its own paid service** (Basic SKU, eastus2). The free slot is taken by the old shared `gptkb-axgpampkq3yfa`, and paid removes the 3-index cap. One index per persona: `sonic-menu-items`, `mcdonalds-menu-items`, `dunkin-menu-items`, ingested from each pack's `menu/menuItems.json` by the postprovision hook (#84). The identity gets data-plane roles only on this service |
| Storage | Its own account for ingestion, as today |
| ACA environment, Log Analytics, ACR, user-assigned identity | Its own, as today, with `AzureAIDriveThru` names |
| Container app `python` | One gunicorn worker, sticky ingress, `/health` probe, EasyAuth, `APP_SESSION_SECRET`, `PERSONAS=sonic,mcdonalds,dunkin`, `DEFAULT_PERSONA=sonic`, `AZURE_AI_MODEL_DEPLOYMENTS` (#85, #87) |
| Container app `dotnet` | Same settings and the same Foundry account, Search and identity. Added by S7 (#17) |
| Entra | **One** new app registration with redirect URIs for both hostnames, created by Squanchy with Brian's account during #85 (section 17). The three old registrations are deleted at cutover (#88) |

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
| #13 S3 middle tier | The `realtime` processor with per-session persona and model (reasoning from the catalog); the `cascade` processor through the Foundry v1 chat endpoint with the same tools; `local` last (section 17) |
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
| R10 | Local mode drifts because CI can't run it | Unit tests with mocked models; the manual checklist before each release |

## 16. Decisions (Brian, 2026-09-26)

| Q | Question | Decision | Where |
| --- | --- | --- | --- |
| 1 | Switching model | Per-session picker; **one** deployment, in a **new** Azure environment | Sections 5, 10; #74, #85 |
| 2 | Personas on the main URL | All three on a single URL | Section 5; #74, #80 |
| 3 | #64 floats | Not happy-hour discounted; can fill the combo drink slot. Added as Sonic menu items (not in the source export) | Sections 4.3, 6; #72; #64 closed |
| 4 | Off-menu fallback | None. Not on the menu in the source data means it can't be ordered. The keyword fallback is removed and conformance changes accordingly | Section 6; #72, #73 |
| 5 | McDonald's happy hour | McDonald's has none; only Sonic and Dunkin. Keep the per-persona enable/disable | 4.2 `pricing.happyHour: null`; #78 |
| 6 | Dunkin happy hour | Announced like Sonic | #79 |
| 7 | Brand-only features | Keep McDonald's local mode, default cloud, as a persona-agnostic pipeline. Remove the Dunkin crew dashboard, CRM simulator and Azure Local edge stack. Demo features work the same across personas except brand-specific logic | Rows 37, 46 to 48; #81, #86 |
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
3. **C# local mode:** ported, last in the C# track (#13).
4. **Two backend hostnames:** accepted (section 10.1, option A). Option B stays the fallback.
