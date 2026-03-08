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
├── MainWindow.axaml / .cs        — K-PACS-style study browser with Database / Filesystem modes
├── StudyViewerWindow.axaml / .cs — Multi-viewport study viewer with thumbnails, LUTs, stack tool
├── ViewerTypes.cs                — ColorScheme, ViewerTool, MouseWheelMode enumerations
├── Models/
│   └── ImageboxModels.cs         — Browser, study, import, filesystem tree, and query models
├── Controls/
│   └── DicomViewPanel.axaml / .cs— Core viewer control: zoom, pan, window/level, stack scrolling, overlays
├── Services/
│   ├── ImageboxBootstrap.cs      — Local imagebox path setup under LocalApplicationData
│   ├── ImageboxRepository.cs     — SQLite storage for studies, series, instances, and search
│   ├── DicomImportService.cs     — Import into local imagebox/SQLite
│   ├── DicomFilesystemScanService.cs — Scan-only preview of filesystem/DICOMDIR studies
│   ├── DicomStudyDeletionService.cs  — Delete study from SQLite and stored files
│   ├── DicomPseudonymizationService.cs — Pseudonymize imported studies in-place
│   └── ViewerStudyContext.cs     — Study viewer input context
└── Rendering/
    ├── DicomPixelRenderer.cs     — Pixel rendering engine (platform-independent)
    └── ColorLut.cs               — Color lookup tables (platform-independent)
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
- **Preview-before-import workflow**: filesystem scans build preview studies first, and actual import happens only on explicit `Import` or `View`
- **Study actions**: view, import, pseudonymize, and delete study (including disk files)
- **Multi-viewport study viewer** with thumbnail strip, layout selection, LUT switching, and stack-tool drag behavior ported from the Delphi version
- **Search/filter support** in both Database and Filesystem mode, including multi-select modality filtering
- Shares the same platform-independent rendering engine and color LUTs across the current C# viewer stack
- Uses Avalonia pointer events, StorageProvider dialogs, and Semi.Avalonia styling

### 🔲 Still To Do

The following major components from the original K-PACS have **not yet been ported**:

| Component | Description |
|---|---|
| **Network Query/Retrieve UI** | Remote study browser, server selection, and retrieve workflow |
| **Email Mode** | Import/export or mail-driven workflows |
| **Server / SCP** | C-STORE receiver, Query/Retrieve provider, service management |
| **DICOM Server** | SCP services (C-STORE receiver, Query/Retrieve provider) |
| **Print** | DICOM Print SCU, print preview, film layout |
| **Report Writer** | Structured report authoring and display |
| **CD Burner** | DICOM media creation with auto-run viewer |
| **Settings & Configuration** | DICOM network settings, local storage, server management |
| **RIS Interface** | Worklist-driven workflow integration |
| **Measurements & Annotations** | Distance, angle, ROI, freehand drawing, text annotations |
| **Advanced Viewing** | Hanging protocols, lightbox tiling, cine playback, magnifier |

## Technology

| | |
|---|---|
| **Language** | C# 13 |
| **Runtime** | .NET 10 |
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

## Running

```bash
# Avalonia viewer (any platform)
dotnet run --project KPACS.Viewer.Avalonia/KPACS.Viewer.Avalonia.csproj
```

## Avalonia Browser Workflow

The Avalonia application currently focuses on a local K-PACS-style workflow:

- **Database** — studies already imported into the local SQLite imagebox
- **Filesystem** — browse drives/folders, scan DICOM folders or DICOMDIR media, preview studies, then import on demand
- **Network** — placeholder tab for future query/retrieve work
- **Email** — placeholder tab for future mail/export workflows

### Local imagebox

The Avalonia application stores imported studies under the current user's local application data folder:

```text
%LOCALAPPDATA%/KPACS.Viewer.Avalonia/Imagebox
```

This contains:

- `imagebox.db` — SQLite metadata database
- `Studies/` — imported DICOM files organized by Study Instance UID / Series Instance UID

### Current viewer capabilities

- Window/level, zoom, pan, fit-to-window, and color LUT switching
- Stack tool with accelerated drag scrolling for series browsing
- Thumbnail strip for jumping between series
- Series overview panel in the browser
- Pseudonymization of imported studies
- Delete-study workflow that removes both database rows and stored files

## Background

K-PACS was created around 2004 as a free DICOM workstation. It provided a full-featured PACS client including DICOM storage, query/retrieve, viewing, printing, CD burning, and modality worklist — capabilities that were typically only available in expensive commercial products at the time. It gained significant adoption worldwide, particularly in smaller imaging facilities and educational settings.

**K-PACS.neo** preserves the architectural concepts of the original while modernizing the codebase for the .NET platform with cross-platform support via Avalonia.

## License

This project is licensed under the [MIT License](LICENSE).
