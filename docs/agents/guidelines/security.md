# Security patterns

Adds no rules. Links the root hard rules and points at the code that implements them.

## Secrets and credentials

- Root: no secrets in git, no default credentials, UserAdmin prints a password once, gitleaks stop-and-report.
- Agents don't read `.env` (except `.env.example`).
- Local gitleaks (same pin as `ci.yml` `secrets`):

```
docker run --rm -v "$PWD":/repo zricethezav/gitleaks:v8.30.1@sha256:c00b6bd0aeb3071cbcb79009cb16a60dd9e0a7c60e2be9ab65d25e6bc8abbb7f git /repo --config /repo/.gitleaks.toml --redact --no-banner -v --log-opts=origin/main..HEAD
```

## Session

- `__Host-` Secure cookie (`AuthSetup.CookieName`).
- `X-XSRF-TOKEN` checked by `Auth/AntiforgeryFilter.cs` on `/api`.
- Tests use an https base address so the Secure cookie is sent.

## Headers and surfaces

- `Hardening/SecurityHeaders.cs`. HSTS outside Development.
- Swagger is admin-only behind `SWAGGER_ENABLED`.
- CSV formula-injection defence in `Csv.Escape`.
- Rate limits have no off switch (root free-tier rule).
- Entitlements follow the root E7 rule.
