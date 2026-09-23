# Validation — French Originals

## Version 1.0.2.0 — 2026-09-23 UTC

- **39/39 safety and reporting tests passed**, using the official Jellyfin 12.0.0 entity types and .NET SDK 10.0.401. The five added image regressions cover order-independent fingerprints without mutation, successful verified saves after image reordering, repeat-run idempotence, retained legacy completion history, and detection of real artwork changes. One case independently exercises removal, addition, replacement, type change, case change, and duplicate addition; each still stops after one write without a completion marker.
- Reproduction: the same image `(Type, Path)` entries in a different sequence produce different fingerprints with the 1.0.1 algorithm, while 1.0.2 accepts them. No actual image list is reordered by the comparison.
- DLL SHA-256: `00140d5b9f01b4d29ad8d0a9aac8c3f3e440536dcf10bdca92e4d84ffc1183b9`.
- The configuration page is unchanged from the tested 1.0.1 build. Full native-server integration was not rerun for this release.

Upstream evidence from Jellyfin v12.1: [ItemPersistenceService.SaveImagesAsync](https://github.com/jellyfin/jellyfin/blob/v12.1/Jellyfin.Server.Implementations/Item/ItemPersistenceService.cs) deletes/reinserts image records; [BaseItemMapper.MapImageToEntity](https://github.com/jellyfin/jellyfin/blob/v12.1/Jellyfin.Server.Implementations/Item/BaseItemMapper.cs) gives them new random IDs, and mapping reads image enumeration order directly; [RetrieveItem](https://github.com/jellyfin/jellyfin/blob/v12.1/Jellyfin.Server.Implementations/Item/BaseItemRepository.Querying.cs) includes images without an explicit image-order contract. [BaseItem.ValidateImages](https://github.com/jellyfin/jellyfin/blob/v12.1/MediaBrowser.Controller/Entities/BaseItem.cs) can also remove missing local-image references during a normal metadata save; this is a real reference change and still fails verification.

The user's report identifies `Images` but omits its expected/actual lists. These tests prove the ordering correction, not that the reported film's mismatch was exclusively ordering. No production-server changes or live provider checks were performed here.

## Version 1.0.1.0 — 2026-09-23 UTC

- Release assembly compiled with .NET SDK **10.0.401**, targeting **net10.0**, using the locked official Jellyfin **12.0.0** NuGet dependencies. Warnings are treated as errors.
- **34/34 safety and reporting tests passed.** In addition to the original 26 cases below, tests verify hidden unchanged rows in preview/apply, completion tracking without a write, summary-only and language-only reporting, accurate preview labels, store-side verification diagnostics, missing read-back items, and save exceptions. The protected-field failure test now checks exact expected/actual values and the failed report row.
- The jsdom configuration-page check passed, including old unchanged-row suppression on refresh, duplicate-title suppression, and safe text rendering.
- DLL SHA-256: `ef677f0d461655b7bdff4ec7011250052c18e6090580c3ffad4e266cbfe81739`.

The native server tests described below were run for **1.0.0**, not repeated for this update. Version 1.0.1 has not been deployed to the reporting user's server. The real-library verification failure remains unresolved: the old journal contained the proposed values and a protected-field hash but omitted the actual mismatched fields. This update adds that evidence without relaxing checks or claiming a confirmed root-cause fix.

## Archived validation for version 1.0.0.0

Validation date: 2026-09-22 UTC.

### Build

- Compiled Release assembly against the official `Jellyfin.Controller` and `Jellyfin.Model` **12.0.0** NuGet packages, targeting **net10.0**.
- Built with .NET SDK **10.0.401** on Linux x64. The plugin DLL is managed code and has no bundled native dependencies.
- Warnings are treated as errors. Release build and the safety-test executable compiled successfully.
- DLL SHA-256: `15dc35b2702fced2a05249b66dfd62e6360955be53115c7b184d9a1c931e7768`.

### Safety tests

**26/26 passed.** The test executable covers French ISO/regional codes; exclusion of unknown and non-French originals; parent/child selection and overrides; global and individual locks; provider identity mismatch; preservation of existing text when provider fields are blank; preview with zero writes; idempotence; already-French items; the batch guard; active scans; concurrent edits; failed and partial lookups; fairness across batches; cancellation before and after a commit; overlapping runs; corrupt completion files; unavailable audit storage; verification failure; audit ordering; language-only mode; and invalid settings.

These tests use the actual Jellyfin entity types and in-memory adapters for controlled failure cases. Run with:

```sh
dotnet run --project tests/FrenchOriginals.Tests -c Release
```

### Native integration

The same release DLL passed integration tests in separate disposable servers built from the official **v12.0** and **v12.1** Jellyfin source tags. No changes were made to Jellyfin's source to run the tests. The supported `--nonetchange` option was used because this environment does not expose network-interface change notifications.

Each fixture library contained two movies (French and English originals), a French series, one season, and two episodes. One episode had an explicit English metadata preference. A separate **test-only** provider returned deterministic English/French metadata so the tests did not depend on external provider uptime or real media.

Verified:

- Plugin loads as Active and registers its native scheduled task with no default triggers.
- Jellyfin serves the embedded configuration page (explicitly checked on 12.1).
- The report endpoint rejects unauthenticated requests.
- Preview selects four eligible items and makes no library metadata changes.
- Apply saves and reads back French preferences, titles, and summaries through the real Jellyfin library/provider APIs.
- The English-original movie and explicit English episode override remain unchanged; the movie rating is preserved.
- The next run selects no completed items and performs no additional writes.

To reproduce using a locally built Jellyfin server, first build the release plugin and the fixture, then supply a **new empty directory**:

```sh
dotnet build src/Jellyfin.Plugin.FrenchOriginals -c Release
dotnet build tests/SmokeProvider -c Release
python3 tests/run-smoke.py --dotnet /absolute/path/to/dotnet \
  --server /absolute/path/to/jellyfin.dll --work /absolute/path/to/new-empty-directory
```

The fixture also requires `ffmpeg` on PATH. Never install the test provider on a real server. The release ZIP excludes the fixture and every test assembly.

### Configuration page

A jsdom test passed for loading camelCase API results, preview defaults, selecting libraries, saving numeric settings, starting the native task, and rendering report titles as text without executing injected HTML. Run `npm ci --prefix tests`, then `npm test --prefix tests`.

This is a DOM behavior check, not a visual check inside every Jellyfin client. A full browser rendering check was not completed because a browser binary was unavailable in this build environment.

### Practical limits at the original build

The plugin has not been installed on the user's Unraid server or tested against that library or its third-party plugins. Live TMDb/TVDb/etc. translation availability was not tested. The matching checks validate identity, not the natural language of arbitrary returned prose. Preview a small batch on the destination server first. Future Jellyfin releases and unusual provider behavior may require another compatibility check.
