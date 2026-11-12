# Multiple classification workflows

This release separates reusable **document types**, **classification releases**, and **workflows**. A workflow determines how an uploaded packet is classified and assembled. Every accepted run keeps a frozen copy of the configuration it used.

## Configure a process

Workflow editing opens in **Guided** mode: Basics, Documents, Review rules, then Finish. Each step occupies its own page with Back/Next navigation. Save drafts at any step; preview and publish from Finish. **Advanced** shows the complete editor, including provider settings and processing capabilities. Switching modes keeps current edits.

The header's light/dark switch follows the system preference initially and remembers an explicit choice in the browser.

1. Create document-type definitions in the workflow management screen. Give each a stable ID, name, description, keyword hints and classification instructions. Include the built-in `unknown` definition in every release.
2. Configure each type's splitting policy, or select **inherit** to use the workflow default. Save and publish the type versions you intend to use.
3. Create a release selecting exact type versions, then publish it.
4. Create a workflow selecting that release, its processing mode, model/profile, default splitting policy and identifier requirements. Save the draft.
5. Upload a synthetic sample through the draft-preview action. Inspect predicted types, boundaries and review reasons. Save changes and repeat when necessary.
6. Publish the workflow. It becomes available in the upload selector. Optionally make it the default for submissions that omit an explicit selection.

The initial General logistics workflow preserves existing simulation behavior. Invoice processing and Receiving paperwork are example drafts that can be adapted and published. Multiple workflows can use the same release with different default splitting policies; a type-specific policy takes precedence.

## Splitting policies

| Policy | Behavior |
| --- | --- |
| Inherit | Use the workflow default; available on document types. |
| Classifier | Use the classifier's independent page-boundary proposals. |
| Separate | Start a document for every page. |
| Consecutive | Combine consecutive pages of the same type. |
| Manual | Propose boundaries and flag them for reviewer confirmation. |
| Fixed | Split consecutive pages of a type into groups of the configured size; flag incomplete groups. |
| First | Start a new document when the configured first-page pattern matches. |
| Last | End a document when the configured last-page pattern matches. |
| Identifier | Start a new document when the first captured identifier changes. |

Rules operate within source-file boundaries. Reviewers can manually join pages across files or reassemble interleaved pages. Missing or conflicting evidence produces review reasons, and original pages are retained. Unknown types require review. All exports require explicit approval.

The split-policy selector shows a short explanation and example for the selected policy. Marker-based policies include expandable pattern help. Manual review starts with separate pages so the reviewer can merge them into documents.

Patterns use bounded **.NET regular expressions**. For identifier splitting, include a capture group, for example `Invoice number:\s*([A-Z0-9-]+)`. Use the sample preview to verify what the pattern captures. An invoice number changing between consecutive invoice pages is a document-boundary signal; it does not itself validate a shipment or assign a business reference.

## Versions and historical work

Drafts are editable. Published versions are immutable; create a new version to change them, or clone an entry to start a separate identity. Release references are exact versions, so publishing a new type version does not silently update existing releases.

Each accepted packet stores workflow/release identity, versions, a non-secret resolved snapshot and its hash in the same transaction as its durable processing message. Editing, publishing or archiving catalog entries cannot change an accepted run. Retries, recovery and dead-letter replay retain the same snapshot.

Archiving prevents new selection while preserving historical access. Choose another default before archiving the current default workflow. Reprocessing normally retains the original configuration, including archived versions. Explicitly choosing another published workflow creates a new linked processing run with that selection.

Upload retries keep the original selected workflow even when the default changes. Reusing an idempotency key with a changed selection returns a conflict. The browser preserves the selection attached to an uncertain upload so retrying a lost response does not create a different run.

Legacy packets remain readable using a deterministic legacy interpretation. Existing saved history and delivery artifacts remain intact. New workflow-aware exports include configuration provenance.

## Draft previews

Save the draft before previewing. A preview freezes the saved workflow revision and its referenced definition/release versions, including draft dependencies. It executes the same simulation classification and splitting code used by published simulation workflows. Test runs are explicitly marked, excluded from production workflow summaries and blocked from simulated delivery. Their exports retain test provenance.

Previewing a draft helps test rule behavior; it does not measure live OCR or model quality. Publish referenced types and releases before publishing the workflow itself.

## Providers and review

Simulation extracts embedded PDF text and uses configured local rules. Scanned pages without text remain unknown. Live workflows use the approved server-side provider profile and configured model; credentials are never stored in workflow definitions or snapshots. The application reports configuration readiness, while successful live authentication and quality require separate service verification.

Enabled identifier kinds control candidate extraction. Required kinds add review reasons when evidence is missing. Conflicting candidates remain visible for manual business-reference assignment. Workflow-specific type choices apply when reviewing both current and historical runs.

## Evaluation and operations

Workflow summaries distinguish versions and report packet states and reviewer corrections. The evaluator retains only workflow/release IDs, versions and the snapshot hash, together with the test-run marker; prompts and definition contents are excluded from aggregate reports. Optional ground-truth workflow identity can reject scoring against the wrong configuration. Older snapshots without workflow metadata report null provenance.

Run the packaged acceptance suite, synthetic benchmark and recovery checks as described in the README. The workflow durability check changes and archives a workflow before forcing termination, then verifies that restore, retry, replay and linked reprocessing preserve the original snapshot.

## Design references

Workflow versions pin definitions and split rules so a packet can be reviewed against the configuration used at intake.
