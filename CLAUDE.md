# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

Media Dedup Backup Tool - A Windows 11 desktop application (C# .NET 8 WPF MVVM) that scans user-selected disks for media files, detects duplicates by checksum, and copies unique files into an organized folder structure on a destination disk.

## Build Commands

```bash
# Build the solution
dotnet build Code/MediaBackupTool/MediaBackupTool.sln

# Build release
dotnet build Code/MediaBackupTool/MediaBackupTool.sln -c Release

# Run the application
dotnet run --project Code/MediaBackupTool/MediaBackupTool/MediaBackupTool.csproj
```

## Architecture

### Technology Stack
- **.NET 8** with **WPF** (Windows Presentation Foundation)
- **MVVM** architecture (bindings-first)
- **SQLite** embedded database (Microsoft.Data.Sqlite + SQLitePCLRaw)
- **SharpCompress** for ZIP/RAR archive handling

### Planned Service Layer
The application follows a layered architecture:
- **UI (WPF Views)**: XAML views, no heavy logic
- **ViewModels**: State, commands, observable properties, validation
- **Application Services**: Scan, Hash, Metadata, Planning, Copy, Power management
- **Data Access Layer**: SQLite repository, migrations, bulk insert
- **Infrastructure**: Logging, settings, file system abstraction

### Key Services (per spec)
1. **ProjectService** - Project lifecycle management
2. **ScanService** - File enumeration with filters
3. **MetadataService** - EXIF/image metadata extraction
4. **HashService** - SHA-1/SHA-256/SHA3-256 hashing with caching
5. **DuplicateService** - Groups files by hash
6. **FolderNamingService** - Smart folder structure generation
7. **PlanService** - Editable folder tree model
8. **CopyService** - Safe copy with verification
9. **ArchiveService** - ZIP/RAR scanning with safety limits
10. **PowerManagementService** - Prevent sleep during operations

### Data Pipeline
Scan → Filter → Hash → Group Duplicates → Plan Folders → Copy

### State Machine States
Idle → Scanning → ScanPaused → Hashing → HashPaused → Planning → ReadyToCopy → Copying → CopyPaused → Completed/Faulted

## Key Design Constraints

- Target scale: ~3,000,000 files, 100,000-500,000 images across 5-8 disks
- Never load full file lists into RAM - use streaming enumeration
- Skip reparse points (junctions/symlinks) to prevent loops
- UI updates throttled to 2-5 times per second
- All heavy work runs off UI thread
- SQLite in WAL mode with batch transactions for performance
- Two logs (debug + warnings/errors) cleared on app startup
- Four CPU profiles: Eco, Balanced, Fast, Max

## File Type Handling

Images: JPG, JPEG, PNG, GIF, BMP, TIFF, WEBP, HEIC, HEIF
Movies: MP4, MOV, M4V, MKV, AVI (with hybrid partial hash)

HEIC/HEIF requires Windows HEIF Image Extension codec for preview/metadata.

## Project File Structure

A "project" creates a folder containing:
- `Project.db` (SQLite database)
- `Logs/Debug.log` and `Logs/WarningsErrors.log`
- `Cache/Thumbnails/` (optional)
