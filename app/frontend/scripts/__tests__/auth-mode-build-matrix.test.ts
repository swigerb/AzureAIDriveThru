// @vitest-environment node
//
// This suite imports `../validate-auth-config.mjs`, which imports `vite`'s `loadEnv` and thus
// esbuild -- see `validate-auth-config.test.ts` for why that needs the Node test environment
// rather than the repo's default jsdom.
import { describe, it, expect } from 'vitest';
import { gateScenarios } from '../auth-mode-build-matrix.mjs';
import { validateAuthConfig } from '../validate-auth-config.mjs';

/**
 * Build-matrix test (PR #148 review round 2, item B1 -- "Tests incl. a build-matrix test like
 * Retail Pulse's"). Runs the full config-gate scenario table (shared with the standalone
 * `scripts/auth-mode-build-matrix.mjs`, which also drives a real `vite build` per mode when run
 * with `--full`) through `validate-auth-config.mjs`'s `validateAuthConfig` in-process, so this is
 * fast enough to run on every `npm test`.
 *
 * This is also the safety net for the deliberate duplication between this file's rules and
 * `src/auth/authMode.ts`'s `resolveAuthMode` (see that file's `authMode.test.ts` for the resolver's
 * own exhaustive tests, covering the same scenarios plus the `DEV` dev-server escape hatch this
 * guard does not have): every entry below is one row of the SAME truth table both implementations
 * must agree on.
 */
describe('auth-mode build matrix -- config gate', () => {
  it.each(gateScenarios)('$name -> expectPass=$expectPass', ({ env, expectPass }) => {
    const result = validateAuthConfig(env);
    expect(result.ok).toBe(expectPass);
  });
});
