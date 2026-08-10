using System;
using System.Text.Json.Serialization;

namespace PageToMovie.Core.Models;

#region Extended Storage, Cache, UI & Operations Enums (121-150)

/// <summary>
/// Types of underlying storage providers for project files and assets.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StorageProviderTypeKind
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
public enum ProjectFileKindType
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
public enum FileAccessModeKind
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
public enum DatabaseMigrationStatusKind
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
public enum ProjectBackupTypeKind
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
public enum StorageCleanupStrategyKind
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
public enum DiskSpaceAlertLevelKind
{
    Normal,
    LowDiskSpace,
    Warning,
    Critical,
    OutOfSpace
}

/// <summary>
/// Storage backends for caching intermediate computation and rendered clips.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum CacheStorageBackendKind
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
public enum CacheEvictionPolicyKind
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
public enum FileCompressionKind
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
public enum ProjectLockStateKind
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
public enum DatabaseIndexingModeKind
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
public enum AssetValidationResultKind
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
public enum StorageTierPolicyKind
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
public enum DirectoryStructurePatternKind
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
public enum FileHashAlgorithmKind
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
public enum DataExportFormatKind
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
public enum ProjectImportConflictKind
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
public enum StorageRetentionPeriodKind
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
public enum DatabaseTransactionKind
{
    ReadUncommitted,
    ReadCommitted,
    RepeatableRead,
    Serializable,
    Snapshot
}

/// <summary>
/// Core page navigation route destinations across the application.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PageNavigationRouteKind
{
    Home,
    Adaptation,
    Characters,
    Scenes,
    Review,
    Configuration,
    Cost,
    Admin,
    Unknown
}

/// <summary>
/// Layout container width sizing strategy.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LayoutContainerWidthKind
{
    Fluid,
    Fixed,
    Centered,
    FullWidth,
    Narrow
}

/// <summary>
/// Sidebar navigation menu collapse presentation modes.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SidebarCollapseModeKind
{
    Expanded,
    Collapsed,
    Mini,
    Hidden,
    Auto
}

/// <summary>
/// Visual and operational state of UI components.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ComponentDisplayStateKind
{
    Default,
    Loading,
    Success,
    Error,
    Disabled,
    Hidden
}

/// <summary>
/// Data table column sorting directions.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TableSortDirectionKind
{
    Ascending,
    Descending,
    None
}

/// <summary>
/// Data list and table pagination presentation modes.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PaginationModeKind
{
    PageNumbers,
    InfiniteScroll,
    LoadMore,
    Disabled
}

/// <summary>
/// Form input control element types.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FormInputTypeKind
{
    Text,
    Number,
    Select,
    Checkbox,
    Radio,
    TextArea,
    FileInput,
    Toggle
}

/// <summary>
/// Form field and form container validation status states.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum FormValidationStateKind
{
    Unvalidated,
    Valid,
    Invalid,
    Warning,
    Validating
}

/// <summary>
/// Toast notification anchor placement positions on screen.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ToastPositionKind
{
    TopLeft,
    TopCenter,
    TopRight,
    BottomLeft,
    BottomCenter,
    BottomRight
}

/// <summary>
/// Alert notification dismissal interaction behaviors.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AlertDismissBehaviorKind
{
    Manual,
    AutoDismiss,
    Permanent,
    RequireAction
}

#endregion

#region Extensions & Parsing

/// <summary>
/// Extension methods and string parsers for StorageAndCacheExtendedEnums.
/// </summary>
public static class StorageAndCacheExtendedEnumExtensions
{
    public static string ToApiString(this StorageProviderTypeKind val) => val switch
    {
        StorageProviderTypeKind.LocalStorage => "local_storage",
        StorageProviderTypeKind.AmazonS3 => "amazon_s3",
        StorageProviderTypeKind.AzureBlob => "azure_blob",
        StorageProviderTypeKind.GoogleCloudStorage => "google_cloud_storage",
        StorageProviderTypeKind.Minio => "minio",
        StorageProviderTypeKind.Memory => "memory",
        _ => "local_storage"
    };
    public static StorageProviderTypeKind ParseStorageProviderTypeKind(string? s, StorageProviderTypeKind defaultValue = StorageProviderTypeKind.LocalStorage)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<StorageProviderTypeKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static StorageProviderTypeKind ToStorageProviderTypeKind(this string? s, StorageProviderTypeKind defaultValue = StorageProviderTypeKind.LocalStorage)
        => ParseStorageProviderTypeKind(s, defaultValue);

    public static string ToApiString(this ProjectFileKindType val) => val switch
    {
        ProjectFileKindType.FountainScript => "fountain_script",
        ProjectFileKindType.ShotPlan => "shot_plan",
        ProjectFileKindType.CastManifest => "cast_manifest",
        ProjectFileKindType.VideoClip => "video_clip",
        ProjectFileKindType.AudioTrack => "audio_track",
        ProjectFileKindType.ThumbnailImage => "thumbnail_image",
        ProjectFileKindType.ProjectMetadata => "project_metadata",
        ProjectFileKindType.ExportedMovie => "exported_movie",
        ProjectFileKindType.TempArtifact => "temp_artifact",
        _ => "project_metadata"
    };
    public static ProjectFileKindType ParseProjectFileKindType(string? s, ProjectFileKindType defaultValue = ProjectFileKindType.ProjectMetadata)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ProjectFileKindType>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static ProjectFileKindType ToProjectFileKindType(this string? s, ProjectFileKindType defaultValue = ProjectFileKindType.ProjectMetadata)
        => ParseProjectFileKindType(s, defaultValue);

    public static string ToApiString(this FileAccessModeKind val) => val switch
    {
        FileAccessModeKind.ReadOnly => "read_only",
        FileAccessModeKind.ReadWrite => "read_write",
        FileAccessModeKind.WriteOnly => "write_only",
        FileAccessModeKind.AppendOnly => "append_only",
        FileAccessModeKind.ExclusiveLock => "exclusive_lock",
        _ => "read_only"
    };
    public static FileAccessModeKind ParseFileAccessModeKind(string? s, FileAccessModeKind defaultValue = FileAccessModeKind.ReadOnly)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<FileAccessModeKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static FileAccessModeKind ToFileAccessModeKind(this string? s, FileAccessModeKind defaultValue = FileAccessModeKind.ReadOnly)
        => ParseFileAccessModeKind(s, defaultValue);

    public static string ToApiString(this DatabaseMigrationStatusKind val) => val switch
    {
        DatabaseMigrationStatusKind.NotStarted => "not_started",
        DatabaseMigrationStatusKind.InProgress => "in_progress",
        DatabaseMigrationStatusKind.Applied => "applied",
        DatabaseMigrationStatusKind.Failed => "failed",
        DatabaseMigrationStatusKind.RolledBack => "rolled_back",
        DatabaseMigrationStatusKind.Skipped => "skipped",
        _ => "not_started"
    };
    public static DatabaseMigrationStatusKind ParseDatabaseMigrationStatusKind(string? s, DatabaseMigrationStatusKind defaultValue = DatabaseMigrationStatusKind.NotStarted)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<DatabaseMigrationStatusKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static DatabaseMigrationStatusKind ToDatabaseMigrationStatusKind(this string? s, DatabaseMigrationStatusKind defaultValue = DatabaseMigrationStatusKind.NotStarted)
        => ParseDatabaseMigrationStatusKind(s, defaultValue);

    public static string ToApiString(this ProjectBackupTypeKind val) => val switch
    {
        ProjectBackupTypeKind.Full => "full",
        ProjectBackupTypeKind.Incremental => "incremental",
        ProjectBackupTypeKind.Differential => "differential",
        ProjectBackupTypeKind.Snapshot => "snapshot",
        ProjectBackupTypeKind.AutoSave => "auto_save",
        _ => "full"
    };
    public static ProjectBackupTypeKind ParseProjectBackupTypeKind(string? s, ProjectBackupTypeKind defaultValue = ProjectBackupTypeKind.Full)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ProjectBackupTypeKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static ProjectBackupTypeKind ToProjectBackupTypeKind(this string? s, ProjectBackupTypeKind defaultValue = ProjectBackupTypeKind.Full)
        => ParseProjectBackupTypeKind(s, defaultValue);

    public static string ToApiString(this StorageCleanupStrategyKind val) => val switch
    {
        StorageCleanupStrategyKind.Immediate => "immediate",
        StorageCleanupStrategyKind.Scheduled => "scheduled",
        StorageCleanupStrategyKind.ThresholdBased => "threshold_based",
        StorageCleanupStrategyKind.ManualOnly => "manual_only",
        StorageCleanupStrategyKind.Never => "never",
        _ => "scheduled"
    };
    public static StorageCleanupStrategyKind ParseStorageCleanupStrategyKind(string? s, StorageCleanupStrategyKind defaultValue = StorageCleanupStrategyKind.Scheduled)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<StorageCleanupStrategyKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static StorageCleanupStrategyKind ToStorageCleanupStrategyKind(this string? s, StorageCleanupStrategyKind defaultValue = StorageCleanupStrategyKind.Scheduled)
        => ParseStorageCleanupStrategyKind(s, defaultValue);

    public static string ToApiString(this DiskSpaceAlertLevelKind val) => val switch
    {
        DiskSpaceAlertLevelKind.Normal => "normal",
        DiskSpaceAlertLevelKind.LowDiskSpace => "low_disk_space",
        DiskSpaceAlertLevelKind.Warning => "warning",
        DiskSpaceAlertLevelKind.Critical => "critical",
        DiskSpaceAlertLevelKind.OutOfSpace => "out_of_space",
        _ => "normal"
    };
    public static DiskSpaceAlertLevelKind ParseDiskSpaceAlertLevelKind(string? s, DiskSpaceAlertLevelKind defaultValue = DiskSpaceAlertLevelKind.Normal)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<DiskSpaceAlertLevelKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static DiskSpaceAlertLevelKind ToDiskSpaceAlertLevelKind(this string? s, DiskSpaceAlertLevelKind defaultValue = DiskSpaceAlertLevelKind.Normal)
        => ParseDiskSpaceAlertLevelKind(s, defaultValue);

    public static string ToApiString(this CacheStorageBackendKind val) => val switch
    {
        CacheStorageBackendKind.InMemory => "in_memory",
        CacheStorageBackendKind.SqliteDb => "sqlite_db",
        CacheStorageBackendKind.Redis => "redis",
        CacheStorageBackendKind.DiskDirectory => "disk_directory",
        CacheStorageBackendKind.Hybrid => "hybrid",
        _ => "in_memory"
    };
    public static CacheStorageBackendKind ParseCacheStorageBackendKind(string? s, CacheStorageBackendKind defaultValue = CacheStorageBackendKind.InMemory)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<CacheStorageBackendKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static CacheStorageBackendKind ToCacheStorageBackendKind(this string? s, CacheStorageBackendKind defaultValue = CacheStorageBackendKind.InMemory)
        => ParseCacheStorageBackendKind(s, defaultValue);

    public static string ToApiString(this CacheEvictionPolicyKind val) => val switch
    {
        CacheEvictionPolicyKind.Lru => "lru",
        CacheEvictionPolicyKind.Lfu => "lfu",
        CacheEvictionPolicyKind.Fifo => "fifo",
        CacheEvictionPolicyKind.TimeToLive => "time_to_live",
        CacheEvictionPolicyKind.SizeBased => "size_based",
        CacheEvictionPolicyKind.Manual => "manual",
        _ => "lru"
    };
    public static CacheEvictionPolicyKind ParseCacheEvictionPolicyKind(string? s, CacheEvictionPolicyKind defaultValue = CacheEvictionPolicyKind.Lru)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<CacheEvictionPolicyKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static CacheEvictionPolicyKind ToCacheEvictionPolicyKind(this string? s, CacheEvictionPolicyKind defaultValue = CacheEvictionPolicyKind.Lru)
        => ParseCacheEvictionPolicyKind(s, defaultValue);

    public static string ToApiString(this FileCompressionKind val) => val switch
    {
        FileCompressionKind.None => "none",
        FileCompressionKind.Gzip => "gzip",
        FileCompressionKind.Zip => "zip",
        FileCompressionKind.Zstd => "zstd",
        FileCompressionKind.Brotli => "brotli",
        FileCompressionKind.Lz4 => "lz4",
        _ => "none"
    };
    public static FileCompressionKind ParseFileCompressionKind(string? s, FileCompressionKind defaultValue = FileCompressionKind.None)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<FileCompressionKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static FileCompressionKind ToFileCompressionKind(this string? s, FileCompressionKind defaultValue = FileCompressionKind.None)
        => ParseFileCompressionKind(s, defaultValue);

    public static string ToApiString(this ProjectLockStateKind val) => val switch
    {
        ProjectLockStateKind.Unlocked => "unlocked",
        ProjectLockStateKind.SharedRead => "shared_read",
        ProjectLockStateKind.ExclusiveWrite => "exclusive_write",
        ProjectLockStateKind.Archived => "archived",
        ProjectLockStateKind.ReadOnlyLock => "read_only_lock",
        _ => "unlocked"
    };
    public static ProjectLockStateKind ParseProjectLockStateKind(string? s, ProjectLockStateKind defaultValue = ProjectLockStateKind.Unlocked)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ProjectLockStateKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static ProjectLockStateKind ToProjectLockStateKind(this string? s, ProjectLockStateKind defaultValue = ProjectLockStateKind.Unlocked)
        => ParseProjectLockStateKind(s, defaultValue);

    public static string ToApiString(this DatabaseIndexingModeKind val) => val switch
    {
        DatabaseIndexingModeKind.Automatic => "automatic",
        DatabaseIndexingModeKind.Manual => "manual",
        DatabaseIndexingModeKind.Deferred => "deferred",
        DatabaseIndexingModeKind.Disabled => "disabled",
        DatabaseIndexingModeKind.ReindexRequired => "reindex_required",
        _ => "automatic"
    };
    public static DatabaseIndexingModeKind ParseDatabaseIndexingModeKind(string? s, DatabaseIndexingModeKind defaultValue = DatabaseIndexingModeKind.Automatic)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<DatabaseIndexingModeKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static DatabaseIndexingModeKind ToDatabaseIndexingModeKind(this string? s, DatabaseIndexingModeKind defaultValue = DatabaseIndexingModeKind.Automatic)
        => ParseDatabaseIndexingModeKind(s, defaultValue);

    public static string ToApiString(this AssetValidationResultKind val) => val switch
    {
        AssetValidationResultKind.Valid => "valid",
        AssetValidationResultKind.MissingFile => "missing_file",
        AssetValidationResultKind.CorruptedData => "corrupted_data",
        AssetValidationResultKind.InvalidFormat => "invalid_format",
        AssetValidationResultKind.ChecksumMismatch => "checksum_mismatch",
        AssetValidationResultKind.SizeMismatch => "size_mismatch",
        AssetValidationResultKind.PendingValidation => "pending_validation",
        _ => "valid"
    };
    public static AssetValidationResultKind ParseAssetValidationResultKind(string? s, AssetValidationResultKind defaultValue = AssetValidationResultKind.Valid)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<AssetValidationResultKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static AssetValidationResultKind ToAssetValidationResultKind(this string? s, AssetValidationResultKind defaultValue = AssetValidationResultKind.Valid)
        => ParseAssetValidationResultKind(s, defaultValue);

    public static string ToApiString(this StorageTierPolicyKind val) => val switch
    {
        StorageTierPolicyKind.Hot => "hot",
        StorageTierPolicyKind.Warm => "warm",
        StorageTierPolicyKind.Cold => "cold",
        StorageTierPolicyKind.Archive => "archive",
        StorageTierPolicyKind.AutoTiering => "auto_tiering",
        _ => "hot"
    };
    public static StorageTierPolicyKind ParseStorageTierPolicyKind(string? s, StorageTierPolicyKind defaultValue = StorageTierPolicyKind.Hot)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<StorageTierPolicyKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static StorageTierPolicyKind ToStorageTierPolicyKind(this string? s, StorageTierPolicyKind defaultValue = StorageTierPolicyKind.Hot)
        => ParseStorageTierPolicyKind(s, defaultValue);

    public static string ToApiString(this DirectoryStructurePatternKind val) => val switch
    {
        DirectoryStructurePatternKind.Flat => "flat",
        DirectoryStructurePatternKind.ByScene => "by_scene",
        DirectoryStructurePatternKind.ByAssetType => "by_asset_type",
        DirectoryStructurePatternKind.DateBased => "date_based",
        DirectoryStructurePatternKind.Hierarchical => "hierarchical",
        _ => "flat"
    };
    public static DirectoryStructurePatternKind ParseDirectoryStructurePatternKind(string? s, DirectoryStructurePatternKind defaultValue = DirectoryStructurePatternKind.Flat)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<DirectoryStructurePatternKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static DirectoryStructurePatternKind ToDirectoryStructurePatternKind(this string? s, DirectoryStructurePatternKind defaultValue = DirectoryStructurePatternKind.Flat)
        => ParseDirectoryStructurePatternKind(s, defaultValue);

    public static string ToApiString(this FileHashAlgorithmKind val) => val switch
    {
        FileHashAlgorithmKind.Md5 => "md5",
        FileHashAlgorithmKind.Sha1 => "sha1",
        FileHashAlgorithmKind.Sha256 => "sha256",
        FileHashAlgorithmKind.Crc32 => "crc32",
        FileHashAlgorithmKind.XxHash => "xxhash",
        _ => "sha256"
    };
    public static FileHashAlgorithmKind ParseFileHashAlgorithmKind(string? s, FileHashAlgorithmKind defaultValue = FileHashAlgorithmKind.Sha256)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<FileHashAlgorithmKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static FileHashAlgorithmKind ToFileHashAlgorithmKind(this string? s, FileHashAlgorithmKind defaultValue = FileHashAlgorithmKind.Sha256)
        => ParseFileHashAlgorithmKind(s, defaultValue);

    public static string ToApiString(this DataExportFormatKind val) => val switch
    {
        DataExportFormatKind.Json => "json",
        DataExportFormatKind.Xml => "xml",
        DataExportFormatKind.Csv => "csv",
        DataExportFormatKind.Yaml => "yaml",
        DataExportFormatKind.ZipArchive => "zip_archive",
        DataExportFormatKind.Fountain => "fountain",
        _ => "json"
    };
    public static DataExportFormatKind ParseDataExportFormatKind(string? s, DataExportFormatKind defaultValue = DataExportFormatKind.Json)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<DataExportFormatKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static DataExportFormatKind ToDataExportFormatKind(this string? s, DataExportFormatKind defaultValue = DataExportFormatKind.Json)
        => ParseDataExportFormatKind(s, defaultValue);

    public static string ToApiString(this ProjectImportConflictKind val) => val switch
    {
        ProjectImportConflictKind.Overwrite => "overwrite",
        ProjectImportConflictKind.Skip => "skip",
        ProjectImportConflictKind.Rename => "rename",
        ProjectImportConflictKind.Merge => "merge",
        ProjectImportConflictKind.PromptUser => "prompt_user",
        _ => "overwrite"
    };
    public static ProjectImportConflictKind ParseProjectImportConflictKind(string? s, ProjectImportConflictKind defaultValue = ProjectImportConflictKind.Overwrite)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ProjectImportConflictKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static ProjectImportConflictKind ToProjectImportConflictKind(this string? s, ProjectImportConflictKind defaultValue = ProjectImportConflictKind.Overwrite)
        => ParseProjectImportConflictKind(s, defaultValue);

    public static string ToApiString(this StorageRetentionPeriodKind val) => val switch
    {
        StorageRetentionPeriodKind.Transient => "transient",
        StorageRetentionPeriodKind.ThirtyDays => "30_days",
        StorageRetentionPeriodKind.NinetyDays => "90_days",
        StorageRetentionPeriodKind.OneYear => "1_year",
        StorageRetentionPeriodKind.Indefinite => "indefinite",
        StorageRetentionPeriodKind.Custom => "custom",
        _ => "indefinite"
    };
    public static StorageRetentionPeriodKind ParseStorageRetentionPeriodKind(string? s, StorageRetentionPeriodKind defaultValue = StorageRetentionPeriodKind.Indefinite)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<StorageRetentionPeriodKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static StorageRetentionPeriodKind ToStorageRetentionPeriodKind(this string? s, StorageRetentionPeriodKind defaultValue = StorageRetentionPeriodKind.Indefinite)
        => ParseStorageRetentionPeriodKind(s, defaultValue);

    public static string ToApiString(this DatabaseTransactionKind val) => val switch
    {
        DatabaseTransactionKind.ReadUncommitted => "read_uncommitted",
        DatabaseTransactionKind.ReadCommitted => "read_committed",
        DatabaseTransactionKind.RepeatableRead => "repeatable_read",
        DatabaseTransactionKind.Serializable => "serializable",
        DatabaseTransactionKind.Snapshot => "snapshot",
        _ => "read_committed"
    };
    public static DatabaseTransactionKind ParseDatabaseTransactionKind(string? s, DatabaseTransactionKind defaultValue = DatabaseTransactionKind.ReadCommitted)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<DatabaseTransactionKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static DatabaseTransactionKind ToDatabaseTransactionKind(this string? s, DatabaseTransactionKind defaultValue = DatabaseTransactionKind.ReadCommitted)
        => ParseDatabaseTransactionKind(s, defaultValue);

    public static string ToApiString(this PageNavigationRouteKind val) => val.ToString().ToLowerInvariant();
    public static PageNavigationRouteKind ParsePageNavigationRouteKind(string? s, PageNavigationRouteKind defaultValue = PageNavigationRouteKind.Home)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<PageNavigationRouteKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static PageNavigationRouteKind ToPageNavigationRouteKind(this string? s, PageNavigationRouteKind defaultValue = PageNavigationRouteKind.Home)
        => ParsePageNavigationRouteKind(s, defaultValue);

    public static string ToApiString(this LayoutContainerWidthKind val) => val switch
    {
        LayoutContainerWidthKind.FullWidth => "full_width",
        _ => val.ToString().ToLowerInvariant()
    };
    public static LayoutContainerWidthKind ParseLayoutContainerWidthKind(string? s, LayoutContainerWidthKind defaultValue = LayoutContainerWidthKind.Fluid)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<LayoutContainerWidthKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static LayoutContainerWidthKind ToLayoutContainerWidthKind(this string? s, LayoutContainerWidthKind defaultValue = LayoutContainerWidthKind.Fluid)
        => ParseLayoutContainerWidthKind(s, defaultValue);

    public static string ToApiString(this SidebarCollapseModeKind val) => val.ToString().ToLowerInvariant();
    public static SidebarCollapseModeKind ParseSidebarCollapseModeKind(string? s, SidebarCollapseModeKind defaultValue = SidebarCollapseModeKind.Expanded)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<SidebarCollapseModeKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static SidebarCollapseModeKind ToSidebarCollapseModeKind(this string? s, SidebarCollapseModeKind defaultValue = SidebarCollapseModeKind.Expanded)
        => ParseSidebarCollapseModeKind(s, defaultValue);

    public static string ToApiString(this ComponentDisplayStateKind val) => val.ToString().ToLowerInvariant();
    public static ComponentDisplayStateKind ParseComponentDisplayStateKind(string? s, ComponentDisplayStateKind defaultValue = ComponentDisplayStateKind.Default)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ComponentDisplayStateKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static ComponentDisplayStateKind ToComponentDisplayStateKind(this string? s, ComponentDisplayStateKind defaultValue = ComponentDisplayStateKind.Default)
        => ParseComponentDisplayStateKind(s, defaultValue);

    public static string ToApiString(this TableSortDirectionKind val) => val.ToString().ToLowerInvariant();
    public static TableSortDirectionKind ParseTableSortDirectionKind(string? s, TableSortDirectionKind defaultValue = TableSortDirectionKind.None)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<TableSortDirectionKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static TableSortDirectionKind ToTableSortDirectionKind(this string? s, TableSortDirectionKind defaultValue = TableSortDirectionKind.None)
        => ParseTableSortDirectionKind(s, defaultValue);

    public static string ToApiString(this PaginationModeKind val) => val switch
    {
        PaginationModeKind.PageNumbers => "page_numbers",
        PaginationModeKind.InfiniteScroll => "infinite_scroll",
        PaginationModeKind.LoadMore => "load_more",
        PaginationModeKind.Disabled => "disabled",
        _ => "page_numbers"
    };
    public static PaginationModeKind ParsePaginationModeKind(string? s, PaginationModeKind defaultValue = PaginationModeKind.PageNumbers)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<PaginationModeKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static PaginationModeKind ToPaginationModeKind(this string? s, PaginationModeKind defaultValue = PaginationModeKind.PageNumbers)
        => ParsePaginationModeKind(s, defaultValue);

    public static string ToApiString(this FormInputTypeKind val) => val switch
    {
        FormInputTypeKind.TextArea => "text_area",
        FormInputTypeKind.FileInput => "file_input",
        _ => val.ToString().ToLowerInvariant()
    };
    public static FormInputTypeKind ParseFormInputTypeKind(string? s, FormInputTypeKind defaultValue = FormInputTypeKind.Text)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<FormInputTypeKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static FormInputTypeKind ToFormInputTypeKind(this string? s, FormInputTypeKind defaultValue = FormInputTypeKind.Text)
        => ParseFormInputTypeKind(s, defaultValue);

    public static string ToApiString(this FormValidationStateKind val) => val.ToString().ToLowerInvariant();
    public static FormValidationStateKind ParseFormValidationStateKind(string? s, FormValidationStateKind defaultValue = FormValidationStateKind.Unvalidated)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<FormValidationStateKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static FormValidationStateKind ToFormValidationStateKind(this string? s, FormValidationStateKind defaultValue = FormValidationStateKind.Unvalidated)
        => ParseFormValidationStateKind(s, defaultValue);

    public static string ToApiString(this ToastPositionKind val) => val switch
    {
        ToastPositionKind.TopLeft => "top_left",
        ToastPositionKind.TopCenter => "top_center",
        ToastPositionKind.TopRight => "top_right",
        ToastPositionKind.BottomLeft => "bottom_left",
        ToastPositionKind.BottomCenter => "bottom_center",
        ToastPositionKind.BottomRight => "bottom_right",
        _ => "bottom_right"
    };
    public static ToastPositionKind ParseToastPositionKind(string? s, ToastPositionKind defaultValue = ToastPositionKind.BottomRight)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<ToastPositionKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static ToastPositionKind ToToastPositionKind(this string? s, ToastPositionKind defaultValue = ToastPositionKind.BottomRight)
        => ParseToastPositionKind(s, defaultValue);

    public static string ToApiString(this AlertDismissBehaviorKind val) => val switch
    {
        AlertDismissBehaviorKind.AutoDismiss => "auto_dismiss",
        AlertDismissBehaviorKind.RequireAction => "require_action",
        _ => val.ToString().ToLowerInvariant()
    };
    public static AlertDismissBehaviorKind ParseAlertDismissBehaviorKind(string? s, AlertDismissBehaviorKind defaultValue = AlertDismissBehaviorKind.AutoDismiss)
        => string.IsNullOrWhiteSpace(s) ? defaultValue : Enum.TryParse<AlertDismissBehaviorKind>(s.Replace("_", ""), true, out var r) ? r : defaultValue;
    public static AlertDismissBehaviorKind ToAlertDismissBehaviorKind(this string? s, AlertDismissBehaviorKind defaultValue = AlertDismissBehaviorKind.AutoDismiss)
        => ParseAlertDismissBehaviorKind(s, defaultValue);
}

#endregion
