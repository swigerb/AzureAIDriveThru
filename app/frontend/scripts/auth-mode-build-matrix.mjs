// @ts-check
/**
 * Auth-mode build/test matrix (PR #148 review round 2, item B1) -- modeled on Retail Pulse's
 * `scripts/provider-build-matrix.mjs`, trimmed to our two-mode contract (`Entra`/`Development`,
 * no GitHub/Anonymous).
 *
 * Two parts:
 *   1. Config-gate matrix (fast, in-process, exported as `gateScenarios` below): every documented
 *      scenario for `validate-auth-config.mjs`'s `validateAuthConfig`. Asserted on every `npm test`
 *      via `scripts/__tests__/auth-mode-build-matrix.test.ts` (no process spawning there).
 *   2. Full build matrix (`--full`, slow -- spawns a REAL `vite build` per mode): builds Entra,
 *      Development, and an unconfigured/unset-mode scenario with SAFE, SYNTHETIC, PUBLIC ids only,
 *      then inspects each emitted `index.html`'s immutable `drivethru-auth-mode` meta tag with the
 *      exact same parser/predicate production tooling uses (`auth-mode-meta.mjs`). Also proves the
 *      unset+no-ids scenario's build genuinely FAILS (`vite build` exits non-zero) rather than
 *      merely asserting the pure resolver throws in isolation.
 *
 * Run the full matrix manually as part of PR validation: `node scripts/auth-mode-build-matrix.mjs
 * --full` (or `npm run test:auth-mode-matrix:full`). Not wired into the default `npm test` --
 * spawning `vite build` four times would meaningfully slow down every CI run for a guarantee the
 * fast in-process gate matrix (part 1) already gives on every commit.
 *
 * All identifiers are synthetic and public -- no real tenant/app is involved.
 */
import { spawnSync } from 'node:child_process';
import { existsSync, rmSync, readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';
import { validateAuthConfig } from './validate-auth-config.mjs';
import { parseAuthModeMeta, isProductionEntra } from './auth-mode-meta.mjs';

const frontendRoot = join(dirname(fileURLToPath(import.meta.url)), '..');
const MATRIX_OUT = join(frontendRoot, 'dist-matrix');

// Safe, synthetic, PUBLIC identifiers -- not tied to any real tenant/app.
const SYN = {
  tenant: '11111111-1111-1111-1111-111111111111',
  client: '22222222-2222-2222-2222-222222222222',
};

/**
 * The config-gate scenario table -- exported so the vitest wrapper
 * (`scripts/__tests__/auth-mode-build-matrix.test.ts`) can assert every scenario in-process,
 * without spawning a child process per case. Mirrors `src/auth/authMode.ts`'s `resolveAuthMode`
 * rules exactly (see that file's doc comment for the full rationale).
 * @type {Array<{ name: string, env: Record<string, string | undefined>, expectPass: boolean }>}
 */
export const gateScenarios = [
  {
    name: 'Entra + valid synthetic ids',
    env: { VITE_AUTH_MODE: 'Entra', VITE_ENTRA_TENANT_ID: SYN.tenant, VITE_ENTRA_CLIENT_ID: SYN.client },
    expectPass: true,
  },
  { name: 'Entra + missing ids', env: { VITE_AUTH_MODE: 'Entra' }, expectPass: false },
  {
    name: 'Entra + placeholder ids',
    env: { VITE_AUTH_MODE: 'Entra', VITE_ENTRA_TENANT_ID: '<tenant-id>', VITE_ENTRA_CLIENT_ID: '<client-id>' },
    expectPass: false,
  },
  {
    name: 'Entra + empty-GUID ids',
    env: {
      VITE_AUTH_MODE: 'Entra',
      VITE_ENTRA_TENANT_ID: '00000000-0000-0000-0000-000000000000',
      VITE_ENTRA_CLIENT_ID: '00000000-0000-0000-0000-000000000000',
    },
    expectPass: false,
  },
  {
    name: 'Entra + partial ids (tenant only)',
    env: { VITE_AUTH_MODE: 'Entra', VITE_ENTRA_TENANT_ID: SYN.tenant },
    expectPass: false,
  },
  { name: 'Development (mode only, no ids)', env: { VITE_AUTH_MODE: 'Development' }, expectPass: true },
  {
    name: 'Development + Entra ids (refused -- likely a leaked env var)',
    env: { VITE_AUTH_MODE: 'Development', VITE_ENTRA_TENANT_ID: SYN.tenant, VITE_ENTRA_CLIENT_ID: SYN.client },
    expectPass: false,
  },
  { name: 'Unknown mode "Okta"', env: { VITE_AUTH_MODE: 'Okta' }, expectPass: false },
  {
    name: 'Unset mode, no ids (this guard only ever gates a production-style `npm run build`) -- the crux of B1',
    env: {},
    expectPass: false,
  },
  {
    name: 'Unset mode, both ids present (backwards compatible with pre-VITE_AUTH_MODE builds)',
    env: { VITE_ENTRA_TENANT_ID: SYN.tenant, VITE_ENTRA_CLIENT_ID: SYN.client },
    expectPass: true,
  },
  {
    name: 'Unset mode, partial ids',
    env: { VITE_ENTRA_TENANT_ID: SYN.tenant },
    expectPass: false,
  },
];

function runMatrix() {
  const argv = new Set(process.argv.slice(2));
  const full = argv.has('--full');
  const gateOnly = argv.has('--gate-only') || !full;

  let failures = 0;
  /** @param {boolean} ok @param {string} name @param {string} [detail] */
  const report = (ok, name, detail = '') => {
    if (ok) {
      console.log(`  [PASS] ${name}`);
    } else {
      console.error(`  [FAIL] ${name}${detail ? ' -- ' + detail : ''}`);
      failures++;
    }
  };

  /** Build a deterministic child env: strip ambient VITE_* then apply the scenario. */
  function scenarioEnv(env) {
    /** @type {Record<string, string | undefined>} */
    const base = { ...process.env };
    for (const k of Object.keys(base)) {
      if (k.startsWith('VITE_')) delete base[k];
    }
    return { ...base, ...env };
  }

  console.log('=== Frontend auth-mode config-gate matrix ===');
  for (const s of gateScenarios) {
    const result = validateAuthConfig(s.env);
    report(result.ok === s.expectPass, s.name, result.ok === s.expectPass ? '' : `got ok=${result.ok} (${result.error ?? 'no error'})`);
  }

  if (!gateOnly) {
  /**
   * Full build matrix: all three scenarios build with SAFE SYNTHETIC ids/env only. Each emitted
   * `index.html` is inspected with the exact parser/predicate production tooling uses, so this is
   * a behavioral proof, not just a config-gate assertion.
   * @type {Array<{ mode: string, rawMode: string, env: Record<string, string>, expectBuild: boolean }>}
   */
  const buildModes = [
    {
      mode: 'entra',
      rawMode: 'Entra',
      env: { VITE_AUTH_MODE: 'Entra', VITE_ENTRA_TENANT_ID: SYN.tenant, VITE_ENTRA_CLIENT_ID: SYN.client },
      expectBuild: true,
    },
    { mode: 'development', rawMode: 'Development', env: { VITE_AUTH_MODE: 'Development' }, expectBuild: true },
    { mode: 'unset-unconfigured', rawMode: '', env: {}, expectBuild: false },
    {
      // Rick's follow-up note on #145 (re-review of PR #148, item B1): `NODE_ENV=development vite
      // build` (no `--mode` flag, no `VITE_AUTH_MODE`, no ids) resolves Vite's own `mode` to
      // 'development' and bakes `import.meta.env.DEV === true` into the bundle -- proven separately
      // by inspecting `resolveConfig`'s output for this exact combination. The guard in
      // `vite.config.ts` must NOT be fooled by that: it keys its own `isDevServer` flag on Vite's
      // `command` (`command === 'serve'`), which stays `false` for every `vite build` regardless of
      // `NODE_ENV`/`mode`, so this build must still fail closed exactly like the plain
      // 'unset-unconfigured' scenario above.
      mode: 'unset-unconfigured-node-env-development',
      rawMode: '',
      env: { NODE_ENV: 'development' },
      expectBuild: false,
    },
  ];

  console.log(
    `\n=== Frontend full build matrix + auth-mode meta behavioral test (${buildModes.map((b) => b.mode).join(', ')}) ===`,
  );

  for (const b of buildModes) {
    const outDir = join('dist-matrix', b.mode);
    const built = spawnSync('npx', ['vite', 'build', '--outDir', outDir, '--emptyOutDir'], {
      cwd: frontendRoot,
      env: scenarioEnv(b.env),
      encoding: 'utf8',
      shell: process.platform === 'win32',
    });
    const succeeded = built.status === 0;
    report(
      succeeded === b.expectBuild,
      `${b.mode}: \`vite build\` ${b.expectBuild ? 'succeeds' : 'FAILS closed'}`,
      succeeded === b.expectBuild ? '' : (built.stderr || built.stdout || '').trim().split('\n').slice(-3).join(' | '),
    );
    if (!b.expectBuild || !succeeded) continue;

    const htmlPath = join(MATRIX_OUT, b.mode, 'index.html');
    const html = existsSync(htmlPath) ? readFileSync(htmlPath, 'utf8') : '';
    const content = parseAuthModeMeta(html);
    const expected = b.mode === 'entra' ? 'Entra' : 'Development';
    report(
      content === expected,
      `${b.mode}: emitted index.html carries immutable auth-mode meta = '${expected}'`,
      content === null ? 'meta tag missing' : `got '${content}'`,
    );

    const prodEntra = isProductionEntra(content);
    if (b.mode === 'entra') {
      report(prodEntra, `${b.mode}: satisfies the production-Entra verifier predicate`);
    } else {
      report(!prodEntra, `${b.mode}: FAILS the production-Entra verifier predicate (as required)`);
    }
    }
  }

  // -- cleanup --
  try {
    if (existsSync(MATRIX_OUT)) rmSync(MATRIX_OUT, { recursive: true, force: true });
  } catch {
    /* best effort */
  }

  console.log('');
  if (failures === 0) {
    console.log('Auth-mode build matrix: ALL SCENARIOS PASSED.');
    process.exit(0);
  } else {
    console.error(`Auth-mode build matrix: ${failures} scenario(s) FAILED.`);
    process.exit(1);
  }
}

// CLI entry point: only run the (side-effecting, process.exit-ing) matrix when this file is
// executed directly (`node scripts/auth-mode-build-matrix.mjs`) -- NOT when imported, so the
// vitest wrapper (`scripts/__tests__/auth-mode-build-matrix.test.ts`) can import `gateScenarios`
// without triggering a full run + `process.exit`.
const invokedDirectly =
  typeof process !== 'undefined' &&
  Array.isArray(process.argv) &&
  process.argv[1] &&
  import.meta.url === new URL(`file://${process.argv[1].replace(/\\/g, '/')}`).href;

if (invokedDirectly) runMatrix();

