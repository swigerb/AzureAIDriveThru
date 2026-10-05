using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Conformance.Harness;
using Xunit;

namespace Conformance.Tests;

/// <summary>
/// Issue #76, Rick's PR #101 review item 1: replaces the hard-coded dotnet CI leg filter (a
/// <c>FullyQualifiedName~ClassNamePart</c> substring match, which any future test class whose
/// name happens to contain one of those substrings would silently join) with an explicit
/// <c>[Trait("Dotnet", "ready")]</c> on exactly the scenarios docs/dotnet_mapping.md documents as
/// green against the C# skeleton (167 distinct tagged test *methods* as of PR #149 round 2 -- several
/// <c>[Theory]</c> methods have multiple <c>[InlineData]</c>/<c>[MemberData]</c> rows each, so
/// <c>dotnet test</c>'s own pass count for the same filter is higher than 167 result rows; this test
/// counts methods, matching the <c>FullyQualifiedName</c> filter it replaced). The dotnet CI leg now runs
/// <c>--filter "Dotnet=ready&amp;Category!=Browser"</c> instead.
///
/// This test is the guard that the tagged count can't silently shrink: a PR that removes or
/// renames a tagged scenario without adding a replacement fails here, instead of just quietly
/// running fewer scenarios in a CI log nobody reads. C# PRs #13-#16 are expected to *grow* this
/// count as more of the skeleton gets a real pipeline wired in (docs/dotnet_mapping.md) -- they do
/// that by adding the trait directly in their own test files, with no workflow/CI edit required
/// (the dotnet leg's filter already covers any newly tagged scenario for free).
///
/// Issue #143/ADR-002 (R10, Rick's PR #158 round 1 review): tagged nine <c>Scenarios/Auth</c>
/// auth-row test classes. Five of them (<c>AuthModeLaunchTests</c>, <c>AuthRowLoggingTests</c>,
/// <c>AuthRowRealtimeTokenTests</c>, <c>AuthRowRestTokenTests</c>, <c>AuthRowSpecialCaseTests</c> --
/// 18 methods) route every test method through <c>RunAuthRowAsync</c>/<c>AssertFailsFastAsync</c>,
/// which call <c>Assert.Skip</c> (via <see cref="Conformance.Harness.AuthRowCapability.ShouldSkipCurrentBackend"/>)
/// before any backend interaction: they show up as skipped, not run, on the dotnet leg until issue
/// #147 flips <c>DotnetEnforcesAuth</c>. Tagging them was safe (skipped tests can't fail the dotnet
/// leg), but <see cref="AuthRowGatedTypeNames"/> excludes them from THIS floor: counting a
/// skip-only method here would let a real regression (removing genuinely-passing coverage
/// elsewhere) hide behind these always-skipped rows staying tagged, which defeats the point of a
/// coverage floor. The other four Auth classes -- <c>AuthRowCasesTests</c>/
/// <c>AuthRowRealtimeAssertionsTests</c> (pure token-minting/assertion-helper unit tests, no
/// backend, never gated) and row 16's <c>DevelopmentPassThroughUnsetModeTests</c>/
/// <c>DevelopmentPassThroughExplicitModeTests</c> (real, ungated, already-passing-today
/// pass-through behaviour) -- count normally, since they run and assert something real against the
/// dotnet leg today.
///
/// PR #158 CI-trigger fix (dev merge, bringing in #149's floor of 167 and #156): the merged tree's
/// raw tagged-method count (before excluding the skip-gated set below) is 196 (167 real + 29 from
/// R10's tagging); subtracting the 18 skip-gated methods above gives the 178 floor below.
///
/// Issue 165: the new Breakfast/Lunch menu-mode conformance scenarios
/// (<c>Scenarios/Ordering/MenuModeConformanceTests.cs</c>) add five tagged, ungated methods
/// (mode switch x2, out-of-mode rejection, search filter, packs-without-modes-unaffected),
/// verified green against both backends -- raising the floor from 178 to 183.
///
/// Issue 165 round 2 (Rick's PR #166 round-1 review, required items 5 and 7):
/// <c>Scenarios/Ordering/MenuModeRejectionConformanceTests.cs</c> adds five more tagged, ungated
/// methods -- unrecognized/empty/repeated `?mode=` all rejected with a real pre-upgrade HTTP 400
/// (closing the gap Rick flagged: "the Python 400 test was vacuous... and C# had no test of the
/// 400 at all"), an omitted `?mode=` defaulting to lunch, and a log-capture pin proving the raw
/// rejected value never reaches either backend's own logs verbatim -- verified green against both
/// Issue 165 round 2 (Rick's PR #166 round-1 review, required item 6): search vs. add-time
/// semantics for a period-less item within a dayparts pack now agree in both directions --
/// <c>Scenarios/Ordering/MenuModeConformanceTests.cs</c> adds one more tagged, ungated
/// <c>[Theory]</c> method (`Periodless_item_can_be_added_in_either_mode_a_dayparts_pack_supports`,
/// two <c>[InlineData]</c> rows: breakfast and lunch) proving "Delta Burger" -- a genuine
/// dayparts-pack item with no `menuPeriod` of its own -- is addable over the wire in either mode,
/// matching the filter-string fix's own admission of period-less items -- raising the floor from
/// 188 to 189.
///
/// Issue #164 (Rick's PR #167 round-3 review, required item 12): PR #167 adds one scenario,
/// <c>PersonaDiscoveryConformanceTests.Api_persona_detail_pins_tax_rate_and_ui_blocks_against_disk</c>
/// (R7, commit 48edade). It is tagged <c>[Trait("Dotnet", "ready")]</c> and is not skip-gated,
/// so it raises the floor 189 to 190. The dotnet leg executes 190 distinct passing methods, none
/// of them in the five gated Auth classes (TRX: 485 passed, 61 not executed). A reflection probe
/// agrees (a floor of 191 fails with "but found 190").
///
/// Issue #170: fixes a live production bug (a bound-persona session's client `session.update`
/// rebuilt the upstream session without that persona's own system prompt, falling back to the
/// deployment default). Adds <c>PersonaSessionUpdateInstructionsConformanceTests.cs</c>'s two
/// Theory methods (<c>RealPackPersonaSessionUpdateConformanceTests</c> and
/// <c>FixturePackPersonaSessionUpdateConformanceTests</c>, both
/// <c>Client_session_update_carries_the_bound_personas_own_instructions</c>) -- generic,
/// brand-agnostic coverage, for every discovered persona pack, that the forwarded client-update
/// session carries that SAME pack's own instructions and none of the others', closing the exact
/// gap that let #170 ship (<c>SmokeSessionBootstrapTests</c> already proved the browser's
/// session.update is forwarded, but never asserted `instructions`). Both methods are tagged
/// <c>[Trait("Dotnet", "ready")]</c>, ungated, and verified green against both backends -- raising
/// the floor 190 to 192.
///
/// Issue #170 round 2 (Rick's PR #175 round-2 review, required item R1): the instructions check
/// above only ever covered the ORDINARY client session.update rebuild, never the REJECTED-update
/// fallback path (<c>RealtimeProcessor.HandleErrorAsync</c> --&gt;
/// <c>RealtimeSessionBuilder.BuildFallbackSessionUpdate</c>) -- Rick's own round-1 mutation
/// forcing the C# fallback onto the deployment default persona's prompt survived every existing
/// test, since every prior fallback scenario only ever ran on the single default-persona
/// connection. <c>PersonaSessionUpdateFallbackConformanceTests.cs</c>'s two Theory methods
/// (<c>RealPackPersonaSessionUpdateFallbackConformanceTests</c> and
/// <c>FixturePackPersonaSessionUpdateFallbackConformanceTests</c>, both
/// <c>Rejected_bootstrap_recovers_via_a_fallback_carrying_the_bound_personas_own_instructions</c>)
/// close that gap generically, for every discovered persona pack, on both legs: a scripted
/// rejection of the bootstrap session.update, asserting the FALLBACK's own `instructions` carry
/// that SAME pack's identity text and none of the others'. Both methods are tagged
/// <c>[Trait("Dotnet", "ready")]</c>, ungated, and verified green against both backends -- raising
/// the floor 192 to 194.
///
/// Issue #170 round 3 (Rick's PR #175 round-2 review, required item R4): the instructions-only
/// checks above never covered `session.tools[].description` -- every session's tool list (both
/// the realtime and cascade backends) was built once from the deployment default persona's own
/// `prompts/tool_schemas.yaml`, so a bound persona's own system prompt and menu were correct but
/// its tool descriptions still named the default persona's own brand and ticket/order-screen
/// name. <c>PersonaSessionUpdateToolsConformanceTests.cs</c>'s two Theory methods
/// (<c>RealPackPersonaSessionUpdateToolsConformanceTests</c> and
/// <c>FixturePackPersonaSessionUpdateToolsConformanceTests</c>, both
/// <c>Client_session_update_carries_the_bound_personas_own_tool_descriptions</c>) close that gap
/// generically, for every discovered persona pack, on both legs: the forwarded client-update
/// session's `search` tool description carries that SAME pack's own text and none of the others'.
/// Both methods are tagged <c>[Trait("Dotnet", "ready")]</c>, ungated, and verified green against
/// both backends -- raising the floor 194 to 196.
///
/// Issue #179: a guest's combo drink was resized by the model calling `remove &lt;item&gt;
/// &lt;old size&gt;` then `add &lt;item&gt; &lt;new size&gt;`; the `remove` didn't vacate the
/// combo slot it had been filling, so the following `add` created a standalone duplicate line
/// instead of resizing the combo's own drink. <c>ComboComponentResizeConformanceTests.cs</c> adds
/// two tagged, ungated <c>[Theory]</c> methods (<c>Discovered_pack_resizes_the_combo_drink_via_remove_then_add</c>,
/// reproducing the exact live sequence, and <c>Discovered_pack_resizes_the_combo_drink_via_explicit_modify</c>,
/// covering the new explicit resize action), each driven by <c>ComboBundleDiscovery</c> dynamically
/// discovering every real pack with a genuinely-open drinks slot (no brand names in the test file
/// itself) -- verified green against both backends for every real pack discovered on disk whose
/// own menu qualifies today (a pack with no bundle at all is naturally excluded) -- raising the floor
/// 196 to 198.
///
/// Issue #179 round 2 (#184, Rick's required items 1/2): the pricing model changed from a
/// stateful delta/upcharge to a pure, path-independent function of the final order, and a pack's
/// own `bundles.resizeRule` can now be `wholeBundleSize` (resizing ANY slot component cascades
/// into resizing the WHOLE bundle), which the generic per-component scenario above does not apply
/// to and now correctly excludes. <c>ComboComponentResizeConformanceTests.cs</c>'s pricing
/// assertion was fixed to the new flat total, and a new tagged, ungated <c>[Theory]</c> method,
/// <c>WholeBundleSizeResizeConformanceTests.Discovered_whole_bundle_size_pack_resizes_the_meal_and_relabels_its_slots</c>,
/// covers the `wholeBundleSize` mechanism generically (dynamically discovering any pack with that
/// rule from its own persona.json, no brand names) -- verified green against both backends, and
/// mutation-checked (dotnet leg) by temporarily reverting the bundle's own reprice-on-resize line
/// in OrderState.cs, confirming the new test fails -- raising the floor 198 to 199.
///
/// Issue #184 round 3 (Rick's review, item H): two new tagged, ungated Theory methods in
/// <c>ComboComponentResizeConformanceTests.cs</c> -- a path-independence check (ordering a size
/// up front totals identically to resizing into it later) and a two-bundle-instance check (a
/// resize lands on the instance that actually holds the named item, by identity, never an
/// arbitrary first match) -- verified green against both backends, raising the floor 199 to 201.
///
/// Issue #184 round 4 (Rick's round-3 review, items 2/4/7): two more tagged, ungated methods
/// cover first-absorption path independence for wholeBundleSize packs and pack-owned
/// wholeBundleSize golden vectors, raising the floor 201 to 203.
///
/// Issue #205: one tagged, ungated Fact covers the `componentUpcharge` bundle rule across both
/// backends, including the wire `componentUpcharges` field and resize-back-to-included-size path,
/// raising the floor 203 to 204.
///
/// Issue #21 "flip candidates to check early" (csharp-100-plan.md): the real tagged-method count
/// had already drifted to 208 since the floor was last raised (prior waves tagging ahead of this
/// floor's own updates). This pass tags 11 more genuinely-passing, already-ported rows -- all 10
/// <c>Scenarios/Security/ClientToServerAllowListTests.cs</c> methods (browser-to-upstream
/// realtime allow-list hardening -- fully ported in
/// <c>Backend/Realtime/ClientServerFilter.cs</c>/<c>RealtimeProcessor.cs</c>), plus
/// <c>OriginValidationTests.Exact_origin_is_accepted</c> (its two siblings were already tagged;
/// this was the one genuinely-untagged row left). All 11 verified green against the C# backend (3
/// clean runs each, no flakes) -- raising the floor 204 to 219 (208 + 11).
///
/// Issue #21 round 2 (PR #230 review, Rick's item 3): `ClientServerFilter.cs`'s `EventIdRegex`/
/// `Base64Regex` used a `$`-anchored pattern with plain `Regex.IsMatch`, which (absent
/// `RegexOptions.Multiline`/`Singleline`) also matches just before a single trailing `\n` -- unlike
/// rtmt.py's own `_CLIENT_EVENT_ID_RE.fullmatch(...)`/`_CLIENT_BASE64_RE.fullmatch(...)`, which
/// require the WHOLE string to be consumed. `OriginValidator.cs`'s `MatchesHost` compared
/// `Uri.Authority`, which silently drops both userinfo and an explicit default port, unlike
/// rtmt.py's `_origin_matches_host`, which compares the raw `urlsplit(...).netloc` (preserving
/// both). Both are now faithful ports (`\z` anchors; a manual netloc-extraction helper), backed by
/// three new tagged, ungated test methods verified green against both backends (3 clean runs each
/// against the C# backend, no flakes), each mutation-checked by temporarily reverting its
/// corresponding fix and confirming red:
/// <c>ClientToServerAllowListTests.Trailing_newline_event_id_response_id_and_audio_fail_like_pythons_fullmatch</c>,
/// <c>OriginValidationTests.Origin_with_userinfo_is_rejected_with_403</c>, and
/// <c>OriginValidationTests.Origin_with_explicit_default_port_is_rejected_against_a_portless_host</c>
/// -- raising the floor 219 to 222 (219 + 3).
///
/// Issue #13 Wave 5 (PR #236, rebased onto #230/#21 above): <c>Backend.Sessions.CascadeProcessor</c>
/// lands and is registered in <c>ProcessorRegistry</c>, so
/// <c>Scenarios/Cascade/CascadeConformanceTests.cs</c>'s 7 rows (session-metadata dispatch, the
/// tool-calling round trip to get_order, update_order pricing parity with realtime, a not-on-menu
/// rejection shape, the automatic greeting on connect, barge-in cancelling an in-flight turn, and a
/// 429-from-chat-completion recovery) are now tagged <c>[Trait("Dotnet", "ready")]</c> at the class
/// level -- verified green against the C# backend across 3 consecutive local runs
/// (<c>CONFORMANCE_BACKEND=dotnet</c>) with no flakiness, plus 2 further full-suite runs pinned to
/// 2 CPUs on native Linux (matching the ubuntu-latest CI runner) to rule out a CI-only timing flake.
///
/// Merge-order note for PR #226 (#147, Beth's C# auth work, independently raises this SAME floor
/// 204 to 222 against the stale pre-#21 baseline): PR #230/#21 lands first at 222 (including this
/// round's +3); PR #226 must then rebase onto that base and re-target its own floor to 222 + 18 =
/// 240 (not 222) to account for both rounds of #21 tagging on top of the original 204.
///
/// Issue #13 Wave 4/4b (PR #235 rate-limit ladder + PR #237 tool-failure cap, both merged/landing
/// on top of the 222 baseline above): rather than project the new count by arithmetic across two
/// concurrently-rebasing PRs, the real count was measured directly on PR #237's branch (after #235
/// had already merged to dev) by temporarily asserting on the actual
/// <see cref="CountFloorEligibleDotnetReadyTestMethods"/> value, then reverting -- **239**. PR #237
/// raises the floor 222 to 239 here. Any PR still rebasing on top of this (e.g. #226, #244) MUST
/// re-measure fresh at its own rebase time the same way, not add its own historical delta (e.g.
/// "+18") to 239 blindly -- those deltas were computed against the stale 222 baseline and may double
/// count methods (such as this wave's 3 tool-failure-cap rows) already folded into 239.
///
/// #236 Rick re-review item 5 (rebasing onto the 239 baseline above, which already includes #235's
/// rate-limit ladder): per Rick's explicit "recount fresh, don't do arithmetic" instruction, a
/// fresh run of <see cref="CountFloorEligibleDotnetReadyTestMethods"/> (same as
/// <c>Conformance.Tests.exe -list methods -trait Dotnet=ready</c>, minus the 18
/// <see cref="AuthRowGatedTypeNames"/> methods) was taken on this branch tip after rebasing onto
/// origin/dev (which by then carried #226/#147's C# auth work, #235's rate-limit ladder, #237,
/// #241, and everything else merged ahead of this PR) by temporarily asserting on the actual
/// count, then reverting -- **256**. This matches the "+#226 = 256" projection from Rick's own
/// earlier review round (#226's 18 Auth methods landing on top of the 238 baseline that included
/// #235), now confirmed by direct measurement rather than arithmetic. This raises the floor 239 to
/// 256.
///
/// Refs #76 remaining scope: adding <c>RealPackBundleAutoFillConformanceTests</c>'s three new
/// tagged Theory methods plus <c>RealPackCapabilityCoverageTests</c>' three tagged Facts raises the
/// fresh reflection count to 262. Any PR still rebasing on top of this MUST re-measure fresh at its
/// own rebase time the same way, not add a historical delta to 262 blindly.
///
/// Rick's PR #266 review item 2 (required before approval): the duplicate
/// <c>Every_real_pack_with_an_extra_item_has_an_extras_theory_row</c> Fact in
/// <c>RealPackCapabilityCoverageTests</c> was removed (it verbatim-duplicated the Fact already
/// owned by <c>RealPackExtrasCoverageTests</c>), dropping the floor-eligible count by exactly one
/// method, 262 to <b>261</b>. Re-measured directly with <c>Conformance.Tests.exe -list methods
/// -trait Dotnet=ready</c> (279 methods) minus the 18 <see cref="AuthRowGatedTypeNames"/> methods
/// -- a fresh reflection count, never arithmetic.
///
/// Issue #147 (ADR-002, PR #226): app/backend-dotnet now enforces Entra JwtBearer authentication
/// end to end (JwtBearer validation as a fallback authorization policy, the anonymous allow-list,
/// `?access_token=` on `/realtime` only, and the layered session token's oid binding), so
/// <see cref="Conformance.Harness.AuthRowCapability.DotnetEnforcesAuth"/> flips to true. Every
/// method in the five previously skip-gated <c>Scenarios/Auth</c> classes (<c>AuthModeLaunchTests</c>,
/// <c>AuthRowLoggingTests</c>, <c>AuthRowRealtimeTokenTests</c>, <c>AuthRowRestTokenTests</c>,
/// <c>AuthRowSpecialCaseTests</c> -- 18 methods as of this PR's first commit) now produce a real
/// pass/fail signal on the dotnet leg too (they already did on the python leg), so these methods
/// count toward the floor for the first time -- see "Issue #147 round 4" below for the exact
/// final count once this PR's own follow-up rounds (which add 3 more tagged, ungated rows to these
/// same five classes) and the rebase onto PR #230's floor are both accounted for.
///
/// Issue #147 round 2 (coordinator note citing Rick's PR #226 review): the hard-coded
/// <c>AuthRowGatedTypeNames</c> type-name exclusion list this class used to carry (removed by this
/// round) required a human to remember to add/remove entries every time
/// <see cref="Conformance.Harness.AuthRowCapability.DotnetEnforcesAuth"/> changed -- and nothing
/// would fail loudly if they forgot, since this floor is only ever a lower bound: if that flag
/// ever flipped back to false, these methods would silently start reporting Skipped again, and
/// the raw count could still clear the (by-then-stale) floor purely from unrelated growth
/// elsewhere, hiding the regression completely. Replaced with
/// <see cref="Conformance.Harness.AuthRowCapabilityGatedAttribute"/>, declared directly on the five
/// classes above: <see cref="CountFloorEligibleDotnetReadyTestMethods"/> now excludes a
/// attribute-carrying class's methods only while
/// <see cref="Conformance.Harness.AuthRowCapability.Enforces"/> actually resolves false for
/// <c>"dotnet"</c> -- no separate list to keep in sync -- and the floor Fact additionally
/// asserts <c>AuthRowCapability.Enforces("dotnet")</c> directly, so a regression on that one flag
/// fails this test immediately and unambiguously, independent of how much slack the raw count
/// happens to have from unrelated scenario growth.
///
/// Issue #147 round 4 (coordinator-directed rebase onto PR #230/#21's merged floor of 222): PR
/// #230 and its round-2 follow-up landed first (see the two "Issue #21" paragraphs above), raising
/// dev's own floor 204 -> 222 before this PR merged. Rebasing this PR's seven commits onto that
/// base and re-running <see cref="CountFloorEligibleDotnetReadyTestMethods"/> (per the
/// coordinator's explicit instruction to measure, not hand-compute) gives 248, not the naively
/// expected 222 + 18 = 240, because of two compounding factors: (1) this PR's own two follow-up
/// rounds (mirroring Python PR #222/#163's case-insensitive Bearer-scheme row, and PR #225/#223's
/// two Row-12 case-insensitive-extension/dotfile-suffix rows) each already added their own tagged,
/// ungated conformance methods to the same five previously-gated classes, raising their total from
/// 18 to 21 (7+1+1+2+10 across <c>AuthModeLaunchTests</c>/<c>AuthRowLoggingTests</c>/
/// <c>AuthRowRealtimeTokenTests</c>/<c>AuthRowRestTokenTests</c>/<c>AuthRowSpecialCaseTests</c>);
/// and (2) dev's own non-Auth tagged-method count had already organically drifted 5 rows ahead of
/// its own stated 222 floor by the time this PR rebased onto it (227, not 222) -- the exact same
/// "floor is a lower bound, the real count can run ahead of it between raises" shape documented by
/// the "Issue #21 'flip candidates to check early'" paragraph above (204 floor, 208 actual). So:
/// 227 (dev's actual non-Auth count) + 21 (now-countable Auth rows) = 248, and the floor is set to
/// that exact measured number, consistent with every prior raise in this class's history.
///
/// Per the "Issue #13 Wave 4/4b" note above, this PR re-measures the floor fresh at its own
/// rebase time (onto the post-#241 `origin/dev`, which also enables the Browser conformance leg)
/// rather than projecting by historical delta -- see "Issue #147 round 5 (Rick's security
/// re-review, rebase onto #241)" below for the exact final measured count and its arithmetic.
///
/// Issue #15 (PR #244, C# sessions/resilience, rebased on top of #237's 239 baseline): Rick's #244
/// review added five new tagged, ungated scenarios closing gaps his own review found --
/// <c>RateLimitIdleInteractionTests.Repeated_guest_speech_keeps_the_session_alive_past_idle_timeout_seconds</c>
/// (issue 1, guest-speech activity, mutation-checked), <c>ResumeHandshakeTests.A_resume_sent_after_the_first_frame_timeout_fallback_is_rejected_as_late</c>
/// (issue 2, late-resume-after-timeout, mutation-checked),
/// <c>ResumeRehydrationAndNudgeTests.Resuming_mid_conversation_rehydrates_the_recorded_guest_transcript</c>
/// (issue 3, RecordTurn wiring), <c>CloseCodeTests.Superseding_a_stuck_peer_that_never_acks_the_close_still_completes_promptly</c>
/// (issue 4, supersede-close ordering -- also caught and fixed a real pre-existing regression this
/// same work introduced, see <c>ResumeHandshakeTests.Resuming_from_a_still_attached_socket_supersedes_it_with_4002</c>),
/// and <c>ResumeHandshakeTests.Resuming_carries_over_the_original_sessions_token_and_round_trip_state</c>
/// (issue 5, session_token/round_trip_index/round_trip_token continuity). Measured directly the
/// same way (temporarily asserting on <see cref="CountFloorEligibleDotnetReadyTestMethods"/>'s
/// actual value at rebase time, not projected by arithmetic) -- **275**, reflecting whatever
/// else had also landed on dev in the meantime on top of #237's 239. Raises the floor 239 to 275.
///
/// Issue #15 (PR #244, Rick's round-2 re-review): the supersede-close race fix (background close
/// with a short timeout + a synchronous <c>SupersededFlag</c> gating tool dispatch, replacing the
/// prior round's awaited-inline close that could still block the NEW connection's own forwarding
/// against a non-draining stale peer) adds one new tagged scenario,
/// <c>ResumeHandshakeTests.Resuming_from_a_still_attached_socket_whose_transport_cannot_drain_still_forwards_the_new_sockets_own_session_update_promptly</c>,
/// alongside the pre-existing <c>Resuming_from_a_still_attached_socket_supersedes_it_with_4002</c>
/// (kept as the simple well-behaved-peer baseline rather than overwritten, so 4002/CloseStatus
/// coverage isn't lost). Measured directly the same way -- **276**. Raises the floor 275 to 276.
///
/// Rebase of #244 onto a since-advanced origin/dev (5 commits: the Browser conformance leg, a
/// port-bind-race retry fix, the search-index C# port, an i18n deflake, and others) brought in
/// other PRs' own newly-tagged <c>Dotnet=ready</c> rows on top of this branch's 276. Measured
/// directly the same way (not projected by arithmetic) immediately after the rebase -- **286**.
/// Raises the floor 276 to 286; this PR adds no new tagged rows of its own in this step, it is
/// purely absorbing what had already landed on dev.
///
/// Issue #15 (PR #244, Rick's round-2 re-review, follow-up): CI run 37210749254 caught the
/// background supersede-close itself racing a healthy stale peer -- the unconditional
/// <c>staleCts?.Cancel()</c> in its <c>finally</c> block fired immediately after the 4002 close
/// frame was sent, and .NET's <see cref="System.Net.WebSockets.WebSocket"/> cancellation semantics
/// abort the *whole* socket (not just the pending call) when a token tied to an in-flight
/// <c>ReceiveAsync</c> fires, so under real CPU scheduling pressure the cancel could occasionally
/// win the race against the stale socket's own <c>ReceiveAsync</c> observing its peer's close
/// handshake, leaving <c>CloseStatus</c> null instead of 4002. Fixed by waiting for the stale
/// socket's <see cref="System.Net.WebSockets.WebSocket.State"/> to leave
/// <c>Open</c>/<c>CloseSent</c> (bounded by the same close-timeout budget already used for the
/// send) before ever cancelling its CTS -- the winner's own forwarding path is untouched and never
/// waits on the loser, so widening <c>SupersededCloseTimeout</c> to 10s earlier does not reintroduce
/// a stall. Reproduced the original race under genuine 24-core CPU saturation with the fix reverted
/// (confirming the diagnosis), then confirmed 10/10 clean runs under the same load with the fix
/// restored. This is a Backend.Tests-only unit-level fix (new coverage lives in
/// <c>CloseSupersededStaleConnectionAsyncTests.Does_not_cancel_the_stale_cts_until_the_socket_settles_or_the_timeout_elapses</c>,
/// not a Conformance scenario) and adds no new <c>Dotnet=ready</c>-tagged conformance rows of its
/// own. Rebasing this step onto the latest origin/dev (dd06d562, bringing in #236's CASCADE
/// processor, #234's frame dispatch, #233's log self-timestamping, and other PRs' own newly-tagged
/// rows merged ahead of this branch) measured directly, not projected -- **293**. Raises the floor
/// 286 to 293.
///
/// PR #253 review item 3 (rebasing onto the 256 baseline above, which already includes #236/#261/
/// #263/#264): now that dev's own C# <c>CascadeProcessor</c> sanitizes <c>extension.set_voice</c>
/// (#236's own review fix), the 4 <c>Scenarios/Cascade/CascadeMenuModeAndVoiceConformanceTests</c>
/// rows (per-persona default voice, `?mode=` breakfast/lunch binding, and the set_voice
/// sanitization row itself) are tagged <c>[Trait("Dotnet", "ready")]</c> too -- verified green
/// against the C# backend across 3 consecutive local runs with no flakiness. A fresh run of
/// <see cref="CountFloorEligibleDotnetReadyTestMethods"/> on this branch tip (same
/// temporarily-assert-then-revert measurement technique as every prior round) gives **260**
/// (256 + these 4), confirming the delta by direct count rather than arithmetic. This raises the
/// floor 256 to 260.
///
/// Issue #147 round 5 (Rick's security re-review of PR #226, 3 findings fixed): findings #2's
/// FakeEntraIssuer/AuthRowCases additions (missing-nbf row, malformed-roles-shape row,
/// malformed-scp-shape row -- 3 new <c>AuthRowTokenCase.All</c> entries, each run through
/// <c>AuthRowRestTokenTests.Row_asserts_on_every_REST_path</c> and
/// <c>AuthRowRealtimeTokenTests.Row_asserts_on_realtime</c>, i.e. 2 tagged methods x 3 new cases =
/// 6, plus the existing per-row `Theory` methods now enumerating 3 more cases each counts those
/// extra Theory instances individually since <see cref="CountFloorEligibleDotnetReadyTestMethods"/>
/// counts distinct test cases, not just method declarations) plus unrelated organic growth
/// elsewhere on `origin/dev` since the 260 measurement account for the remainder. Findings #1 and
/// #3 (the JWKS/OIDC cooldown decorator and the LastKnownGoodLifetime shrink) are both covered at
/// the Backend.Tests pipeline-integration level (<c>EntraPipelineCooldownTests</c>'s three
/// scenarios: cold outage, warm-cache+forged-kid-flood, and key-rotation-after-LKG-expiry) rather
/// than at this conformance level -- both require either a real 30s (finding #1) or real 300s
/// (finding #3) wall-clock wait to observe the cooldown/LKG-expiry boundary for real against an
/// external process's unmodifiable production `TimeSpan.FromSeconds(300)`/cooldown window, which
/// is impractical for a CI-speed conformance suite; Backend.Tests can shrink both windows via an
/// injectable <c>FakeTimeProvider</c>/constructor parameter instead. This mirrors the precedent
/// already set for the cooldown gate itself when #223 first landed it. A fresh run of
/// <see cref="CountFloorEligibleDotnetReadyTestMethods"/> on this branch tip (same
/// temporarily-assert-then-revert measurement technique as every prior round -- assert/raise to an
/// unreachable bound, read the actual count off the failure message, then set the real value)
/// gives **281**. This raises the floor 260 to 281.
///
/// Issue #15 (PR #244, merge reconciliation): the coordinator's merge of <c>origin/dev</c> into
/// this branch (bringing in #253's 260-floor paragraph above alongside this branch's own
/// pre-merge 293-floor paragraph further up) left the test method named
/// <c>At_least_293_scenarios...</c> while asserting <c>count &gt;= 260</c> -- an interim
/// placeholder the coordinator deliberately left for this session to correct by direct
/// measurement rather than arithmetic. A fresh run of
/// <see cref="CountFloorEligibleDotnetReadyTestMethods"/> on this branch tip, post-merge (same
/// temporarily-assert-then-revert technique as every prior round), gives **297**, reflecting both
/// this PR's own five #244-review rows and #253's four cascade rows landing on top of whatever
/// else had merged to dev in the meantime. Raises the floor 260 to 297; test method and assertion
/// renamed/updated to match.
///
/// PR #266 merge with origin/dev (coordinator, 2026-10-05): with both #253's 4 cascade rows and
/// this PR's auto-fill rows present, a fresh <c>Conformance.Tests.exe -list methods -trait
/// Dotnet=ready</c> lists 283 methods; minus the 18 <see cref="AuthRowGatedTypeNames"/> methods
/// that gives <b>265</b>, the floor asserted below. Any PR still rebasing on top of this MUST
/// re-measure fresh at its own rebase time the same way, not add a historical delta to 265 blindly.
/// </summary>
public sealed class DotnetTraitCoverageTests
{
    private const string TraitName = "Dotnet";
    private const string TraitValue = "ready";

    [Fact]
    public void At_least_323_scenarios_are_tagged_dotnet_ready_and_not_skip_gated()
    {
        // Rick's PR #226 review: assert the capability directly, not just the derived count --
        // see this class's own doc comment for why a bare ">= 222" check alone can't be trusted to
        // catch this specific regression.
        Assert.True(AuthRowCapability.Enforces("dotnet"),
            "AuthRowCapability.Enforces(\"dotnet\") must stay true: flipping it back to false " +
            "would silently move the 21 AuthRowCapabilityGated Scenarios/Auth test methods from " +
            "Passed back to Skipped on the dotnet leg, and this floor's own count (which excludes " +
            "AuthRowCapabilityGated classes whenever Enforces(\"dotnet\") is false) could still " +
            "clear its lower bound from unrelated growth elsewhere, hiding the regression.");

        var count = CountFloorEligibleDotnetReadyTestMethods();

        Assert.True(count >= 323,
            $"Expected at least 323 test method(s) tagged [Trait(\"{TraitName}\", \"{TraitValue}\")] " +
            $"and not unconditionally skip-gated by AuthRowCapability (the dotnet leg's " +
            $"`--filter \"{TraitName}={TraitValue}&Category!=Browser\"` baseline, minus the five " +
            "skip-only Scenarios/Auth classes -- see this class's own doc comment; " +
            $"docs/dotnet_mapping.md), but found {count}. If a tagged scenario was removed or " +
            "renamed without a replacement, the dotnet CI leg silently lost coverage. 323 is a " +
            "FRESH count (merge with origin/dev after #266, coordinator 2026-10-05), not " +
            "arithmetic -- re-measure with `Conformance.Tests.exe -list methods -trait " +
            "Dotnet=ready` minus the AuthRowCapabilityGated methods before raising this floor " +
            "again.");
    }


    /// <summary>
    /// Counts every <c>[Fact]</c>/<c>[Theory]</c> test *method* (a <c>[Theory]</c> with N
    /// <c>[InlineData]</c> rows still counts once here, same as the
    /// <c>FullyQualifiedName</c>-based filter this replaces -- both count distinct methods, not
    /// distinct data rows) whose effective Dotnet trait is "ready", combining method-level and
    /// class-level <c>[Trait]</c> attributes the same way xunit's own trait-based filtering does:
    /// a class-level trait applies to every test method declared in that class. Excludes any type
    /// carrying <see cref="Conformance.Harness.AuthRowCapabilityGatedAttribute"/> while
    /// <see cref="Conformance.Harness.AuthRowCapability.Enforces"/> resolves false for
    /// <c>"dotnet"</c>: those methods are unconditionally <c>Assert.Skip</c>'d on the dotnet leg in
    /// that state (see this class's own doc comment), so they never contribute a real pass/fail
    /// signal and must not count toward the coverage floor.
    ///
    /// Issue #143/ADR-002 (R10): abstract types are skipped outright -- xunit never discovers an
    /// abstract class as a runnable test class in its own right, only its concrete subclasses --
    /// and each concrete subclass's own (non-<c>DeclaredOnly</c>) methods are walked so a
    /// <c>[Fact]</c> declared once on a shared abstract base (see
    /// <c>Scenarios.Auth.DevelopmentPassThroughTestsBase</c>, run twice over via its two sealed,
    /// separately-<c>[Trait]</c>-tagged, separately-fixtured subclasses) is credited once per
    /// concrete subclass that actually runs it -- matching how many real xunit test cases the
    /// dotnet leg's own <c>--filter</c> actually selects, not how many methods happen to be typed
    /// out once in source.
    /// </summary>
    private static int CountFloorEligibleDotnetReadyTestMethods()
    {
        var assembly = typeof(DotnetTraitCoverageTests).Assembly;
        var count = 0;

        foreach (var type in assembly.GetTypes())
        {
            if (type.IsAbstract)
            {
                continue;
            }

            if (type.IsDefined(typeof(AuthRowCapabilityGatedAttribute), inherit: true)
                && !AuthRowCapability.Enforces("dotnet"))
            {
                continue;
            }

            var classHasTrait = HasDotnetReadyTrait(type.GetCustomAttributes<TraitAttribute>(inherit: true));

            foreach (var method in type.GetMethods(
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            {
                if (!method.IsDefined(typeof(FactAttribute), inherit: true))
                {
                    continue;
                }

                var methodHasTrait = HasDotnetReadyTrait(method.GetCustomAttributes<TraitAttribute>(inherit: true));
                if (classHasTrait || methodHasTrait)
                {
                    count++;
                }
            }
        }

        return count;
    }

    private static bool HasDotnetReadyTrait(IEnumerable<TraitAttribute> traits) =>
        traits.Any(t => t.Name == TraitName && t.Value == TraitValue);
}
