# ArcGIS Playback

ArcGIS Playback is an ArcGIS Pro 3.3 add-in for recording an editing session as a portable `.unplayback.json` change package, then replaying that package into a target utility-network map. It is intended for controlled pre-production-to-production migration where production GlobalIDs differ and `FacilityID` is the durable business identifier.

## Workflow

1. In the source/pre-production map, choose **Start Recording** and enter the ArcFM session name, package attribution, description, file name, and destination. The active branch version is prefilled and can be corrected if required.
2. Edit normally with ArcGIS Pro tools or Pro SDK add-ins. A **RECORDING** indicator confirms capture is active.
3. Choose **Save Recording** to stop capture and write the package.
4. In the target production map/version, choose **Playback Recording**, select the package, and start playback.
5. When target data cannot be resolved or an edit fails, playback pauses. Choose **Yes** after correcting data to retry, **No** to skip that operation, or **Cancel** to stop while retaining already-applied edits.

Use **Preview Playback** to draw a non-editing, point-and-line sketch of the recorded feature geometry in the active map. Blue represents adds, gold updates, and red deletes. During actual playback, a progress dialog lists each operation and its current result.

If recording was not started, use **Capture Version Changes** while the active map is connected to a saved named version. It creates a package containing the final inserts, updates, deletes, and Utility Network association changes in that version compared with the service's configured `DEFAULT`; it does not recreate the original order of edits. Capture is limited to Utility Network source feature classes and tables—dirty areas, error layers, traces, and unrelated map data are explicitly excluded. The command stops rather than saving an incomplete package when the service reports rows or association endpoints it cannot read.

## Identity and target resolution

Production GlobalIDs are deliberately not used as a cross-environment key. Playback resolves rows using the package-local ID for a feature created earlier in the same playback, then `FacilityID` for an existing production feature, narrowed by source/table and subtype when available. For unkeyed spatial junctions, playback can use a unique subtype-matched location or an existing association to an already-resolved endpoint; ambiguous matches pause for review.

For an edit or association involving an existing feature, populate `FacilityID` in the source before recording. A package cannot reliably recreate an association to an existing endpoint that has neither a prior package ID nor a FacilityID.

## Scope and safeguards

The recorder journals feature and object-table creates, updates, deletes, geometry edits, and association changes observed through normal Pro edit events. To protect ArcGIS Pro’s edit pipeline, event callbacks record in memory; packages are saved every 15 seconds and when **Save Recording** is used. Association reads are debounced until editing is idle and serialized so they do not compete with placement tools. Replay supports subtype feature layers and subtype object tables, including association endpoints represented by subtype layers/tables.

The source map extent is recorded with the package and playback zooms the active map to it before edits begin. Always use a clean target version and validate a representative package before production. Replay is intentionally interactive on unresolved rows and failed operations; it does not silently substitute a GlobalID from pre-production. Existing associations are recognized as already satisfied rather than reported as failures. Attachments, inspection/work-management records, traces, subnetworks, and integration-specific side effects require their own tested capture/replay rules before they are migration-ready.
