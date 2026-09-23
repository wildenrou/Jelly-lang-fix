# Changes

## 1.0.3.0 — 2026-09-23 — beta

- Check local artwork before metadata saves. Missing or inaccessible image files now cause a clear, retryable item skip, while the rest of the batch continues.
- Check during preview and again in the Jellyfin adapter before any cached text assignment or save. This prevents Jellyfin's normal missing-file cleanup from being invoked for affected items.
- Record affected filenames in the report and full paths in `artwork-unavailable` audit entries. Skipped items rotate behind unattempted items and can update after artwork repair.
- Keep already-matching items hidden and preserve completion history. Unexpected image changes after a save still fail strict verification.
- Reproduce the reported eight-to-six image-reference pattern with Jellyfin's own cleanup method; add tests for the production adapter's zero-write guard, continued batches, previews, retries, and file-disappearance timing.

## 1.0.2.0 — 2026-09-23 — beta

- Fix false `Images` verification and concurrent-edit failures caused solely by image-list ordering. Jellyfin recreates image row IDs during saves and does not guarantee their read-back order.
- Compare the same image types and paths in a stable order without mutating the item's artwork. Missing, added, replaced, retyped, case-changed, or duplicate-count changes still stop the batch.
- Accept existing completion fingerprints, preserving progress across the upgrade. New completion fingerprints remain stable across image-order changes.
- Retain hidden unchanged report rows, clear summary/language labels, and detailed failure journals.

The image-order failure is reproduced and covered by regression tests. The reporting user's exact expected/actual image lists are still needed to distinguish that case from missing-file cleanup or another actual artwork change on their server.

## 1.0.1.0 — 2026-09-23 — diagnostic beta

- Hide items that need no changes from preview and apply reports. Report refresh also filters unchanged rows from older saved reports.
- Show a new title only when the title changes. Label summary and language preference updates explicitly.
- Record exact expected/actual field differences when a stored item fails verification, and include the failed item in the report.
- Capture protected-field values in the pre-write audit record. Retain the existing strict verification and immediate batch stop.
- Publish the source, Jellyfin repository catalog, installation downloads, and original French Originals logo.
- Correct the package's license label to match the bundled GPL version 2 text.

Known issue: a real-library verification mismatch reported with version 1.0.0 could not be diagnosed from its hash-only journal. This version supplies the missing evidence; the underlying cause is not yet confirmed. It is not a verified fix for that server mismatch.

## 1.0.0.0 — 2026-09-22

- Initial native Jellyfin 12 plugin with French-original selection, provider ID matching, preview, scope and batch controls, completion tracking, audit journaling, and scheduled/on-demand execution.
