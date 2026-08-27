# TODO

Things parked deliberately, with enough detail to act on without the
conversation that found them.

## Open

### Create the `TARGET_PROJECT Source Key` field on the target

Apply reports this on every copy:

> No field named 'TARGET_PROJECT Source Key' on the target's create screen. The duplicate
> check cannot run and the copy will not record its origin.

Not a code problem — the field does not exist in Jira yet. It needs to be a
**text** custom field on the TARGET_PROJECT project, named exactly `TARGET_PROJECT Source Key`
(configurable as `Atlassian:Target:ProvenanceFieldName`), and **on the create
screen**.

It holds the source issue key and is the only duplicate check there is: remote
links are not JQL-searchable, so without it a second run over the same tickets
silently creates a second copy of everything. We control the your-company tenant,
so this is ours to do.

### CLAUDE.md is stale in two places

- *"`Sprint` is a trap"* — states there is no greenhopper Sprint field on the
  source. Wrong: `customfield_SOURCE_ID` is one. The Phase 0 sweep missed it, which
  is what let the 400 below happen. The mapper is fixed; the note is not.
- The recorded issue-type rules (`New Feature` → Story, `Production Issue` →
  Bug). Both were replaced on 18/08/2026 by: Epic, Story, Bug, Task and
  Sub-task match by name, everything else falls back to Task
  (`Mapping:FallbackIssueType`).

## Done

### Inline images in a copied description

**Settled 18/08/2026 against the live target.**

A copied description lost its images even though the files copied fine. Two
causes, one after the other:

1. Attachments were uploaded *after* the description was written, so there was
   nothing to point the media nodes at. Fixed by reordering: create, upload,
   then rewrite the description a second time.
2. The rewritten node still did not work. A `file` media node needs a **Media
   Services UUID**, and no REST endpoint returns one for an attachment uploaded
   over the API — `GET /rest/api/3/attachment/{id}` returns only `id`, `self`,
   `filename`, `author`, `created`, `size`, `mimeType`, `properties`,
   `content`, `thumbnail`. Sending the attachment id instead is accepted with a
   **204 and then silently discarded**, which is why the copy came back with no
   media node at all.

Resolved by emitting an **external** media node pointing at the target's own
attachment content URL, matched to the source node by file name (`attrs.alt`) —
the only value a media node and an attachment record share. Verified against a live copy: Jira stores the node and
`expand=renderedFields` returns a real `<img>`.


### Sprint was being carried across to the target

**Found 18/08/2026 previewing a real source issue. Confirmed live the same day
as a failed copy, then fixed.**

```
Target tenant returned 400 for POST rest/api/3/issue.
{"errors":{"customfield_TARGET_ID":"Specify a valid value for Sprint"}}
```

| | Field id | `schema.custom` |
|---|---|---|
| Source (source-company) | `customfield_SOURCE_ID` | `com.pyxis.greenhopper.jira:gh-sprint` |
| Target (your-company) | `customfield_TARGET_ID` | `com.pyxis.greenhopper.jira:gh-sprint` |

The custom type keys were identical, so `TypesAgree` returned true and the
source object was carried over whole — sprint `8796` on board `200`, both of
which belong to source-company. A sprint id means nothing on the other tenant, so
there was never a correct value to map it to.

Fixed by `NeverMappedByName` in
[`FieldMapper.cs`](src/TicketCloner.Api/Mapping/FieldMapper.cs), keyed by
**name** because the field id differs per tenant — which is exactly why
`NeverSettableOnCreate`, keyed by id, could not express it. Sprint is now
dropped and the row says why. Covered by
`Sprint_is_dropped_whatever_type_the_source_carries` across all three source
shapes, and end-to-end by
`Sprint_is_dropped_even_though_both_tenants_agree_on_its_type`, with both
tenant fakes carrying a greenhopper Sprint so the old fixture gap cannot
reopen.
