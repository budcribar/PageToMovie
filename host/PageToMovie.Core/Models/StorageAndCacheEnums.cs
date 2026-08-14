using System;
using System.Text.Json.Serialization;

namespace PageToMovie.Core.Models;

#region Project Storage & Cache Enums (121-140)

/// <summary>
/// Types of underlying storage providers for project files and assets.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StorageProviderType
{
    LocalStorage,
    AmazonS3,
    AzureBlob,
    GoogleCloudStorage,
    Minio,
    Memory
}

/// <summary>
/// Categorized kinds of files stored within a project workspace.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectFileKind
{
    FountainScript,
    ShotPlan,
    CastManifest,
    VideoClip,
    AudioTrack,
    ThumbnailImage,
    ProjectMetadata,
    ExportedMovie,
    TempArtifact
}

/// <summary>
/// File access and lock modes for storage operation permissions.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FileAccessMode
{
    ReadOnly,
    ReadWrite,
    WriteOnly,
    AppendOnly,
    ExclusiveLock
}

/// <summary>
/// Execution status of database schema migrations.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DatabaseMigrationStatus
{
    NotStarted,
    InProgress,
    Applied,
    Failed,
    RolledBack,
    Skipped
}

/// <summary>
/// Backup strategy modes for project archiving and snapshotting.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectBackupType
{
    Full,
    Incremental,
    Differential,
    Snapshot,
    AutoSave
}

/// <summary>
/// Cleanup strategies for managing transient media assets and temporary files.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StorageCleanupStrategy
{
    Immediate,
    Scheduled,
    ThresholdBased,
    ManualOnly,
    Never
}

/// <summary>
/// Severity alert levels for disk space usage monitoring.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiskSpaceAlertLevel
{
    Normal,
    LowDiskSpace,
    Warning,
    Critical,
    OutofSpace
}

/// <summary>
/// Storage backends for caching intermediate computation and rendered clips.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CacheStorageBackend
{
    InMemory,
    SqliteDb,
    Redis,
    DiskDirectory,
    Hybrid
}

/// <summary>
/// Eviction policies for managing cache capacity limits.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CacheEvictionPolicy
{
    Lru,
    Lfu,
    Fifo,
    TimeToLive,
    SizeBased,
    Manual
}

/// <summary>
/// Compression algorithms supported for project files and caches.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FileCompressionAlgorithm
{
    None,
    Gzip,
    Zip,
    Zstd,
    Brotli,
    Lz4
}

/// <summary>
/// State of concurrency locks on project workspaces.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectLockState
{
    Unlocked,
    SharedRead,
    ExclusiveWrite,
    Archived,
    ReadOnlyLock
}

/// <summary>
/// Database indexing strategy modes for search and query acceleration.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DatabaseIndexingMode
{
    Automatic,
    Manual,
    Deferred,
    Disabled,
    ReindexRequired
}

/// <summary>
/// Results of media asset integrity and existence validation.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AssetValidationResult
{
    Valid,
    MissingFile,
    CorruptedData,
    InvalidFormat,
    ChecksumMismatch,
    SizeMismatch,
    PendingValidation
}

/// <summary>
/// Storage tiering policies governing asset lifecycle and retention.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StorageTierPolicy
{
    Hot,
    Warm,
    Cold,
    Archive,
    AutoTiering
}

/// <summary>
/// Directory structural organization patterns for project assets.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DirectoryStructurePattern
{
    Flat,
    ByScene,
    ByAssetType,
    DateBased,
    Hierarchical
}

/// <summary>
/// Hashing algorithms for file integrity verification and deduplication.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FileHashAlgorithm
{
    Md5,
    Sha1,
    Sha256,
    Crc32,
    XxHash
}

/// <summary>
/// Supported data formats for exporting project data.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DataExportFormat
{
    Json,
    Xml,
    Csv,
    Yaml,
    ZipArchive,
    Fountain
}

/// <summary>
/// Conflict resolution strategies when importing existing projects.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProjectImportConflictStrategy
{
    Overwrite,
    Skip,
    Rename,
    Merge,
    PromptUser
}

/// <summary>
/// Retention period settings for cached files and project backups.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StorageRetentionPeriod
{
    Transient,
    ThirtyDays,
    NinetyDays,
    OneYear,
    Indefinite,
    Custom
}

/// <summary>
/// Transaction isolation levels for database operations.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DatabaseTransactionIsolation
{
    ReadUncommitted,
    ReadCommitted,
    RepeatableRead,
    Serializable,
    Snapshot
}

#endregion

#region Extensions & Parsing

/// <summary>
/// Extension methods and string parsers for StorageAndCacheEnums.
/// </summary>
public static class StorageAndCacheEnumExtensions
{
    public static string ToApiString(this StorageProviderType val) => val switch
    {
        StorageProviderType.LocalStorage => "local_storage",
        StorageProviderType.AmazonS3 => "amazon_s3",
        StorageProviderType.AzureBlob => "azure_blob",
        StorageProviderType.GoogleCloudStorage => "google_cloud_storage",
        StorageProviderType.Minio => "minio",
        StorageProviderType.Memory => "memory",
        _ => "local_storage"
    };

    public static string ToApiString(this ProjectFileKind val) => val switch
    {
        ProjectFileKind.FountainScript => "fountain_script",
        ProjectFileKind.ShotPlan => "shot_plan",
        ProjectFileKind.CastManifest => "cast_manifest",
        ProjectFileKind.VideoClip => "video_clip",
        ProjectFileKind.AudioTrack => "audio_track",
        ProjectFileKind.ThumbnailImage => "thumbnail_image",
        ProjectFileKind.ProjectMetadata => "project_metadata",
        ProjectFileKind.ExportedMovie => "exported_movie",
        ProjectFileKind.TempArtifact => "temp_artifact",
        _ => "project_metadata"
    };

    public static string ToApiString(this FileAccessMode val) => val switch
    {
        FileAccessMode.ReadOnly => "read_only",
        FileAccessMode.ReadWrite => "read_write",
        FileAccessMode.WriteOnly => "write_only",
        FileAccessMode.AppendOnly => "append_only",
        FileAccessMode.ExclusiveLock => "exclusive_lock",
        _ => "read_write"
    };

    public static string ToApiString(this DatabaseMigrationStatus val) => val switch
    {
        DatabaseMigrationStatus.NotStarted => "not_started",
        DatabaseMigrationStatus.InProgress => "in_progress",
        DatabaseMigrationStatus.Applied => "applied",
        DatabaseMigrationStatus.Failed => "failed",
        DatabaseMigrationStatus.RolledBack => "rolled_back",
        DatabaseMigrationStatus.Skipped => "skipped",
        _ => "not_started"
    };

    public static string ToApiString(this ProjectBackupType val) => val switch
    {
        ProjectBackupType.Full => "full",
        ProjectBackupType.Incremental => "incremental",
        ProjectBackupType.Differential => "differential",
        ProjectBackupType.Snapshot => "snapshot",
        ProjectBackupType.AutoSave => "auto_save",
        _ => "full"
    };

    public static string ToApiString(this StorageCleanupStrategy val) => val switch
    {
        StorageCleanupStrategy.Immediate => "immediate",
        StorageCleanupStrategy.Scheduled => "scheduled",
        StorageCleanupStrategy.ThresholdBased => "threshold_based",
        StorageCleanupStrategy.ManualOnly => "manual_only",
        StorageCleanupStrategy.Never => "never",
        _ => "scheduled"
    };

    public static string ToApiString(this DiskSpaceAlertLevel val) => val switch
    {
        DiskSpaceAlertLevel.Normal => "normal",
        DiskSpaceAlertLevel.LowDiskSpace => "low_disk_space",
        DiskSpaceAlertLevel.Warning => "warning",
        DiskSpaceAlertLevel.Critical => "critical",
        DiskSpaceAlertLevel.OutofSpace => "out_of_space",
        _ => "normal"
    };

    public static string ToApiString(this CacheStorageBackend val) => val switch
    {
        CacheStorageBackend.InMemory => "in_memory",
        CacheStorageBackend.SqliteDb => "sqlite_db",
        CacheStorageBackend.Redis => "redis",
        CacheStorageBackend.DiskDirectory => "disk_directory",
        CacheStorageBackend.Hybrid => "hybrid",
        _ => "in_memory"
    };

    public static string ToApiString(this CacheEvictionPolicy val) => val switch
    {
        CacheEvictionPolicy.Lru => "lru",
        CacheEvictionPolicy.Lfu => "lfu",
        CacheEvictionPolicy.Fifo => "fifo",
        CacheEvictionPolicy.TimeToLive => "time_to_live",
        CacheEvictionPolicy.SizeBased => "size_based",
        CacheEvictionPolicy.Manual => "manual",
        _ => "lru"
    };

    public static string ToApiString(this FileCompressionAlgorithm val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this ProjectLockState val) => val switch
    {
        ProjectLockState.Unlocked => "unlocked",
        ProjectLockState.SharedRead => "shared_read",
        ProjectLockState.ExclusiveWrite => "exclusive_write",
        ProjectLockState.Archived => "archived",
        ProjectLockState.ReadOnlyLock => "read_only_lock",
        _ => "unlocked"
    };

    public static string ToApiString(this DatabaseIndexingMode val) => val switch
    {
        DatabaseIndexingMode.Automatic => "automatic",
        DatabaseIndexingMode.Manual => "manual",
        DatabaseIndexingMode.Deferred => "deferred",
        DatabaseIndexingMode.Disabled => "disabled",
        DatabaseIndexingMode.ReindexRequired => "reindex_required",
        _ => "automatic"
    };

    public static string ToApiString(this AssetValidationResult val) => val switch
    {
        AssetValidationResult.Valid => "valid",
        AssetValidationResult.MissingFile => "missing_file",
        AssetValidationResult.CorruptedData => "corrupted_data",
        AssetValidationResult.InvalidFormat => "invalid_format",
        AssetValidationResult.ChecksumMismatch => "checksum_mismatch",
        AssetValidationResult.SizeMismatch => "size_mismatch",
        AssetValidationResult.PendingValidation => "pending_validation",
        _ => "valid"
    };

    public static string ToApiString(this StorageTierPolicy val) => val switch
    {
        StorageTierPolicy.Hot => "hot",
        StorageTierPolicy.Warm => "warm",
        StorageTierPolicy.Cold => "cold",
        StorageTierPolicy.Archive => "archive",
        StorageTierPolicy.AutoTiering => "auto_tiering",
        _ => "hot"
    };

    public static string ToApiString(this DirectoryStructurePattern val) => val switch
    {
        DirectoryStructurePattern.Flat => "flat",
        DirectoryStructurePattern.ByScene => "by_scene",
        DirectoryStructurePattern.ByAssetType => "by_asset_type",
        DirectoryStructurePattern.DateBased => "date_based",
        DirectoryStructurePattern.Hierarchical => "hierarchical",
        _ => "by_asset_type"
    };

    public static string ToApiString(this FileHashAlgorithm val) => val.ToString().ToLowerInvariant();

    public static string ToApiString(this DataExportFormat val) => val switch
    {
        DataExportFormat.Json => "json",
        DataExportFormat.Xml => "xml",
        DataExportFormat.Csv => "csv",
        DataExportFormat.Yaml => "yaml",
        DataExportFormat.ZipArchive => "zip_archive",
        DataExportFormat.Fountain => "fountain",
        _ => "json"
    };

    public static string ToApiString(this ProjectImportConflictStrategy val) => val switch
    {
        ProjectImportConflictStrategy.Overwrite => "overwrite",
        ProjectImportConflictStrategy.Skip => "skip",
        ProjectImportConflictStrategy.Rename => "rename",
        ProjectImportConflictStrategy.Merge => "merge",
        ProjectImportConflictStrategy.PromptUser => "prompt_user",
        _ => "prompt_user"
    };

    public static string ToApiString(this StorageRetentionPeriod val) => val switch
    {
        StorageRetentionPeriod.Transient => "transient",
        StorageRetentionPeriod.ThirtyDays => "30_days",
        StorageRetentionPeriod.NinetyDays => "90_days",
        StorageRetentionPeriod.OneYear => "1_year",
        StorageRetentionPeriod.Indefinite => "indefinite",
        StorageRetentionPeriod.Custom => "custom",
        _ => "indefinite"
    };

    public static string ToApiString(this DatabaseTransactionIsolation val) => val switch
    {
        DatabaseTransactionIsolation.ReadUncommitted => "read_uncommitted",
        DatabaseTransactionIsolation.ReadCommitted => "read_committed",
        DatabaseTransactionIsolation.RepeatableRead => "repeatable_read",
        DatabaseTransactionIsolation.Serializable => "serializable",
        DatabaseTransactionIsolation.Snapshot => "snapshot",
        _ => "read_committed"
    };

    public static StorageProviderType ParseStorageProviderType(string? s, StorageProviderType defaultValue = StorageProviderType.LocalStorage)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<StorageProviderType>(s, true, out var r) ? r : defaultValue;
    }

    public static ProjectFileKind ParseProjectFileKind(string? s, ProjectFileKind defaultValue = ProjectFileKind.ProjectMetadata)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<ProjectFileKind>(s, true, out var r) ? r : defaultValue;
    }

    public static FileAccessMode ParseFileAccessMode(string? s, FileAccessMode defaultValue = FileAccessMode.ReadWrite)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<FileAccessMode>(s, true, out var r) ? r : defaultValue;
    }

    public static DatabaseMigrationStatus ParseDatabaseMigrationStatus(string? s, DatabaseMigrationStatus defaultValue = DatabaseMigrationStatus.NotStarted)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<DatabaseMigrationStatus>(s, true, out var r) ? r : defaultValue;
    }

    public static ProjectBackupType ParseProjectBackupType(string? s, ProjectBackupType defaultValue = ProjectBackupType.Full)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<ProjectBackupType>(s, true, out var r) ? r : defaultValue;
    }

    public static StorageCleanupStrategy ParseStorageCleanupStrategy(string? s, StorageCleanupStrategy defaultValue = StorageCleanupStrategy.Scheduled)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<StorageCleanupStrategy>(s, true, out var r) ? r : defaultValue;
    }

    public static DiskSpaceAlertLevel ParseDiskSpaceAlertLevel(string? s, DiskSpaceAlertLevel defaultValue = DiskSpaceAlertLevel.Normal)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<DiskSpaceAlertLevel>(s, true, out var r) ? r : defaultValue;
    }

    public static CacheStorageBackend ParseCacheStorageBackend(string? s, CacheStorageBackend defaultValue = CacheStorageBackend.InMemory)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<CacheStorageBackend>(s, true, out var r) ? r : defaultValue;
    }

    public static CacheEvictionPolicy ParseCacheEvictionPolicy(string? s, CacheEvictionPolicy defaultValue = CacheEvictionPolicy.Lru)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<CacheEvictionPolicy>(s, true, out var r) ? r : defaultValue;
    }

    public static FileCompressionAlgorithm ParseFileCompressionAlgorithm(string? s, FileCompressionAlgorithm defaultValue = FileCompressionAlgorithm.None)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<FileCompressionAlgorithm>(s, true, out var r) ? r : defaultValue;
    }

    public static ProjectLockState ParseProjectLockState(string? s, ProjectLockState defaultValue = ProjectLockState.Unlocked)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<ProjectLockState>(s, true, out var r) ? r : defaultValue;
    }

    public static DatabaseIndexingMode ParseDatabaseIndexingMode(string? s, DatabaseIndexingMode defaultValue = DatabaseIndexingMode.Automatic)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<DatabaseIndexingMode>(s, true, out var r) ? r : defaultValue;
    }

    public static AssetValidationResult ParseAssetValidationResult(string? s, AssetValidationResult defaultValue = AssetValidationResult.Valid)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<AssetValidationResult>(s, true, out var r) ? r : defaultValue;
    }

    public static StorageTierPolicy ParseStorageTierPolicy(string? s, StorageTierPolicy defaultValue = StorageTierPolicy.Hot)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<StorageTierPolicy>(s, true, out var r) ? r : defaultValue;
    }

    public static DirectoryStructurePattern ParseDirectoryStructurePattern(string? s, DirectoryStructurePattern defaultValue = DirectoryStructurePattern.ByAssetType)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<DirectoryStructurePattern>(s, true, out var r) ? r : defaultValue;
    }

    public static FileHashAlgorithm ParseFileHashAlgorithm(string? s, FileHashAlgorithm defaultValue = FileHashAlgorithm.Sha256)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<FileHashAlgorithm>(s, true, out var r) ? r : defaultValue;
    }

    public static DataExportFormat ParseDataExportFormat(string? s, DataExportFormat defaultValue = DataExportFormat.Json)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<DataExportFormat>(s, true, out var r) ? r : defaultValue;
    }

    public static ProjectImportConflictStrategy ParseProjectImportConflictStrategy(string? s, ProjectImportConflictStrategy defaultValue = ProjectImportConflictStrategy.PromptUser)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<ProjectImportConflictStrategy>(s, true, out var r) ? r : defaultValue;
    }

    public static StorageRetentionPeriod ParseStorageRetentionPeriod(string? s, StorageRetentionPeriod defaultValue = StorageRetentionPeriod.Indefinite)
    {
        if (string.IsNullOrWhiteSpace(s)) return defaultValue;
        var lower = s.ToLowerInvariant().Trim();
        if (lower.Contains("transient")) return StorageRetentionPeriod.Transient;
        if (lower.Contains("30")) return StorageRetentionPeriod.ThirtyDays;
        if (lower.Contains("90")) return StorageRetentionPeriod.NinetyDays;
        if (lower.Contains("1") || lower.Contains("year")) return StorageRetentionPeriod.OneYear;
        if (lower.Contains("indefinite")) return StorageRetentionPeriod.Indefinite;
        if (lower.Contains("custom")) return StorageRetentionPeriod.Custom;
        return Enum.TryParse<StorageRetentionPeriod>(s, true, out var r) ? r : defaultValue;
    }

    public static DatabaseTransactionIsolation ParseDatabaseTransactionIsolation(string? s, DatabaseTransactionIsolation defaultValue = DatabaseTransactionIsolation.ReadCommitted)
    {
        if (string.IsNullOrWhiteSpace(s))
            return defaultValue;
        return Enum.TryParse<DatabaseTransactionIsolation>(s, true, out var r) ? r : defaultValue;
    }

}

#endregion
