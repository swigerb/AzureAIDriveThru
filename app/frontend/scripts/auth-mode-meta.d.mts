export declare const AUTH_MODE_META_NAME: string;

export declare function normalizeAuthMode(raw: string | undefined | null): 'Entra' | 'Development';

export declare function parseAuthModeMeta(html: string | undefined | null): string | null;

export declare function isProductionEntra(content: string | null | undefined): boolean;

export declare function renderAuthModeMetaTag(rawMode: string | undefined | null): string;
