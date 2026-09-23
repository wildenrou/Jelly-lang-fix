using Jellyfin.Plugin.FrenchOriginals.Configuration;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.FrenchOriginals;

public sealed class MetadataTaskService(IItemStore items, ITextLookup lookup, StateStore state,
    ILogger<MetadataTaskService> logger)
{
    private readonly SemaphoreSlim gate = new(1, 1);

    public async Task Run(RunOptions options, IProgress<double> progress, CancellationToken token)
    {
        if (!await gate.WaitAsync(0, token).ConfigureAwait(false))
            throw new InvalidOperationException("French Originals is already running.");
        var report = new RunReport { PreviewOnly = options.PreviewOnly };
        Journal? journal = null;
        try
        {
            if (items.Busy) throw new InvalidOperationException("Jellyfin is scanning or refreshing metadata. Run after that work finishes.");
            var completed = state.LoadCompleted();
            journal = state.OpenJournal(options.RetentionDays);
            journal.Write("run-start", new { options.PreviewOnly, options.UpdateText, options.IncludeChildren, options.ItemLimit, options.MaxChanges });
            state.SaveReport(report);
            List<WorkItem> candidates = [];
            // Complete inventory and validate exact IDs before changing a single library item.
            foreach (var work in items.Inventory(options, token))
            {
                token.ThrowIfCancellationRequested();
                report.Inspected++;
                var item = items.Read(work.Id);
                var parent = work.SeriesId.HasValue ? items.Read(work.SeriesId.Value) : null;
                if (item is null || !Rules.Eligible(item, parent) || (!options.UpdateText && Rules.IsChild(item)))
                { report.Skipped++; continue; }
                if ((!options.UpdateText && Rules.IsFrench(item.PreferredMetadataLanguage)) ||
                    (!options.RecheckCompleted && completed.TryGetValue(work.Id, out var entry) &&
                    entry.Fingerprint == Rules.CompletionKey(item, options.UpdateText)))
                { report.AlreadyComplete++; continue; }
                candidates.Add(work);
            }
            // Failed lookups move to the back on the next run, so they cannot starve later items.
            var ordered = candidates.OrderBy(w => completed.GetValueOrDefault(w.Id)?.LastAttemptUtc ?? DateTime.MinValue).ThenBy(w => w.Id);
            var selected = (options.ItemLimit > 0 ? ordered.Take(options.ItemLimit) : ordered).ToList();
            report.Candidates = candidates.Count;
            report.Selected = selected.Count;
            if (!options.PreviewOnly && selected.Count > options.MaxChanges)
                throw new InvalidOperationException($"Selected {selected.Count} items, exceeding the guard of {options.MaxChanges}. Preview and lower the item limit or raise the guard. No metadata changed.");
            for (var index = 0; index < selected.Count; index++)
            {
                token.ThrowIfCancellationRequested();
                if (items.Busy) throw new InvalidOperationException("A library scan or metadata refresh started. Completed changes are recorded; rerun later.");
                var work = selected[index];
                var item = items.Read(work.Id);
                var parent = work.SeriesId.HasValue ? items.Read(work.SeriesId.Value) : null;
                if (item is null || !Rules.Eligible(item, parent)) { report.Skipped++; continue; }
                var before = Rules.Snapshot(item);
                LocalizedText? text = null;
                if (options.UpdateText)
                {
                    text = await lookup.Fetch(item, options.ProviderTimeoutSeconds, token).ConfigureAwait(false);
                    if (text is null)
                    {
                        report.Skipped++;
                        var row = new ReportRow(item.Id, item.Name, item.GetType().Name, "Skipped",
                            Detail: "No usable French response with a matching existing provider ID. Will retry on a later run.");
                        report.Add(row);
                        journal.Write("lookup-skipped", row);
                        if (!options.PreviewOnly)
                        {
                            completed[item.Id] = new(null, DateTime.UtcNow);
                            state.SaveCompleted(completed);
                        }
                        progress.Report(100d * (index + 1) / selected.Count);
                        state.SaveReport(report);
                        if (options.DelayMilliseconds > 0) await Task.Delay(options.DelayMilliseconds, token).ConfigureAwait(false);
                        continue;
                    }
                }
                token.ThrowIfCancellationRequested();
                // Recheck both the item and French parent after a potentially slow provider call.
                var fresh = items.Read(work.Id);
                var freshParent = work.SeriesId.HasValue ? items.Read(work.SeriesId.Value) : null;
                if (fresh is null || Rules.Snapshot(fresh) != before || !Rules.Eligible(fresh, freshParent))
                {
                    report.Skipped++;
                    report.Add(new(item.Id, item.Name, item.GetType().Name, "Skipped", Detail: "Changed while fetching metadata."));
                    continue;
                }
                var change = Rules.Plan(fresh, text);
                var differs = change.Differs(fresh);
                var action = (options.PreviewOnly ? "Would update " : "Updated ") + change.ChangedFields(fresh);
                var detail = text is null ? "Language preference only" : $"Provider: {text.Provider}";
                var complete = text is null ||
                    ((!string.IsNullOrWhiteSpace(text.Name) || fresh.LockedFields.Contains(MetadataField.Name)) &&
                     (!string.IsNullOrWhiteSpace(text.Overview) || fresh.LockedFields.Contains(MetadataField.Overview)));
                if (!complete) detail += "; incomplete text, will retry later";
                var reportRow = new ReportRow(fresh.Id, fresh.Name, fresh.GetType().Name, action,
                    change.Name != fresh.Name ? change.Name : null, detail);
                if (!differs) report.NoChangeNeeded++;
                if (options.PreviewOnly)
                    journal.Write("preview", new { Before = before, Proposed = change, Provider = text?.Provider });
                else
                {
                    if (differs)
                    {
                        // A flush failure here prevents the corresponding library write.
                        var protectedBefore = Rules.ProtectedMetadata(fresh);
                        journal.Write("prepared", new { Before = before, ProtectedBefore = protectedBefore, Proposed = change, Provider = text?.Provider });
                        MediaBrowser.Controller.Entities.BaseItem? after;
                        try
                        {
                            await items.Save(fresh, change, token).ConfigureAwait(false);
                            after = items.Read(work.Id);
                            Rules.Verify(before, protectedBefore, change, after);
                        }
                        catch (MetadataVerificationException ex)
                        {
                            report.Add(reportRow with { Action = "Verification failed", NewTitle = null, Detail = ex.Message });
                            journal.Write("verification-failed", ex.Failure);
                            throw;
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            report.Add(reportRow with { Action = "Update failed", NewTitle = null, Detail = ex.Message });
                            journal.Write("update-failed", new { Before = before, Proposed = change, Error = ex.Message, ErrorType = ex.GetType().Name });
                            throw;
                        }
                        // Verify rejects a missing item before reaching this point.
                        if (after is null) throw new InvalidOperationException("Item disappeared after saving.");
                        journal.Write("verified", new { Before = before, After = Rules.Snapshot(after) });
                        report.Changed++;
                        fresh = after;
                    }
                    completed[work.Id] = new(complete ? Rules.CompletionKey(fresh, options.UpdateText) : null, DateTime.UtcNow);
                    state.SaveCompleted(completed);
                }
                if (differs) report.Add(reportRow);
                state.SaveReport(report);
                progress.Report(100d * (index + 1) / Math.Max(1, selected.Count));
                if (options.DelayMilliseconds > 0) await Task.Delay(options.DelayMilliseconds, token).ConfigureAwait(false);
            }
            report.Status = options.PreviewOnly ? "Preview complete" : "Completed";
            report.Message = $"{report.Candidates} candidates; {report.Selected} selected; {report.Changed} changed; {report.Skipped} skipped; {report.AlreadyComplete} already complete; {report.NoChangeNeeded} unchanged (hidden).";
            progress.Report(100);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            report.Status = "Cancelled";
            report.Message = "Completed updates are recorded. Run again to continue.";
            throw;
        }
        catch (Exception ex)
        {
            report.Status = "Failed";
            report.Message = ex.Message;
            logger.LogError(ex, "French Originals stopped. See its last-run report and audit journal.");
            throw;
        }
        finally
        {
            report.FinishedUtc = DateTime.UtcNow;
            try
            {
                journal?.Write("run-end", report);
                state.SaveReport(report);
                logger.LogInformation("French Originals: {Status}. {Message}", report.Status, report.Message);
            }
            finally { journal?.Dispose(); gate.Release(); }
        }
    }
}
