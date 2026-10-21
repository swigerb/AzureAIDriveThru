import "vitest";
import type { TestingLibraryMatchers } from "@testing-library/jest-dom/matchers";

// Vitest 5 augments Matchers rather than the single-parameter Assertion interface.
declare module "vitest" {
    interface Matchers<R extends void | Promise<void> = void | Promise<void>, T = unknown>
        extends TestingLibraryMatchers<unknown, R> {}
}
