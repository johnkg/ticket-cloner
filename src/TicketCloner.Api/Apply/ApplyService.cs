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

        foreach (var plan in request.Plans)
        {
            outcomes.Add(await ApplyOneAsync(plan, request, cancellationToken));
        }

        return new ApplyResponse(outcomes);
    }

    private async Task<TicketOutcome> ApplyOneAsync(
        MappingPlan plan,
        ApplyRequest request,
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
            var provenanceField = createScreen.FirstOrDefault(field =>
                field.Name.Equals(Target.ProvenanceFieldName, StringComparison.OrdinalIgnoreCase));

            var sourceUrlField = createScreen.FirstOrDefault(field =>
                field.Name.Equals(Target.SourceUrlFieldName, StringComparison.OrdinalIgnoreCase));

            if (provenanceField is null)
            {
                // Without it there is no JQL-searchable record of where a copy
                // came from, so a second run duplicates everything silently.
                steps.Add(new StepOutcome("Provenance", false,
                    $"No field named '{Target.ProvenanceFieldName}' on the target's create screen. " +
                    "The duplicate check cannot run and the copy will not record its origin."));
            }
            else if (request.SkipDuplicates)
            {
                var existing = await writer.FindExistingCopyAsync(
                    provenanceField.Name, plan.SourceKey, cancellationToken);

                if (existing is not null)
                {
                    steps.Add(new StepOutcome("Duplicate check", true, $"Already copied as {existing}."));
                    return Skipped(plan, steps, $"{plan.SourceKey} has already been copied as {existing}.");
                }

                steps.Add(new StepOutcome("Duplicate check", true, "No existing copy."));
            }

            if (sourceUrlField is null)
            {
                steps.Add(new StepOutcome("Source URL", false,
                    $"No field named '{Target.SourceUrlFieldName}' on the target's create screen. " +
                    "The copy will not carry the original's address."));
            }

            var payload = await BuildFieldsAsync(
                plan, issue, provenanceField?.FieldId, sourceUrlField?.FieldId, cancellationToken);
            steps.AddRange(payload.Notes);

            var created = await writer.CreateAsync(payload.Fields, cancellationToken);
            steps.Add(new StepOutcome("Create", true, $"Created {created.Key}."));

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
        string? provenanceFieldId,
        string? sourceUrlFieldId,
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

        if (provenanceFieldId is not null)
        {
            fields[provenanceFieldId] = plan.SourceKey;
        }

        if (sourceUrlFieldId is not null)
        {
            fields[sourceUrlFieldId] = issue.Url;
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
