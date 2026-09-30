import { render, screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { BrandHero, CalloutPill, SpotlightCard, SessionTokenPanel, formatSessionToken } from "../App";
import type { PersonaDetail, PersonaHeroSpotlight } from "@/types/persona";

// Issue 164 A1-A5/A8/B2/D2: covers the rewritten two-column `BrandHero` (logo/badge/headline/
// description/callouts/spotlight) and its child components directly, complementing the existing
// full-`RootApp` logo-fallback coverage in `App.brandHero.test.tsx`.

const theme = { light: { primary: "200 80% 50%", secondary: "40 60% 40%", background: "0 0% 98%", foreground: "0 0% 10%" } };

const spotlightCard1: PersonaHeroSpotlight = {
    icon: "assets/spotlight-1.svg",
    kicker: "SIGNATURE SLUSHES",
    title: "Cherry Limeade & more",
    rows: [
        { label: "Slush of the Day", value: "Cherry Limeade" },
        { label: "Carhop Pick", value: "SuperSONIC Cheeseburger" }
    ]
};

const spotlightCard2: PersonaHeroSpotlight = {
    icon: "assets/spotlight-2.svg",
    kicker: "CARHOP FAVORITE",
    title: "SuperSONIC® Double Cheeseburger",
    body: "100% pure beef with melty American cheese",
    accent: "Perfect pairing: Large Tots & a Shake"
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

        expect(screen.getByText("SIGNATURE SLUSHES")).toBeInTheDocument();
        expect(screen.getByText("Cherry Limeade & more")).toBeInTheDocument();
        expect(screen.getByText("CARHOP FAVORITE")).toBeInTheDocument();
        expect(screen.getByText("SuperSONIC® Double Cheeseburger")).toBeInTheDocument();
    });

    it("omits the spotlight column entirely when the pack has no spotlight cards", () => {
        render(<BrandHero logoUrl="/personas/test-alpha/assets/logo.svg" persona={detailFor({ hero: { ...detailFor().hero, spotlight: [] } })} />);

        expect(screen.queryByText("SIGNATURE SLUSHES")).not.toBeInTheDocument();
    });

    it("uses the shared neutral hero.badge default when the pack doesn't override it", () => {
        render(<BrandHero logoUrl="/personas/test-alpha/assets/logo.svg" persona={detailFor()} />);
        // react-i18next is globally mocked to echo the key -- issue #80 F3.
        expect(screen.getByText("hero.badge")).toBeInTheDocument();
    });

    it("renders a persona-specific hero.badge override instead of the shared default (A5)", () => {
        render(<BrandHero logoUrl="/personas/test-alpha/assets/logo.svg" persona={detailFor({ hero: { ...detailFor().hero, badge: "VOICE CREW DEMO" } })} />);

        expect(screen.getByText("VOICE CREW DEMO")).toBeInTheDocument();
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
});

describe("CalloutPill (issue 164 A3)", () => {
    it.each([
        ["primary", "from-brand-primary"],
        ["secondary", "from-brand-secondary"],
        ["accent", "from-brand-accent"]
    ] as const)("draws its gradient from the %s brand role", (tone, expectedClass) => {
        const { container } = render(<CalloutPill title="REWARDS READY" detail="Some detail copy" tone={tone} />);
        expect(container.firstElementChild?.className).toContain(expectedClass);
    });

    it("renders the title and detail text passed in", () => {
        render(<CalloutPill title="LIVE MENU" detail="Azure AI Search keeps items current" tone="secondary" />);
        expect(screen.getByText("LIVE MENU")).toBeInTheDocument();
        expect(screen.getByText("Azure AI Search keeps items current")).toBeInTheDocument();
    });
});

describe("SpotlightCard (issue 164 A1/A2)", () => {
    it("renders card 1's rows as label/value pairs", () => {
        render(<SpotlightCard card={spotlightCard1} personaId="test-alpha" isSecond={false} />);

        expect(screen.getByText("Slush of the Day")).toBeInTheDocument();
        expect(screen.getByText("Cherry Limeade")).toBeInTheDocument();
        expect(screen.getByText("Carhop Pick")).toBeInTheDocument();
    });

    it("renders card 2's body sentence and accent pairing line", () => {
        render(<SpotlightCard card={spotlightCard2} personaId="test-alpha" isSecond={true} />);

        expect(screen.getByText("100% pure beef with melty American cheese")).toBeInTheDocument();
        expect(screen.getByText("Perfect pairing: Large Tots & a Shake")).toBeInTheDocument();
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
        const icon = screen.getByAltText("SIGNATURE SLUSHES");
        expect(icon).toHaveAttribute("src", "/personas/test-alpha/assets/spotlight-1.svg");
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
