export interface AuthConfigValidationResult {
  readonly ok: boolean;
  readonly error?: string;
}

export function validateEntraIds(
  tenantId: string | undefined,
  clientId: string | undefined,
): AuthConfigValidationResult;

export function validateAuthConfig(
  env: Record<string, string | undefined>,
): AuthConfigValidationResult;
