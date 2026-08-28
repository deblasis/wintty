<!-- LOGO -->
<h1>
<p align="center">
  <img src="https://github.com/user-attachments/assets/eed9e6f8-dfc5-4e29-b3bb-53ca39cf6aeb" alt="Logo" width="128" />
  <br><a href="https://wintty.io?utm_source=gh_readme">Wintty</a>
</p>
</h1>
<p align="center">
  Ghostty's speed and features, native on Windows.
  <br />
  A DX12 renderer and a WinUI 3 shell on <code>libghostty</code>, the Zig core
  proven by <a href="https://ghostty.org">Ghostty</a>.
  <br />
  <a href="https://wintty.io/download?utm_source=gh_readme">Download</a>
  ·
  <a href="https://wintty.io/docs?utm_source=gh_readme">Documentation</a>
  ·
  <a href="#build-from-source">Build from source</a>
  ·
  <a href="CONTRIBUTING.md">Contributing</a>
</p>

> [!IMPORTANT]
> ## Wintty (Ghostty R&D Soft Fork)
>
> <p align="center">🍬🍴</p>
>
> This is a soft fork focused on bringing Ghostty to Windows.
> The `windows` branch is the default and contains all Windows-specific work
> rebased on top of upstream `main`, which is synced daily.
>
> 17 PRs of this fork's build and CI work were merged upstream before the
> project continued here; upstream doesn't have capacity for Windows right
> now, so this is where Windows lives. It started as a public build-in-public
> effort (the old progress meters live in the git history) and it is a daily
> driver today.

## Get Wintty

**Sponsored build (recommended).** [Sponsor any amount](https://github.com/sponsors/deblasis),
then [sign in with GitHub](https://wintty.io/download) and download the signed
installer. You get automatic in-app updates, both `stable` and `tip` channels,
and the sponsors-only Discord channel. Pro adds more; see the
[tiers page](https://wintty.io/docs/install/tiers).

**Build from source (free, always).** Clone, `just`, done:

```bash
git clone https://github.com/deblasis/wintty && cd wintty
just              # tests + build the DLL
just build-win    # the app
```

You need [Zig](https://ziglang.org/) (version pinned in `build.zig.zon`) and
[Just](https://github.com/casey/just) (why Just, and how CI works:
[windows/docs/tooling.md](windows/docs/tooling.md)). Full instructions:
[Build from Source](https://wintty.io/docs/install/build).

> [!WARNING]
> Development goes very fast. Depending on when you build, you might hit
> stability issues: CI runs in batches a few times a day, so a bad commit can
> fall through the cracks and you might just be unlucky. Point at a tag for a
> calmer ride.

## What you get

- Real terminal emulation on the Zig core Ghostty ships (Kitty graphics
  protocol included), rendered through DirectX 12 with DXGI and DirectComposition
- A native WinUI 3 shell: vertical tabs, splits, quick terminal, command palette,
  full settings UI, profiles with shell auto-discovery, session restore
- Windows integration: Mica / Acrylic / Crystal backdrops, toast notifications,
  jump lists, taskbar progress, High Contrast support, single instance
- Shell support out of the box: pwsh, cmd, WSL distros, MSYS2
- A thin C# layer over `libghostty.dll` via P/Invoke, same architecture as
  macOS where Swift wraps the same core

The full feature matrix with per-tier availability:
[tiers page](https://wintty.io/docs/install/tiers).

## Architecture in one paragraph

`libghostty` keeps all terminal emulation in Zig. The Windows app wraps it in
C# (WinUI 3) for windowing, input, and platform integration. The DX12 renderer
supports three surface modes at the library level, HWND, SwapChainPanel
(composition), and shared texture, so embedders pick whichever fits their host.
No compile-time flags; the device picks the path from what the caller provides.
.NET examples live in [libghostty-dotnet](https://github.com/deblasis/libghostty-dotnet).

## Roadmap

Renderer throughput next: scroll optimization, adaptive presentation, waitable
swap chains ([#93](https://github.com/deblasis/wintty/issues/93), [#94](https://github.com/deblasis/wintty/issues/94)).
Then the native-integration gaps: system tray, "Open Terminal Here", default
terminal handoff, multi-window ([#81](https://github.com/deblasis/wintty/issues/81)).
The full issue tracker is the real roadmap.

## Sponsors

**To everyone who has kindly sponsored this work: THANK YOU. 🙏** You are why
this ships. Perks are live: signed binaries, automatic updates, the Discord
channel, and the Pro tier. [Join them](https://github.com/sponsors/deblasis)
or [reach out](https://x.com/polyMatto).

## Crash reports

Wintty captures crashes locally and never sends anything anywhere on its own.
If you hit a crash and want it fixed, open an issue; a support dump you can
review before sending is available on request. Details in the
[privacy notice](https://wintty.io/privacy).

## Upstream

Wintty is built on [Ghostty](https://ghostty.org) by
[Mitchell Hashimoto](https://github.com/mitchellh) and contributors. All
credit for the terminal emulation core, and much of the speed, is theirs.
Anything not Windows-specific (macOS, Linux/GTK, the full libghostty story)
lives upstream: [ghostty.org/docs](https://ghostty.org/docs).
