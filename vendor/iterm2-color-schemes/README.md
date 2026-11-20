The Ghostty colour schemes from
https://github.com/mbadolato/iTerm2-Color-Schemes, installed as the bundled
themes (`share/ghostty/themes`) by `src/build/GhosttyResources.zig`.

`ghostty/` holds the theme files, unmodified, from the `ghostty-themes.tgz`
asset of the release named below. They are copied here rather than fetched by
the build because that archive has theme names with a colon in them, which are
not valid Windows file names, so it cannot be unpacked on Windows.

Two sets of themes in that release are left out:

- The 18 that upstream's `licenses.yml` marks `nonfree`, meaning the licence
  does not allow redistribution: Espresso, Grass, Homebrew, Man Page, Monokai
  Classic, Monokai Pro, Monokai Pro Light, Monokai Pro Light Sun, Monokai Pro
  Machine, Monokai Pro Octagon, Monokai Pro Ristretto, Monokai Pro Spectrum,
  Novel, Ocean, Pro, Red Sands, Terminal Basic, Terminal Basic Dark.
- The 3 with a colon in the name: `Analog Heartbeat: Phosphor`,
  `Neon Raincheck: After Hours`, `Paper Crane: Midnight Ink`.

`LICENSE` (MIT) covers the collection, and says itself that each theme stays
under its author's own licence. `licenses.yml` is upstream's record of those
per-theme licences and `CREDITS.md` its list of authors; both are copied from
the same release, unmodified, so they still list the themes left out here.
Upstream describes `licenses.yml` as still to be checked by its maintainers,
and it lists most themes as `unknown`.

To update: take `ghostty-themes.tgz` from a newer release, apply the same two
exclusions against that release's `licenses.yml`, and replace the four things
in this directory together.

Release: release-20261102-150615-3d8a6a1
Commit: 3d8a6a1a983d7fe721aa6cb9a5b98066cd74303a
`ghostty-themes.tgz` sha256: b61b297e4245e0a5e46ac1e8d92ec8f7390d4394db4bafc93cfa3209f215435e
