# Media Dedup Backup Tool (Windows 11) — Specification & Architecture (C# .NET 8 WPF MVVM)

**Document purpose**  
This document is the single source of truth for building a Windows 11 desktop application in **C# .NET 8 WPF** (bindings-first, MVVM) that can:

- Scan user-selected **fixed disks and USB disks** (and/or specific folders).
- Handle **only the file types the user selects** (first version focuses on **images**, but includes a ready profile for **images + movies**).
- Build a **local embedded database** of discovered files + checksums.
- Detect duplicates by checksum and **copy only one instance** of each duplicate set into a target disk/folder.
- Propose a **smart target folder structure** (not a single dump folder), biased toward **human-made** wording in the source path, with options.
- Let the user review/edit the proposed folder tree in a **TreeView with inline rename** and **per-folder checkbox** (enabled by default) to decide what gets copied.
- Keep the GUI responsive under heavy load, show detailed progress + ETA, support pause/resume, and prevent PC sleep while work is active.
- Persist everything so the user can close the app and continue later.

**Non-code constraint**  
This document intentionally contains **no code**. It describes the structure, algorithms, data model, UI flows, and implementation techniques.

---

## Table of contents

1. Vision and constraints  
2. Scope (MVP vs future)  
3. Key user workflows  
4. Functional requirements  
5. Non-functional requirements (performance, robustness, UX)  
6. Technology choices and rationale  
7. High-level architecture (MVVM + services)  
8. Data pipeline (scan → hash → plan → copy)  
9. Database design (SQLite)  
10. File enumeration strategy (multi-disk, multi-core, throttled)  
11. Hashing strategy (SHA levels + movie hybrid partial hash)  
12. Archive scanning strategy (ZIP/RAR + nested + safety)  
13. Metadata extraction strategy (images, HEIC/HEIF, movies)  
14. Smart folder naming algorithm (human wording + date inference + safety limits)  
15. Copy planning and destination conflict handling  
16. Copy execution and verification  
17. UI specification (TreeView plan editor, preview panel, progress & diagnostics)  
18. CPU usage control (4 user-friendly profiles)  
19. Logging and diagnostics (two logs cleared on startup)  
20. Power management (prevent sleep while active)  
21. Persistence & resume model (project files)  
22. Error handling & resilience policy  
23. Testing plan  
24. Release packaging and deployment  
25. References (research sources)

---

## 1. Vision and constraints

### 1.1 Primary goal
Create a **single-user, local, private** backup assistant that finds and deduplicates media across many disks and copies unique files into a new organized structure on a destination disk, **without disturbing the originals**.

### 1.2 Core constraints (from user requirements)
- **Windows 11**, **WPF**, **.NET 8**.
- Scan roots are chosen by the user; the app does **not** auto-populate scan paths.
- Scan only **fixed disks and USB disks**.
- Do **not** follow reparse points (junctions/symlinks) to prevent loops and unintended traversal.
- Duplicate definition: **same checksum**.
- When duplicates exist, **copy exactly one** instance; never delete/move/modify sources.
- GUI must remain responsive under high CPU/disk load and provide detailed progress/ETA.
- Must scale to:
  - ~**5–8 disks**
  - ~**3,000,000 files total**
  - ~**100,000–500,000 images**
- Database must require **no special installation**.
- Provide pause/resume for scanning and copying; persist so the user can close and continue later.
- Support scanning inside **.zip** and **.rar** (optional feature toggle).
- Support **HEIC/HEIF** images and common movie formats.
- Provide two logs: a verbose debug log and a warnings/errors log, both **cleared on application startup**.

---

## 2. Scope

### 2.1 MVP (first releasable version)
MVP is “useful and safe” for backing up images, while laying architecture for movies and archives.

MVP must include:
- Project create/load (persistent DB and settings).
- Scan roots selection (folders) on fixed/USB drives.
- File type filter: at minimum images: JPG/JPEG, PNG, GIF, BMP, TIFF, WEBP, HEIC/HEIF (case-insensitive).
- Filter by file size (min/max).
- Optional filter by image dimensions (min width/height).
- Compute checksum (SHA level chosen at start and fixed for project).
- Duplicate detection (hash match).
- Propose destination folder structure (smart naming algorithm v1).
- TreeView plan editor with:
  - Inline rename (rename only that node)
  - Per-folder checkbox (default enabled)
  - Clear visibility of source paths and duplicate occurrences
- Copy unique files to target with safe conflict handling.
- Optional “verify copied file” mode.

### 2.2 v1 extended (still first “solution” but may be phased)
- Add movie formats + hybrid partial hash.
- Add archive scanning (.zip/.rar) with nested option.
- Add estimated time to finish for scan/hash/copy.
- Add CPU usage profiles and dynamic throttling.
- Add image preview panel (fast thumbnails).

### 2.3 Explicit non-goals
- No deletion or modification of source files.
- No network/cloud sync.
- No multi-user concurrency.
- No requirement for perfect “human meaning” understanding; provide good heuristics + user edit controls.

---

## 3. Key user workflows

### 3.1 “New Project” workflow (first time)
1. User clicks **New Project**.
2. User chooses:
   - Project name
   - Project storage folder (where DB + logs + cache live)
   - Hash level (fixed for this project)
   - Media profile (Images only / Images + Movies)
3. App creates a new local project database and initializes logs (cleared/overwritten).

### 3.2 “Select sources” workflow
1. User adds one or more scan roots (folders) from fixed/USB drives.
2. UI shows these roots and allows:
   - Enable/disable root
   - Exclude subfolders patterns (optional but recommended)
3. User selects profile (or customizes extensions/filters).

### 3.3 “Scan” workflow (cataloging)
1. User presses **Start Scan**.
2. App enumerates file system entries under selected roots.
3. For each file:
   - Quickly apply extension and size filters
   - Insert candidate into DB (minimal metadata first)
4. UI shows:
   - Current root/path
   - Files scanned/sec
   - Candidates found
   - Errors encountered count
   - Disk-level progress when possible

User can **pause** or **cancel**.

### 3.4 “Hash” workflow
1. User presses **Compute Hashes** (or it starts automatically after scan).
2. Hashing runs on candidates only and respects CPU usage profile.
3. Hash results are stored in DB.

Movies: hash strategy differs (hybrid partial hash).

### 3.5 “Plan destination folders” workflow
1. After hash stage (or partially), the app:
   - Builds “unique file sets”
   - Chooses a “preferred source instance” per unique file
   - Generates a proposed folder structure
2. User reviews folder tree in TreeView:
   - Can rename folders inline (node-only rename)
   - Can uncheck a folder to skip copying that folder (folder-only toggle)
   - Can inspect duplicates and source locations
   - Can preview images

### 3.6 “Copy” workflow
1. User clicks **Start Copy**.
2. App copies only unique files (one per hash group) into destination folders.
3. If enabled, verify copied file correctness.
4. Progress UI shows throughput and ETA; user can pause/cancel.
5. Optional end-of-job action (allow sleep / shutdown).

### 3.7 “Resume later” workflow
1. User closes app at any time.
2. On restart, user opens project.
3. The app loads DB state and continues:
   - scan remaining roots
   - hash remaining candidates
   - continue copy plan where left off

---

## 4. Functional requirements

### 4.1 File type handling
- Only user-selected file types are processed (hashed/copied/previewed).
- Directory traversal still occurs to find those types.
- Extensions must be treated case-insensitively.
- Default media profile “Images + Movies” includes:
  - Images: JPG, JPEG, PNG, GIF, BMP, TIFF, WEBP, HEIC, HEIF
  - Movies: MP4, MOV, M4V, MKV, AVI (customizable)

### 4.2 Filters
At minimum:
- File size: min/max (bytes, KB, MB, GB)
- Optional: image dimensions min/max
- Optional future: date ranges, EXIF camera model, rating, etc.

Filter performance requirement:
- Filters should be applied as early as possible, using cheap checks first:
  1) extension  
  2) file size  
  3) metadata (dimensions/EXIF) only if needed  

### 4.3 Duplicate detection (by checksum)
- Duplicates are defined as “same hash output under the project’s chosen algorithm.”
- For each duplicate group, only **one** file is copied.
- All occurrences remain recorded and visible in UI.

### 4.4 Target folder selection and planning
- User selects a target root folder on destination disk.
- The app proposes a structured set of target folders, not a single folder.
- Proposed folder names must be editable by the user before copying.

### 4.5 Folder tree editing
- TreeView displays the **proposed target structure**.
- Each folder node has:
  - Checkbox (default enabled) indicating whether that folder is included in copy.
  - Inline rename (applies only to that node).
  - Counts: unique files, duplicates, total size.
- Selecting a folder shows detail panel:
  - List of unique files to be copied into that folder
  - For each unique file: all source occurrences (paths), and which occurrence is chosen as the “preferred instance” for naming.
  - Thumbnail preview (for images).

### 4.6 Archive scanning option
If enabled by the user:
- Scan and treat files inside .zip and .rar as normal candidates:
  - Hash and deduplicate by content
  - Optionally extract/copy into target structure
- Support nested archives (zip-in-zip, rar-in-zip) as an option with max depth.
- Password-protected archives:
  - Do not prompt during scan; record and show list after scan.
- “Very large archive” handling:
  - User sets a maximum archive file size threshold (above threshold: skip or “index only” per policy).
  - Must also protect against decompression bombs (see Section 12.5).

### 4.7 Pause/Resume/Cancel
- Pause must work for:
  - scanning
  - hashing
  - copying
- Cancel must:
  - stop safely
  - persist partial state to DB
  - never corrupt the project

### 4.8 Sleep prevention while active
- While scanning/hashing/copying is active, the app must request that Windows does not enter idle sleep.
- Once all work is stopped/paused/completed, the request must be released.

---

## 5. Non-functional requirements

### 5.1 Performance
- Must handle millions of files without loading file lists into RAM.
- Must avoid UI updates for every file (use throttling/batching).
- Must make scanning and hashing scale across multiple CPU cores **but not overwhelm disk I/O**.
- Must allow “background-friendly” mode (low impact) and “max speed” mode.

### 5.2 Robustness
- Must tolerate:
  - access denied
  - file disappears/moves during scan
  - file changes during hash/copy
  - locked files
  - long paths / invalid characters
  - removable USB disconnects
- Must record errors and continue.

### 5.3 Safety and correctness
- Must not modify sources.
- Copy should be “safe by default”:
  - copy to temporary file then atomic rename
  - optional hash verify after copy
- Archive extraction must prevent path traversal and resource exhaustion.

### 5.4 UX
- UI must remain responsive.
- Provide clear “what’s happening”:
  - which stage
  - current root/path/file
  - throughput
  - estimated time remaining
  - counts (scanned/candidates/hashed/copied/errors)
- Make it easy to use: predefined profiles.

---

## 6. Technology choices and rationale (research-based)

### 6.1 Embedded database recommendation: SQLite
SQLite is recommended as the primary embedded DB because:
- It is **self-contained, serverless, zero-configuration, transactional**, and stores data in a local file.  
  (Source: SQLite “About” page)  
- It supports **WAL (write-ahead logging)** which can improve concurrency and performance, especially for read + write workloads.  
  (Source: sqlite.org WAL documentation)
- It has excellent tooling, stability, and long-term maintenance likelihood.

Packaging approach for .NET:
- Use **Microsoft.Data.Sqlite** with **SQLitePCLRaw bundle** so the native SQLite library is shipped with the app (no user install).  
  (Source: Microsoft.Data.Sqlite custom versions + SQLitePCLRaw bundles)

Why not “install a DB server”:
- User requirement explicitly disallows special installation.
- SQLite is a single-file DB appropriate for private single-user workloads.

### 6.2 WPF MVVM architecture
- WPF UI must stay responsive, meaning all heavy work must run off the UI thread.
- WPF uses a single UI thread with a Dispatcher; background threads must marshal updates to UI.  
  (Source: Microsoft WPF threading model documentation)

### 6.3 TreeView performance and virtualization
TreeView can become slow with many nodes unless optimized:
- Use UI virtualization patterns as recommended by Microsoft’s WPF performance guidance.  
  (Source: Microsoft “Improve the performance of a TreeView” / “Optimize control performance” docs)

### 6.4 Archive library recommendation: SharpCompress
SharpCompress is recommended because it supports ZIP, RAR, 7z, etc. in managed .NET and targets modern .NET versions.
- It can unrar/unzip and provides streaming access.  
  (Source: SharpCompress GitHub and NuGet pages)

### 6.5 HEIC/HEIF support on Windows
- HEIF/HEIC decoding on Windows is typically provided through **Windows Imaging Component (WIC)** extension codecs, installed via Microsoft Store (“HEIF Image Extension”).  
  (Source: Microsoft HEIF codec documentation)
- The application must handle the case where HEIF/HEVC codecs are missing: preview/metadata may be limited and should degrade gracefully.

### 6.6 SHA-3 availability caveat
- .NET includes SHA-3 APIs (for example SHA3-256) but cryptography features may depend on OS support.  
  (Source: Microsoft docs on SHA3_256 and cross-platform cryptography)
- The application should include a fallback implementation for SHA-3 (for example via a managed crypto library) if the platform does not support it.

---

## 7. High-level architecture (MVVM + services)

### 7.1 Layering
- **UI (WPF Views)**: XAML views and styles, zero heavy logic.
- **ViewModels**: state, commands, observable properties, validation, navigation.
- **Application services** (business logic): scan, hash, metadata, planning, copy, estimation, power mgmt.
- **Data access layer**: DB repository, migrations, transactions, bulk insert.
- **Infrastructure**: logging, settings, file system abstraction, OS integration.

### 7.2 Key services (recommended)
1. ProjectService  
   - create/open/close project  
   - manage project folder structure  
2. ScanService  
   - file enumeration  
   - filters  
   - writes candidates to DB  
3. MetadataService  
   - lightweight extraction of image dimensions and EXIF date  
   - movie metadata (duration/resolution if needed)  
4. HashService  
   - compute configured hash  
   - movie hybrid partial hash  
   - hashing cache (skip unchanged files)  
5. DuplicateService  
   - groups by hash  
   - chooses preferred instance  
6. FolderNamingService  
   - proposes folder structure  
   - offers alternative strategies  
7. PlanService  
   - builds editable folder tree model  
   - applies user renames/checkboxes  
8. CopyService  
   - copy execution  
   - verification  
   - retry policy  
9. ArchiveService  
   - discover archive entries  
   - scan nested archives if enabled  
   - safe extraction  
10. PowerManagementService  
   - prevent sleep while active  
   - release on stop  
11. LoggingService  
   - debug + warnings/errors logs  
12. EstimationService  
   - compute throughput + ETA per stage  

### 7.3 State machine
Define explicit states so the UI always knows what can happen next:

- Idle  
- Scanning  
- ScanPaused  
- Hashing  
- HashPaused  
- Planning  
- ReadyToCopy  
- Copying  
- CopyPaused  
- Completed  
- Faulted (recoverable)

The state machine must be persisted in DB so project reload resumes correctly.

---

## 8. Data pipeline (scan → hash → plan → copy)

### 8.1 Why a pipeline?
For 3M files, a pipeline prevents:
- Huge memory lists (never keep all candidates in RAM)
- UI freezing
- Uncontrolled parallelism that destroys disk performance

Pipeline also enables:
- Resume after crash or app close
- Clear progress metrics per stage
- CPU usage profiles

### 8.2 Recommended pipeline stages
Stage A — Enumerate (cheap)  
- Traverse file system from selected roots.
- Emit minimal candidate records: path, size, timestamps, attributes, extension.

Stage B — Filter (cheap → expensive)  
- Apply user filters in order: extension → size → metadata (if required).

Stage C — Persist candidates (DB write batching)  
- Insert candidate records in batches and inside transactions.

Stage D — Hash (CPU + I/O heavy)  
- Compute checksum based on chosen algorithm and file type strategy.

Stage E — Group duplicates  
- Create “UniqueFile” records keyed by hash.
- Link FileInstances (occurrences) to UniqueFile.

Stage F — Propose plan  
- Choose preferred instance per UniqueFile.
- Derive target folder paths and destination file names.
- Build folder tree.

Stage G — Copy  
- Copy one instance per UniqueFile, according to folder plan and checkboxes.

Stage H — Verify (optional)  
- Compare hash of copied file to expected (or confirm size+partial for movies, depending on safety setting).

### 8.3 Backpressure and bounded queues
Between stages, use bounded buffering to avoid memory blow-up:
- Enumeration must slow down if DB writer or hasher is behind.
- Hashing must slow down if disk I/O is saturated.

### 8.4 UI update throttling
Do not update UI for every processed file:
- Maintain counters in background threads.
- Only push UI updates at a fixed cadence (example: 2–5 times per second).
- Update detailed “current file” text less frequently than raw counters to reduce binding overhead.

---

## 9. Database design (SQLite)

### 9.1 Project model
A “project” is a folder containing:
- `Project.db` (SQLite)
- `Logs/Debug.log` and `Logs/WarningsErrors.log`
- `Cache/Thumbnails/` (optional)
- `Exports/` (optional reports)

### 9.2 Why SQLite fits this use case
- Single-user, local, file-based DB.
- Scales well to hundreds of thousands / millions of rows when indexed correctly.
- Supports WAL and safe transactions.

SQLite is described as “self-contained, serverless, zero-configuration, transactional” by sqlite.org (see References).

### 9.3 DB performance rules (must follow)
- Use transactions for batch inserts.  
  (Source: Microsoft.Data.Sqlite bulk insert guidance)
- Reuse prepared/parameterized statements for repeated inserts.  
  (Source: Microsoft.Data.Sqlite bulk insert guidance)
- Prefer a single writer model: one DB writer thread to reduce lock contention.
- Use WAL mode for concurrency improvements if compatible with project needs.  
  (Source: sqlite.org WAL documentation)

### 9.4 Recommended schema (conceptual)
Use normalized tables with a few denormalized computed fields for speed.

**ProjectSettings**
- ProjectId
- CreatedUtc
- HashLevel (0–3)
- HashAlgorithmName (derived)
- MovieHashMode (Full / HybridPartial)
- ArchiveScanningEnabled (bool)
- ArchiveMaxFileSizeBytes
- ArchiveNestedEnabled (bool)
- ArchiveMaxDepth
- CpuProfile (Eco/Balanced/Fast/Max)
- TargetRootPath (string)
- DefaultProfileId (FK)

**Profiles**
- ProfileId
- Name (Images, Images+Movies, Custom)
- IncludedExtensions (stored as rows or JSON)
- Filters (min/max size, min image width/height, etc.)
- MovieExtensions list
- ImageExtensions list

**ScanRoots**
- RootId
- ProjectId
- RootPath
- Enabled
- LastScanUtc
- RootType (Fixed/USB)
- VolumeId (stable volume identifier if available)

**FileInstances** (each discovered file occurrence)
- FileInstanceId
- RootId
- FullPath
- RelativePath (optional for storage efficiency)
- FileName
- Extension
- SizeBytes
- CreatedUtc (from filesystem)
- ModifiedUtc (from filesystem)
- Attributes (bitmask)
- IsFromArchive (bool)
- ArchiveContainerPath (nullable)
- ArchiveEntryPath (nullable)
- Status (Discovered / FilteredOut / HashPending / Hashed / CopyPlanned / Copied / Error)
- ErrorCode + ErrorMessage (nullable)
- HashId (FK, nullable)
- PreferredForUniqueFile (bool)

**Hashes**
- HashId
- HashAlgorithm (same for project but store anyway)
- HashBytes (BLOB)
- HashHexShort (for UI and filenames)
- SizeBytes (redundant but helps)
- PartialHashInfo (for movies hybrid mode: stores parameters used)
- ComputedUtc

**UniqueFiles**
- UniqueFileId
- HashId (unique index)
- RepresentativeFileInstanceId (FK)
- FileTypeCategory (Image/Movie/Other)
- CopyDecision (Copy/Skip)
- PlannedFolderNodeId (FK)
- PlannedFileName
- CopiedUtc (nullable)
- VerifiedUtc (nullable)

**DuplicateLinks**
- UniqueFileId
- FileInstanceId
(Optionally store duplicate grouping details for fast query)

**FolderNodes** (the proposed target folder tree)
- FolderNodeId
- ParentFolderNodeId (nullable)
- DisplayName
- ProposedRelativePath
- UserEditedName (nullable)
- CopyEnabled (bool)
- Stats: UniqueCount, DuplicateCount, TotalSizeBytes (computed/cache)

**CopyJobs**
- CopyJobId
- UniqueFileId
- DestinationFullPath
- Status (Pending/InProgress/Copied/Verified/Skipped/Error)
- AttemptCount
- LastError
- StartedUtc / CompletedUtc

### 9.5 Indices (high impact)
Critical indices:
- FileInstances: (Extension), (SizeBytes), (Status), (RootId), (HashId)
- Hashes: unique index on (HashBytes) or (HashHexShort + HashBytes)
- UniqueFiles: unique index on (HashId)
- CopyJobs: index on (Status), (DestinationFullPath)
- FolderNodes: index on (ParentFolderNodeId)

### 9.6 Storage efficiency considerations
For millions of records, path strings dominate DB size. Consider:
- Store RootPath once in ScanRoots.
- Store RelativePath per instance (RootPath + RelativePath reconstructs FullPath).
- Store FileName separately for quick display/filter.

### 9.7 Integrity and crash recovery
- Use DB transactions at each stage boundary.
- Maintain “work queues” in DB via Status fields.
- If app crashes:
  - On next launch, detect “InProgress” statuses and roll them back to “Pending” for safe retry.

---

## 10. File enumeration strategy (multi-disk, multi-core, throttled)

### 10.1 Do not use memory-heavy enumeration
Avoid methods that return full arrays of files (they allocate huge memory). Prefer streaming enumeration patterns (Microsoft advises enumerable collections are better for large sets).  
(Source: Microsoft “How to enumerate directories and files”)

### 10.2 Reparse points must be skipped
Because user required “No reparse points”:
- On Windows, detect file attributes indicating reparse points and do not descend into them.

### 10.3 Error policy during enumeration
- Access denied: skip and record warning (do not abort scan).
- Directory disappears: skip and record warning.
- USB drive removed: pause scan with a “root unavailable” state; user can reconnect or disable root and continue.

### 10.4 Multi-disk concurrency model (practical)
Goal: keep CPU busy without turning HDDs into random-seek storms.

Recommended model:
- One enumerator per scan root (or per physical volume), but limit active enumerators:
  - For SSD/NVMe: allow more concurrency
  - For HDD: keep concurrency low (often 1 active enumerator per HDD)
- Hashing concurrency should be decoupled from enumeration:
  - enumeration is mostly metadata I/O
  - hashing is heavy sequential reads

### 10.5 Backpressure rules
- If hashing queue is full or DB writer is behind, enumeration must slow down.
- If UI is overloaded with updates, reduce update cadence.

### 10.6 Hidden/system folder defaults (recommended)
For user experience and speed, consider default excludes for:
- System Volume Information
- $RECYCLE.BIN
- Windows system folders (if user selects C:\)
Provide toggles; do not silently skip without visibility.

---

## 11. Hashing strategy (SHA levels + movie hybrid partial hash)

### 11.1 Hash level definition (0–3)
Because the UI uses levels 0–3, define them explicitly:

- Level 0: No cryptographic hash.  
  Use for ultra-fast cataloging only (file name + size + timestamps).  
  Not reliable for final duplicate decisions.

- Level 1: SHA-1  
  Fast, widely supported. Collision resistance is not strong for adversarial scenarios, but may be acceptable for private dedup use. Provide warning.

- Level 2: SHA-256 (SHA-2 family)  
  Recommended default for safety and broad support.

- Level 3: SHA3-256 (SHA-3 family)  
  Strong and modern; may depend on OS support. Provide fallback.

Note: The hash level is selected at project creation and is fixed for that project. All hashes in DB are comparable because the algorithm does not change mid-project.

### 11.2 Hash caching (skip unchanged)
User requirement: cache hashes so unchanged files do not get re-hashed across sessions.

Cache key:
- FullPath (or RootId+RelativePath)
- SizeBytes
- ModifiedUtc (last write time)

If all match previous record and hash exists → reuse hash.

### 11.3 Hashing I/O strategy
- Hashing should read files in large sequential blocks.
- Use OS hints to optimize sequential reads (where applicable).
- Limit parallel hashing based on CPU profile and disk type.

### 11.4 Movie hybrid partial hash (first N MB + last N MB)
User requirement: movies use hybrid partial hash.

Define parameters:
- N (MB): configurable in settings/profile (example: default 8–32 MB).
- Include file size in the fingerprint.
- Fingerprint = size + hash(first N MB) + hash(last N MB).

Collision policy:
- Because this is not a full hash, provide an optional “confirm duplicates with full hash before copy” switch.
- If “verify copy” is enabled, verification for movies should default to “size + same partial hash” unless the user chooses “full verify”.

### 11.5 “Ultra-fast scan” mode
User requirement: ultra-fast scan using file name and size.

Define:
- Mode catalogs candidates and estimates duplicates by (file name + size).  
- Mark duplicates found in this mode as “unconfirmed.”
- Provide a one-click transition to “Compute hashes now” to confirm.

---

## 12. Archive scanning strategy (ZIP/RAR + nested + safety)

### 12.1 User toggle model
Archive scanning must be optional because it can explode runtime.

Settings:
- Enable archive scanning (bool)
- Allowed archive types: zip, rar (optionally 7z later)
- Nested scanning enabled (bool) with max depth (integer)
- Max archive file size (bytes) to process
- Max total uncompressed bytes per archive (safety limit)
- Max entry count per archive (safety limit)
- Policy for encrypted archives: “record + skip”

### 12.2 Representation in DB
Archive entries should be stored as FileInstances with:
- IsFromArchive = true
- ArchiveContainerPath = path to the archive on disk
- ArchiveEntryPath = internal path inside archive

Unique identity:
- ContainerPath + EntryPath

### 12.3 Hashing archive entries
Hashing must be computed on decompressed stream data.
- This can be expensive; respect CPU/I/O throttles.
- Consider prioritizing non-archive candidates first so the app remains useful sooner.

### 12.4 Extraction/copy behavior
When user enables “copy from archives”:
- Extract file to destination folder according to the same folder naming algorithm.
- Ensure safe extraction:
  - Prevent path traversal (“..” segments)
  - Normalize separators
  - Disallow absolute entry paths
- If extracted file name conflicts, apply the same conflict policy as normal copies.

### 12.5 Safety limits (must have)
Archives can be hostile (zip bombs). Even for private users, corrupted archives can behave badly.

Must implement:
- Max archive size threshold (user-configured)
- Max uncompressed bytes threshold (hard default + user override)
- Max number of entries threshold
- Time budget per archive (optional but recommended)
- Nested depth limit
- If limits exceeded: stop processing that archive and record warning.

### 12.6 Library recommendation
Use SharpCompress for ZIP/RAR handling because it supports .NET 8 and can read RAR and ZIP (see References).

---

## 13. Metadata extraction strategy (images, HEIC/HEIF, movies)

### 13.1 Goals
Metadata is used for:
- filter by image size (dimensions)
- folder naming (date, event inference)
- preview thumbnails
- optional: movie duration/resolution

### 13.2 Minimize cost
Reading full metadata for 500k images can be expensive.
Strategy:
- Only extract metadata when needed by filters or naming rules.
- Cache extracted metadata in DB so it is not re-read across sessions.
- Prefer “header-only” or “streaming metadata extraction” libraries.

### 13.3 HEIC/HEIF specifics
On Windows, HEIF decoding is typically provided by WIC extension codec (HEIF Image Extension).
(Source: Microsoft HEIF codec documentation)

Implications:
- If codec is missing:
  - Hashing still works (hash is content-based and does not need decoding)
  - Preview/metadata extraction may fail; must degrade gracefully:
    - show placeholder thumbnail
    - store “codec missing” warning
- Provide a UI hint in diagnostics: “Install HEIF Image Extension for preview support.”

### 13.4 Recommended metadata library approach
For metadata (EXIF, timestamps, some container formats):
- Consider using a library that supports HEIF and common media containers (QuickTime/MP4) for dates/duration.
Example: metadata-extractor release notes indicate HEIF support. (See References.)

For thumbnail preview:
- Prefer Windows-native thumbnail generation when available, because it can leverage installed codecs and system cache.

### 13.5 Metadata fields to store (minimal useful set)
Images:
- Width, Height
- EXIF DateTimeOriginal (if present)
- Camera make/model (optional)
- Orientation (optional)
- GPS (optional)

Movies:
- Container create date (if available)
- Duration (optional)
- Resolution (optional)

Store “metadata extraction status” and error reason if fails.

---

## 14. Smart folder naming algorithm

This is the heart of the application: generate a human-friendly, stable destination structure that the user can correct before copy.

### 14.1 Inputs
For each unique file (hash group), gather:
- All source occurrences (paths)
- Preferred instance path (computed)
- File metadata (EXIF date, dimensions, etc.)
- File system dates (created/modified)
- Profile rules (preferred strategies)

### 14.2 Outputs
- ProposedRelativeFolderPath (under target root)
- ProposedFileName (with extension)
- Additional tags for UI explanation (“why this folder name was chosen”)

### 14.3 Core principles
1. **Bias toward human-made folder segments** (user preference).  
2. Avoid dumping everything in one folder.  
3. Keep names stable and predictable.  
4. Avoid too-long paths and illegal Windows filename characters.  
5. Provide alternatives and let user rename.

### 14.4 Segment classification: “human-made” vs “device/system”
Implement a scoring heuristic for each source path segment.

A segment is more likely “human-made” if:
- Contains letters in any Unicode alphabet (not only A–Z)
- Contains spaces or common word separators (space, hyphen, underscore)
- Contains mixed case or normal language patterns
- Has low digit ratio (e.g., less than 40% digits)
- Does NOT match known camera/import patterns

A segment is more likely “device/system-generated” if it matches patterns such as:
- DCIM, 100APPLE, 101APPLE, 115APPLE
- “Internal Storage”
- “Camera”, “Screenshots” (these may be semi-human; treat as neutral)
- pure numeric segments: YYYYMMDD, 202211__, IMG_1234, DSC0001

Important: do not hardcode English-only assumptions. Use Unicode category checks and simple numeric/letter ratios to support different languages.

### 14.5 Date inference rules
Preferred date sources (in order):
1. EXIF DateTimeOriginal (images)
2. Container metadata date (movies)
3. File modified date
4. File created date
5. Fallback: unknown date

Define a “date confidence” value so UI can show “High/Medium/Low confidence”.

### 14.6 Choosing the “preferred source instance” among duplicates
Because duplicates can exist in different folders, choose the path that best represents human intent.

Score each occurrence path by:
- Highest sum of human-made segment scores
- Penalize segments known as “backup/import/system”
- Reward segments that look like event names (contains words and maybe a date)
- Prefer shallower path if scores tie (usually more curated)
- Store the decision explanation for UI

Allow the UI to show:
- The chosen preferred instance
- Alternative occurrences

(Optional later feature: user override “use this occurrence as preferred for naming”)

### 14.7 Folder naming strategies (provide many options)
Offer these strategies as selectable per profile:

**Strategy A — Date-first, event-second**
- `YYYY\YYYY-MM-DD {EventName}\`
Where:
- Date from inference rules
- EventName from best human segment near the date in the source path

**Strategy B — Event-first, date-second**
- `{EventName}\YYYY-MM-DD\`

**Strategy C — Source-human-tail**
- Find the best “human” segment group in the source path and mirror it:
- `{HumanTail}\`
Example: `20200619 Pellbo Midsommar\`

**Strategy D — Device/import aware**
- Bucket by detected source:
- `iPhone\YYYY\...`
- `Camera\YYYY\...`

**Strategy E — Minimal (safe)**
- `YYYY\YYYY-MM-DD\`
No event names, minimizes path length risk.

The default should favor the user preference:
- Keep human source wording when present, but remain safe on path length.

### 14.8 HumanTail extraction (recommended method)
Given a source path split into segments:
1. Remove segments that are clearly system/device-generated (DCIM, Internal Storage, numeric-only, “__” noise).
2. Search for the segment with the highest human score.
3. Expand around it to include adjacent segments if they are also human-ish and not too long.
4. Normalize:
   - Trim repeated date prefixes (e.g., “2024-05-14 001” → “2024-05-14”)
   - Remove bracket wrappers like “[-Bilder-]” if configured
5. Result becomes EventName/HumanTail candidate.

### 14.9 Normalization & Windows-safe names
Windows folder/file name rules:
- Remove invalid characters: < > : " / \ | ? *
- Trim trailing dots/spaces
- Avoid reserved device names (CON, PRN, AUX, NUL, COM1…)
- Keep Unicode (do not force ASCII) to support different languages.

### 14.10 Path length safety
Even though long paths can be enabled, do not rely on it being enabled on the user’s system.

Design policy:
- Target path builder must enforce a maximum length budget.
- If budget exceeded:
  1) shorten event name (truncate words)
  2) if still too long, replace long segment with shortened version + short hash
  3) as last resort, fall back to Strategy E (date-only)

Long path background:
- Windows supports extended-length paths with the “\\?\” prefix and can go up to ~32K characters, but apps must opt in and system policy may apply. (See References.)

### 14.11 Explanation and transparency
For each proposed folder node, store “Why” metadata:
- Selected date source and confidence
- Selected human segment(s)
- Any normalization performed
This is displayed in the UI so user trusts the plan.

---

## 15. Copy planning and destination conflict handling

### 15.1 Copy unit: UniqueFile
Copy is executed per UniqueFile (one per hash).

### 15.2 Destination folder assignment
Each UniqueFile is assigned to a FolderNode in the proposed tree based on the naming algorithm.

### 15.3 Destination file naming
Default: keep original file name from preferred source instance.

Conflict cases:
- Two unique files can have same file name in same destination folder.
Resolution policy:
1. If a name collision occurs, append a short hash suffix.
2. If still collides (extremely unlikely), append an incrementing counter.

### 15.4 TreeView folder checkbox behavior
- Checkbox is per folder only.
- If folder is unchecked, all UniqueFiles under it are skipped from copying.
- UI should show that skipped files remain in DB and can be copied later by re-enabling.

### 15.5 “Show source mapping” requirement
For each folder and file in plan:
- Show which source file will be copied (preferred instance)
- Show all duplicates (paths)
- Provide quick “copy source path” action

---

## 16. Copy execution and verification

### 16.1 Copy safety rules
- Never overwrite existing files silently.
- Copy into a temporary file first, then rename to final name.
- If target file exists:
  - if it matches expected hash → mark as already copied
  - else apply name collision policy

### 16.2 Verification option
If enabled:
- After copy, verify destination content matches expected:
  - For images: full hash compare (same algorithm as project)
  - For movies: configurable:
    - compare partial fingerprint
    - or compute full hash and compare (safer but slower)

### 16.3 Retry policy
On transient errors (file locked, temporary IO error):
- Retry a few times with increasing delay.
- If still fails:
  - mark CopyJob as Error
  - continue with other jobs
  - show error list and allow “Retry failed” later

### 16.4 Pause/resume behavior in copy stage
Pause should:
- stop starting new copy jobs
- allow the currently copying file to finish (or optionally abort safely)
Resume continues pending jobs.

### 16.5 Optional post-completion action
User option:
- Do nothing
- Allow sleep again (default is always to release requests)
- Shutdown PC (optional; ensure user confirmation at start)

---

## 17. UI specification

### 17.1 Main navigation layout
Recommended tabs/sections:
1. Project
2. Sources & Filters
3. Scan & Hash
4. Plan (TreeView)
5. Copy
6. Duplicates & Search
7. Logs / Diagnostics
8. Settings

### 17.2 Project page
- New project
- Open project
- Recent projects list
- Project summary: #files scanned, #candidates, #hashed, #unique, #copied

### 17.3 Sources & Filters page
- Scan roots list with add/remove
- Show drive type (Fixed/USB)
- Filter editor:
  - extension include list (pre-filled by profile)
  - file size min/max
  - image size min width/height
- Archive settings toggle and limits
- Movie settings:
  - hybrid partial hash parameters (N MB)

### 17.4 Scan & Hash page
- Big Start/Stop/Pause buttons
- Progress per stage:
  - Scan: directories visited, files scanned, candidates found
  - Hash: queued, in progress, completed
- Current activity:
  - current root, current folder, current file (throttled updates)
- Throughput metrics:
  - scan files/sec
  - hash MB/sec
- ETA:
  - per stage and total

### 17.5 Plan page (TreeView + preview panel)
Left: TreeView of proposed target folders.
- Inline rename on folder label.
- Checkbox per folder (default checked).
- Display counts per node (unique count, size).
- Visual indicator if node name was auto-generated vs user-edited.

Right: Details panel for selected folder:
- Folder “why” explanation (date source, human segment used)
- List of UniqueFiles in folder:
  - File name
  - size
  - date used for naming
  - duplicate count
- For selected UniqueFile:
  - Preferred source path
  - All duplicate paths list

Preview panel:
- Fast thumbnail for images.
- Use caching (memory + disk thumbnail cache).
- If HEIC thumbnail fails due to missing codecs, show placeholder and explain.

### 17.6 Copy page
- Target root selection
- Start/Pause/Cancel copy
- Progress: files copied, bytes copied, throughput, ETA
- Verification status
- Failed jobs list with “Retry failed” button

### 17.7 Duplicates & Search page
- Search by hash, file name, extension, size, date
- Show duplicate groups:
  - Unique hash
  - number of occurrences
  - list of paths
- Export report (CSV/JSON) (optional)

### 17.8 Logs / Diagnostics page
- Buttons:
  - Open log folder
  - Copy summary to clipboard
- Display:
  - latest warnings/errors
  - statistics on most common errors (access denied, path too long, codec missing)

### 17.9 WPF performance requirements (must follow)
- Use virtualization for large lists (ListView/DataGrid).
- TreeView: apply Microsoft’s recommended performance improvements and virtualization where possible.
  (See References: TreeView performance docs.)
- Do not bind millions of items directly to ObservableCollections.
  - Use paging or virtualized views backed by DB queries.
- Throttle property change notifications for high-frequency progress updates.

---

## 18. CPU usage control (4 profiles)

User requirement: “make it as 4 different profiles so it is easy for the user to set.”

Define four profiles that tune:
- Hash worker parallelism
- Copy worker parallelism
- DB batch sizes
- UI update cadence
- Thread/process priority (optional)

Recommended profiles:

### 18.1 Eco (lowest impact)
- 1 hash worker
- 1 copy worker
- Conservative DB batching
- Lower priority
- UI updates at low frequency
Use case: user wants to keep PC responsive for other work.

### 18.2 Balanced
- Moderate parallelism (e.g., 25–40% of logical cores for hashing, capped)
- 1–2 copy workers
- Normal priority
Default profile.

### 18.3 Fast
- Higher parallelism (e.g., 60–75% of logical cores, capped)
- 2 copy workers
- More aggressive DB batching
Use case: user is mostly idle but still wants UI usable.

### 18.4 Max
- Max safe parallelism (e.g., cores - 1), with disk-aware caps
- 2–4 copy workers depending on destination disk speed
- Highest allowed normal priority (avoid “Realtime”)
Use case: dedicated run overnight.

Important: do not hardcode numbers only. Implement adaptive logic:
- Measure actual throughput and queue backlogs.
- If disk is saturated (MB/s stops improving), reduce concurrency automatically.

---

## 19. Logging and diagnostics

### 19.1 Logs (two files)
Requirement:
- One verbose debug log
- One warnings/errors log
- Both cleared on startup

Recommendation:
- Store logs in `ProjectFolder\Logs\`
- On startup:
  - Overwrite (truncate) both logs
  - Optionally archive previous logs to `Logs\Archive\` if user enables “Keep last run logs” (default off to match requirement strictly)

### 19.2 Log content
Debug log (verbose):
- stage transitions
- thread counts and CPU profile
- scan root start/stop
- performance metrics snapshots
- archive scan details
- folder naming decisions (summary)

Warnings/errors log:
- file access denied
- path too long
- hashing failures
- archive failures
- copy failures + retries
- codec missing warnings for HEIC preview

### 19.3 Correlation and reproducibility
Every log entry should include:
- timestamp (UTC + local)
- stage
- project id
- root id / file instance id (when relevant)

Provide a “Copy diagnostic bundle” feature:
- exports logs + project summary + settings (no private file content, but includes paths)

---

## 20. Power management (prevent sleep while active)

Windows provides APIs to request the system stay awake.

Two viable approaches:
1. Power requests via PowerSetRequest (with a reason string).  
   (Source: Microsoft PowerSetRequest docs)
2. SetThreadExecutionState with ES_SYSTEM_REQUIRED.  
   (Source: Microsoft SetThreadExecutionState docs)

Important constraints (from Microsoft docs):
- These requests do not prevent user-initiated sleep (lid close, power button, explicit Sleep). Apps should respect user actions.

Implementation requirements:
- When scan/hash/copy is active: acquire request.
- When paused/stopped/completed: release request immediately.
- Provide UI status indicator: “Sleep prevented while running”.

---

## 21. Persistence & resume model (project files)

### 21.1 Persisted items
Must persist:
- scan roots and enable flags
- profile selection and filters
- hash level and algorithm
- archive settings
- hashing cache (path+size+modified → hash)
- scan progress state (which roots completed)
- copy plan (folder tree, user edits, checkboxes)
- copy progress (which UniqueFiles already copied/verified)

### 21.2 Resume behavior
On project load:
- Validate that target root is available
- Validate scan roots availability (USB can be missing)
- Offer:
  - Continue where left off
  - Disable missing roots
  - Re-scan changed roots

### 21.3 “Reset project” option
User wants ability to clear and start new:
- Provide “Reset project” that creates a fresh DB but can keep:
  - profiles
  - scan roots list
This should be explicit and irreversible without backup.

---

## 22. Error handling & resilience policy

### 22.1 General rule: never stop for one bad file
- Record error, continue.

### 22.2 Common errors and expected behavior
- UnauthorizedAccessException / SecurityException: record warning, skip.
- PathTooLong: attempt long path strategy; if still fails, record error and skip.
- IOException (file locked): retry if hashing/copying; skip if persistent.
- File not found (changed during processing): record warning, skip.
- Archive encrypted: record and skip.
- Archive corruption: record and skip.
- HEIC codec missing: record warning, still hash/copy.

### 22.3 Data consistency checks
Before hashing/copy:
- Confirm file size and modified timestamp still match DB record.
After hashing:
- Store hash + record times.

For copy verification:
- If mismatch:
  - mark as failed verification
  - keep the copied file but rename it to indicate failure (optional)
  - allow retry

### 22.4 Database corruption policy
- Keep periodic DB backups (optional advanced feature).
- Provide “DB integrity check” tool.
- If corruption detected:
  - offer repair attempt or export recovered data

---

## 23. Testing plan

### 23.1 Unit tests
- Folder naming algorithm:
  - different languages in paths
  - numeric-heavy camera folders
  - very long names
  - illegal character removal
- Duplicate grouping logic:
  - same hash multiple paths
  - collision handling in destination
- Archive safety rules:
  - path traversal entry names
  - nested depth limit
  - max uncompressed bytes

### 23.2 Integration tests
- Small synthetic directory trees with:
  - duplicates
  - access denied folders
  - long path examples
  - mixed extensions
- Run scan → hash → plan → copy → verify.

### 23.3 Performance tests
- Test with hundreds of thousands of files (generate or use sample sets).
- Validate:
  - memory usage stays bounded
  - UI remains interactive
  - throughput scales with CPU profile

---

## 24. Release packaging and deployment

- Build as Windows x64 desktop app.
- Include all dependencies (self-contained deployment recommended).
- Include embedded SQLite native library through bundling (no user install).
- Provide a simple installer or portable zip.
- Ensure the app has permissions needed; run as normal user (no admin required), but note that some folders may be inaccessible.

---

## 25. References (research sources)

The following sources informed the choices and constraints in this document:

- SQLite “About” (self-contained, serverless, zero-config): https://sqlite.org/about.html  
- SQLite WAL documentation: https://sqlite.org/wal.html  
- SQLite PRAGMA reference (durability/performance tradeoffs): https://sqlite.org/pragma.html  
- Microsoft.Data.Sqlite bulk insert guidance (transactions + reuse commands): https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/bulk-insert  
- Microsoft.Data.Sqlite custom SQLite versions / SQLitePCLRaw bundles: https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/custom-versions  
- Microsoft WPF threading model (Dispatcher + UI thread): https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/threading-model  
- Microsoft TreeView performance guidance: https://learn.microsoft.com/en-us/dotnet/desktop/wpf/controls/how-to-improve-the-performance-of-a-treeview  
- Microsoft WPF control performance / virtualization: https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/optimizing-performance-controls  
- Microsoft SetThreadExecutionState (prevent idle sleep): https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-setthreadexecutionstate  
- Microsoft PowerSetRequest (power requests): https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-powersetrequest  
- Microsoft maximum path length limitation / extended paths: https://learn.microsoft.com/en-us/windows/win32/fileio/maximum-file-path-limitation  
- .NET long path support notes: https://github.com/microsoft/dotnet/blob/main/Documentation/compatibility/long-path-support.md  
- Microsoft HEIF codec (WIC extension codec): https://learn.microsoft.com/en-us/windows/win32/wic/heif-codec  
- SharpCompress (ZIP/RAR support): https://github.com/adamhathcock/sharpcompress and https://www.nuget.org/packages/sharpcompress/  
- SHA3_256 API docs: https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.sha3_256  
- Cross-platform cryptography support notes (OS-dependent features): https://learn.microsoft.com/en-us/dotnet/standard/security/cross-platform-cryptography  
- metadata-extractor release notes mentioning HEIF support: https://github.com/drewnoakes/metadata-extractor/releases

---
