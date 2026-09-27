import { afterEach, describe, expect, it, vi } from "vitest";

import { backendTargetUrl, currentBackendId } from "../backends";
import type { PersonaBackendEntry } from "@/types/persona";

// Issue #80 F11's pure helpers (design doc §5.2/§10.1). Covered separately from
// `backend-picker.test.tsx` to guard the origin-matching and URL-building rules on their own,
// including the "empty url means this origin" convention `app/backend/app.py`'s
// `_backend_entries` docstring establishes.

const BACKENDS: PersonaBackendEntry[] = [
    { id: "python", url: "" },
    { id: "dotnet", url: "https://dotnet.example.com" }
];

afterEach(() => {
    vi.unstubAllGlobals();
});

describe("currentBackendId", () => {
    it("matches an explicit backend url against the given origin", () => {
        expect(currentBackendId(BACKENDS, "https://dotnet.example.com")).toBe("dotnet");
    });

    it("treats an empty-url entry as 'this origin' whenever no other entry matches", () => {
        expect(currentBackendId(BACKENDS, "https://python.example.com")).toBe("python");
        expect(currentBackendId(BACKENDS, "https://localhost:5173")).toBe("python");
    });

    it("falls back to the first entry when nothing has an empty url and nothing matches", () => {
        const noSelfEntry: PersonaBackendEntry[] = [
            { id: "python", url: "https://python.example.com" },
            { id: "dotnet", url: "https://dotnet.example.com" }
        ];
        expect(currentBackendId(noSelfEntry, "https://vite-dev-server.example.com")).toBe("python");
    });

    it("returns an empty string when there are no backends at all", () => {
        expect(currentBackendId([], "https://python.example.com")).toBe("");
    });

    it("reads window.location.origin when no origin argument is supplied", () => {
        vi.stubGlobal("location", { origin: "https://dotnet.example.com" });
        expect(currentBackendId(BACKENDS)).toBe("dotnet");
    });
});

describe("backendTargetUrl", () => {
    it("builds the target backend's URL with explicit persona and model query params", () => {
        const target = backendTargetUrl({ id: "dotnet", url: "https://dotnet.example.com" }, "test-alpha", "gpt-realtime-2.1");
        const url = new URL(target);
        expect(url.origin).toBe("https://dotnet.example.com");
        expect(url.searchParams.get("persona")).toBe("test-alpha");
        expect(url.searchParams.get("model")).toBe("gpt-realtime-2.1");
    });

    it("resolves an empty-url ('this origin') backend against window.location.origin", () => {
        vi.stubGlobal("location", { origin: "https://python.example.com" });
        const target = backendTargetUrl({ id: "python", url: "" }, "test-beta", "gpt-5-mini");
        const url = new URL(target);
        expect(url.origin).toBe("https://python.example.com");
        expect(url.searchParams.get("persona")).toBe("test-beta");
    });

    it("omits persona/model params that are falsy rather than writing them as empty strings", () => {
        const target = backendTargetUrl({ id: "dotnet", url: "https://dotnet.example.com" }, "", "");
        const url = new URL(target);
        expect(url.searchParams.has("persona")).toBe(false);
        expect(url.searchParams.has("model")).toBe(false);
    });

    // Rick's PR 134 review nit: `backend.url` is server config (not guest input), but a
    // misconfigured entry with a `javascript:`/`file:` scheme must never be handed to
    // `location.assign` -- only http:/https: targets are ever returned.
    it("throws rather than returning a URL for a disallowed scheme", () => {
        expect(() => backendTargetUrl({ id: "evil", url: "javascript:alert(1)" }, "test-alpha", "gpt-realtime-2.1")).toThrow();
        expect(() => backendTargetUrl({ id: "evil", url: "file:///etc/passwd" }, "test-alpha", "gpt-realtime-2.1")).toThrow();
    });

    it("allows both http: and https: schemes", () => {
        expect(() => backendTargetUrl({ id: "dotnet", url: "https://dotnet.example.com" }, "", "")).not.toThrow();
        expect(() => backendTargetUrl({ id: "dotnet", url: "http://dotnet.example.com" }, "", "")).not.toThrow();
    });
});
