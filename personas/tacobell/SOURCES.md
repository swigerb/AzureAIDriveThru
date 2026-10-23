# Taco Bell demo data and assets

Retrieved **2026-10-22**. This pack is a curated demonstration, not a live menu,
restaurant 036309 price feed, or Taco Bell account integration.

| Surface | Source and scope |
| --- | --- |
| Menu names | Official [tacos](https://www.tacobell.com/food/tacos), [burritos](https://www.tacobell.com/food/burritos), [specialties](https://www.tacobell.com/food/specialties), [nachos](https://www.tacobell.com/food/nachos), [drinks](https://www.tacobell.com/food/drinks), and [value menu](https://www.tacobell.com/food/luxe-value-menu) category pages. [Cinnamon Twists](https://www.tacobell.com/food/sweets/cinnamon-twists) is confirmed by its official product page. The official [boxes and combos page](https://www.tacobell.com/food/boxes-and-combos) confirms customizable combos; individual three-crunchy/three-soft-taco legacy URLs returned legal-only content, so those user-requested meal configurations/inclusions are explicitly illustrative rather than verified current store offerings. Short descriptions and illustrations are original. The pack contains 23 selected items in nine categories, including four user-requested sauce packets, not the entire menu or verified store inventory. |
| Prices, sizes and tax | [Store 036309 menu](https://www.tacobell.com/food?store=036309) did not expose itemized store prices in accessible content. **Every numeric menu price, drink size offering, and 8% tax rate in this pack is an illustrative demo assumption**, visibly labeled in the UI and prompts. No sample price is represented as verified or current. There is no automatic refresh. |
| UI colors | The [official site's public CSS](https://www.tacobell.com/_next/static/css/5404ed4473507abd.css) exposed link purple `#501098` and focus violet `#9A23F8`; an official CMS theme exposed `#EA42D1`. These are observed website values, not a verified comprehensive brand-guide palette. Other shades are derived demo/accessibility colors. The downloaded current logo itself contains `#5B2994`. |
| Type | The same official CSS names `InterstateRegular` (body), `BrandonGrotesqueWeb-Black`, `GintoNordBold`, and GT America compressed/condensed/expanded/mono/standard variants. **No proprietary font files are bundled or loaded.** The pack uses locally available Arial with the app's sans-serif fallback, not an exact licensed Taco Bell typeface. |
| Logo | `assets/logo.svg` is the unchanged [Taco Bell 2023 SVG](https://upload.wikimedia.org/wikipedia/en/b/b7/Taco_Bell_2023.svg), downloaded from the [user-supplied Wikipedia file page](https://en.wikipedia.org/wiki/File:Taco_Bell_2023.svg), which attributes the mark to [Yum's Taco Bell brand page](https://www.yum.com/wps/portal/yumbrands/Yumbrands/company/our-brands/taco-bell). The file is non-free copyrighted/trademark material, **not an open-source asset**. The user reports Yum Corporation's permission for this demo; that permission is scoped to the demo, not a general redistribution license. This logo is not relicensed under the repository's software license. TACO BELL and the Bell logo belong to Taco Bell IP Holder, LLC. |
| Official media alternative | The [Yum media library](https://www.yum.com/wps/portal/yumbrands/Yumbrands/media-library) and its [PNG asset](https://www.yum.com/wps/wcm/connect/yumbrands/7c987259-4492-4e67-bcc4-2eb292935969/tb-logo-color-png.png?MOD=AJPERES) timed out from the implementation host. The user's authorized Wikipedia alternate was used instead. Logos-world was also inspected, but is not the asset source. Public availability alone was not treated as permission. |
| Rewards facts | The official [Rewards page](https://www.tacobell.com/rewards) describes earning in the app, kiosk or drive-thru; 10 points per qualifying dollar, rewards at 250 points, and Fire tier at 2,000 points earning 11 points per dollar. Program terms, eligibility and rewards can change. The accessible page did not verify the precise app check-in-code UX; the help portal returned a JavaScript loading shell. |
| Simulated rewards policy | `DEMO-PLUS` seeds 500 fictional points; `DEMO-NEW` seeds zero. These are not valid Taco Bell codes. The server allows one sample plain Crunchy Taco **or Cinnamon Twists** redemption at 250 demo points, computes 10 points per **paid pre-tax** sample dollar rounded down once per order, and awards/debits only at explicit demo completion. Cinnamon Twists is a user-directed fictional demo reward, **not verified official reward eligibility**. It is offered first when present, otherwise the plain taco is offered. A reservation locks the selected item until cancellation/removal, so later additions cannot silently switch the free item. This simplified point-spending model is **not a claim that actual Taco Bell rewards spend balances this way**. No real tier, expiry, signup, marketing consent, account validity, payment or purchase is simulated as an actual service. |
| Other assets | `taco-illustration.svg` is original artwork; no food photography is copied. The neutral English apology clip is reused from the repository's existing generated demo assets. |

## State and privacy contract

`speechStyle: naturalDriveThru` opts this pack into brief change acknowledgments and
private order/rewards context. Other packs retain `fullReadback` when the field is absent.
The welcome asks about app check-in; a linked welcome and current balance require a fresh
authoritative `get_order` result with `checkedIn: true`. Routine changes do not announce
totals. Explicit total requests and final summaries use current server amounts; a late
amendment requires a new final summary. Normal spoken ordering contains no simulation,
software, or internal-instruction narration. A direct question about real account/payment
integration must still be answered truthfully. Visible simulation/privacy disclosures stay
in place; this voice style does not enable real transactions.

The persona explicitly skips spoken preambles for routine menu, order and rewards
tool calls and asks for one acknowledgment after success. This follows the model's
documented preamble controls in the official
[Realtime prompting guide](https://developers.openai.com/api/docs/guides/voice-prompting)
(consulted 2026-10-22), which describes default preambles and both commentary and
final output as user-visible. The application continues to relay model speech;
there is no speech filtering, muting, extra model generation, or pipeline change.
Fake-client tests establish the instructions sent to the model, not live adherence.
Natural-voice acceptance remains unverified for this policy.

The opening greeting opts into validated, response-scoped instructions instead of a
synthetic guest message or a persistent "greet exactly" system rule. Realtime sends one
`response.create` with empty `input` and `tools`, and `tool_choice: none`; its real assistant
output remains in the default conversation. Cascade uses one existing Foundry chat request
with temporary system instructions and no tools, then retains only its actual assistant
answer through the existing speech/history path. Later turns retain the persona, actual
conversation and normal tools without the one-shot instruction. No old conversation is
deleted or filtered; started sessions still resume silently, including an interrupted greeting.
This follows the [Realtime conversation guide](https://developers.openai.com/api/docs/guides/realtime-conversations)
(consulted 2026-10-23). Fake-client tests establish request scope, ordering and cancellation,
not that the observed model greeting reset or earlier spoken meta-note is eliminated;
those causal effects and strict natural-voice acceptance remain unverified.

Mild, Hot, Fire and Diablo Sauce Packet lines use explicitly illustrative zero prices
and persist requested quantities, including two packets. Names are user-directed; store
availability and complimentary pricing were not verified from a current store feed.
Packets are neither rewardable nor point-earning. Crunchy Taco and Cinnamon Twists remain
the only configured reward choices; Beefy 5-Layer Burrito is not a configured reward.

The input accepts only the two displayed fictional codes. Unknown values are rejected
in the browser before transmission; the backend independently checks an exact allow-list
and neither stores nor echoes submitted identifiers. Do not enter real member data.
Codes are not passed to AI tools or an account service. Only the simulated balance and
check-in flag are associated with the current session/order.

Redemption reserves one plain item already on the ticket; customized items are not eligible.
Removing the last eligible line releases the reservation without spending points. Repeated
redemption or completion cannot spend/award twice. Completed tickets cannot be edited until
reset. Reset clears the ticket/reservation/award marker but retains the session's check-in
and balance; reconnect/resume retains both. Ending/expiring the session discards the ledger;
another session always starts unchecked with zero points.

The sample three-taco combo uses one Standard meal line with crunchy or soft shells,
one included Medium drink and a sample $0.50 Large Baja Blast upgrade. The shared
component-upcharge engine prices this consistently whether the drink or meal is added
first. Chips and cheese and Cinnamon Twists are separate sides, never absorbed into this
drink-only bundle. Free omissions are retained on the ticket (`No Tomatoes`); this is not
an ingredient/allergen guarantee. Shell changes replace the exact prior combo and preserve
agreed omissions. A read-back/total does not complete the order: forgotten items can be
added until the user explicitly selects **Complete order**.

The Edge customization regression uses deterministic synthetic recognized guest utterances
and model tool calls through real `search`, `update_order`, `get_order` and WebSocket handlers.
Its assistant read-backs come from actual server summaries. It verifies rendering,
calculations and state transitions, **not live speech recognition or model understanding**.
The test's local lexical search fake uses distinctive terms (`3`, `Baja`, `Cinnamon`,
`Chips`) and asserts each requested canonical product appears in search results before adding.

Enable `tacobell` in `PERSONAS` when an explicit enabled-persona list is configured.
The existing default persona remains unchanged. A future authorized live deployment needs
the normal `tacobell-menu-items` search index populated from this pack; this change does not
provision Azure resources, upload an index, deploy, or merge anything.
