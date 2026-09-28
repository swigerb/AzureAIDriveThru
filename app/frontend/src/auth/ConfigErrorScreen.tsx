/**
 * Safe, dependency-light configuration-error screen rendered by `src/index.tsx` when the auth
 * bootstrap fails closed (e.g. an explicit Entra build with missing/placeholder tenant/client
 * configuration) -- ADR-002, design §18.6, issue #145.
 *
 * Intentionally has NO Tailwind/App/MSAL imports and makes NO API or WebSocket calls: it must
 * render safely even if something else in the bootstrap sequence is badly broken. The error text
 * is deliberately generic (no ids, secrets, or PII) so it is safe to show in any environment.
 */
export function ConfigErrorScreen() {
  return (
    <div
      role="alert"
      data-testid="config-error-screen"
      style={{
        minHeight: '100vh',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        padding: '24px',
        background: '#15161a',
        color: '#f5f5f5',
        fontFamily: "'Segoe UI', system-ui, -apple-system, BlinkMacSystemFont, sans-serif",
      }}
    >
      <main
        style={{
          maxWidth: '520px',
          width: '100%',
          background: '#1f2024',
          border: '1px solid #35363b',
          borderRadius: '12px',
          padding: '32px',
          boxShadow: '0 8px 32px rgba(0,0,0,0.4)',
        }}
      >
        <div
          aria-hidden="true"
          style={{
            fontSize: '13px',
            fontWeight: 600,
            letterSpacing: '0.08em',
            textTransform: 'uppercase',
            color: '#c8c9cc',
            marginBottom: '12px',
          }}
        >
          AI Drive-Thru
        </div>
        <h1 style={{ fontSize: '22px', margin: '0 0 12px', fontWeight: 600 }}>Sign-in is unavailable</h1>
        <p style={{ margin: '0 0 16px', lineHeight: 1.5, color: '#e6e6e6' }}>
          This deployment is not configured correctly, so signing in has been disabled to keep your
          data safe. No app data is loaded on this screen.
        </p>
        <p style={{ margin: 0, lineHeight: 1.5, color: '#a3a4a8', fontSize: '14px' }}>
          If you are an administrator, verify the Entra ID configuration for this environment and
          redeploy. If you reached this page unexpectedly, please contact your administrator.
        </p>
      </main>
    </div>
  );
}
