# Sponsor — compile-time-gated code

This directory contains code that is compiled only when the MSBuild property
`SponsorBuild=true` is set (via `-p:SponsorBuild=true` or the `SPONSOR_BUILD`
environment variable read by `Ghostty.csproj`).

In default / OSS / user-built builds, every file under this directory is
excluded from compilation by a `<Compile Remove="Sponsor/**/*.cs" />` rule in
`windows/Ghostty/Ghostty.csproj`. The sponsor-gated surface is therefore
visible in source but absent from the output assembly.

## Activation

OAuth + DPAPI-encrypted JWT cache. Production impl is
`Ghostty.Core.Sponsor.Auth.OAuthTokenProvider` (replaces the dev-only
`EnvTokenProvider`); Windows-concrete glue lives in
`Ghostty.Core.Sponsor.Auth.*` under platform-gated classes
(`DpapiJwtStore`, `HttpLoopbackListener`). Palette entries
`wintty.signIn` and `wintty.signOut` are the only user-facing surface
in D.2.5; first-run dialog and settings card deferred.

Token claims: `sub`, `login`, `tier_cents`, `channel_allow`,
`default_channel`, `jti`, `exp`. Client never verifies the signature;
Worker is authority.

Refresh: proactive at `exp - 24h`, reactive on Worker 401, transient
retries every 10 minutes. Sign-out revokes best-effort then deletes the
local blob.
