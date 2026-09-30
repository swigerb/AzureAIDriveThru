import path from "path";
import react from "@vitejs/plugin-react";
import { defineConfig, loadEnv, type Plugin } from "vite";
import { renderAuthModeMetaTag } from "./scripts/auth-mode-meta.mjs";
import { resolveAuthMode } from "./src/auth/authMode";

/**
 * Build-time auth-mode guard + immutable marker plugin (PR #148 review round 2, item B1).
 *
 * Runs the SAME `authMode.ts` resolver the runtime uses (`index.tsx`'s bootstrap, `AuthGate.tsx`),
 * fed from Vite's own `loadEnv(mode, envDir, 'VITE_')` -- which honours `.env`/`.env.production`/
 * `.env.development` files, not just the inherited shell env -- so a value set only in a `.env`
 * file (as `scripts/docker-build.sh`/`deploy.sh` write one) is seen by the guard exactly like the
 * runtime bundle sees it via `import.meta.env`.
 *
 * `buildStart` fails the build via `this.error(...)` on an invalid configuration -- this is a REAL
 * Vite build guard, not just the npm `prebuild` script (`validate-auth-config.mjs`), so `vite
 * build` invoked directly (bypassing `npm run build`'s `prebuild` hook) cannot skip it.
 *
 * The resolved mode also drives the immutable `drivethru-auth-mode` marker tag baked into
 * `index.html`, so the marker can never disagree with what was actually enforced -- previously the
 * marker read the raw `VITE_AUTH_MODE` value directly (via `process.env`, not `loadEnv`), so it
 * could read `Development` even for an unset-mode build that this guard now refuses to allow.
 */
function authModePlugin(isDevServer: boolean): Plugin {
    let resolvedMode: "entra" | "development" = "development";

    return {
        name: "drivethru-auth-mode",
        config(config, { mode }) {
            // Skip under Vitest: this shared vite.config.ts also configures the test runner (no
            // separate vitest.config.ts), and the guard is for `vite build`/`vite dev` -- unit
            // tests exercise `resolveAuthMode` directly (`authMode.test.ts`) with their own
            // explicit env fixtures, and a stray single Entra env var in a dev machine's shell
            // should not fail an unrelated `npm test` run.
            if (process.env.VITEST) return;

            const env = loadEnv(mode, config.envDir ?? process.cwd(), "VITE_");
            try {
                resolvedMode = resolveAuthMode({
                    VITE_AUTH_MODE: env.VITE_AUTH_MODE,
                    VITE_ENTRA_TENANT_ID: env.VITE_ENTRA_TENANT_ID,
                    VITE_ENTRA_CLIENT_ID: env.VITE_ENTRA_CLIENT_ID,
                    DEV: isDevServer,
                }).mode;
            } catch (error) {
                // Vite's `config` hook runs before `buildStart`'s plugin context (with `this.error`)
                // is available -- throwing here still fails `vite build`/`vite dev` immediately, with
                // the same message, before any module is transformed.
                throw error instanceof Error ? error : new Error(String(error));
            }
        },
        transformIndexHtml(html) {
            const tag = renderAuthModeMetaTag(resolvedMode);
            return html.replace("</head>", `    ${tag}\n  </head>`);
        }
    };
}

// https://vitejs.dev/config/
export default defineConfig(({ command }) => ({
    plugins: [react(), authModePlugin(command === "serve")],
    build: {
        outDir: "../backend/static",
        emptyOutDir: true,
        sourcemap: false,
        chunkSizeWarningLimit: 1000,
        target: "es2020",
        minify: "esbuild",
        cssMinify: true,
        rollupOptions: {
            output: {
                // Function form (not the object form) so that subpath imports
                // like `react-dom/client` and the `react/jsx-runtime` used by
                // the automatic JSX transform are matched by resolved path.
                // The object form only matched the bare specifiers, so React
                // fell through into the entry chunk and react-vendor built empty.
                manualChunks(id) {
                    if (!id.includes("node_modules")) return;
                    if (/[\\/]node_modules[\\/](react|react-dom|scheduler)[\\/]/.test(id)) return "react-vendor";
                    if (/[\\/]node_modules[\\/]@radix-ui[\\/]/.test(id)) return "ui-vendor";
                    if (/[\\/]node_modules[\\/](i18next|react-i18next|i18next-browser-languagedetector|i18next-http-backend)[\\/]/.test(id)) return "i18n";
                    if (/[\\/]node_modules[\\/](framer-motion|motion-dom|motion-utils)[\\/]/.test(id)) return "motion";
                },
                assetFileNames: "assets/[name]-[hash][extname]",
                chunkFileNames: "js/[name]-[hash].js",
                entryFileNames: "js/[name]-[hash].js"
            }
        }
    },
    resolve: {
        preserveSymlinks: true,
        alias: {
            "@": path.resolve(__dirname, "./src")
        }
    },
    server: {
        proxy: {
            "/realtime": {
                target: "ws://localhost:8000",
                ws: true,
                // #34: rewriteWsOrigin alone still forwarded Origin: ws://localhost:8000 with
                // Host: localhost:5173 (mismatched), so the backend's origin check 403'd every
                // dev-mode connection. changeOrigin rewrites the Host header (and Origin) to match
                // the proxy target, so both match and the backend's same-origin check passes.
                changeOrigin: true,
                rewriteWsOrigin: true
            },
            // #80 F1/F3/F4: PersonaProvider/MenuPanel now fetch these REST routes (already live
            // on the python backend, design doc section 5.2) instead of reading bundled frontend
            // copies, so local `npm run dev` needs to reach the backend for them too.
            "/api": { target: "http://localhost:8000", changeOrigin: true },
            "/personas": { target: "http://localhost:8000", changeOrigin: true }
        }
    },
    test: {
        globals: true,
        environment: "jsdom",
        setupFiles: "./src/test/setup.ts",
        css: true,
        coverage: {
            provider: "v8",
            reporter: ["text", "lcov"],
            include: ["src/components/ui/order-summary.tsx", "src/components/ui/status-message.tsx"]
        }
    }
}));
