using System.Text.Json.Nodes;
using Microsoft.Extensions.Options;
using TicketCloner.Api.Adf;
using TicketCloner.Api.Atlassian;
using TicketCloner.Api.Configuration;
using TicketCloner.Api.Contracts;
using TicketCloner.Api.Jira;
using TicketCloner.Api.Mapping;

namespace TicketCloner.Api.Apply;

/// <summary>
/// Turns approved plans into issues on the target.
///
/// Failures are per ticket and never abort the run: tickets are independent,
/// unlike a cherry-pick sequence where one conflict strands everything after
/// it. Every ticket reports what happened so a person knows exactly what is
/// left to finish by hand.
/// </summary>
public sealed class ApplyService(
    SourceIssueReader source,
    TargetIssueWriter writer,
    ExistingCopyFinder existingCopies,
    SprintReader sprints,
    MappingService mapping,
    TargetMetadataReader metadata,
    UserResolver users,
    AdfRewriter adf,
    IOptions<AtlassianOptions> options,
    ILogger<ApplyService> logger)
{
    private TenantOptions Target => options.Value.Target;

    public async Task<ApplyResponse> ApplyAsync(ApplyRequest request, CancellationToken cancellationToken)
    {
        var outcomes = new List<TicketOutcome>();

        // Several children can share one epic. Whatever is found or created for
        // a given source epic is remembered for the rest of the run, so a batch
        // of ten children under one epic makes one epic, not ten.
        var epics = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // One lookup for the run, not one per ticket. The sprint is the same
        // answer every time and a copy run makes a lot of calls already. A
        // chosen sprint wins over "whatever is current": it is the more
        // specific instruction, and the UI only ever sends one of the two.
        var sprint = request.SprintId is { } chosen
            ? await sprints.GetTargetAsync(chosen, cancellationToken)
            : request.AddToActiveSprint
                ? await sprints.GetActiveAsync(cancellationToken)
                : new ActiveSprintResult(null, null);

        foreach (var plan in request.Plans)
        {
            outcomes.Add(await ApplyOneAsync(plan, request, epics, sprint, outcomes, cancellationToken));
        }

        return new ApplyResponse(outcomes);
    }

    private async Task<TicketOutcome> ApplyOneAsync(
        MappingPlan plan,
        ApplyRequest request,
        Dictionary<string, string> epics,
        ActiveSprintResult sprint,
        List<TicketOutcome> outcomes,
        CancellationToken cancellationToken)
    {
        var steps = new List<StepOutcome>();

        try
        {
            // A plan with blockers was never creatable. Refusing here as well as
            // in the UI means a stale plan cannot slip through.
            if (!plan.CanCreate)
            {
                return Skipped(plan, steps, $"The plan has {plan.Blockers.Count} blocker(s): {string.Join(" ", plan.Blockers)}");
            }

            var issue = await source.GetAsync(plan.SourceKey, cancellationToken);
            if (issue is null)
            {
                return Failed(plan, steps, $"The source tenant no longer returns {plan.SourceKey}.");
            }

            var createScreen = await metadata.GetFieldsAsync(plan.TargetIssueTypeId, cancellationToken);

            // The one record of origin, and the duplicate check with it. There
            // used to be a second, key-only field beside it; two fields
            // answering the same question is how they drift apart.
            var sourceUrlField = createScreen.FirstOrDefault(field =>
                field.Name.Equals(Target.SourceUrlFieldName, StringComparison.OrdinalIgnoreCase));

            if (request.SkipDuplicates)
            {
                var duplicate = await CheckForExistingCopyAsync(
                    plan, issue, sourceUrlField, steps, cancellationToken);

                if (duplicate is not null)
                {
                    return duplicate;
                }
            }

            if (sourceUrlField is null)
            {
                steps.Add(new StepOutcome("Source URL", false,
                    $"No field named '{Target.SourceUrlFieldName}' on the target's create screen. " +
                    "The copy will not carry the original's address."));
            }

            // Before the child is created, so it can be parented in the same
            // call rather than created loose and adopted afterwards.
            var epicKey = await ResolveEpicAsync(
                plan, request, epics, outcomes, sourceUrlField, steps, cancellationToken);

            var payload = await BuildFieldsAsync(
                plan, issue, sourceUrlField?.FieldId, epicKey, createScreen, cancellationToken);
            steps.AddRange(payload.Notes);

            // No "Created TGT-1234" step: the outcome already carries TargetKey
            // and TargetUrl, which the results render as the heading and a link,
            // beside a status pill that already says Created.
            var created = await writer.CreateAsync(payload.Fields, cancellationToken);

            await AddToSprintAsync(created, request, sprint, createScreen, steps, cancellationToken);

            await AddRemoteLinkAsync(created, issue, steps, cancellationToken);

            // Attachments go up BEFORE anything that references them is
            // written. A media node points at an attachment id, and the ids it
            // must point at do not exist until the files do - which is why the
            // description and the comments come after this, not before.
            var attachmentIds = request.IncludeAttachments
                ? await AddAttachmentsAsync(created, issue, steps, cancellationToken)
                : new Dictionary<string, string>();

            await RestoreDescriptionMediaAsync(created, issue, payload, attachmentIds, steps, cancellationToken);

            if (request.IncludeComments)
            {
                await AddCommentsAsync(created, issue, payload, attachmentIds, steps, cancellationToken);
            }

            var problems = steps.Count(step => !step.Succeeded);

            return new TicketOutcome(
                plan.SourceKey, created.Key, created.Url,
                problems == 0 ? ApplyStatus.Created : ApplyStatus.CreatedWithProblems,
                problems == 0
                    ? $"Created {created.Key}."
                    : $"Created {created.Key}, but {problems} step(s) need finishing by hand.",
                steps);
        }
        catch (Exception failed)
        {
            // One ticket's failure must not sink the rest of the batch.
            logger.LogError(failed, "Copying {SourceKey} failed", plan.SourceKey);
            return Failed(plan, steps, Describe(failed));
        }
    }

    /// <summary>
    /// The epic this copy belongs under, creating it first if it is missing and
    /// the caller approved it.
    ///
    /// Returns null when there is no epic to sit under, or when one could not
    /// be arranged - in which case the copy is still made, unparented, and the
    /// step says so. One awkward parent must not cost somebody the copy.
    /// </summary>
    private async Task<string?> ResolveEpicAsync(
        MappingPlan plan,
        ApplyRequest request,
        Dictionary<string, string> epics,
        List<TicketOutcome> outcomes,
        TargetField? sourceUrlField,
        List<StepOutcome> steps,
        CancellationToken cancellationToken)
    {
        if (plan.Epic is null)
        {
            return null;
        }

        var sourceEpic = plan.Epic.SourceKey;

        if (epics.TryGetValue(sourceEpic, out var alreadyThisRun))
        {
            steps.Add(new StepOutcome("Epic", true, $"Parented under {alreadyThisRun}."));
            return alreadyThisRun;
        }

        // Looked up again rather than trusting the plan: it has been through
        // the browser, and somebody may have made the epic in the meantime.
        var existing = sourceUrlField is null
            ? null
            : await existingCopies.FindEpicAsync(
                sourceUrlField.Name, sourceUrlField.FieldId,
                sourceEpic, plan.Epic.SourceUrl, plan.Epic.Summary, cancellationToken);

        if (existing is { SummaryMatches: true })
        {
            epics[sourceEpic] = existing.Key;
            steps.Add(new StepOutcome("Epic", true, $"Parented under the existing {existing.Key}."));
            return existing.Key;
        }

        if (request.CreateEpicsFor?.Contains(sourceEpic, StringComparer.OrdinalIgnoreCase) != true)
        {
            steps.Add(new StepOutcome("Epic", false,
                $"{sourceEpic} has no counterpart here and was not selected for creation, " +
                "so this copy has no parent."));
            return null;
        }

        try
        {
            var (epicPlan, error) = await mapping.PreviewAsync(sourceEpic, null, cancellationToken);

            if (epicPlan is null)
            {
                steps.Add(new StepOutcome("Epic", false, $"Could not plan {sourceEpic}: {error}"));
                return null;
            }

            // Cloned exactly the way any ticket is - same mapping rules, same
            // source URL field, same reporting. The duplicate check is off
            // because the epic-specific search above is the stricter one and
            // has already run.
            // An epic is a container, not work in a sprint - so it is created
            // outside one however the run is configured.
            var outcome = await ApplyOneAsync(
                epicPlan,
                request with { SkipDuplicates = false, AddToActiveSprint = false, SprintId = null },
                epics,
                new ActiveSprintResult(null, null),
                outcomes,
                cancellationToken);

            outcomes.Add(outcome);

            if (outcome.TargetKey is null)
            {
                steps.Add(new StepOutcome("Epic", false,
                    $"Creating a copy of {sourceEpic} failed, so this copy has no parent. {outcome.Summary}"));
                return null;
            }

            epics[sourceEpic] = outcome.TargetKey;

            steps.Add(new StepOutcome("Epic", true,
                $"Created {outcome.TargetKey} for {sourceEpic} and parented this under it."));

            return outcome.TargetKey;
        }
        catch (Exception failed)
        {
            logger.LogError(failed, "Creating the epic {SourceEpic} failed", sourceEpic);
            steps.Add(new StepOutcome("Epic", false,
                $"Creating a copy of {sourceEpic} failed, so this copy has no parent. {Describe(failed)}"));
            return null;
        }
    }

    /// <summary>
    /// Has this issue already been copied? A duplicate is one that agrees on
    /// BOTH counts: its External Issue ID holds this source issue's URL, and
    /// its summary is identical.
    ///
    /// Returns a Skipped outcome when it finds one, otherwise null and the run
    /// carries on.
    /// </summary>
    private async Task<TicketOutcome?> CheckForExistingCopyAsync(
        MappingPlan plan,
        SourceIssue issue,
        TargetField? sourceUrlField,
        List<StepOutcome> steps,
        CancellationToken cancellationToken)
    {
        if (sourceUrlField is null)
        {
            steps.Add(new StepOutcome("Duplicate check", false,
                $"Cannot run: the target has no '{Target.SourceUrlFieldName}' field to match on, " +
                "so a second run would copy this again."));
            return null;
        }

        // Searched again rather than trusting plan.ExistingCopy: the plan has
        // been through the browser, and something may have been created in the
        // meantime anyway.
        var existing = await existingCopies.FindAsync(
            sourceUrlField.Name, sourceUrlField.FieldId,
            plan.SourceKey, issue.Url, issue.Summary, cancellationToken);

        if (existing is { SummaryMatches: true })
        {
            steps.Add(new StepOutcome("Duplicate check", true,
                $"Already copied as {existing.Key}: identical summary, and its " +
                $"{sourceUrlField.Name} is this issue's URL."));

            return Skipped(plan, steps, $"{plan.SourceKey} has already been copied as {existing.Key}.");
        }

        if (existing is not null)
        {
            // Same origin, different summary. Not a duplicate by the rule, and
            // worth saying out loud: it is usually the same ticket reworded on
            // this side, and copying again makes a second one.
            steps.Add(new StepOutcome("Duplicate check", false,
                $"{existing.Key} already points at this issue in {sourceUrlField.Name}, but its " +
                $"summary reads '{existing.Summary}' rather than '{issue.Summary}'. Copying anyway, " +
                "because a duplicate has to match on both."));

            return null;
        }

        steps.Add(new StepOutcome("Duplicate check", true, "No existing copy."));
        return null;
    }

    /// <summary>
    /// Jira puts the actual reason in the response body - which field it
    /// objected to, and why. The exception message alone only says a 400
    /// happened, which is no use to someone finishing the copy by hand.
    /// </summary>
    private static string Describe(Exception failed) => failed switch
    {
        AtlassianApiException { ResponseBody: { Length: > 0 } body } atlassian =>
            $"{atlassian.Message} {body}",
        _ => failed.Message,
    };

    // ---------------------------------------------------------------- create

    /// <param name="MediaStripped">How many images the create had to leave out
    /// of the description, and therefore how many the second pass should put
    /// back once the attachments exist.</param>
    private sealed record CreatePayload(
        JsonObject Fields,
        IReadOnlyDictionary<string, string> AccountIds,
        int MediaStripped,
        List<StepOutcome> Notes);

    private async Task<CreatePayload> BuildFieldsAsync(
        MappingPlan plan,
        SourceIssue issue,
        string? sourceUrlFieldId,
        string? epicKey,
        IReadOnlyList<TargetField> createScreen,
        CancellationToken cancellationToken)
    {
        var notes = new List<StepOutcome>();

        var fields = new JsonObject
        {
            ["project"] = new JsonObject { ["key"] = plan.TargetProjectKey },
            ["issuetype"] = new JsonObject { ["id"] = plan.TargetIssueTypeId },
        };

        // Only Mapped rows are sent. Anything else was reported in the preview
        // as dropped or unmappable and must not be smuggled through.
        foreach (var row in plan.Rows.Where(row =>
                     row.Status == MappingStatus.Mapped &&
                     row.TargetFieldId is { Length: > 0 } &&
                     row.MappedValue is not null))
        {
            fields[row.TargetFieldId!] = row.MappedValue!.DeepClone();
        }

        var accountIds = await ResolveUsersAsync(issue, fields, notes, cancellationToken);

        // Media is stripped rather than rewritten: attachments cannot be
        // uploaded until the issue exists, so their target ids are unknown at
        // this point. RestoreDescriptionMediaAsync puts them back afterwards.
        var rewritten = adf.Rewrite(issue.Description, new AdfRewriteContext(
            new Uri(issue.Url), accountIds, new Dictionary<string, string>()));

        if (rewritten.Document is not null)
        {
            fields["description"] = rewritten.Document;
        }

        // Media removals are expected here and temporary, so they are not
        // reported as a problem the way a flattened mention is - the second
        // pass reports what it managed to put back.
        var media = rewritten.Removals.Where(IsMedia).ToList();
        var rest = rewritten.Removals.Where(removal => !IsMedia(removal)).ToList();

        if (rest.Count > 0)
        {
            notes.Add(new StepOutcome("Description", false,
                $"Rewritten for the target tenant: {string.Join("; ", rest)}."));
        }

        if (sourceUrlFieldId is not null)
        {
            fields[sourceUrlFieldId] = issue.Url;
        }

        // Set on the create itself where the screen allows it, which it does
        // for Story, Bug and Task. Jira fills in Epic Link from this by itself,
        // so the two never disagree.
        if (epicKey is not null &&
            createScreen.Any(field => field.FieldId.Equals("parent", StringComparison.OrdinalIgnoreCase)))
        {
            fields["parent"] = new JsonObject { ["key"] = epicKey };
        }

        return new CreatePayload(fields, accountIds, media.Count, notes);
    }

    private static bool IsMedia(string removal) =>
        removal.StartsWith("media node", StringComparison.Ordinal);

    /// <summary>
    /// The description is written twice on purpose.
    ///
    /// At create time every media node points at an attachment id belonging to
    /// the source tenant, and there is nothing here to repoint them at, so they
    /// come out - a media node carrying a foreign id renders as a broken image.
    /// Once the files have been re-uploaded their target ids exist, and the
    /// same rewrite runs again with the images back in place.
    /// </summary>
    private async Task RestoreDescriptionMediaAsync(
        CreatedIssue created,
        SourceIssue issue,
        CreatePayload payload,
        IReadOnlyDictionary<string, string> attachmentIds,
        List<StepOutcome> steps,
        CancellationToken cancellationToken)
    {
        if (payload.MediaStripped == 0)
        {
            return;
        }

        if (attachmentIds.Count == 0)
        {
            steps.Add(new StepOutcome("Description images", false,
                $"{payload.MediaStripped} image(s) are missing from the description because no " +
                "attachment was re-uploaded for them to point at."));
            return;
        }

        try
        {
            var rewritten = adf.Rewrite(issue.Description, new AdfRewriteContext(
                new Uri(issue.Url), payload.AccountIds, attachmentIds));

            if (rewritten.Document is null)
            {
                return;
            }

            var stillMissing = rewritten.Removals.Count(IsMedia);
            var restored = payload.MediaStripped - stillMissing;

            if (restored <= 0)
            {
                steps.Add(new StepOutcome("Description images", false,
                    $"None of the {payload.MediaStripped} image(s) could be restored: the " +
                    "re-uploaded attachments do not match the ids the description refers to."));
                return;
            }

            await writer.UpdateDescriptionAsync(created.Key, rewritten.Document, cancellationToken);

            steps.Add(stillMissing == 0
                ? new StepOutcome("Description images", true,
                    $"Restored {restored} image(s) in the description once the attachments existed.")
                : new StepOutcome("Description images", false,
                    $"Restored {restored} of {payload.MediaStripped} image(s); " +
                    $"{stillMissing} had no matching attachment."));
        }
        catch (Exception failed)
        {
            steps.Add(new StepOutcome("Description images", false, Describe(failed)));
        }
    }

    /// <summary>
    /// Reporter and assignee are re-resolved against the target's directory
    /// rather than trusted from the plan, because whether an account exists
    /// there is not something the preview could know for certain.
    /// </summary>
    private async Task<Dictionary<string, string>> ResolveUsersAsync(
        SourceIssue issue,
        JsonObject fields,
        List<StepOutcome> notes,
        CancellationToken cancellationToken)
    {
        var accountIds = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (fieldId, user) in new[] { ("reporter", issue.Reporter), ("assignee", issue.Assignee) })
        {
            if (user is null || !fields.ContainsKey(fieldId))
            {
                continue;
            }

            var resolved = await users.ResolveAsync(user, cancellationToken);

            if (resolved.AccountId is null)
            {
                fields.Remove(fieldId);
                notes.Add(new StepOutcome($"{Capitalise(fieldId)}", false, resolved.Reason));
                continue;
            }

            fields[fieldId] = new JsonObject { ["accountId"] = resolved.AccountId };
            accountIds[user.AccountId] = resolved.AccountId;

            if (resolved.IsFallback)
            {
                // Not a failure - the fallback is a normal path here - but the
                // original name has to survive somewhere a human will see it.
                notes.Add(new StepOutcome($"{Capitalise(fieldId)}", true, resolved.Reason));
            }
        }

        return accountIds;
    }

    // ---------------------------------------------------------------- extras

    /// <summary>
    /// Puts the copy in a target sprint - the board's current one, or one
    /// chosen from its list; by now the two are the same shape.
    ///
    /// AFTER the create, deliberately, even though the field is on the create
    /// screen. The sprint field rejected a value once already with
    /// "Specify a valid value for Sprint", and on the create that 400 costs the
    /// whole ticket. Here the copy exists first, so a refusal costs the sprint
    /// and reports itself - which is the same bargain every other extra makes.
    /// </summary>
    private async Task AddToSprintAsync(
        CreatedIssue created,
        ApplyRequest request,
        ActiveSprintResult sprint,
        IReadOnlyList<TargetField> createScreen,
        List<StepOutcome> steps,
        CancellationToken cancellationToken)
    {
        if (!request.AddToActiveSprint && request.SprintId is null)
        {
            return;
        }

        if (sprint.Sprint is not { } active)
        {
            steps.Add(new StepOutcome("Sprint", false,
                sprint.Reason ?? "No sprint could be resolved."));
            return;
        }

        // Found by schema, never by id or name: ids differ per tenant, and the
        // source has two fields called "Sprint" that are not this one.
        var field = createScreen.FirstOrDefault(candidate =>
            candidate.SchemaCustom == SprintReader.SprintSchema);

        if (field is null)
        {
            steps.Add(new StepOutcome("Sprint", false,
                "The target has no sprint field on this issue type's screen, so the copy " +
                $"could not be added to {active.Name}."));
            return;
        }

        try
        {
            // A bare id. The field reads BACK as an array of sprint objects,
            // which is not the shape it is written with.
            await writer.UpdateFieldsAsync(
                created.Key,
                new JsonObject { [field.FieldId] = active.Id },
                cancellationToken);

            steps.Add(new StepOutcome("Sprint", true, $"Added to {active.Name}."));
        }
        catch (Exception failed)
        {
            logger.LogWarning(failed, "Could not add {Key} to sprint {Sprint}", created.Key, active.Id);

            steps.Add(new StepOutcome("Sprint", false,
                $"Could not add the copy to {active.Name}. {Describe(failed)}"));
        }
    }

    private async Task AddRemoteLinkAsync(
        CreatedIssue created,
        SourceIssue issue,
        List<StepOutcome> steps,
        CancellationToken cancellationToken)
    {
        try
        {
            await writer.AddRemoteLinkAsync(created.Key, issue.Url, issue.Key, cancellationToken);
            steps.Add(new StepOutcome("Remote link", true, $"Linked back to {issue.Key}."));
        }
        catch (Exception failed)
        {
            steps.Add(new StepOutcome("Remote link", false, Describe(failed)));
        }
    }

    private async Task AddCommentsAsync(
        CreatedIssue created,
        SourceIssue issue,
        CreatePayload payload,
        IReadOnlyDictionary<string, string> attachmentIds,
        List<StepOutcome> steps,
        CancellationToken cancellationToken)
    {
        if (issue.Comments.Count == 0)
        {
            return;
        }

        var copied = 0;
        var failures = new List<string>();

        foreach (var comment in issue.Comments)
        {
            try
            {
                // The same context the description's second pass uses, so an
                // image pasted into a comment survives for the same reason.
                var rewritten = adf.Rewrite(comment.Body, new AdfRewriteContext(
                    new Uri(issue.Url), payload.AccountIds, attachmentIds));
                await writer.AddCommentAsync(created.Key, Attribute(comment, rewritten.Document), cancellationToken);
                copied++;
            }
            catch (Exception failed)
            {
                failures.Add($"{comment.Id}: {Describe(failed)}");
            }
        }

        steps.Add(failures.Count == 0
            ? new StepOutcome("Comments", true,
                $"Copied {copied} comment(s), each attributed to its original author.")
            : new StepOutcome("Comments", false,
                $"Copied {copied} of {issue.Comments.Count}. Failed: {string.Join("; ", failures)}"));
    }

    /// <summary>
    /// Every comment is authored by the running token - Jira has no
    /// impersonation on this route - so the original author and timestamp go
    /// into the body. Without that the history is simply a lie.
    /// </summary>
    private static JsonNode Attribute(SourceComment comment, JsonNode? body)
    {
        var author = comment.Author?.DisplayName ?? "Unknown";
        var when = comment.Created?.ToString("dd/MM/yyyy HH:mm") ?? "an unknown date";

        var content = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "paragraph",
                ["content"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = $"{author} wrote on {when}:",
                        ["marks"] = new JsonArray { new JsonObject { ["type"] = "em" } },
                    },
                },
            },
        };

        if (body?["content"] is JsonArray original)
        {
            foreach (var node in original)
            {
                content.Add(node?.DeepClone());
            }
        }

        return new JsonObject
        {
            ["type"] = "doc",
            ["version"] = 1,
            ["content"] = content,
        };
    }

    /// <returns>Attachment FILE NAME -> an absolute URL to the re-uploaded copy
    /// on the target. Keyed by name because that is the only value a media node
    /// and an attachment record share; their ids are different identifier
    /// spaces entirely.</returns>
    private async Task<Dictionary<string, string>> AddAttachmentsAsync(
        CreatedIssue created,
        SourceIssue issue,
        List<StepOutcome> steps,
        CancellationToken cancellationToken)
    {
        var ids = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (issue.Attachments.Count == 0)
        {
            return ids;
        }

        var copied = 0;
        var failures = new List<string>();

        foreach (var attachment in issue.Attachments)
        {
            try
            {
                var content = await source.GetAttachmentAsync(attachment.Id, cancellationToken);

                var targetId = await writer.UploadAttachmentAsync(
                    created.Key, attachment.FileName, content, attachment.MimeType, cancellationToken);

                if (targetId is not null)
                {
                    ids[attachment.FileName] = targetId;
                }


                copied++;
            }
            catch (Exception failed)
            {
                failures.Add($"{attachment.FileName}: {Describe(failed)}");
            }
        }

        steps.Add(failures.Count == 0
            ? new StepOutcome("Attachments", true, $"Re-uploaded {copied} file(s).")
            : new StepOutcome("Attachments", false,
                $"Re-uploaded {copied} of {issue.Attachments.Count}. Failed: {string.Join("; ", failures)}"));

        return ids;
    }

    // ---------------------------------------------------------------- helpers

    private static TicketOutcome Skipped(MappingPlan plan, List<StepOutcome> steps, string why) =>
        new(plan.SourceKey, null, null, ApplyStatus.Skipped, why, steps);

    private static TicketOutcome Failed(MappingPlan plan, List<StepOutcome> steps, string why) =>
        new(plan.SourceKey, null, null, ApplyStatus.Failed, why, steps);

    private static string Capitalise(string value) =>
        string.Concat(char.ToUpperInvariant(value[0]), value[1..]);
}
