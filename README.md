<!-- LOGO -->
<h1 align="center">
  <img src="https://github.com/user-attachments/assets/eed9e6f8-dfc5-4e29-b3bb-53ca39cf6aeb" alt="Wintty logo" width="128" />
  <br>Wintty
</h1>
<p align="center">
  A native Windows terminal emulator on the Ghostty core.
  <br />
  A DirectX 12 (GPU) renderer and a WinUI 3 shell on <code>libghostty</code>,
  the Zig emulation core proven by <a href="https://ghostty.org">Ghostty</a>.
  <br />
  <a href="https://wintty.io/download?utm_source=gh_readme&utm_content=nav">Download</a>
  ·
  <a href="https://wintty.io/docs?utm_source=gh_readme&utm_content=nav">Documentation</a>
  ·
  <a href="#build-from-source">Build from source</a>
  ·
  <a href="CONTRIBUTING.md">Contributing</a>
  ·
  <a href="HACKING.md">Developing</a>
</p>

## Get Wintty

**Sponsored build (recommended).** [Sponsor any amount](https://github.com/sponsors/deblasis)
to unlock the signed, auto-updating build, then
[sign in with GitHub](https://wintty.io/download?utm_source=gh_readme&utm_content=get-wintty)
and download it. Any sponsorship gets you the installer, automatic in-app
updates, both `stable` and `tip` channels, and the sponsors-only Discord
channel. **Pro is $9.99 a year** and adds more; the full breakdown is on the
[tiers page](https://wintty.io/docs/install/tiers?utm_source=gh_readme).

Requires Windows 10 (1809+) or Windows 11, x64, and a DirectX 12
feature-level-12.0 GPU. There is no software-renderer fallback, so VMs without
GPU passthrough and Remote Desktop sessions will not start it. The installer
is signed; SmartScreen may still show "Windows protected your PC" on first
run of a newly published build (choose More info, then Run anyway).

### Build from source (free and open source, always; MIT)

Clone, `just`, done:

```bash
git clone https://github.com/deblasis/wintty && cd wintty
just              # tests + build the DLL
just build-win    # the app
```

Prerequisites, Windows 10 (1809+) x64: [Zig](https://ziglang.org/) 0.16 or
newer (floor set by `minimum_zig_version` in `build.zig.zon`),
[Just](https://github.com/casey/just), [PowerShell 7](https://aka.ms/powershell),
and the [.NET 10 SDK](https://dotnet.microsoft.com/) (version pinned in
`global.json`). `just build-win` is a Windows-only recipe. Why Just, and how
merges are gated: [windows/docs/tooling.md](windows/docs/tooling.md).

> [!WARNING]
> Development moves fast, and there is no always-on hosted CI on this branch:
> merges are gated on a green local `just signoff` run against the exact
> commit, so the occasional bad commit can land between batches. For a
> calmer ride, build a tagged source snapshot instead of the branch tip
> (browse tags on the releases page).

> [!IMPORTANT]
> ### An independent soft fork of Ghostty
>
> <p align="center">🍬🍴</p>
>
> Wintty is an independent project, not affiliated with or endorsed by the
> Ghostty project or Mitchell Hashimoto. It is a soft fork focused on
> bringing Ghostty to Windows: the `windows` branch is the default and is
> rebased on upstream `main` daily. 21 of this fork's build and CI PRs were
> merged upstream before the project continued here; upstream is not
> planning Windows for now, so this is where Windows lives. It started as a
> public build-in-public effort and it is a daily driver today.

## What you get

- Real terminal emulation on the Zig core Ghostty ships, Kitty graphics
  protocol included (the Windows transport for it is fork work), rendered
  through DirectX 12 with DXGI and DirectComposition (fork work)
- A native WinUI 3 shell: vertical tabs, splits, quick terminal, command
  palette, full settings UI, profiles with shell auto-discovery, session
  restore
- Windows integration: Mica / Acrylic / Crystal backdrops, toast notifications,
  jump lists, taskbar progress, High Contrast support, single instance
- Shell support out of the box: pwsh, cmd, WSL distros, MSYS2
- A thin C# layer over `ghostty.dll` via P/Invoke, same architecture as macOS
  where Swift wraps the same core

The full per-tier feature matrix:
[Wintty tiers and features](https://wintty.io/docs/install/tiers?utm_source=gh_readme).

## Architecture in one paragraph

`libghostty` keeps all terminal emulation in Zig. The Windows app wraps it in
C# (WinUI 3) for windowing, input, and platform integration. The DX12 renderer
supports three surface modes at the library level, HWND, SwapChainPanel
(composition), and shared texture, so embedders pick whichever fits their
host. No compile-time flags; the device picks the path from what the caller
provides. Note that upstream's libghostty C API is still young and unversioned,
so embedders should expect some churn. .NET examples live in
[libghostty-dotnet](https://github.com/deblasis/libghostty-dotnet).

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
If you hit a crash and want it fixed, open an issue; the app can also produce
a support dump that you can review in full before choosing to send it.
Details in the [privacy notice](https://wintty.io/privacy).

## License

MIT, inherited from Ghostty. See [LICENSE](LICENSE). The sponsored binaries
are a convenience, not a license change: the source builds the same thing.

## Upstream

Wintty is built on [Ghostty](https://ghostty.org) by
[Mitchell Hashimoto](https://github.com/mitchellh) and contributors. All
credit for the terminal emulation core, and much of the speed, is theirs.
Anything not Windows-specific (macOS, Linux/GTK, the full libghostty story)
lives upstream: [ghostty.org/docs](https://ghostty.org/docs).
