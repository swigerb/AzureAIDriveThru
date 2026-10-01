import { readdirSync, readFileSync } from "node:fs";
import path from "node:path";
import { fileURLToPath } from "node:url";

import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { BrandHero, CalloutPill, SpotlightCard, SessionTokenPanel, formatSessionToken, HERO_BODY_TEXT_CLASS } from "../App";
import type { PersonaDetail, PersonaHeroSpotlight } from "@/types/persona";
import { resolveTextRole, textRoleClass, textRoleDarkClass, DEFAULT_TEXT_ROLES } from "@/lib/personaTextRoles";

// Issue 164 A1-A5/A8/B2/D2: covers the rewritten two-column `BrandHero` (logo/badge/headline/
// description/callouts/spotlight) and its child components directly, complementing the existing
// full-`RootApp` logo-fallback coverage in `App.brandHero.test.tsx`.

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, "../../../../");
const personasDir = path.resolve(repoRoot, "personas");

/** Discovers every real persona pack on disk -- same convention as heroContrast.test.ts/
 * brandDefaultTokens.test.ts, so a future pack is covered the moment it lands rather than needing
 * a hardcoded id list here. */
function discoverPersonaPackIds(): string[] {
    return readdirSync(personasDir, { withFileTypes: true })
        .filter(entry => entry.isDirectory())
        .map(entry => entry.name)
        .filter(id => {
            try {
                readFileSync(path.resolve(personasDir, id, "persona.json"), "utf-8");
                return true;
            } catch {
                return false;
            }
        })
        .sort();
}

/** Builds a real PersonaDetail straight off a real pack's own persona.json -- the same `ui.*`
 * spread-flat-plus-voice/locales/features/menuUrl/models/taxRate shape `app/backend/app.py`'s
 * `/api/personas/{id}` wire response uses (types/persona.ts's own doc comment). Used by the
 * "BrandHero layout (issue 172)" suite below so its structural/reading-order assertions run
 * against every real pack's own content instead of synthetic fixture copy. */
function loadRealPersonaDetail(packId: string): PersonaDetail {
    const raw = JSON.parse(readFileSync(path.resolve(personasDir, packId, "persona.json"), "utf-8"));
    const ui = raw.ui;
    return {
        id: raw.id,
        roleName: raw.roleName,
        title: ui.title,
        theme: ui.theme,
        assets: ui.assets,
        strings: ui.strings,
        hero: ui.hero,
        legal: ui.legal,
        voice: raw.voice,
        locales: raw.locales,
        features: raw.features,
        menuUrl: `/personas/${raw.id}/menu.json`,
        models: raw.models,
        taxRate: raw.pricing?.taxRate ?? "0",
        sessionBar: ui.sessionBar,
        categoryIcons: ui.categoryIcons,
        textRoles: ui.textRoles
    };
}

const theme = { light: { primary: "200 80% 50%", secondary: "40 60% 40%", background: "0 0% 98%", foreground: "0 0% 10%" } };

// Issue #164 R6 (PR #167 round 1 review): neutral, non-brand fixture copy in the style of the
// `test-alpha`/`test-beta`/`test-gamma` personas used throughout this file -- no real product
// names from any pack's menu.
const spotlightCard1: PersonaHeroSpotlight = {
    icon: "assets/spotlight-1.svg",
    kicker: "SIGNATURE PICKS",
    title: "Fixture Favorites & more",
    rows: [
        { label: "Pick of the Day", value: "Fixture Special" },
        { label: "Staff Pick", value: "Fixture Cheeseburger" }
    ]
};

const spotlightCard2: PersonaHeroSpotlight = {
    icon: "assets/spotlight-2.svg",
    kicker: "STAFF FAVORITE",
    title: "Fixture Double Cheeseburger",
    body: "100% pure beef with melty American cheese",
    accent: "Perfect pairing: Fixture Fries & a Shake"
};

function detailFor(overrides: Partial<PersonaDetail> = {}): PersonaDetail {
    return {
        id: "test-alpha",
        roleName: "carhop",
        title: "Test Alpha Fixture",
        theme,
        assets: { logo: "assets/logo.svg", favicon: "assets/favicon.ico" },
        strings: { en: {} },
        hero: {
            headline: "Test Alpha ordering powered by Microsoft Foundry",
            description: "Fixture hero description sentence.",
            callouts: [
                { title: "REWARDS READY", detail: "Voice orders auto-sync with fixture rewards", tone: "primary" },
                { title: "FOUNDRY INFUSION", detail: "Microsoft Foundry keeps conversations flowing", tone: "accent" },
                { title: "LIVE MENU", detail: "Azure AI Search keeps fixture items current", tone: "secondary" }
            ],
            spotlight: [spotlightCard1, spotlightCard2]
        },
        legal: "Fixture-only disclaimer.",
        voice: { default: "marin" },
        locales: { default: "en", supported: ["en"] },
        features: { dayparts: false },
        menuUrl: "/personas/test-alpha/menu.json",
        models: { realtime: { default: "gpt-realtime-2.1", models: [] } },
        taxRate: "0.08",
        ...overrides
    };
}

describe("BrandHero (issue 164 A1-A5/A8/B2)", () => {
    it("renders the headline, description, and every callout pill from persona.hero", () => {
        render(<BrandHero logoUrl="/personas/test-alpha/assets/logo.svg" persona={detailFor()} />);

        expect(screen.getByText("Test Alpha ordering powered by Microsoft Foundry")).toBeInTheDocument();
        expect(screen.getByText("Fixture hero description sentence.")).toBeInTheDocument();
        expect(screen.getByText("REWARDS READY")).toBeInTheDocument();
        expect(screen.getByText("Voice orders auto-sync with fixture rewards")).toBeInTheDocument();
        expect(screen.getByText("FOUNDRY INFUSION")).toBeInTheDocument();
        expect(screen.getByText("LIVE MENU")).toBeInTheDocument();
    });

    it("renders both spotlight cards' kickers and titles (A1/A2)", () => {
        render(<BrandHero logoUrl="/personas/test-alpha/assets/logo.svg" persona={detailFor()} />);

        expect(screen.getByText("SIGNATURE PICKS")).toBeInTheDocument();
        expect(screen.getByText("Fixture Favorites & more")).toBeInTheDocument();
        expect(screen.getByText("STAFF FAVORITE")).toBeInTheDocument();
        expect(screen.getByText("Fixture Double Cheeseburger")).toBeInTheDocument();
    });

    it("omits the spotlight column entirely when the pack has no spotlight cards", () => {
        render(<BrandHero logoUrl="/personas/test-alpha/assets/logo.svg" persona={detailFor({ hero: { ...detailFor().hero, spotlight: [] } })} />);

        expect(screen.queryByText("SIGNATURE PICKS")).not.toBeInTheDocument();
    });

    it("uses the shared neutral hero.badge default when the pack doesn't override it", () => {
        render(<BrandHero logoUrl="/personas/test-alpha/assets/logo.svg" persona={detailFor()} />);
        // react-i18next is globally mocked to echo the key -- issue #80 F3.
        expect(screen.getByText("hero.badge")).toBeInTheDocument();
    });

    it("renders a persona-specific hero.badge override instead of the shared default (A5)", () => {
        render(<BrandHero logoUrl="/personas/test-alpha/assets/logo.svg" persona={detailFor({ hero: { ...detailFor().hero, badge: "CUSTOM HERO BADGE" } })} />);

        expect(screen.getByText("CUSTOM HERO BADGE")).toBeInTheDocument();
        expect(screen.queryByText("hero.badge")).not.toBeInTheDocument();
    });

    // Issue 164 B2: only a pack that sets `assets.logoTile` gets the white rounded
    // tile behind its logo -- every other pack's logo renders bare, matching its own original.
    it("wraps the logo in a white tile only when assets.logoTile is set (B2)", () => {
        const { container } = render(
            <BrandHero logoUrl="/personas/test-alpha/assets/logo.png" persona={detailFor({ assets: { logo: "assets/logo.png", favicon: "assets/favicon.ico", logoTile: true } })} />
        );

        const logo = screen.getByAltText("Test Alpha Fixture logo");
        expect(logo.parentElement?.className).toContain("bg-white");
        expect(logo.parentElement?.className).toContain("rounded-2xl");
        expect(container.querySelectorAll("img").length).toBeGreaterThan(0);
    });

    it("does not wrap the logo in a tile when assets.logoTile is absent", () => {
        render(<BrandHero logoUrl="/personas/test-alpha/assets/logo.svg" persona={detailFor()} />);

        const logo = screen.getByAltText("Test Alpha Fixture logo");
        expect(logo.parentElement?.className ?? "").not.toContain("bg-white");
    });

    // Issue 164 A8/D2: the hero section itself must carry zero `dark:` classes -- all three
    // originals render this card as a uniformly light frosted surface regardless of page theme.
    it("never renders a dark: class anywhere on the hero section (A8/D2)", () => {
        const { container } = render(<BrandHero logoUrl="/personas/test-alpha/assets/logo.svg" persona={detailFor()} />);

        const section = container.querySelector("section");
        expect(section).not.toBeNull();
        expect(section?.innerHTML).not.toMatch(/dark:/);
    });

    // Issue #164 R1 (PR #167 round 1 review): the description and the "Powered by" tech line both
    // use the exported HERO_BODY_TEXT_CLASS constant (text-brand-ink/90), not a lower, inadequate
    // alpha -- see heroContrast.test.ts for the actual WCAG contrast assertion.
    it("renders the description and powered-by line at HERO_BODY_TEXT_CLASS contrast (R1)", () => {
        render(<BrandHero logoUrl="/personas/test-alpha/assets/logo.svg" persona={detailFor()} />);

        expect(screen.getByText("Fixture hero description sentence.").className).toContain(HERO_BODY_TEXT_CLASS);
        expect(screen.getByText("hero.poweredBy").parentElement?.className).toContain(HERO_BODY_TEXT_CLASS);
    });

    // Issue #164 R2(b) (PR #167 round 1 review): the hero badge pill's text color is pack-driven
    // via ui.textRoles.badge (default "primary"), not a hardcoded text-brand-primary.
    it("colors the hero badge from the default 'primary' role when the pack sets no textRoles override", () => {
        render(<BrandHero logoUrl="/personas/test-alpha/assets/logo.svg" persona={detailFor()} />);
        expect(screen.getByText("hero.badge").className).toContain("text-brand-primary");
    });

    it("colors the hero badge from an explicit ui.textRoles.badge override", () => {
        render(<BrandHero logoUrl="/personas/test-alpha/assets/logo.svg" persona={detailFor({ textRoles: { badge: "primaryDeep" } })} />);
        expect(screen.getByText("hero.badge").className).toContain("text-brand-primary-deep");
    });
});

// Issue 172 round 2 (Rick's review of PR #174): round 1's layout tests mostly asserted the
// `xl:col-start-*`/`xl:row-span-*` class strings that placed the spotlight stack across both of
// the left column's rows -- exactly the bug round 2 fixes (it stretched the logo row to the
// stack's height, pushing the headline down). Asserting those classes would make the test suite
// defend the very bug being fixed. This suite instead asserts BEHAVIOR: every hero element is
// present exactly once, in reading order (logo, badge, headline, description, spotlight cards,
// callouts, tech line -- the order a screen reader follows), the logo row and headline/description
// block live together in one left column while the spotlight stack is a separate sibling column,
// and none of round 1's row/column placement classes remain. It runs pack-driven over every real
// persona pack discovered on disk (no brand name appears literally in this file), per Rick's ask.
describe("BrandHero layout (issue 172)", () => {
    const realPackIds = discoverPersonaPackIds();

    it("found at least one real persona pack on disk to test against", () => {
        expect(realPackIds.length).toBeGreaterThan(0);
    });

    for (const packId of realPackIds) {
        // packId is a disk-discovered directory name, never a literal brand string in this
        // file's own source -- see the header comment.
        describe(`a real pack (${realPackIds.indexOf(packId) + 1} of ${realPackIds.length})`, () => {
            const persona = loadRealPersonaDetail(packId);
            const logoUrl = `/personas/${packId}/${persona.assets.logo}`;

            it("renders every hero element exactly once, in reading order: logo, badge, headline, description, spotlight cards, callouts, tech line", () => {
                render(<BrandHero logoUrl={logoUrl} persona={persona} />);

                const logo = screen.getAllByAltText(`${persona.title} logo`);
                expect(logo).toHaveLength(1);

                const badgeCopy = persona.hero.badge ?? "hero.badge";
                const badge = screen.getAllByText(badgeCopy);
                expect(badge).toHaveLength(1);

                const heading = screen.getByRole("heading", { level: 1 });
                expect(heading.textContent).toBe(persona.hero.headline);

                const description = screen.getAllByText(persona.hero.description);
                expect(description).toHaveLength(1);

                const spotlightKickers = persona.hero.spotlight.map(card => {
                    const matches = screen.getAllByText(card.kicker);
                    expect(matches).toHaveLength(1);
                    return matches[0];
                });

                const calloutTitles = persona.hero.callouts.map(callout => {
                    const matches = screen.getAllByText(callout.title);
                    expect(matches).toHaveLength(1);
                    return matches[0];
                });

                const azureLogo = screen.getAllByAltText("Microsoft Azure");
                expect(azureLogo).toHaveLength(1);

                const poweredBy = screen.getAllByText("hero.poweredBy");
                expect(poweredBy).toHaveLength(1);

                // Reading order: logo, badge, headline, description, each spotlight card (in
                // pack order), each callout (in pack order), the Azure logo, then the powered-by
                // line -- compareDocumentPosition's FOLLOWING bit is DOM order, independent of
                // any CSS (jsdom does no layout, so this is the only reliable order check).
                const orderedElements = [
                    logo[0],
                    badge[0],
                    heading,
                    description[0],
                    ...spotlightKickers,
                    ...calloutTitles,
                    azureLogo[0],
                    poweredBy[0]
                ];
                for (let i = 0; i < orderedElements.length - 1; i++) {
                    const earlier = orderedElements[i];
                    const later = orderedElements[i + 1];
                    expect(earlier.compareDocumentPosition(later) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
                }
            });

            it("keeps the logo row and headline/description in one left column, with the spotlight stack as its separate sibling column and no row-placement classes left over from round 1", () => {
                const { container } = render(<BrandHero logoUrl={logoUrl} persona={persona} />);

                const section = container.querySelector("section") as HTMLElement;
                const gridWrapper = section.firstElementChild as HTMLElement;
                const leftColumn = gridWrapper.firstElementChild as HTMLElement;

                // The left column's only two children are the logo row and the headline/
                // description block -- the spotlight stack is NOT one of its children.
                expect(leftColumn.children.length).toBe(2);
                const logo = screen.getByAltText(`${persona.title} logo`);
                const heading = screen.getByRole("heading", { level: 1 });
                expect(leftColumn.contains(logo)).toBe(true);
                expect(leftColumn.contains(heading)).toBe(true);

                if (persona.hero.spotlight.length > 0) {
                    const spotlightColumn = gridWrapper.lastElementChild as HTMLElement;
                    expect(spotlightColumn).not.toBe(leftColumn);
                    expect(spotlightColumn.contains(leftColumn)).toBe(false);
                    expect(spotlightColumn.textContent).toContain(persona.hero.spotlight[0].kicker);
                }

                // Round 1's bug: the spotlight stack carried `xl:row-start-*`/`xl:row-span-2`/
                // `xl:col-start-*`, spanning both of the left column's rows and stretching the
                // logo row to match the stack's height. Neither column places itself by row or
                // column start anymore -- this is a regression guard against that exact bug.
                expect(gridWrapper.innerHTML).not.toMatch(/row-start|row-span|col-start/);
            });

            it("keeps the callout pills and the tech line in one full-width footer row as the section's second (and last) top-level child", () => {
                const { container } = render(<BrandHero logoUrl={logoUrl} persona={persona} />);

                const section = container.querySelector("section") as HTMLElement;
                const gridWrapper = section.firstElementChild as HTMLElement;
                const footer = gridWrapper.nextElementSibling as HTMLElement;

                // The footer row is a sibling AFTER the grid wrapper, not nested inside it.
                expect(footer).not.toBeNull();
                for (const callout of persona.hero.callouts) {
                    expect(footer.textContent).toContain(callout.title);
                    expect(gridWrapper.textContent).not.toContain(callout.title);
                }
                expect(footer.textContent).toContain("hero.poweredBy");

                // section has exactly 2 top-level children: the xl grid wrapper, then the footer row.
                expect(section.children.length).toBe(2);
                expect(footer.nextElementSibling).toBeNull();
            });
        });
    }
});

describe("CalloutPill (issue 164 A3)", () => {
    it.each([
        ["primary", "from-brand-primary to-brand-primary-light"],
        // Issue #164 R3 (PR #167 round 1 review): "secondary" ends at "-light" (brighter), not
        // "-strong" (darker) -- the original gradients brighten toward their end for the two
        // packs that override secondaryLight; "-strong" rendered both packs'
        // pills as a solid dark wash instead of a gradient.
        ["secondary", "from-brand-secondary to-brand-secondary-light"],
        ["accent", "from-brand-accent to-brand-accent-light"]
    ] as const)("draws its gradient from the %s brand role", (tone, expectedClasses) => {
        const { container } = render(<CalloutPill title="REWARDS READY" detail="Some detail copy" tone={tone} />);
        for (const expectedClass of expectedClasses.split(" ")) {
            expect(container.firstElementChild?.className).toContain(expectedClass);
        }
    });

    it("renders the title and detail text passed in", () => {
        render(<CalloutPill title="LIVE MENU" detail="Azure AI Search keeps items current" tone="secondary" />);
        expect(screen.getByText("LIVE MENU")).toBeInTheDocument();
        expect(screen.getByText("Azure AI Search keeps items current")).toBeInTheDocument();
    });
});

describe("SpotlightCard (issue 164 A1/A2)", () => {
    // Issue 172: when BrandHero's `items-stretch` grows one card to match its (taller) sibling,
    // the extra height must center the card's own content rather than leave a dead gap pinned to
    // one edge -- both cards opt into that with `h-full flex flex-col justify-center`.
    it("centers its content vertically so items-stretch height growth reads as breathing room, not dead space (card 1)", () => {
        const { container } = render(<SpotlightCard card={spotlightCard1} personaId="test-alpha" isSecond={false} />);
        const card = container.firstElementChild as HTMLElement;
        expect(card.className).toContain("h-full");
        expect(card.className).toContain("flex-col");
        expect(card.className).toContain("justify-center");
    });

    it("centers its content vertically the same way for card 2", () => {
        const { container } = render(<SpotlightCard card={spotlightCard2} personaId="test-alpha" isSecond={true} />);
        const card = container.firstElementChild as HTMLElement;
        expect(card.className).toContain("h-full");
        expect(card.className).toContain("flex-col");
        expect(card.className).toContain("justify-center");
    });

    // Issue 172: when the card is narrower than its full-width default (e.g. side by side under
    // the text at sm-lg widths), the icon tile must not shrink down to a sliver when the
    // kicker/title text is long -- `shrink-0` on the tile keeps it fixed size, `min-w-0` on the
    // text column lets the text wrap instead of fighting the tile for room.
    it("keeps the icon tile a fixed size instead of letting it shrink alongside long title text", () => {
        render(<SpotlightCard card={spotlightCard1} personaId="test-alpha" isSecond={false} />);
        const icon = screen.getByAltText("SIGNATURE PICKS");
        const tile = icon.parentElement;
        expect(tile?.className).toContain("shrink-0");
    });

    // Issue 172 round 2 (Rick's review): row copy is pack-driven, so it can't assume it will
    // always fit one line -- an unconditional `whitespace-nowrap` overflowed its pill at 360/390px
    // for more than one real pack. `sm:whitespace-nowrap` keeps today's single-line look at sm
    // (640px) and up, while letting the pair wrap below that instead of overflowing.
    it("keeps each row's label and value on one line at sm and up, but lets them wrap below sm", () => {
        render(<SpotlightCard card={spotlightCard1} personaId="test-alpha" isSecond={false} />);
        const label = screen.getByText("Pick of the Day");
        const value = screen.getByText("Fixture Special");
        expect(label.className).toContain("sm:whitespace-nowrap");
        expect(label.className).not.toMatch(/(?<!sm:)whitespace-nowrap/);
        expect(value.className).toContain("sm:whitespace-nowrap");
        expect(value.className).not.toMatch(/(?<!sm:)whitespace-nowrap/);
    });

    it("renders card 1's rows as label/value pairs", () => {
        render(<SpotlightCard card={spotlightCard1} personaId="test-alpha" isSecond={false} />);

        expect(screen.getByText("Pick of the Day")).toBeInTheDocument();
        expect(screen.getByText("Fixture Special")).toBeInTheDocument();
        expect(screen.getByText("Staff Pick")).toBeInTheDocument();
    });

    it("renders card 2's body sentence and accent pairing line", () => {
        render(<SpotlightCard card={spotlightCard2} personaId="test-alpha" isSecond={true} />);

        expect(screen.getByText("100% pure beef with melty American cheese")).toBeInTheDocument();
        expect(screen.getByText("Perfect pairing: Fixture Fries & a Shake")).toBeInTheDocument();
    });

    it("defaults card 2's tone to secondary when the pack omits it", () => {
        const { container } = render(<SpotlightCard card={spotlightCard2} personaId="test-alpha" isSecond={true} />);
        expect(container.firstElementChild?.className).toContain("border-brand-secondary/25");
    });

    it("honors an explicit tone override on card 2 (e.g. one original's 'accent')", () => {
        const { container } = render(<SpotlightCard card={{ ...spotlightCard2, tone: "accent" }} personaId="test-beta" isSecond={true} />);
        expect(container.firstElementChild?.className).toContain("border-brand-accent/25");
    });

    it("applies an explicit tint as an inline background wash instead of the shared gradient class", () => {
        const { container } = render(<SpotlightCard card={{ ...spotlightCard2, tint: "#FF69B4" }} personaId="test-gamma" isSecond={true} />);
        const card = container.firstElementChild as HTMLElement;
        expect(card.style.backgroundColor).not.toBe("");
        expect(card.className).not.toContain("bg-linear-to-br from-brand-secondary/10");
    });

    it("builds the icon src from the pack's own personaId and pack-relative path", () => {
        render(<SpotlightCard card={spotlightCard1} personaId="test-alpha" isSecond={false} />);
        const icon = screen.getByAltText("SIGNATURE PICKS");
        expect(icon).toHaveAttribute("src", "/personas/test-alpha/assets/spotlight-1.svg");
    });

    // Issue #164 R4 (PR #167 round 1 review): each card-1 row value colors independently via
    // `row.tone`, not a single hardcoded brand-primary for every row.
    it("defaults a card-1 row's value color to primary when the row omits tone", () => {
        render(<SpotlightCard card={spotlightCard1} personaId="test-alpha" isSecond={false} />);
        expect(screen.getByText("Fixture Special").className).toContain("text-brand-primary");
    });

    it("honors an explicit row.tone override on a card-1 row", () => {
        const card: PersonaHeroSpotlight = {
            ...spotlightCard1,
            rows: [{ label: "Staff Pick", value: "Fixture Cheeseburger", tone: "secondary" }]
        };
        render(<SpotlightCard card={card} personaId="test-alpha" isSecond={false} />);
        expect(screen.getByText("Fixture Cheeseburger").className).toContain("text-brand-secondary");
    });

    // Issue #164 R4: card 2's pairing-line color is `accentTone` when the pack sets it,
    // independent of `tone` (which still only drives the border/wash/kicker).
    it("derives card 2's pairing-line color from tone when accentTone is absent", () => {
        render(<SpotlightCard card={spotlightCard2} personaId="test-alpha" isSecond={true} />);
        // spotlightCard2 has no explicit `tone`, so it defaults to "secondary", whose
        // SPOTLIGHT_BODY_STYLES.accent is "text-brand-primary" -- the pre-R4 mapping, preserved.
        expect(screen.getByText("Perfect pairing: Fixture Fries & a Shake").className).toContain("text-brand-primary");
    });

    it("honors an explicit accentTone override on card 2, independent of tone", () => {
        const card = { ...spotlightCard2, tone: "secondary" as const, accentTone: "secondary" as const };
        render(<SpotlightCard card={card} personaId="test-alpha" isSecond={true} />);
        // Without the accentTone override this would render text-brand-primary (tone="secondary"'s
        // SPOTLIGHT_BODY_STYLES mapping) -- this is one pack's R4 fix (the previously-mistinted pairing line).
        expect(screen.getByText("Perfect pairing: Fixture Fries & a Shake").className).toContain("text-brand-secondary");
    });
});

describe("SessionTokenPanel chips variant (issue 164 C3/E4)", () => {
    const identifiers = { sessionToken: "43ef412e1234567890abcdef4a1e", roundTripIndex: 0, roundTripToken: "43ef410000" };

    it("renders a static two-chip row with no expand/collapse affordance", () => {
        render(<SessionTokenPanel identifiers={identifiers} history={[]} variant="chips" />);

        expect(screen.getByText("Session Token")).toBeInTheDocument();
        expect(screen.getByText(/^Round 0$/)).toBeInTheDocument();
        expect(screen.queryByRole("button")).not.toBeInTheDocument();
    });

    it("truncates long tokens the same way the original did", () => {
        render(<SessionTokenPanel identifiers={identifiers} history={[]} variant="chips" />);
        expect(screen.getByText(formatSessionToken(identifiers.sessionToken))).toBeInTheDocument();
        expect(screen.getByText(formatSessionToken(identifiers.roundTripToken, 6))).toBeInTheDocument();
    });

    it("never renders a dark: class in the chips variant, regardless of page theme (E4)", () => {
        const { container } = render(<SessionTokenPanel identifiers={identifiers} history={[]} variant="chips" />);
        expect(container.innerHTML).not.toMatch(/dark:/);
    });

    it("keeps the collapsible plain variant's expand/collapse button when variant is omitted", () => {
        render(<SessionTokenPanel identifiers={identifiers} history={[]} />);
        expect(screen.getByRole("button", { name: /toggle session token history/i })).toBeInTheDocument();
    });

    // Issue #164 R2 (PR #167 round 1 review): the "Session Token" chip's text color uses the
    // badge role, so a pack that overrides it to primaryDeep renders readable text on its chip wash.
    it("colors the 'Session Token' chip from the default badge role when textRoles is absent", () => {
        render(<SessionTokenPanel identifiers={identifiers} history={[]} variant="chips" />);
        expect(screen.getByText("Session Token").className).toContain("text-brand-primary");
    });

    it("colors the 'Session Token' chip from an explicit badge role override", () => {
        render(<SessionTokenPanel identifiers={identifiers} history={[]} variant="chips" textRoles={{ badge: "primaryDeep" }} />);
        expect(screen.getByText("Session Token").className).toContain("text-brand-primary-deep");
    });
});

describe("personaTextRoles (issue 164 R2)", () => {
    it("resolves every slot's shared default when the pack sets no override", () => {
        expect(resolveTextRole("badge", undefined)).toBe(DEFAULT_TEXT_ROLES.badge);
        expect(resolveTextRole("countChip", undefined)).toBe(DEFAULT_TEXT_ROLES.countChip);
        expect(resolveTextRole("footerTagline", undefined)).toBe(DEFAULT_TEXT_ROLES.footerTagline);
        expect(resolveTextRole("footerExtra", undefined)).toBe(DEFAULT_TEXT_ROLES.footerExtra);
        expect(DEFAULT_TEXT_ROLES).toEqual({ badge: "primary", countChip: "secondary", footerTagline: "secondary", footerExtra: "secondary" });
    });

    it("lets a pack override any single slot without affecting the others", () => {
        const roles = { countChip: "accent" as const, footerTagline: "accent" as const };
        expect(resolveTextRole("badge", roles)).toBe("primary");
        expect(resolveTextRole("countChip", roles)).toBe("accent");
        expect(resolveTextRole("footerTagline", roles)).toBe("accent");
        expect(resolveTextRole("footerExtra", roles)).toBe("secondary");
    });

    it.each([
        ["primary", "text-brand-primary"],
        ["primaryDeep", "text-brand-primary-deep"],
        ["secondary", "text-brand-secondary"],
        ["accent", "text-brand-accent"],
        ["ink", "text-brand-ink"]
    ] as const)("maps the %s role to its light-mode Tailwind class", (role, expectedClass) => {
        expect(textRoleClass(role)).toBe(expectedClass);
    });

    // Issue #164 R2(c): the item-count chip's dark-mode variant reuses the primary tint for
    // primaryDeep, and the secondary tint for accent (fixes one pack's unreadable dark count chip).
    it.each([
        ["primary", "dark:text-brand-primary-tint"],
        ["primaryDeep", "dark:text-brand-primary-tint"],
        ["secondary", "dark:text-brand-secondary-tint"],
        ["accent", "dark:text-brand-secondary-tint"],
        ["ink", "dark:text-white/80"]
    ] as const)("maps the %s role to its dark-mode Tailwind class", (role, expectedClass) => {
        expect(textRoleDarkClass(role)).toBe(expectedClass);
    });
});

describe("formatSessionToken (issue 164 C3)", () => {
    it("truncates a long token to prefix…suffix", () => {
        expect(formatSessionToken("43ef412e1234567890abcdef4a1e")).toBe("43ef412e…4a1e");
    });

    it("leaves a short token untouched", () => {
        expect(formatSessionToken("abc123")).toBe("abc123");
    });

    it("returns an empty string for an empty token", () => {
        expect(formatSessionToken("")).toBe("");
    });

    it("supports a custom prefix length (round-trip token's 6/4 split)", () => {
        expect(formatSessionToken("43ef410000abcdefgh", 6, 4)).toBe("43ef41…efgh");
    });
});
