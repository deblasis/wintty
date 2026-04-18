# Sponsor — compile-time-gated code

This directory contains code that is compiled only when the MSBuild property
`SponsorBuild=true` is set (via `-p:SponsorBuild=true` or the `SPONSOR_BUILD`
environment variable read by `Ghostty.csproj`).

In default / OSS / user-built builds, every file under this directory is
excluded from compilation by a `<Compile Remove="Sponsor/**/*.cs" />` rule in
`windows/Ghostty/Ghostty.csproj`. The sponsor-gated surface is therefore
visible in source but absent from the output assembly.
