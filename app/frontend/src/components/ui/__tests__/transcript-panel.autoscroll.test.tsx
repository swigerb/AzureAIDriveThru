import { render } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import TranscriptPanel from "../transcript-panel";

// GH-176: on a fresh load the page scrolled 725-1010px past the hero with no user input, because
// transcript-panel.tsx called scrollIntoView() on mount, which scrolls every scrollable ancestor
// needed to bring the target into view -- including the window itself. These tests pin down the
// fix: autoscroll must only ever move the panel's own container (via scrollTop), never the
// window/scrollIntoView, and must never fire on mount (empty or non-empty transcript), only when
// new entries arrive afterwards.

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

    it("never scrolls (container, scrollIntoView, or window) on mount with an empty transcript", () => {
        render(<TranscriptPanel transcripts={[]} />);
        expect(scrollIntoViewSpy).not.toHaveBeenCalled();
        expect(scrollToSpy).not.toHaveBeenCalled();
        expect(scrollTopWrites).toEqual([]);
        expect(window.scrollY).toBe(0);
    });

    it("never scrolls (container, scrollIntoView, or window) on mount with a non-empty transcript", () => {
        render(<TranscriptPanel transcripts={makeTranscript(5)} />);
        expect(scrollIntoViewSpy).not.toHaveBeenCalled();
        expect(scrollToSpy).not.toHaveBeenCalled();
        expect(scrollTopWrites).toEqual([]);
        expect(window.scrollY).toBe(0);
    });

    it("scrolls only its own container (not the window) when new entries arrive", () => {
        const { rerender, container } = render(<TranscriptPanel transcripts={makeTranscript(1)} />);
        const scrollContainer = container.firstElementChild as HTMLElement;
        // jsdom never lays out real scrollable content, so scrollHeight is always 0 -- stub it on
        // this specific instance so the assertion can distinguish "scrolled to bottom" from "not
        // scrolled" rather than both reading back as 0.
        Object.defineProperty(scrollContainer, "scrollHeight", { value: 999, configurable: true });

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
