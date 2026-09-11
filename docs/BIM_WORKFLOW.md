# Scoped BIM review and parameter changes

These instructions describe a reusable working method. They do not authorize
model edits or establish drawing, construction, or design approval. Keep live
project records and customer drawings in the ignored local evidence folders.

## Establish the current scope

Record the requested action and its document, building, level, category, and
element boundaries before querying data used to calculate changes. A request
for walls on one floor does not include doors, floors, ceilings, fixtures, or
other categories. A later request for doors establishes a separate scope;
earlier exclusions remain in force for the other categories.

Pin the running session and document fingerprint. Resolve the level from the
actual instance, applicable level parameters, or verified host relationship.
If a reference level and the physical location disagree, retain the evidence
and decide that case explicitly. Never turn a historical element ID, room
name, dimension, or previous floor assumption into a live selection rule.

An instruction to review or compare permits reading and reporting. An
instruction to apply an agreed correction permits that correction within its
stated scope. Preserve authorization already given; ask only for a missing
decision that changes the result. Check the actual host's coordination support
as described in [work scope coordination](WORK_SCOPE_COORDINATION.md).

## Build a parameter plan from evidence

1. Snapshot instance IDs and UniqueIds, type and host IDs, current parameters,
   geometry needed for verification, and existing warnings.
2. Distinguish instance parameters from shared type parameters. Resolve the
   exact name and parameter ID/GUID and reject ambiguous or read-only fields.
3. Write the requested value precedence explicitly, including any author-based
   revision rule and values that must remain unchanged. Preserve existing
   author marks during metadata-only edits unless the user requests otherwise.
4. Use other floors as reference data. Prefer the same exact type, then a
   verified matching family, function, and material. Other-floor references
   do not authorize editing those floors.
5. Check reference quality before copying values. Different trade values on
   the same type can be stale text or different intended uses. Compare the
   applied type, actual materials, and functional role instead of choosing an
   unqualified majority. Keep real finish walls and placeholder walls distinct.
6. Preserve existing nonempty values unless the agreed rule calls for their
   correction. Do not invent dates, issue codes, WBS values, material approval,
   or construction status from a type name.
7. Never copy another instance's unique door number. A valid identifier on the
   same instance can be a reference; a numeric automatic Mark alone does not
   establish a drawing door number. Keep unresolved matches visible.
8. Distinguish leaf size, overall opening size, family dimensions, and management
   schedule size. Curtain-panel instances can have different widths on the
   same type. If a management size and modeled height differ, resolve the
   intended field meaning before writing it. A user-selected management value
   does not authorize changing physical dimensions.

Keep each planned change's old value, new value, rule, and reference IDs in a
local record. Explicitly list unresolved fields rather than applying a guessed
value to complete every cell.

## Apply and verify efficiently

Reuse an existing review and type mapping when still applicable. Re-read the
current identity, scope, and values before writing; repeat source extraction
only when the source, mapping, or affected model state has changed.

Apply independent changes in bounded batches using a transaction and an
idempotency key. Validate the category and level again at the write boundary,
as well as expected old values and ownership. If the user edited an element
after the snapshot, update the plan for that item instead of overwriting it.

After commit, make a separate read to verify every planned value. Check
unchanged author marks, types, dimensions, location, host, and orientation as
appropriate. Compare warnings and the relevant properties of elements outside
the mutation scope. A successful transaction alone is not verification.

Use numeric checks for straightforward metadata changes. Generate images for
unresolved geometry, a new family substitution, or another visual question;
do not create an image for every routine parameter change. Report applied and
pending counts with the reason for any delay or remaining decision.

## Recover an accidental scope expansion

Use the run's before-state and its exact changed-value list. Restore only those
out-of-scope fields whose live value still matches what that run wrote. Preserve
later user changes and report conflicts. Avoid a blanket Undo across subsequent
work. Re-query the restored values and preserve the original error record with
a clear link to the corrective result.

Record whether the model was saved or synchronized. Source-control publication
does not save or synchronize an open Revit model. Publish reusable instructions
and code; keep client drawings, live model dumps, session tokens, and local
project reports out of the public repository.
