# K-PACS.neo

A modern C# port of key components from **K-PACS**, the free DICOM workstation I originally wrote about 20 years ago. K-PACS was one of the first freely available DICOM viewers and became widely used in radiology departments, veterinary clinics, and research labs around the world.

This project aims to bring the core DICOM infrastructure into the .NET ecosystem, built on top of [fo-dicom](https://github.com/fo-dicom/fo-dicom) and targeting **.NET 10**.

The old **WPF viewer has been retired from this repository**. Active UI development now happens only in the **Avalonia** application.

## Project Structure

```
KPACS.DCMClasses/               — Core DICOM class library (.NET 10, fo-dicom 5.1.3)
├── DicomTypes.cs                — Enumerations (content types, VR types, bit depths, …)
├── DicomTagConstants.cs         — UID constants, private tag definitions, UID registry
├── DicomTagValue.cs             — Tag value wrapper (group, element, VR, value, name)
├── DicomBaseObject.cs           — Base class with notification support
├── DicomHeader.cs               — Core DICOM dataset wrapper (read/write/navigate tags)
├── DicomFunctions.cs            — Utility functions (date/time, UID generation, name parsing, …)
├── DicomImage.cs                — DICOM image file handling and pixel data access
├── DicomDirectory.cs            — DICOMDIR creation and reading
├── DicomPdf.cs                  — Encapsulated PDF DICOM objects
├── DicomSecondaryCapture.cs     — Secondary Capture creation from raw pixel data
├── DicomStructuredReport.cs     — Structured Report (SR) creation and content management
├── DicomPresentationState.cs    — Grayscale Softcopy Presentation State (GSPS)
├── DicomNetworkClient.cs        — DICOM networking (C-ECHO, C-FIND, C-MOVE, C-STORE, Worklist)
├── DicomNetworkThread.cs        — Multi-study async network operations with progress
└── Models/
    ├── StudyInfo.cs              — Study / Series / Image info classes
    ├── PrintConfig.cs            — DICOM print configuration
    └── WorklistItem.cs           — Modality worklist item

KPACS.Viewer.Avalonia/          — Cross-platform study browser + DICOM viewer (.NET 10, Avalonia 11.3)
├── App.axaml / App.axaml.cs      — Application entry point, imagebox bootstrap, Semi.Avalonia light theme
├── Program.cs                    — Avalonia desktop entry point
├── MainWindow.axaml / .cs        — K-PACS-style study browser with Database / Network / Filesystem modes
├── StudyViewerWindow.axaml / .cs — Multi-viewport study viewer with thumbnails, LUTs, stack tool, progressive remote loading, and volume-aware slab navigation
├── ViewerTypes.cs                — ColorScheme, ViewerTool, MouseWheelMode enumerations
├── Models/
│   ├── BackgroundJobModels.cs    — Background import/send job states and metadata
│   ├── ImageboxModels.cs         — Browser, study, import, filesystem tree, and query models
│   ├── NetworkModels.cs          — Remote archive and network settings models
│   └── ToastNotificationItem.cs  — Transient in-window notification models
├── Controls/
│   └── DicomViewPanel.axaml / .cs— Core viewer control: zoom, pan, window/level, stack scrolling, overlays, and in-panel volume controls
├── Services/
│   ├── BackgroundJobService.cs   — Central job tracking and per-job log files for import/send
│   ├── ImageboxBootstrap.cs      — Local imagebox path setup under LocalApplicationData
│   ├── ImageboxRepository.cs     — SQLite storage for studies, series, instances, and search
│   ├── DicomImportService.cs     — Metadata indexing plus queued/background import into local imagebox/SQLite
│   ├── DicomFilesystemScanService.cs — Scan-only preview of filesystem/DICOMDIR studies
│   ├── DicomStudyDeletionService.cs  — Delete study from SQLite and stored files
│   ├── DicomPseudonymizationService.cs — Pseudonymize imported studies in-place
│   ├── DicomRemoteStudyBrowserService.cs — Remote C-FIND/C-MOVE/C-STORE integration with queued background send
│   ├── NetworkSettingsService.cs — Persistent network-settings.json management
│   ├── RemoteStudyRetrievalSession.cs — Progressive representative-image-first retrieval
│   ├── StorageScpService.cs      — Local Storage SCP receiver with import pipeline
│   ├── VolumeLoaderService.cs    — Series-to-volume loader for axial/coronal/sagittal reconstruction
│   ├── ViewerStudyContext.cs     — Study viewer input context
│   └── WindowPlacementService.cs — Window geometry persistence
├── Windows/
    ├── NetworkInfoWindow.cs      — Runtime network status dialog
    └── NetworkSettingsWindow.cs  — Network configuration dialog
└── Rendering/
    ├── DicomPixelRenderer.cs     — Pixel rendering engine (platform-independent)
    ├── ColorLut.cs               — Color lookup tables (platform-independent)
    ├── SeriesVolume.cs           — In-memory voxel volume model with patient-space geometry
    └── VolumeReslicer.cs         — Orthogonal reslice and slab projection engine (MPR, MipPR, MinPR, MPVRT)
```

## Current Status

### ✅ Completed: DCMClasses Core Library

The entire **DCMClasses** package has been ported — this is the foundational DICOM data layer that everything else builds on. It covers:

- **DICOM parsing & serialization** — reading/writing DICOM files, tag manipulation, sequence navigation, character set support
- **DICOM networking** — full async C-ECHO, C-FIND (Patient/Study/Series/Image level), C-MOVE, C-STORE SCU, and Modality Worklist queries
- **Image objects** — pixel data access, frame extraction, transfer syntax handling, compression/decompression
- **Specialized SOP classes** — Secondary Capture, Encapsulated PDF, Structured Reports, Presentation State (GSPS with annotations, windowing, spatial transforms)
- **DICOMDIR** — creation and reading of media directory structures
- **Utility functions** — UID generation, date/time conversions, patient name parsing, age calculation

### ✅ Completed: Avalonia Study Browser + Viewer (Cross-Platform)

The Avalonia application has moved beyond a single-file viewer and now includes a working **K-PACS-style local study browser** on top of the cross-platform viewer:

- **Database mode** backed by a local SQLite imagebox
- **Filesystem mode** with Windows-style `Computer` root, drive browsing, folder scan, and optional DICOMDIR usage
- **Network mode** with configured remote archive search, preview of remote series, and representative-image-first retrieval into the local imagebox
- **Preview-before-import workflow**: filesystem scans build preview studies first, and opening a study can copy it into the local imagebox in the background while it is already viewable
- **Study actions**: view, send to remote archive, pseudonymize, and delete study (including multi-select send/delete for local studies)
- **Background job monitor** for import/send tasks with progress, per-job logs, and optional DICOM communication trace inspection
- **Multi-window study viewing** with configurable 1-4 viewer windows, persisted placement per viewer, and a safety-first clear-all-open-viewers flow before opening a new study
- **Multi-viewport study viewer** with thumbnail strip, layout selection, LUT switching, stack-tool drag behavior, direct active-viewport selection, and drag-reassignable series layouting
- **Interactive orientation/navigation overlays** including patient left/right markers and a Shift-held transient 3D cursor for cross-view localization
- **Editable measurement tools** including pixel lens, line, angle, rectangular ROI, and polygon ROI, anchored to the referenced slice geometry in patient space
- **Volume-aware series viewing** for real volumetric CT/MR stacks with cached in-memory voxel volumes, axial/coronal/sagittal reslicing, and slab projection modes
- **In-panel volume interaction badges** shown only for true volume datasets: orientation switching, projection-mode switching, and drag-adjustable slab thickness in mm
- **Search/filter support** in Database, Filesystem, and Network mode, including wildcard remote matching and safe blocking of empty remote queries
- **Local Storage SCP receiving** backed by fo-dicom with automatic import into the local imagebox
- **Operational polish** including toast notifications, persisted browser/viewer window placement, persisted study browser splitters, and non-modal viewer windows
- Shares the same platform-independent rendering engine and color LUTs across the current C# viewer stack
- Uses Avalonia pointer events, StorageProvider dialogs, and Semi.Avalonia styling

### Screenshots

#### Study Browser

![K-PACS.neo Avalonia Study Browser](Images/Study%20Browser.png)

#### Viewer / Workflow Detail

![K-PACS.neo Avalonia viewer workflow detail](Images/Screenshot%202026-03-08%20210211.png)

### 🔲 Still To Do

The following major components from the original K-PACS have **not yet been ported**:

| Component | Description |
|---|---|
| **Email Mode** | Import/export or mail-driven workflows |
| **Query/Retrieve SCP** | Full server-side query/retrieve provider and broader service administration |
| **Print** | DICOM Print SCU, print preview, film layout |
| **Report Writer** | Structured report authoring and display |
| **CD Burner** | DICOM media creation with auto-run viewer |
| **RIS Interface** | Worklist-driven workflow integration |
| **Advanced Annotations** | Text annotations, richer editing UX, and more parity with legacy drawing/report tools |
| **Advanced Viewing** | Hanging protocols, lightbox tiling, cine playback, magnifier, and higher-level workflow presets |

## Technology

| | |
|---|---|
| **Language** | C# 13 |
| **Runtime** | .NET 10 |
| **K-PACS DICOM classes** | Self-developed and ported K-PACS DICOM classes in `KPACS.DCMClasses`, serving as the legacy-port foundation and primary domain model layer |
| **DICOM library** | [fo-dicom 5.1.3](https://github.com/fo-dicom/fo-dicom) |
| **DICOM codecs** | [fo-dicom.Codecs 5.13.2](https://github.com/fo-dicom/fo-dicom) |
| **SQLite** | [Microsoft.Data.Sqlite 9.0.3](https://www.nuget.org/packages/Microsoft.Data.Sqlite/) |
| **Cross-platform Viewer** | [Avalonia 11.3.7](https://avaloniaui.net/) (Windows, Linux, macOS) |
| **Avalonia Theme** | [Semi.Avalonia 11.3.7.3](https://www.nuget.org/packages/Semi.Avalonia/) |
| **Original** | Written in Delphi (Object Pascal), ~150k lines of application code |

## Building

```bash
# Core library
dotnet build KPACS.DCMClasses/KPACS.DCMClasses.csproj

# Cross-platform Avalonia viewer
dotnet build KPACS.Viewer.Avalonia/KPACS.Viewer.Avalonia.csproj
```

## Release Packaging

The repository includes a GitHub Actions workflow at [.github/workflows/release-packages.yml](.github/workflows/release-packages.yml) that builds three native self-contained release artifacts for the Avalonia viewer:

- `win-x64` — single-file `.exe` packed as `.zip`
- `linux-x64` — single-file executable packed as `.tar.gz`
- `osx-arm64` — zipped `KPACS-neo.app` bundle for Apple Silicon Macs

These release builds use:

- self-contained .NET 10 runtime
- single-file publish
- embedded native libraries via self-extraction
- release symbols disabled for smaller artifacts

### Local publish commands

These are the exact publish settings used for release packaging:

```bash
dotnet publish KPACS.Viewer.Avalonia/KPACS.Viewer.Avalonia.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false

dotnet publish KPACS.Viewer.Avalonia/KPACS.Viewer.Avalonia.csproj -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false

dotnet publish KPACS.Viewer.Avalonia/KPACS.Viewer.Avalonia.csproj -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=None -p:DebugSymbols=false
```

### GitHub Release flow

- Create a tag such as `v0.1.0`
- Publish a GitHub Release for that tag
- The workflow builds all three platform packages on native runners and attaches them to the release automatically

You can also run the workflow manually with `workflow_dispatch` to generate test artifacts without publishing a release.

### macOS note

The macOS package is emitted as an unsigned `.app` bundle. That is the most practical first-release shape for “download, unpack, start”, but Gatekeeper may still require the user to right-click the app and choose `Open` once unless the app is later code signed and notarized.

## Running

```bash
# Avalonia viewer (any platform)
dotnet run --project KPACS.Viewer.Avalonia/KPACS.Viewer.Avalonia.csproj
```

## Avalonia Browser Workflow

The Avalonia application now supports a practical local-plus-network K-PACS workflow:

- **Database** — studies already imported into the local SQLite imagebox
- **Filesystem** — browse drives/folders, scan DICOM folders or DICOMDIR media, preview studies, then import on demand
- **Filesystem** — browse drives/folders, scan DICOM folders or DICOMDIR media, preview studies, open them immediately, and let the local copy continue in the background
- **Network** — configure one primary remote archive, query studies, preview remote series, and open studies with progressive background retrieval into the local imagebox
- **Email** — placeholder tab for future mail/export workflows

### Local imagebox

The Avalonia application stores imported studies under the current user's local application data folder:

```text
%LOCALAPPDATA%/KPACS.Viewer.Avalonia/Imagebox
```

This contains:

- `imagebox.db` — SQLite metadata database
- `Studies/` — imported DICOM files organized by Study Instance UID / Series Instance UID

Additional runtime configuration files are stored alongside the imagebox root:

- `network-settings.json` — local AE title/port plus the configured remote archive
- `window-placement.json` — persisted browser/viewer geometry
- `study-browser-layout.json` — persisted patient/study and study/series splitter sizes
- `job-logs/` — per-job import/send logs written by the background job service

### Network workflow notes

- Double-clicking a network study loads remote series metadata first, then opens the viewer as soon as the first representative images arrive.
- Remaining series continue downloading in the background through the configured local Storage SCP.
- Local studies can be sent back to the configured archive with the `Send` action in Database mode.
- Empty network searches are blocked on purpose to avoid accidental full-archive C-FIND requests.

### Volume viewing notes

- Volume controls are shown only when a series could be promoted to a true in-memory volume; 2D datasets such as plain radiography, angiography, or ultrasound continue to use the legacy single-slice viewer path.
- The viewer loads the initial series through the existing file-based path first and upgrades matching series to cached voxel volumes in the background.
- The orientation badge inside the view panel switches between Axial, Coronal, and Sagittal.
- The projection badge inside the view panel switches between MPR, MipPR, MinPR, and MPVRT.
- Dragging on the projection badge changes slab thickness in millimeters; holding Shift while dragging enables finer adjustment.

### Remote DICOM archive configuration examples

The Avalonia browser currently stores network configuration in `network-settings.json` under `%LOCALAPPDATA%/KPACS.Viewer.Avalonia`.

Example structure:

```json
{
    "LocalAeTitle": "KPACS",
    "LocalPort": 11112,
    "InboxDirectory": "C:\\Users\\<you>\\AppData\\Local\\KPACS.Viewer.Avalonia\\network-inbox",
    "SelectedArchiveId": "conquest-local",
    "Archives": [
        {
            "Id": "conquest-local",
            "Name": "Local Conquest",
            "Host": "127.0.0.1",
            "Port": 5678,
            "RemoteAeTitle": "CONQUESTSRV1"
        }
    ]
}
```

#### Conquest PACS example

Use this when Conquest is running locally or on a reachable server and accepts query/retrieve on its configured TCP port.

```json
{
    "LocalAeTitle": "KPACS",
    "LocalPort": 11112,
    "InboxDirectory": "C:\\Users\\<you>\\AppData\\Local\\KPACS.Viewer.Avalonia\\network-inbox",
    "SelectedArchiveId": "conquest-main",
    "Archives": [
        {
            "Id": "conquest-main",
            "Name": "Conquest Main",
            "Host": "192.168.1.50",
            "Port": 5678,
            "RemoteAeTitle": "CONQUESTSRV1"
        }
    ]
}
```

Typical Conquest pairing:

- Local Storage SCP in K-PACS: AE `KPACS`, port `11112`
- Remote archive in K-PACS: host `192.168.1.50`, port `5678`, remote AE `CONQUESTSRV1`
- Matching destination configured in Conquest for C-MOVE/C-STORE back to K-PACS: AE `KPACS` at your workstation IP and port `11112`

#### Orthanc example

Orthanc often uses AE title `ORTHANC` and DICOM port `4242` unless changed in its configuration.

```json
{
    "LocalAeTitle": "KPACS",
    "LocalPort": 11112,
    "InboxDirectory": "C:\\Users\\<you>\\AppData\\Local\\KPACS.Viewer.Avalonia\\network-inbox",
    "SelectedArchiveId": "orthanc-dev",
    "Archives": [
        {
            "Id": "orthanc-dev",
            "Name": "Orthanc Dev",
            "Host": "127.0.0.1",
            "Port": 4242,
            "RemoteAeTitle": "ORTHANC"
        }
    ]
}
```

Checklist for any remote PACS:

- The remote PACS must know the workstation as destination AE `KPACS` on port `11112` (or whatever you configure locally).
- Firewalls must allow inbound traffic to the local Storage SCP port.
- AE titles must match exactly, including case and truncation to the DICOM 16-character limit.
- If C-FIND works but retrieve fails, the first thing to check is usually the remote PACS destination table for the local AE.

### Current viewer capabilities

- Window/level, zoom, pan, fit-to-window, and color LUT switching
- Stack tool with accelerated drag scrolling for series browsing
- Click-to-activate viewports inside the study viewer
- Thumbnail strip for jumping between series and drag-dropping series into arbitrary viewports
- Patient orientation markers on the viewer overlay for left/right validation
- Shift-held 3D cursor that projects the hovered location into compatible views without locking the viewer in localization mode
- Measurement mode selector with editable line, angle, rectangular ROI, polygon ROI, and pixel lens tools
- Measurement objects persist against the correct image geometry and are reprojected only onto compatible slices/views from the same acquisition space
- Series overview panel in the browser with persisted splitter sizing
- Progressive remote retrieval support inside the viewer, including status updates while images arrive
- Pseudonymization of imported studies
- Delete-study workflow that removes both database rows and stored files
- Send-to-archive workflow for local studies

## Background

K-PACS was created around 2004 as a free DICOM workstation. It provided a full-featured PACS client including DICOM storage, query/retrieve, viewing, printing, CD burning, and modality worklist — capabilities that were typically only available in expensive commercial products at the time. It gained significant adoption worldwide, particularly in smaller imaging facilities and educational settings.

**K-PACS.neo** preserves the architectural concepts of the original while modernizing the codebase for the .NET platform with cross-platform support via Avalonia.

## License

This project is licensed under the [MIT License](LICENSE).
