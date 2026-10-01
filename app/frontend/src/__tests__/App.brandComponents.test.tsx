import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { BrandHero, CalloutPill, SpotlightCard, SessionTokenPanel, formatSessionToken, HERO_BODY_TEXT_CLASS } from "../App";
import type { PersonaDetail, PersonaHeroSpotlight } from "@/types/persona";
import { resolveTextRole, textRoleClass, textRoleDarkClass, DEFAULT_TEXT_ROLES } from "@/lib/personaTextRoles";

// Issue 164 A1-A5/A8/B2/D2: covers the rewritten two-column `BrandHero` (logo/badge/headline/
// description/callouts/spotlight) and its child components directly, complementing the existing
// full-`RootApp` logo-fallback coverage in `App.brandHero.test.tsx`.

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

// Issue 172: the hero's dead space around the spotlight cards was fixed by (1) pulling the
// logo/badge into their own full-width row above the two-column grid, (2) top-aligning the
// spotlight column with the headline/description column instead of vertically centering it
// against the whole (taller) old column, and (3) moving the 3 callout pills + tech line out of
// the text column into one full-width footer row below both columns. These tests assert that
// structure directly, pack-driven (no persona-specific branching), for every persona's content.
describe("BrandHero layout (issue 172)", () => {
    it("renders the logo/badge row as the section's first child, ahead of the two-column grid", () => {
        const { container } = render(<BrandHero logoUrl="/personas/test-alpha/assets/logo.svg" persona={detailFor()} />);

        const section = container.querySelector("section");
        const firstChild = section?.firstElementChild;
        expect(firstChild?.textContent).toContain("hero.badge");
        expect(firstChild?.textContent).not.toContain("Test Alpha ordering powered by Microsoft Foundry");
    });

    it("puts the headline/description column and the spotlight column in one lg:grid-cols-5 row with a 3:2 split", () => {
        render(<BrandHero logoUrl="/personas/test-alpha/assets/logo.svg" persona={detailFor()} />);

        const headline = screen.getByText("Test Alpha ordering powered by Microsoft Foundry");
        const textColumn = headline.parentElement;
        const row = textColumn?.parentElement;

        expect(row?.className).toContain("lg:grid-cols-5");
        expect(textColumn?.className).toContain("lg:col-span-3");

        const spotlightColumn = row?.lastElementChild as HTMLElement;
        expect(spotlightColumn.className).toContain("lg:col-span-2");
        expect(spotlightColumn.textContent).toContain("SIGNATURE PICKS");
    });

    it("top-aligns the spotlight column with the text column instead of vertically centering it", () => {
        render(<BrandHero logoUrl="/personas/test-alpha/assets/logo.svg" persona={detailFor()} />);

        const headline = screen.getByText("Test Alpha ordering powered by Microsoft Foundry");
        const row = headline.parentElement?.parentElement;

        // `items-start` (top-aligned) replaces the pre-172 `lg:items-center` that vertically
        // centered the (shorter) spotlight column against the (taller) text column.
        expect(row?.className).toContain("items-start");
        expect(row?.className).not.toContain("items-center");
    });

    it("moves the 3 callout pills and the tech line into one full-width footer row below the two-column grid", () => {
        const { container } = render(<BrandHero logoUrl="/personas/test-alpha/assets/logo.svg" persona={detailFor()} />);

        const section = container.querySelector("section") as HTMLElement;
        const headline = screen.getByText("Test Alpha ordering powered by Microsoft Foundry");
        const twoColumnRow = headline.parentElement?.parentElement as HTMLElement;
        const footer = twoColumnRow.nextElementSibling as HTMLElement;

        // The footer row is a sibling AFTER the two-column grid, not nested inside its text column.
        expect(footer).not.toBeNull();
        expect(footer.textContent).toContain("REWARDS READY");
        expect(footer.textContent).toContain("hero.poweredBy");
        expect(twoColumnRow.textContent).not.toContain("REWARDS READY");

        // All 3 callouts render in a single responsive grid row, not stacked one-per-line.
        const calloutGrid = footer.firstElementChild as HTMLElement;
        expect(calloutGrid.className).toContain("sm:grid-cols-3");
        expect(calloutGrid.children.length).toBe(3);

        // section has exactly 3 top-level children: logo/badge row, two-column grid, footer row.
        expect(section.children.length).toBe(3);
    });

    it("gives the spotlight card grid equal-height, top-aligned cards (items-stretch, self-start)", () => {
        render(<BrandHero logoUrl="/personas/test-alpha/assets/logo.svg" persona={detailFor()} />);

        const card = screen.getByText("SIGNATURE PICKS").closest("div")?.parentElement?.parentElement;
        const spotlightGrid = card?.parentElement as HTMLElement;
        expect(spotlightGrid.className).toContain("items-stretch");
        expect(spotlightGrid.className).toContain("self-start");
    });

    it("keeps the same pack-driven structure regardless of persona content (no brand-specific branching)", () => {
        const altPersona = detailFor({
            id: "test-beta",
            title: "Test Beta Fixture",
            hero: {
                headline: "Test Beta ordering powered by Microsoft Foundry",
                description: "A different fixture description entirely.",
                callouts: [
                    { title: "ONE", detail: "detail one", tone: "primary" },
                    { title: "TWO", detail: "detail two", tone: "secondary" },
                    { title: "THREE", detail: "detail three", tone: "accent" }
                ],
                spotlight: [spotlightCard1, spotlightCard2]
            }
        });
        const { container } = render(<BrandHero logoUrl="/personas/test-beta/assets/logo.svg" persona={altPersona} />);

        const section = container.querySelector("section") as HTMLElement;
        expect(section.children.length).toBe(3);
        const headline = screen.getByText("Test Beta ordering powered by Microsoft Foundry");
        const row = headline.parentElement?.parentElement;
        expect(row?.className).toContain("lg:grid-cols-5");
        expect(screen.getByText("ONE")).toBeInTheDocument();
        expect(screen.getByText("THREE")).toBeInTheDocument();
    });
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

    // Issue 172: at the narrower end of the hero's lg: breakpoint, the icon tile must not shrink
    // down to a sliver when the kicker/title text is long -- `shrink-0` on the tile keeps it fixed
    // size, `min-w-0` on the text column lets the text wrap instead of fighting the tile for room.
    it("keeps the icon tile a fixed size instead of letting it shrink alongside long title text", () => {
        render(<SpotlightCard card={spotlightCard1} personaId="test-alpha" isSecond={false} />);
        const icon = screen.getByAltText("SIGNATURE PICKS");
        const tile = icon.parentElement;
        expect(tile?.className).toContain("shrink-0");
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
