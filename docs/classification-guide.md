# Guided classification

The default classification view has four steps:

1. **Upload** — select a published workflow and upload files, try a sample, or open an existing packet.
2. **Review** — review one document at a time, inspect its source pages, correct its type and reference, and save drafts.
3. **Approve** — check the document groups and approve the current revision.
4. **Export** — download the approved ZIP or individual PDFs.

Open **Menu** for Guided review, Advanced workspace, workflow management, platform tools and the light/dark switch. The application header stays inside this menu in both review modes. The menu supports keyboard navigation, Escape dismissal and return focus to its trigger.

Guided review uses the available screen width, with the document as the main canvas and a narrow labels panel. **Fit page** shows the whole source page; the zoom controls enlarge it for closer inspection, with scrolling available while zoomed. On a phone, **Details** opens the labels panel below the source. Page navigation and document navigation remain separate.

Use **Advanced** for page splitting/reorganization, reference verification, extraction corrections, history, delivery and platform tools. Switching views preserves current review edits. Returning to Guided keeps the selected document. The Advanced inbox holds workflow selection, upload and sample intake.

In Advanced, drag a page to change its position or move it into another document. Drop it in **New document** to separate it into a new group immediately after its original document. Moving the last page out of a group removes that empty group. The moved page stays selected. On touch screens, hold briefly before dragging.

**Move page to document** also supports existing groups and **New document**, including keyboard navigation. Save the draft after reorganizing; changes to approved groups require approval again before export.

Processing, upload retries, approval and downloads use the same API operations as Advanced. Failed processing can be retried; rejected approval keeps the edits and offers Advanced tools. Changing an approved packet requires a new approval before export.

Workflow setup separately uses the Basics, Documents, Review rules and Finish wizard. Split policies include an explanation and example for the current selection.
