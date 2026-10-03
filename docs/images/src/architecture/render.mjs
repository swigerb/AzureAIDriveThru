// Renders arch.html to ../../azure-ai-drive-thru-architecture.png (3840x2160).
// Usage from this folder: npm i -D playwright && npx playwright install chromium && node render.mjs
import { chromium } from 'playwright';
import { fileURLToPath, pathToFileURL } from 'node:url';
import path from 'node:path';

const here = path.dirname(fileURLToPath(import.meta.url));
const browser = await chromium.launch();
const page = await browser.newPage({ viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 2 });
await page.goto(pathToFileURL(path.join(here, 'arch.html')).href);
await page.waitForTimeout(800);
await page.screenshot({ path: path.join(here, '..', '..', 'azure-ai-drive-thru-architecture.png') });
await browser.close();
