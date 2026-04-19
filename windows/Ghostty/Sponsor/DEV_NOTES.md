# Sponsor - developer notes

Smoke procedures for sponsor-gated features across Plan D phases.

## D.2.5 - OAuth sign-in smoke (maintainer only)

Requires: a GitHub account linked to deblasis/wintty sponsorship, or a
self-sponsor test account.

1. Delete `%LocalAppData%\Ghostty\auth.bin` if present.
2. `Remove-Item Env:WINTTY_DEV_JWT -ErrorAction SilentlyContinue`.
3. `just run-win` (SPONSOR_BUILD=true flavor).
4. Command palette -> "Sign in to wintty".
5. Default browser opens to `https://api.wintty.io/auth/github/start?...`.
6. Authorize on GitHub.
7. Redirected to `http://127.0.0.1:<port>/`, page says "You can close this window".
8. App pill transitions from NoToken-silent to Idle.
9. Verify `auth.bin` exists under `%LocalAppData%\Ghostty\`, non-empty, is binary (not JSON - DPAPI envelope).
10. Trigger update check via palette -> manifest call succeeds with bearer.
11. Palette -> "Sign out of wintty".
12. Verify `auth.bin` deleted, pill goes dormant, next update check emits NoToken.
