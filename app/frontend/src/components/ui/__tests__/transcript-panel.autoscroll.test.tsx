import { render } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import TranscriptPanel from "../transcript-panel";

// GH-176: on a fresh load the page scrolled 725-1010px past the hero with no user input, because
// transcript-panel.tsx called scrollIntoView() on mount, which scrolls every scrollable ancestor
// needed to bring the target into view -- including the window itself. These tests pin down the
// fix: autoscroll must only ever move the panel's own container (via scrollTop), never
// scrollIntoView/window.scrollTo and never the window itself.
//
// Round 2 (Rick's review item 1/2): the panel DOES scroll its own container to the bottom on
// mount too -- the mobile transcript sheet (a Radix Sheet) only mounts this component when
// opened mid-conversation, so it must show the newest entries immediately, not the oldest. A
// round-1 "never scroll on mount" guard broke that case. The one thing that must still never
// happen, on mount or afterwards, is the window itself scrolling.

const makeTranscript = (count: number) =>
    Array.from({ length: count }, (_, i) => ({
        text: `line ${i}`,
        isUser: i % 2 === 0,
        timestamp: new Date(2024, 0, 1, 12, i)
    }));

describe("TranscriptPanel autoscroll (GH-176)", () => {
    let scrollIntoViewSpy: ReturnType<typeof vi.fn>;
    let scrollToSpy: ReturnType<typeof vi.fn>;
    let scrollTopWrites: Array<{ target: EventTarget | null; value: number }>;
    let scrollTopDescriptor: PropertyDescriptor | undefined;

    beforeEach(() => {
        // jsdom doesn't implement scrollIntoView/scrollTo -- stub them so a regression back to
        // the old behavior is directly observable instead of throwing "not a function".
        scrollIntoViewSpy = vi.fn();
        scrollToSpy = vi.fn();
        window.HTMLElement.prototype.scrollIntoView = scrollIntoViewSpy;
        window.scrollTo = scrollToSpy as typeof window.scrollTo;
        Object.defineProperty(window, "scrollY", { value: 0, writable: true, configurable: true });

        // Spy on EVERY element's scrollTop write process-wide (rather than one specific node
        // after the fact), so a regression that writes scrollTop during the initial mount commit
        // -- before a test could otherwise attach an instance-level spy -- is still caught.
        scrollTopWrites = [];
        scrollTopDescriptor = Object.getOwnPropertyDescriptor(HTMLElement.prototype, "scrollTop");
        Object.defineProperty(HTMLElement.prototype, "scrollTop", {
            configurable: true,
            get() {
                return 0;
            },
            set(value: number) {
                scrollTopWrites.push({ target: this as EventTarget, value });
            }
        });
    });

    afterEach(() => {
        if (scrollTopDescriptor) {
            Object.defineProperty(HTMLElement.prototype, "scrollTop", scrollTopDescriptor);
        }
        vi.restoreAllMocks();
    });

    it("on mount with an empty transcript, scrolls only its own container (never scrollIntoView/window.scrollTo, never the window)", () => {
        const { container } = render(<TranscriptPanel transcripts={[]} />);
        const scrollContainer = container.firstElementChild as HTMLElement;
        expect(scrollIntoViewSpy).not.toHaveBeenCalled();
        expect(scrollToSpy).not.toHaveBeenCalled();
        // The only scroll on mount is a write to the panel's own container, scrolled to the
        // bottom (scrollTop := scrollHeight) -- never scrollIntoView, never window.scrollTo.
        expect(scrollTopWrites).toEqual([{ target: scrollContainer, value: scrollContainer.scrollHeight }]);
        expect(window.scrollY).toBe(0);
    });

    it("on mount with a non-empty transcript (e.g. the mobile sheet opened mid-conversation), scrolls its own container to the bottom; the window is never touched", () => {
        // Round 2 (Rick's review item 1/2): this is the case the round-1 "never scroll on mount"
        // guard got wrong. The mobile transcript sheet only mounts TranscriptPanel when opened
        // mid-conversation, so on mount it must already show the newest entries, not the oldest.
        const { container } = render(<TranscriptPanel transcripts={makeTranscript(5)} />);
        const scrollContainer = container.firstElementChild as HTMLElement;
        expect(scrollIntoViewSpy).not.toHaveBeenCalled();
        expect(scrollToSpy).not.toHaveBeenCalled();
        // The only scroll on mount is a write to the panel's own container, scrolled to the
        // bottom -- never scrollIntoView, never window.scrollTo, never the window itself.
        expect(scrollTopWrites).toEqual([{ target: scrollContainer, value: scrollContainer.scrollHeight }]);
        expect(window.scrollY).toBe(0);
    });

    it("scrolls only its own container (not the window) when new entries arrive", () => {
        const { rerender, container } = render(<TranscriptPanel transcripts={makeTranscript(1)} />);
        const scrollContainer = container.firstElementChild as HTMLElement;
        // jsdom never lays out real scrollable content, so scrollHeight is always 0 -- stub it on
        // this specific instance so the assertion can distinguish "scrolled to bottom" from "not
        // scrolled" rather than both reading back as 0.
        Object.defineProperty(scrollContainer, "scrollHeight", { value: 999, configurable: true });
        // The mount render above already wrote a scrollTop once (now that mount scrolls too, per
        // item 1) -- clear it so this assertion isolates the write caused by THIS rerender's new
        // entries, which is what this test is actually about.
        scrollTopWrites.length = 0;

        rerender(<TranscriptPanel transcripts={makeTranscript(2)} />);

        expect(scrollTopWrites).toEqual([{ target: scrollContainer, value: 999 }]);
        expect(scrollIntoViewSpy).not.toHaveBeenCalled();
        expect(scrollToSpy).not.toHaveBeenCalled();
        expect(window.scrollY).toBe(0);
    });

    it("applies the container className prop so parents control height/overflow, not the window", () => {
        const { container } = render(
            <TranscriptPanel transcripts={[]} className="h-[calc(100vh-13rem)] overflow-auto pr-4" />
        );
        const scrollContainer = container.firstElementChild as HTMLElement;
        expect(scrollContainer.className).toContain("overflow-auto");
    });
});
