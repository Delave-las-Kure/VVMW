# UFC VizVid Stable

Maintained integration branch: **`ufc/cinema-stable`**. Feature work is qualified
on `ufc/video-mirrors` and then merged here. The GitHub default branch `develop`
and the literal branch `main` are not UFC's integration/release target.

## Current source versus published release

The accepted optional personal source mirrors and source UI from feature commit
`0caf9fd826da77f721b04f11fb68c711b1c7c020` are now integrated here. All package
files match that tested source exactly, including the `1.8.2-cinema.2` candidate
metadata; VPM/release helpers from the stable branch are retained.

This source merge does not publish a package or change the existing VPM listing.
The installation instructions below still describe the immutable published
`1.8.2-ufc.1` release, which does not contain mirrors. Restoring the latest source
requires the exact integration commit, not that older released archive. A future
release needs its own version/tag and verified archive; never replace the old
version's contents.

## Install with VCC / ALCOM

Add this VPM repository:

```text
https://raw.githubusercontent.com/Delave-las-Kure/VVMW/ufc/cinema-stable/VPM/index.json
```

Select **VizVid (UFC Stable)**, package `idv.jlchntoz.vvmw`, version
`1.8.2-ufc.1`. Enable prerelease package visibility to select the UFC version:
the suffix identifies the fork in Semantic Versioning. VPM may describe the
replacement of upstream1.8.2 as a downgrade for this reason.

Keep one VizVid package installation. The upstream package ID and asset GUIDs
are retained for existing scene references. Other VPM dependencies are unchanged.
Use the selected UFC version when restoring a project.

## Release 1.8.2-ufc.1

Upstream: VizVid1.8.2, `0ddb33be520b7a11e614d1d5d13c358d9cf0ec42`.
Release source: `b66bff596973884d25fe2c03776974b8f27f9fab`.
Tag: `ufc-v1.8.2-1`.

The only runtime change is in CalcSyncTime: paused decoder progress is sent
directly rather than divided by playback speed. The playing expression is
unchanged. Package metadata identifies the UFC release. All original package
assets, metadata GUIDs, license and upstream author credit are retained.

The582-file archive is built from exact Git blobs. Its SHA256 is
`da381a90937dc2692cca872abb04ea55f41a3f377066f6646507288c03738af8`.
Two independent output builds produced identical archive bytes.

`Tools/Release/Build-Vpm.ps1` builds the archive and listing. A sparse checkout
may use a local upstream installation as an asset cache; every file is checked
against its committed Git blob, with exact Git content used on mismatch.
The output directory must be new, preserving previous builds.

Upstream updates and feature branches are qualified separately before inclusion
in this branch. Each accepted package receives a new version and immutable tag;
published version contents are not overwritten.
