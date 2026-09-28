// @ts-check
/**
 * Shared, deterministic auth-mode build-metadata helper for the SPA (design 18.6, issue #145).
 *
 * The deployed SPA carries an IMMUTABLE, build-time record of the authentication mode it was
 * compiled for, as `<meta name="drivethru-auth-mode" content="Entra|Development">` baked into
 * `index.html` by the Vite build (see `vite.config.ts`). `scripts/Verify-ProductionAuth.ps1` (#146)
 * reads this tag to prove the deployed bundle is the enforcing build, not just its env pins.
 *
 * The value is NORMALIZED to a fixed enum at build time (never the raw VITE_AUTH_MODE), so an
 * operator cannot inject arbitrary markup via the env var, and the value cannot be overridden at
 * runtime -- it is static text in the served document.
 */

/** The `name` attribute of the immutable auth-mode meta tag. */
export const AUTH_MODE_META_NAME = 'drivethru-auth-mode';

/**
 * Normalize a raw `VITE_AUTH_MODE` value to its canonical marker content. Only an explicit,
 * case-insensitive `entra` resolves to `Entra`; everything else (unset, `Development`, or any
 * unrecognized value) is `Development` -- the fail-closed build guard (`validate-auth-config.mjs`)
 * is what actually blocks an unrecognized mode from ever reaching this point.
 * @param {string | undefined | null} raw
 * @returns {'Entra' | 'Development'}
 */
export function normalizeAuthMode(raw) {
  const v = (raw ?? '').trim().toLowerCase();
  return v === 'entra' ? 'Entra' : 'Development';
}

/**
 * Extract the auth-mode meta content from an HTML document. Tolerant of attribute order and of
 * single/double quotes. Returns the raw content string (possibly empty) when the tag is present,
 * or `null` when there is no such tag.
 * @param {string | undefined | null} html
 * @returns {string | null}
 */
export function parseAuthModeMeta(html) {
  if (typeof html !== 'string' || html.length === 0) return null;
  const metaTags = html.match(/<meta\b[^>]*>/gi);
  if (!metaTags) return null;
  for (const tag of metaTags) {
    const name = /\bname\s*=\s*["']([^"']*)["']/i.exec(tag);
    if (name && name[1].trim().toLowerCase() === AUTH_MODE_META_NAME) {
      const content = /\bcontent\s*=\s*["']([^"']*)["']/i.exec(tag);
      return content ? content[1] : '';
    }
  }
  return null;
}

/**
 * The production predicate: the served SPA is the enforcing Entra build IFF the auth-mode meta
 * content is exactly `Entra`, case-insensitively.
 * @param {string | null | undefined} content
 * @returns {boolean}
 */
export function isProductionEntra(content) {
  return typeof content === 'string' && content.trim().toLowerCase() === 'entra';
}

/**
 * Render the immutable auth-mode meta tag for a given raw mode.
 * @param {string | undefined | null} rawMode
 * @returns {string}
 */
export function renderAuthModeMetaTag(rawMode) {
  return `<meta name="${AUTH_MODE_META_NAME}" content="${normalizeAuthMode(rawMode)}" />`;
}
