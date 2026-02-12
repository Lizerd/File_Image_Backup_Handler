-- Media Backup Tool - Initial Database Schema
-- SQLite with WAL mode for better concurrent read/write performance

-- Enable WAL mode
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA temp_store=MEMORY;
PRAGMA cache_size=-64000;
PRAGMA foreign_keys=ON;

-- ProjectSettings table (one row per project)
CREATE TABLE IF NOT EXISTS ProjectSettings (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    ProjectName TEXT NOT NULL DEFAULT 'New Project',
    HashLevel INTEGER NOT NULL DEFAULT 2,
    CpuProfile INTEGER NOT NULL DEFAULT 1,
    TargetPath TEXT,
    CurrentState INTEGER NOT NULL DEFAULT 0,
    VerifyByDefault INTEGER NOT NULL DEFAULT 1,
    ArchiveScanningEnabled INTEGER NOT NULL DEFAULT 0,
    ArchiveMaxSizeMB INTEGER NOT NULL DEFAULT 500,
    ArchiveNestedEnabled INTEGER NOT NULL DEFAULT 0,
    ArchiveMaxDepth INTEGER NOT NULL DEFAULT 3,
    MovieHashChunkSizeMB INTEGER NOT NULL DEFAULT 64,
    EnabledCategories TEXT NOT NULL DEFAULT 'Image',
    CreatedUtc TEXT NOT NULL,
    LastModifiedUtc TEXT NOT NULL,
    LastError TEXT
);

-- ScanRoots table (user-selected folders to scan)
CREATE TABLE IF NOT EXISTS ScanRoots (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Path TEXT NOT NULL UNIQUE,
    Label TEXT NOT NULL,
    RootType INTEGER NOT NULL DEFAULT 0,
    IsEnabled INTEGER NOT NULL DEFAULT 1,
    LastScanUtc TEXT,
    FileCount INTEGER DEFAULT 0,
    TotalBytes INTEGER DEFAULT 0,
    AddedUtc TEXT NOT NULL
);

CREATE INDEX IF NOT EXISTS IX_ScanRoots_Path ON ScanRoots(Path);

-- FileInstances table (main table - will have millions of rows)
CREATE TABLE IF NOT EXISTS FileInstances (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    ScanRootId INTEGER NOT NULL,
    RelativePath TEXT NOT NULL,
    FileName TEXT NOT NULL,
    Extension TEXT NOT NULL,
    SizeBytes INTEGER NOT NULL,
    ModifiedUtc TEXT NOT NULL,
    Status INTEGER NOT NULL DEFAULT 0,
    Category INTEGER NOT NULL DEFAULT 0,
    HashId INTEGER,
    DiscoveredUtc TEXT NOT NULL,
    ErrorMessage TEXT,
    FOREIGN KEY (ScanRootId) REFERENCES ScanRoots(Id) ON DELETE CASCADE,
    FOREIGN KEY (HashId) REFERENCES Hashes(Id)
);

-- Indexes for FileInstances (critical for performance)
CREATE INDEX IF NOT EXISTS IX_FileInstances_Extension ON FileInstances(Extension);
CREATE INDEX IF NOT EXISTS IX_FileInstances_SizeBytes ON FileInstances(SizeBytes);
CREATE INDEX IF NOT EXISTS IX_FileInstances_Status ON FileInstances(Status);
CREATE INDEX IF NOT EXISTS IX_FileInstances_ScanRootId ON FileInstances(ScanRootId);
CREATE INDEX IF NOT EXISTS IX_FileInstances_HashId ON FileInstances(HashId);

-- Unique constraint to prevent duplicate file entries (same file path in same scan root)
CREATE UNIQUE INDEX IF NOT EXISTS IX_FileInstances_ScanRootId_RelativePath ON FileInstances(ScanRootId, RelativePath);

-- Hashes table (computed hash values)
CREATE TABLE IF NOT EXISTS Hashes (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    HashAlgorithm TEXT NOT NULL,
    HashBytes BLOB NOT NULL,
    HashHex TEXT NOT NULL,
    SizeBytes INTEGER NOT NULL,
    PartialHashInfo TEXT,
    ComputedUtc TEXT NOT NULL
);

CREATE UNIQUE INDEX IF NOT EXISTS IX_Hashes_HashBytes ON Hashes(HashBytes);
CREATE INDEX IF NOT EXISTS IX_Hashes_HashHex ON Hashes(HashHex);

-- UniqueFiles table (one per unique hash)
CREATE TABLE IF NOT EXISTS UniqueFiles (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    HashId INTEGER NOT NULL UNIQUE,
    RepresentativeFileInstanceId INTEGER,
    FileTypeCategory INTEGER NOT NULL DEFAULT 0,
    CopyEnabled INTEGER NOT NULL DEFAULT 1,
    PlannedFolderNodeId INTEGER,
    PlannedFileName TEXT,
    CopiedUtc TEXT,
    VerifiedUtc TEXT,
    DuplicateCount INTEGER NOT NULL DEFAULT 1,
    FOREIGN KEY (HashId) REFERENCES Hashes(Id),
    FOREIGN KEY (RepresentativeFileInstanceId) REFERENCES FileInstances(Id),
    FOREIGN KEY (PlannedFolderNodeId) REFERENCES FolderNodes(Id)
);

CREATE INDEX IF NOT EXISTS IX_UniqueFiles_HashId ON UniqueFiles(HashId);
CREATE INDEX IF NOT EXISTS IX_UniqueFiles_PlannedFolderNodeId ON UniqueFiles(PlannedFolderNodeId);

-- FolderNodes table (proposed destination folder tree)
CREATE TABLE IF NOT EXISTS FolderNodes (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    ParentId INTEGER,
    DisplayName TEXT NOT NULL,
    ProposedRelativePath TEXT NOT NULL,
    UserEditedName TEXT,
    CopyEnabled INTEGER NOT NULL DEFAULT 1,
    UniqueCount INTEGER DEFAULT 0,
    DuplicateCount INTEGER DEFAULT 0,
    TotalSizeBytes INTEGER DEFAULT 0,
    WhyExplanation TEXT,
    FOREIGN KEY (ParentId) REFERENCES FolderNodes(Id)
);

CREATE INDEX IF NOT EXISTS IX_FolderNodes_Parent ON FolderNodes(ParentId);
CREATE INDEX IF NOT EXISTS IX_FolderNodes_Path ON FolderNodes(ProposedRelativePath);

-- CopyJobs table (copy operation tracking)
CREATE TABLE IF NOT EXISTS CopyJobs (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    UniqueFileId INTEGER NOT NULL,
    DestinationFullPath TEXT NOT NULL,
    Status INTEGER NOT NULL DEFAULT 0,
    AttemptCount INTEGER NOT NULL DEFAULT 0,
    LastError TEXT,
    StartedUtc TEXT,
    CompletedUtc TEXT,
    FOREIGN KEY (UniqueFileId) REFERENCES UniqueFiles(Id)
);

CREATE INDEX IF NOT EXISTS IX_CopyJobs_Status ON CopyJobs(Status);
CREATE INDEX IF NOT EXISTS IX_CopyJobs_UniqueFileId ON CopyJobs(UniqueFileId);

-- Profiles table (file type filter profiles)
CREATE TABLE IF NOT EXISTS Profiles (
    Id INTEGER PRIMARY KEY AUTOINCREMENT,
    Name TEXT NOT NULL,
    ImageExtensions TEXT NOT NULL,
    MovieExtensions TEXT,
    MinSizeBytes INTEGER,
    MaxSizeBytes INTEGER,
    MinImageWidth INTEGER,
    MinImageHeight INTEGER,
    IsDefault INTEGER NOT NULL DEFAULT 0
);

-- Insert default profiles
INSERT OR IGNORE INTO Profiles (Id, Name, ImageExtensions, MovieExtensions, IsDefault)
VALUES
    (1, 'Images Only', '.jpg,.jpeg,.png,.gif,.bmp,.tiff,.tif,.webp,.heic,.heif', NULL, 1),
    (2, 'Images + Movies', '.jpg,.jpeg,.png,.gif,.bmp,.tiff,.tif,.webp,.heic,.heif', '.mp4,.mov,.m4v,.mkv,.avi', 0);
