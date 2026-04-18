# Sponsor update smoke test

End-to-end manual verification of the Plan D.2 client -> `api.wintty.io`
Worker -> R2 chain. Requires a dev-minted JWT with `channel_allow`
covering the `stable` channel. Takes about three minutes.

## Prerequisites

- `deblasis/wintty-release` repo cloned as a sibling (`../wintty-release`).
- Node + npm installed (for the JWT mint script).
- A build with `SPONSOR_BUILD=true`.

## 1. Mint a JWT

From `../wintty-release/worker`:

```powershell
npm install                             # first time only
$env:JWT_SIGNING_SECRET = (op read "op://Wintty/JWT_SIGNING_SECRET/credential")  # or paste by hand
node scripts/smoke-mint-jwt.ts --channels=stable,tip
```

Output: a JWT string. Copy it.

## 2. Configure the env

In a fresh PowerShell window (the one that will launch the app):

```powershell
$env:SPONSOR_BUILD = "true"
$env:WINTTY_DEV_JWT = "<paste JWT here>"
```

## 3. Launch

```powershell
just run-win
```

## 4. Drive the check

Open the command palette (`Ctrl+Shift+P`) and run: `Check for updates (real)`.

Expected: the pill appears in the title bar after ~1 second with "Update
Available: vX.Y.Z" (whatever's in R2 at that moment). Clicking the pill
opens the popover with a version line, a "See what's new" link (may be
empty if the manifest doesn't carry notes yet), and an "Install and
Relaunch" button.

## 5. Download

Click "Install and Relaunch". The ProgressRing ticks; the pill shows
"Downloading X%". On completion the pill flips to
"Restart Required" / "Restart to Finish Updating".

## 6. Restart (expected honest failure)

Click "Restart Now". You should see the popover error state with
"Couldn't apply the update. Try again or reinstall." This is **correct**
for D.2 - the dev checkout isn't a Velopack-installed app, so
`ApplyUpdatesAndRestart` can't do its thing. Plan C ships a real signed
`Setup.exe` which unblocks end-to-end apply validation.

## 7. Cancel

Re-run the check, click "Install and Relaunch", then click
"Cancel download" before it completes. The pill should drop back to
"Update Available: vX.Y.Z" (not Idle).

## 8. Negative paths

- **Invalid JWT:** `$env:WINTTY_DEV_JWT = "garbage"; just run-win` ->
  popover shows "Sponsor session expired. Sign in again."
- **No JWT:** `Remove-Item env:WINTTY_DEV_JWT; just run-win` ->
  "Sign in to check for updates."
- **Revoked channel:** mint a JWT with `--channels=other` ->
  "This channel isn't available for your sponsorship tier."

## Troubleshooting

- **Worker 5xx:** check `wrangler tail` on `wintty-release-preview`.
- **NameResolutionFailure:** DNS for `api.wintty.io`; verify with
  `nslookup api.wintty.io`.
- **Pill doesn't appear:** `SPONSOR_BUILD` wasn't set before
  `just run-win`. Confirm with `$env:SPONSOR_BUILD` in the same shell;
  it must be the string `"true"`.
- **401 despite valid JWT:** the Worker signing key changed. Re-mint
  with the current secret and retry.
