# ArcGIS Playback

ArcGIS Playback is an ArcGIS Pro 3.3 add-in for recording an editing session as a portable `.unplayback.json` change package, then replaying that package into a target utility-network map. It is intended for controlled pre-production-to-production migration where production GlobalIDs differ and `FacilityID` is the durable business identifier.

## Build and release

The release version is in [`Configuration/BuildVersion.props`](Configuration/BuildVersion.props). Change `ArcGISPlaybackVersion` before a distributable build.

```powershell
cd C:\Code\arcgis-playback\NetworkChangePlaybackAddin
dotnet build .\NetworkChangePlaybackAddin.csproj -c Release
```

The build copies the package to `release\ArcGISPlayback.ArcPro.3.3.v<version>.esriAddinX`. The `release` directory is intentionally not tracked by Git. Install the resulting `.esriAddinX` into ArcGIS Pro.

## Workflow

1. In the source/pre-production map, choose **Start Recording** and enter the ArcFM session name, package attribution, description, file name, and destination. The active branch version is prefilled and can be corrected if required.
2. Edit normally with ArcGIS Pro tools or Pro SDK add-ins. A **RECORDING** indicator confirms capture is active.
3. Choose **Save Recording** to stop capture and write the package.
4. In the target production map/version, choose **Playback Recording**, select the package, and start playback.
5. When target data cannot be resolved or an edit fails, playback pauses. Choose **Yes** after correcting data to retry, **No** to skip that operation, or **Cancel** to stop while retaining already-applied edits.

## Identity and target resolution

Production GlobalIDs are deliberately not used as a cross-environment key. Playback resolves rows using the package-local ID for a feature created earlier in the same playback, then `FacilityID` for an existing production feature, narrowed by source/table and subtype when available.

For an edit or association involving an existing feature, populate `FacilityID` in the source before recording. A package cannot reliably recreate an association to an existing endpoint that has neither a prior package ID nor a FacilityID.

## Scope and safeguards

The recorder journals feature and object-table creates, updates, deletes, geometry edits, and association changes observed through normal Pro edit events. To protect ArcGIS Pro’s edit pipeline, event callbacks record in memory; packages are saved every 15 seconds and when **Save Recording** is used. Association reads are debounced until editing is idle and serialized so they do not compete with placement tools. Replay supports subtype feature layers and subtype object tables, including association endpoints represented by subtype layers/tables.

Always use a clean target version and validate a representative package before production. Replay is intentionally interactive on unresolved rows and failed operations; it does not silently substitute a GlobalID from pre-production. Attachments, inspection/work-management records, traces, subnetworks, and integration-specific side effects require their own tested capture/replay rules before they are migration-ready.

## Layout

- `Configuration/BuildVersion.props` — single release-version setting.
- `Config.daml` — ArcGIS Pro add-in metadata.
- `release` — generated distributable output, ignored by Git.
