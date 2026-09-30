"""Issue #143/ADR-002: verifies a FakeEntraIssuer-minted JWT with stock PyJWT.

Invoked as a subprocess by FakeEntraIssuerPyJwtValidationTests.cs (never imported directly) --
argv is `<issuer-url> <audience> <token>`. Exits 0 and prints the decoded claims as JSON on
success; exits 1 and prints the PyJWT exception to stderr on any validation failure. Deliberately
uses only PyJWT's own stock, public API (PyJWKClient against the *discovery document's own*
jwks_uri, then jwt.decode with full issuer/audience/signature/lifetime validation) -- no
FakeEntraIssuer-specific knowledge at all, so a pass here is a genuine, independent proof that the
fake's discovery document and JWKS are shaped like a real OIDC issuer's, not just shaped like
whatever this repo's own C# validation code happens to expect.
"""

import json
import sys
import urllib.request

import jwt


def main() -> int:
    if len(sys.argv) != 4:
        print(f"usage: {sys.argv[0]} <issuer-url> <audience> <token>", file=sys.stderr)
        return 2

    issuer_url, audience, token = sys.argv[1], sys.argv[2], sys.argv[3]

    try:
        metadata_url = issuer_url.rstrip("/") + "/.well-known/openid-configuration"
        with urllib.request.urlopen(metadata_url, timeout=10) as response:  # noqa: S310 (loopback fake, test-only)
            metadata = json.load(response)

        jwks_client = jwt.PyJWKClient(metadata["jwks_uri"])
        signing_key = jwks_client.get_signing_key_from_jwt(token)

        claims = jwt.decode(
            token,
            signing_key.key,
            algorithms=["RS256"],
            audience=audience,
            issuer=metadata["issuer"],
        )
    except Exception as exc:  # noqa: BLE001 -- deliberately broad: any failure means "invalid", report and exit 1
        print(f"{type(exc).__name__}: {exc}", file=sys.stderr)
        return 1

    print(json.dumps(claims))
    return 0


if __name__ == "__main__":
    sys.exit(main())
