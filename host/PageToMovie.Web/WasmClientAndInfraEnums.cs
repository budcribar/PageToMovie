using System.Text.Json.Serialization;

namespace PageToMovie.Web;

/// <summary>
/// Domain Name System record types for cloud infrastructure routing.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DnsRecordType
{
    A,
    Aaaa,
    Cname,
    Mx,
    Txt,
    Ns,
    Srv,
    Ptr,
    Caa,
    Unknown
}

/// <summary>
/// Storage volume types for cloud instance persistence.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum StorageVolumeType
{
    BlockStorage,
    ObjectStorage,
    FileStorage,
    EphemeralLocal,
    NetworkFileShare,
    ColdArchive,
    Unknown
}

/// <summary>
/// Event triggers that invoke serverless compute functions.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ServerlessTriggerKind
{
    HttpRequest,
    TimerSchedule,
    QueueMessage,
    BlobStorageCreated,
    PubSubEvent,
    DatabaseStream,
    Webhook,
    Unknown
}

/// <summary>
/// Network security group rule types for firewall traffic filtering.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NetworkSecurityGroupRule
{
    AllowInbound,
    DenyInbound,
    AllowOutbound,
    DenyOutbound,
    StatefulInspection,
    RateLimitRule,
    Unknown
}

/// <summary>
/// Lifecycle and pricing model for virtual machine instances.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InstanceLifeCycle
{
    OnDemand,
    Spot,
    Reserved,
    DedicatedHost,
    Preemptible,
    Unknown
}

/// <summary>
/// Container orchestration restart policies.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContainerRestartPolicy
{
    Always,
    OnFailure,
    Never,
    UnlessStopped,
    Unknown
}

/// <summary>
/// Duration policies for keeping telemetry and application logs.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LogRetentionPolicy
{
    OneDay,
    SevenDays,
    ThirtyDays,
    NinetyDays,
    OneYear,
    Indefinite,
    Unknown
}

/// <summary>
/// Destination targets for automated backup stores.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BackupStorageLocation
{
    LocalDisk,
    RemoteObjectStore,
    CrossRegionReplica,
    TapeArchive,
    MultiCloudVault,
    Unknown
}

/// <summary>
/// Target recovery point and time objectives for disaster recovery.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DisasterRecoveryRpoRto
{
    NearZero,
    FifteenMinutes,
    OneHour,
    FourHours,
    TwentyFourHours,
    BestEffort,
    Unknown
}

/// <summary>
/// Infrastructure as Code (IaC) provisioner engines.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum InfraProvisionerTool
{
    Terraform,
    OpenTofu,
    Pulumi,
    Ansible,
    CloudFormation,
    Bicep,
    Helm,
    Kubectl,
    Unknown
}

/// <summary>
/// WebAssembly execution environment runtime engines.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WasmRuntimeKind
{
    BrowserV8,
    Wasmer,
    Wasmtime,
    WasmEdge,
    NodeJs,
    Bun,
    Unknown
}

/// <summary>
/// Client-side Web APIs used for local state persistence.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClientStorageType
{
    LocalStorage,
    SessionStorage,
    IndexedDb,
    CacheApi,
    Cookies,
    MemoryOnly,
    Unknown
}

/// <summary>
/// Hardware rendering backends for WebGL/WebGPU graphics context.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WebGlRendererBackend
{
    WebGL1,
    WebGL2,
    WebGPU,
    SoftwareRasterizer,
    DirectXBinding,
    VulkanBinding,
    Unknown
}

/// <summary>
/// Progressive Web App installation status.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PwaInstallState
{
    NotInstalled,
    InstallPromptAvailable,
    Installing,
    Installed,
    Dismissed,
    Unsupported,
    Unknown
}

/// <summary>
/// Touch screen gesture classifications.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TouchGestureType
{
    Tap,
    DoubleTap,
    LongPress,
    SwipeLeft,
    SwipeRight,
    SwipeUp,
    SwipeDown,
    PinchZoom,
    Rotate,
    Unknown
}

/// <summary>
/// Screen display orientation modes.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ScreenOrientationMode
{
    PortraitPrimary,
    PortraitSecondary,
    LandscapePrimary,
    LandscapeSecondary,
    AutoRotate,
    Unknown
}

/// <summary>
/// Client network connectivity and meter status.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum NetworkConnectivityState
{
    Online,
    Offline,
    SlowDownlink,
    CellularMetered,
    WiFiUnmetered,
    Unknown
}

/// <summary>
/// Permission state for web notification API.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClientNotificationPermission
{
    Default,
    Granted,
    Denied,
    Unsupported,
    Unknown
}

/// <summary>
/// Keyboard modifier combinations.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum HardwareKeyCombo
{
    ControlKey,
    ShiftKey,
    AltKey,
    MetaKey,
    ControlShift,
    ControlAlt,
    MetaShift,
    Unknown
}

/// <summary>
/// System clipboard access permission state.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClipboardPermission
{
    Prompt,
    Granted,
    Denied,
    ReadTextOnly,
    WriteTextOnly,
    Unknown
}

/// <summary>
/// Client audio destination endpoint types.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioOutputDeviceType
{
    DefaultSpeaker,
    Headphones,
    BluetoothAudio,
    HdmiOutput,
    VirtualAudioCable,
    Unknown
}

/// <summary>
/// Video playback and rendering resolution targets.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoRenderingQuality
{
    Low360p,
    Medium480p,
    High720p,
    FullHd1080p,
    UltraHd4k,
    AutoAdaptive,
    Unknown
}

/// <summary>
/// Web application UI color theme presets.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClientAppTheme
{
    DarkCinematic,
    LightStudio,
    HighContrast,
    SystemDefault,
    CustomAccent,
    Unknown
}

/// <summary>
/// Underlying layout and execution engine of the client browser.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum BrowserVendorEngine
{
    BlinkChromium,
    GeckoFirefox,
    WebKitSafari,
    EdgeHTML,
    Servo,
    Unknown
}

/// <summary>
/// Memory allocation modes for WebAssembly module instances.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum WebAssemblyMemoryMode
{
    Standard32Bit,
    Memory64Bit,
    SharedArrayBuffer,
    DynamicGrowth,
    FixedAllocation,
    Unknown
}

/// <summary>
/// Service worker worker-thread lifecycle state.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ServiceWorkerState
{
    Installing,
    Installed,
    Activating,
    Activated,
    Redundant,
    Stopped,
    Unknown
}

/// <summary>
/// Web Push subscription registration state.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PushSubscriptionState
{
    Unsubscribed,
    Subscribing,
    Subscribed,
    Expired,
    PermissionDenied,
    Unknown
}

/// <summary>
/// User privacy and telemetry collection consent levels.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClientAnalyticsConsent
{
    OptedIn,
    OptedOut,
    EssentialOnly,
    Pending,
    NotRequired,
    Unknown
}

/// <summary>
/// Allowed operation effects for drag-and-drop actions.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DragDropEffect
{
    None,
    Copy,
    Move,
    Link,
    All,
    Unknown
}

/// <summary>
/// Native file system picker selection modes.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ClientFilePickerMode
{
    OpenFile,
    OpenMultipleFiles,
    OpenFolder,
    SaveFile,
    Unknown
}

/// <summary>
/// Extension methods for WebAssembly client and cloud infrastructure enums.
/// </summary>
public static class WasmClientAndInfraEnumExtensions
{
    public static string ToApiString(this DnsRecordType value) => value switch
    {
        DnsRecordType.A => "a",
        DnsRecordType.Aaaa => "aaaa",
        DnsRecordType.Cname => "cname",
        DnsRecordType.Mx => "mx",
        DnsRecordType.Txt => "txt",
        DnsRecordType.Ns => "ns",
        DnsRecordType.Srv => "srv",
        DnsRecordType.Ptr => "ptr",
        DnsRecordType.Caa => "caa",
        _ => "unknown"
    };

    public static DnsRecordType ParseDnsRecordType(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "a" => DnsRecordType.A,
            "aaaa" => DnsRecordType.Aaaa,
            "cname" => DnsRecordType.Cname,
            "mx" => DnsRecordType.Mx,
            "txt" => DnsRecordType.Txt,
            "ns" => DnsRecordType.Ns,
            "srv" => DnsRecordType.Srv,
            "ptr" => DnsRecordType.Ptr,
            "caa" => DnsRecordType.Caa,
            _ => DnsRecordType.A
        };

    public static string ToApiString(this StorageVolumeType value) => value switch
    {
        StorageVolumeType.BlockStorage => "block_storage",
        StorageVolumeType.ObjectStorage => "object_storage",
        StorageVolumeType.FileStorage => "file_storage",
        StorageVolumeType.EphemeralLocal => "ephemeral_local",
        StorageVolumeType.NetworkFileShare => "network_file_share",
        StorageVolumeType.ColdArchive => "cold_archive",
        _ => "unknown"
    };

    public static StorageVolumeType ParseStorageVolumeType(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "block_storage" or "blockstorage" or "block" => StorageVolumeType.BlockStorage,
            "object_storage" or "objectstorage" or "object" => StorageVolumeType.ObjectStorage,
            "file_storage" or "filestorage" or "file" => StorageVolumeType.FileStorage,
            "ephemeral_local" or "ephemerallocal" or "ephemeral" => StorageVolumeType.EphemeralLocal,
            "network_file_share" or "networkfileshare" or "nfs" => StorageVolumeType.NetworkFileShare,
            "cold_archive" or "coldarchive" or "archive" => StorageVolumeType.ColdArchive,
            _ => StorageVolumeType.BlockStorage
        };

    public static string ToApiString(this ServerlessTriggerKind value) => value switch
    {
        ServerlessTriggerKind.HttpRequest => "http_request",
        ServerlessTriggerKind.TimerSchedule => "timer_schedule",
        ServerlessTriggerKind.QueueMessage => "queue_message",
        ServerlessTriggerKind.BlobStorageCreated => "blob_storage_created",
        ServerlessTriggerKind.PubSubEvent => "pubsub_event",
        ServerlessTriggerKind.DatabaseStream => "database_stream",
        ServerlessTriggerKind.Webhook => "webhook",
        _ => "unknown"
    };

    public static ServerlessTriggerKind ParseServerlessTriggerKind(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "http_request" or "httprequest" or "http" => ServerlessTriggerKind.HttpRequest,
            "timer_schedule" or "timerschedule" or "timer" or "cron" => ServerlessTriggerKind.TimerSchedule,
            "queue_message" or "queuemessage" or "queue" => ServerlessTriggerKind.QueueMessage,
            "blob_storage_created" or "blobstoragecreated" or "blob" => ServerlessTriggerKind.BlobStorageCreated,
            "pubsub_event" or "pubsubevent" or "pubsub" => ServerlessTriggerKind.PubSubEvent,
            "database_stream" or "databasestream" or "db_stream" => ServerlessTriggerKind.DatabaseStream,
            "webhook" => ServerlessTriggerKind.Webhook,
            _ => ServerlessTriggerKind.HttpRequest
        };

    public static string ToApiString(this NetworkSecurityGroupRule value) => value switch
    {
        NetworkSecurityGroupRule.AllowInbound => "allow_inbound",
        NetworkSecurityGroupRule.DenyInbound => "deny_inbound",
        NetworkSecurityGroupRule.AllowOutbound => "allow_outbound",
        NetworkSecurityGroupRule.DenyOutbound => "deny_outbound",
        NetworkSecurityGroupRule.StatefulInspection => "stateful_inspection",
        NetworkSecurityGroupRule.RateLimitRule => "rate_limit_rule",
        _ => "unknown"
    };

    public static NetworkSecurityGroupRule ParseNetworkSecurityGroupRule(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "allow_inbound" or "allowinbound" => NetworkSecurityGroupRule.AllowInbound,
            "deny_inbound" or "denyinbound" => NetworkSecurityGroupRule.DenyInbound,
            "allow_outbound" or "allowoutbound" => NetworkSecurityGroupRule.AllowOutbound,
            "deny_outbound" or "denyoutbound" => NetworkSecurityGroupRule.DenyOutbound,
            "stateful_inspection" or "statefulinspection" => NetworkSecurityGroupRule.StatefulInspection,
            "rate_limit_rule" or "ratelimitrule" or "ratelimit" => NetworkSecurityGroupRule.RateLimitRule,
            _ => NetworkSecurityGroupRule.AllowInbound
        };

    public static string ToApiString(this InstanceLifeCycle value) => value switch
    {
        InstanceLifeCycle.OnDemand => "on_demand",
        InstanceLifeCycle.Spot => "spot",
        InstanceLifeCycle.Reserved => "reserved",
        InstanceLifeCycle.DedicatedHost => "dedicated_host",
        InstanceLifeCycle.Preemptible => "preemptible",
        _ => "unknown"
    };

    public static InstanceLifeCycle ParseInstanceLifeCycle(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "on_demand" or "ondemand" => InstanceLifeCycle.OnDemand,
            "spot" => InstanceLifeCycle.Spot,
            "reserved" => InstanceLifeCycle.Reserved,
            "dedicated_host" or "dedicatedhost" => InstanceLifeCycle.DedicatedHost,
            "preemptible" => InstanceLifeCycle.Preemptible,
            _ => InstanceLifeCycle.OnDemand
        };

    public static string ToApiString(this ContainerRestartPolicy value) => value switch
    {
        ContainerRestartPolicy.Always => "always",
        ContainerRestartPolicy.OnFailure => "on_failure",
        ContainerRestartPolicy.Never => "never",
        ContainerRestartPolicy.UnlessStopped => "unless_stopped",
        _ => "unknown"
    };

    public static ContainerRestartPolicy ParseContainerRestartPolicy(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "always" => ContainerRestartPolicy.Always,
            "on_failure" or "onfailure" => ContainerRestartPolicy.OnFailure,
            "never" => ContainerRestartPolicy.Never,
            "unless_stopped" or "unlessstopped" => ContainerRestartPolicy.UnlessStopped,
            _ => ContainerRestartPolicy.Always
        };

    public static string ToApiString(this LogRetentionPolicy value) => value switch
    {
        LogRetentionPolicy.OneDay => "one_day",
        LogRetentionPolicy.SevenDays => "seven_days",
        LogRetentionPolicy.ThirtyDays => "thirty_days",
        LogRetentionPolicy.NinetyDays => "ninety_days",
        LogRetentionPolicy.OneYear => "one_year",
        LogRetentionPolicy.Indefinite => "indefinite",
        _ => "unknown"
    };

    public static LogRetentionPolicy ParseLogRetentionPolicy(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "one_day" or "oneday" or "1d" => LogRetentionPolicy.OneDay,
            "seven_days" or "sevendays" or "7d" => LogRetentionPolicy.SevenDays,
            "thirty_days" or "thirtydays" or "30d" => LogRetentionPolicy.ThirtyDays,
            "ninety_days" or "ninetydays" or "90d" => LogRetentionPolicy.NinetyDays,
            "one_year" or "oneyear" or "1y" => LogRetentionPolicy.OneYear,
            "indefinite" or "forever" => LogRetentionPolicy.Indefinite,
            _ => LogRetentionPolicy.ThirtyDays
        };

    public static string ToApiString(this BackupStorageLocation value) => value switch
    {
        BackupStorageLocation.LocalDisk => "local_disk",
        BackupStorageLocation.RemoteObjectStore => "remote_object_store",
        BackupStorageLocation.CrossRegionReplica => "cross_region_replica",
        BackupStorageLocation.TapeArchive => "tape_archive",
        BackupStorageLocation.MultiCloudVault => "multi_cloud_vault",
        _ => "unknown"
    };

    public static BackupStorageLocation ParseBackupStorageLocation(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "local_disk" or "localdisk" => BackupStorageLocation.LocalDisk,
            "remote_object_store" or "remoteobjectstore" or "s3" or "gcs" => BackupStorageLocation.RemoteObjectStore,
            "cross_region_replica" or "crossregionreplica" => BackupStorageLocation.CrossRegionReplica,
            "tape_archive" or "tapearchive" or "tape" => BackupStorageLocation.TapeArchive,
            "multi_cloud_vault" or "multicloudvault" => BackupStorageLocation.MultiCloudVault,
            _ => BackupStorageLocation.RemoteObjectStore
        };

    public static string ToApiString(this DisasterRecoveryRpoRto value) => value switch
    {
        DisasterRecoveryRpoRto.NearZero => "near_zero",
        DisasterRecoveryRpoRto.FifteenMinutes => "fifteen_minutes",
        DisasterRecoveryRpoRto.OneHour => "one_hour",
        DisasterRecoveryRpoRto.FourHours => "four_hours",
        DisasterRecoveryRpoRto.TwentyFourHours => "twenty_four_hours",
        DisasterRecoveryRpoRto.BestEffort => "best_effort",
        _ => "unknown"
    };

    public static DisasterRecoveryRpoRto ParseDisasterRecoveryRpoRto(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "near_zero" or "nearzero" or "0" => DisasterRecoveryRpoRto.NearZero,
            "fifteen_minutes" or "fifteenminutes" or "15m" => DisasterRecoveryRpoRto.FifteenMinutes,
            "one_hour" or "onehour" or "1h" => DisasterRecoveryRpoRto.OneHour,
            "four_hours" or "fourhours" or "4h" => DisasterRecoveryRpoRto.FourHours,
            "twenty_four_hours" or "twentyfourhours" or "24h" => DisasterRecoveryRpoRto.TwentyFourHours,
            "best_effort" or "besteffort" => DisasterRecoveryRpoRto.BestEffort,
            _ => DisasterRecoveryRpoRto.FifteenMinutes
        };

    public static string ToApiString(this InfraProvisionerTool value) => value switch
    {
        InfraProvisionerTool.Terraform => "terraform",
        InfraProvisionerTool.OpenTofu => "opentofu",
        InfraProvisionerTool.Pulumi => "pulumi",
        InfraProvisionerTool.Ansible => "ansible",
        InfraProvisionerTool.CloudFormation => "cloudformation",
        InfraProvisionerTool.Bicep => "bicep",
        InfraProvisionerTool.Helm => "helm",
        InfraProvisionerTool.Kubectl => "kubectl",
        _ => "unknown"
    };

    public static InfraProvisionerTool ParseInfraProvisionerTool(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "terraform" or "tf" => InfraProvisionerTool.Terraform,
            "opentofu" or "tofu" => InfraProvisionerTool.OpenTofu,
            "pulumi" => InfraProvisionerTool.Pulumi,
            "ansible" => InfraProvisionerTool.Ansible,
            "cloudformation" or "cfn" => InfraProvisionerTool.CloudFormation,
            "bicep" => InfraProvisionerTool.Bicep,
            "helm" => InfraProvisionerTool.Helm,
            "kubectl" or "k8s" => InfraProvisionerTool.Kubectl,
            _ => InfraProvisionerTool.Terraform
        };

    public static string ToApiString(this WasmRuntimeKind value) => value switch
    {
        WasmRuntimeKind.BrowserV8 => "browser_v8",
        WasmRuntimeKind.Wasmer => "wasmer",
        WasmRuntimeKind.Wasmtime => "wasmtime",
        WasmRuntimeKind.WasmEdge => "wasmedge",
        WasmRuntimeKind.NodeJs => "nodejs",
        WasmRuntimeKind.Bun => "bun",
        _ => "unknown"
    };

    public static WasmRuntimeKind ParseWasmRuntimeKind(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "browser_v8" or "browserv8" or "v8" => WasmRuntimeKind.BrowserV8,
            "wasmer" => WasmRuntimeKind.Wasmer,
            "wasmtime" => WasmRuntimeKind.Wasmtime,
            "wasmedge" => WasmRuntimeKind.WasmEdge,
            "nodejs" or "node" => WasmRuntimeKind.NodeJs,
            "bun" => WasmRuntimeKind.Bun,
            _ => WasmRuntimeKind.BrowserV8
        };

    public static string ToApiString(this ClientStorageType value) => value switch
    {
        ClientStorageType.LocalStorage => "local_storage",
        ClientStorageType.SessionStorage => "session_storage",
        ClientStorageType.IndexedDb => "indexed_db",
        ClientStorageType.CacheApi => "cache_api",
        ClientStorageType.Cookies => "cookies",
        ClientStorageType.MemoryOnly => "memory_only",
        _ => "unknown"
    };

    public static ClientStorageType ParseClientStorageType(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "local_storage" or "localstorage" => ClientStorageType.LocalStorage,
            "session_storage" or "sessionstorage" => ClientStorageType.SessionStorage,
            "indexed_db" or "indexeddb" or "idb" => ClientStorageType.IndexedDb,
            "cache_api" or "cacheapi" or "cache" => ClientStorageType.CacheApi,
            "cookies" or "cookie" => ClientStorageType.Cookies,
            "memory_only" or "memoryonly" or "memory" => ClientStorageType.MemoryOnly,
            _ => ClientStorageType.LocalStorage
        };

    public static string ToApiString(this WebGlRendererBackend value) => value switch
    {
        WebGlRendererBackend.WebGL1 => "webgl1",
        WebGlRendererBackend.WebGL2 => "webgl2",
        WebGlRendererBackend.WebGPU => "webgpu",
        WebGlRendererBackend.SoftwareRasterizer => "software_rasterizer",
        WebGlRendererBackend.DirectXBinding => "directx_binding",
        WebGlRendererBackend.VulkanBinding => "vulkan_binding",
        _ => "unknown"
    };

    public static WebGlRendererBackend ParseWebGlRendererBackend(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "webgl1" or "gl1" => WebGlRendererBackend.WebGL1,
            "webgl2" or "gl2" => WebGlRendererBackend.WebGL2,
            "webgpu" or "gpu" => WebGlRendererBackend.WebGPU,
            "software_rasterizer" or "software" => WebGlRendererBackend.SoftwareRasterizer,
            "directx_binding" or "directx" or "dx" => WebGlRendererBackend.DirectXBinding,
            "vulkan_binding" or "vulkan" => WebGlRendererBackend.VulkanBinding,
            _ => WebGlRendererBackend.WebGL2
        };

    public static string ToApiString(this PwaInstallState value) => value switch
    {
        PwaInstallState.NotInstalled => "not_installed",
        PwaInstallState.InstallPromptAvailable => "install_prompt_available",
        PwaInstallState.Installing => "installing",
        PwaInstallState.Installed => "installed",
        PwaInstallState.Dismissed => "dismissed",
        PwaInstallState.Unsupported => "unsupported",
        _ => "unknown"
    };

    public static PwaInstallState ParsePwaInstallState(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "not_installed" or "notinstalled" => PwaInstallState.NotInstalled,
            "install_prompt_available" or "installpromptavailable" => PwaInstallState.InstallPromptAvailable,
            "installing" => PwaInstallState.Installing,
            "installed" => PwaInstallState.Installed,
            "dismissed" => PwaInstallState.Dismissed,
            "unsupported" => PwaInstallState.Unsupported,
            _ => PwaInstallState.NotInstalled
        };

    public static string ToApiString(this TouchGestureType value) => value switch
    {
        TouchGestureType.Tap => "tap",
        TouchGestureType.DoubleTap => "double_tap",
        TouchGestureType.LongPress => "long_press",
        TouchGestureType.SwipeLeft => "swipe_left",
        TouchGestureType.SwipeRight => "swipe_right",
        TouchGestureType.SwipeUp => "swipe_up",
        TouchGestureType.SwipeDown => "swipe_down",
        TouchGestureType.PinchZoom => "pinch_zoom",
        TouchGestureType.Rotate => "rotate",
        _ => "unknown"
    };

    public static TouchGestureType ParseTouchGestureType(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "tap" => TouchGestureType.Tap,
            "double_tap" or "doubletap" => TouchGestureType.DoubleTap,
            "long_press" or "longpress" => TouchGestureType.LongPress,
            "swipe_left" or "swipeleft" => TouchGestureType.SwipeLeft,
            "swipe_right" or "swiperight" => TouchGestureType.SwipeRight,
            "swipe_up" or "swipeup" => TouchGestureType.SwipeUp,
            "swipe_down" or "swipedown" => TouchGestureType.SwipeDown,
            "pinch_zoom" or "pinchzoom" or "pinch" => TouchGestureType.PinchZoom,
            "rotate" => TouchGestureType.Rotate,
            _ => TouchGestureType.Tap
        };

    public static string ToApiString(this ScreenOrientationMode value) => value switch
    {
        ScreenOrientationMode.PortraitPrimary => "portrait_primary",
        ScreenOrientationMode.PortraitSecondary => "portrait_secondary",
        ScreenOrientationMode.LandscapePrimary => "landscape_primary",
        ScreenOrientationMode.LandscapeSecondary => "landscape_secondary",
        ScreenOrientationMode.AutoRotate => "auto_rotate",
        _ => "unknown"
    };

    public static ScreenOrientationMode ParseScreenOrientationMode(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "portrait_primary" or "portraitprimary" or "portrait" => ScreenOrientationMode.PortraitPrimary,
            "portrait_secondary" or "portraitsecondary" => ScreenOrientationMode.PortraitSecondary,
            "landscape_primary" or "landscapeprimary" or "landscape" => ScreenOrientationMode.LandscapePrimary,
            "landscape_secondary" or "landscapesecondary" => ScreenOrientationMode.LandscapeSecondary,
            "auto_rotate" or "autorotate" or "auto" => ScreenOrientationMode.AutoRotate,
            _ => ScreenOrientationMode.LandscapePrimary
        };

    public static string ToApiString(this NetworkConnectivityState value) => value switch
    {
        NetworkConnectivityState.Online => "online",
        NetworkConnectivityState.Offline => "offline",
        NetworkConnectivityState.SlowDownlink => "slow_downlink",
        NetworkConnectivityState.CellularMetered => "cellular_metered",
        NetworkConnectivityState.WiFiUnmetered => "wifi_unmetered",
        _ => "unknown"
    };

    public static NetworkConnectivityState ParseNetworkConnectivityState(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "online" => NetworkConnectivityState.Online,
            "offline" => NetworkConnectivityState.Offline,
            "slow_downlink" or "slowdownlink" or "slow" => NetworkConnectivityState.SlowDownlink,
            "cellular_metered" or "cellularmetered" or "cellular" => NetworkConnectivityState.CellularMetered,
            "wifi_unmetered" or "wifiunmetered" or "wifi" => NetworkConnectivityState.WiFiUnmetered,
            _ => NetworkConnectivityState.Online
        };

    public static string ToApiString(this ClientNotificationPermission value) => value switch
    {
        ClientNotificationPermission.Default => "default",
        ClientNotificationPermission.Granted => "granted",
        ClientNotificationPermission.Denied => "denied",
        ClientNotificationPermission.Unsupported => "unsupported",
        _ => "unknown"
    };

    public static ClientNotificationPermission ParseClientNotificationPermission(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "default" => ClientNotificationPermission.Default,
            "granted" or "allow" => ClientNotificationPermission.Granted,
            "denied" or "block" => ClientNotificationPermission.Denied,
            "unsupported" => ClientNotificationPermission.Unsupported,
            _ => ClientNotificationPermission.Default
        };

    public static string ToApiString(this HardwareKeyCombo value) => value switch
    {
        HardwareKeyCombo.ControlKey => "control_key",
        HardwareKeyCombo.ShiftKey => "shift_key",
        HardwareKeyCombo.AltKey => "alt_key",
        HardwareKeyCombo.MetaKey => "meta_key",
        HardwareKeyCombo.ControlShift => "control_shift",
        HardwareKeyCombo.ControlAlt => "control_alt",
        HardwareKeyCombo.MetaShift => "meta_shift",
        _ => "unknown"
    };

    public static HardwareKeyCombo ParseHardwareKeyCombo(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "control_key" or "controlkey" or "ctrl" => HardwareKeyCombo.ControlKey,
            "shift_key" or "shiftkey" or "shift" => HardwareKeyCombo.ShiftKey,
            "alt_key" or "altkey" or "alt" => HardwareKeyCombo.AltKey,
            "meta_key" or "metakey" or "cmd" or "meta" => HardwareKeyCombo.MetaKey,
            "control_shift" or "controlshift" or "ctrl_shift" => HardwareKeyCombo.ControlShift,
            "control_alt" or "controlalt" or "ctrl_alt" => HardwareKeyCombo.ControlAlt,
            "meta_shift" or "metashift" or "cmd_shift" => HardwareKeyCombo.MetaShift,
            _ => HardwareKeyCombo.ControlKey
        };

    public static string ToApiString(this ClipboardPermission value) => value switch
    {
        ClipboardPermission.Prompt => "prompt",
        ClipboardPermission.Granted => "granted",
        ClipboardPermission.Denied => "denied",
        ClipboardPermission.ReadTextOnly => "read_text_only",
        ClipboardPermission.WriteTextOnly => "write_text_only",
        _ => "unknown"
    };

    public static ClipboardPermission ParseClipboardPermission(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "prompt" => ClipboardPermission.Prompt,
            "granted" or "allow" => ClipboardPermission.Granted,
            "denied" or "block" => ClipboardPermission.Denied,
            "read_text_only" or "readtextonly" or "read" => ClipboardPermission.ReadTextOnly,
            "write_text_only" or "writetextonly" or "write" => ClipboardPermission.WriteTextOnly,
            _ => ClipboardPermission.Prompt
        };

    public static string ToApiString(this AudioOutputDeviceType value) => value switch
    {
        AudioOutputDeviceType.DefaultSpeaker => "default_speaker",
        AudioOutputDeviceType.Headphones => "headphones",
        AudioOutputDeviceType.BluetoothAudio => "bluetooth_audio",
        AudioOutputDeviceType.HdmiOutput => "hdmi_output",
        AudioOutputDeviceType.VirtualAudioCable => "virtual_audio_cable",
        _ => "unknown"
    };

    public static AudioOutputDeviceType ParseAudioOutputDeviceType(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "default_speaker" or "defaultspeaker" or "speaker" => AudioOutputDeviceType.DefaultSpeaker,
            "headphones" or "headset" => AudioOutputDeviceType.Headphones,
            "bluetooth_audio" or "bluetoothaudio" or "bluetooth" => AudioOutputDeviceType.BluetoothAudio,
            "hdmi_output" or "hdmioutput" or "hdmi" => AudioOutputDeviceType.HdmiOutput,
            "virtual_audio_cable" or "virtualaudiocable" or "virtual" => AudioOutputDeviceType.VirtualAudioCable,
            _ => AudioOutputDeviceType.DefaultSpeaker
        };

    public static string ToApiString(this VideoRenderingQuality value) => value switch
    {
        VideoRenderingQuality.Low360p => "low_360p",
        VideoRenderingQuality.Medium480p => "medium_480p",
        VideoRenderingQuality.High720p => "high_720p",
        VideoRenderingQuality.FullHd1080p => "full_hd_1080p",
        VideoRenderingQuality.UltraHd4k => "ultra_hd_4k",
        VideoRenderingQuality.AutoAdaptive => "auto_adaptive",
        _ => "unknown"
    };

    public static VideoRenderingQuality ParseVideoRenderingQuality(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "low_360p" or "low360p" or "360p" => VideoRenderingQuality.Low360p,
            "medium_480p" or "medium480p" or "480p" => VideoRenderingQuality.Medium480p,
            "high_720p" or "high720p" or "720p" => VideoRenderingQuality.High720p,
            "full_hd_1080p" or "fullhd1080p" or "1080p" => VideoRenderingQuality.FullHd1080p,
            "ultra_hd_4k" or "ultrahd4k" or "4k" or "2160p" => VideoRenderingQuality.UltraHd4k,
            "auto_adaptive" or "autoadaptive" or "auto" => VideoRenderingQuality.AutoAdaptive,
            _ => VideoRenderingQuality.High720p
        };

    public static string ToApiString(this ClientAppTheme value) => value switch
    {
        ClientAppTheme.DarkCinematic => "dark_cinematic",
        ClientAppTheme.LightStudio => "light_studio",
        ClientAppTheme.HighContrast => "high_contrast",
        ClientAppTheme.SystemDefault => "system_default",
        ClientAppTheme.CustomAccent => "custom_accent",
        _ => "unknown"
    };

    public static ClientAppTheme ParseClientAppTheme(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "dark_cinematic" or "darkcinematic" or "dark" => ClientAppTheme.DarkCinematic,
            "light_studio" or "lightstudio" or "light" => ClientAppTheme.LightStudio,
            "high_contrast" or "highcontrast" => ClientAppTheme.HighContrast,
            "system_default" or "systemdefault" or "system" => ClientAppTheme.SystemDefault,
            "custom_accent" or "customaccent" or "custom" => ClientAppTheme.CustomAccent,
            _ => ClientAppTheme.DarkCinematic
        };

    public static string ToApiString(this BrowserVendorEngine value) => value switch
    {
        BrowserVendorEngine.BlinkChromium => "blink_chromium",
        BrowserVendorEngine.GeckoFirefox => "gecko_firefox",
        BrowserVendorEngine.WebKitSafari => "webkit_safari",
        BrowserVendorEngine.EdgeHTML => "edge_html",
        BrowserVendorEngine.Servo => "servo",
        _ => "unknown"
    };

    public static BrowserVendorEngine ParseBrowserVendorEngine(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "blink_chromium" or "blinkchromium" or "blink" or "chromium" => BrowserVendorEngine.BlinkChromium,
            "gecko_firefox" or "geckofirefox" or "gecko" or "firefox" => BrowserVendorEngine.GeckoFirefox,
            "webkit_safari" or "webkitsafari" or "webkit" or "safari" => BrowserVendorEngine.WebKitSafari,
            "edge_html" or "edgehtml" or "edge" => BrowserVendorEngine.EdgeHTML,
            "servo" => BrowserVendorEngine.Servo,
            _ => BrowserVendorEngine.BlinkChromium
        };

    public static string ToApiString(this WebAssemblyMemoryMode value) => value switch
    {
        WebAssemblyMemoryMode.Standard32Bit => "standard_32bit",
        WebAssemblyMemoryMode.Memory64Bit => "memory_64bit",
        WebAssemblyMemoryMode.SharedArrayBuffer => "shared_array_buffer",
        WebAssemblyMemoryMode.DynamicGrowth => "dynamic_growth",
        WebAssemblyMemoryMode.FixedAllocation => "fixed_allocation",
        _ => "unknown"
    };

    public static WebAssemblyMemoryMode ParseWebAssemblyMemoryMode(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "standard_32bit" or "standard32bit" or "wasm32" => WebAssemblyMemoryMode.Standard32Bit,
            "memory_64bit" or "memory64bit" or "wasm64" => WebAssemblyMemoryMode.Memory64Bit,
            "shared_array_buffer" or "sharedarraybuffer" or "sab" => WebAssemblyMemoryMode.SharedArrayBuffer,
            "dynamic_growth" or "dynamicgrowth" or "dynamic" => WebAssemblyMemoryMode.DynamicGrowth,
            "fixed_allocation" or "fixedallocation" or "fixed" => WebAssemblyMemoryMode.FixedAllocation,
            _ => WebAssemblyMemoryMode.Standard32Bit
        };

    public static string ToApiString(this ServiceWorkerState value) => value switch
    {
        ServiceWorkerState.Installing => "installing",
        ServiceWorkerState.Installed => "installed",
        ServiceWorkerState.Activating => "activating",
        ServiceWorkerState.Activated => "activated",
        ServiceWorkerState.Redundant => "redundant",
        ServiceWorkerState.Stopped => "stopped",
        _ => "unknown"
    };

    public static ServiceWorkerState ParseServiceWorkerState(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "installing" => ServiceWorkerState.Installing,
            "installed" => ServiceWorkerState.Installed,
            "activating" => ServiceWorkerState.Activating,
            "activated" => ServiceWorkerState.Activated,
            "redundant" => ServiceWorkerState.Redundant,
            "stopped" => ServiceWorkerState.Stopped,
            _ => ServiceWorkerState.Installed
        };

    public static string ToApiString(this PushSubscriptionState value) => value switch
    {
        PushSubscriptionState.Unsubscribed => "unsubscribed",
        PushSubscriptionState.Subscribing => "subscribing",
        PushSubscriptionState.Subscribed => "subscribed",
        PushSubscriptionState.Expired => "expired",
        PushSubscriptionState.PermissionDenied => "permission_denied",
        _ => "unknown"
    };

    public static PushSubscriptionState ParsePushSubscriptionState(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "unsubscribed" => PushSubscriptionState.Unsubscribed,
            "subscribing" => PushSubscriptionState.Subscribing,
            "subscribed" => PushSubscriptionState.Subscribed,
            "expired" => PushSubscriptionState.Expired,
            "permission_denied" or "permissiondenied" => PushSubscriptionState.PermissionDenied,
            _ => PushSubscriptionState.Unsubscribed
        };

    public static string ToApiString(this ClientAnalyticsConsent value) => value switch
    {
        ClientAnalyticsConsent.OptedIn => "opted_in",
        ClientAnalyticsConsent.OptedOut => "opted_out",
        ClientAnalyticsConsent.EssentialOnly => "essential_only",
        ClientAnalyticsConsent.Pending => "pending",
        ClientAnalyticsConsent.NotRequired => "not_required",
        _ => "unknown"
    };

    public static ClientAnalyticsConsent ParseClientAnalyticsConsent(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "opted_in" or "optedin" => ClientAnalyticsConsent.OptedIn,
            "opted_out" or "optedout" => ClientAnalyticsConsent.OptedOut,
            "essential_only" or "essentialonly" => ClientAnalyticsConsent.EssentialOnly,
            "pending" => ClientAnalyticsConsent.Pending,
            "not_required" or "notrequired" => ClientAnalyticsConsent.NotRequired,
            _ => ClientAnalyticsConsent.Pending
        };

    public static string ToApiString(this DragDropEffect value) => value switch
    {
        DragDropEffect.None => "none",
        DragDropEffect.Copy => "copy",
        DragDropEffect.Move => "move",
        DragDropEffect.Link => "link",
        DragDropEffect.All => "all",
        _ => "unknown"
    };

    public static DragDropEffect ParseDragDropEffect(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "none" => DragDropEffect.None,
            "copy" => DragDropEffect.Copy,
            "move" => DragDropEffect.Move,
            "link" => DragDropEffect.Link,
            "all" => DragDropEffect.All,
            _ => DragDropEffect.None
        };

    public static string ToApiString(this ClientFilePickerMode value) => value switch
    {
        ClientFilePickerMode.OpenFile => "open_file",
        ClientFilePickerMode.OpenMultipleFiles => "open_multiple_files",
        ClientFilePickerMode.OpenFolder => "open_folder",
        ClientFilePickerMode.SaveFile => "save_file",
        _ => "unknown"
    };

    public static ClientFilePickerMode ParseClientFilePickerMode(string? value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "open_file" or "openfile" => ClientFilePickerMode.OpenFile,
            "open_multiple_files" or "openmultiplefiles" or "multiple" => ClientFilePickerMode.OpenMultipleFiles,
            "open_folder" or "openfolder" or "folder" => ClientFilePickerMode.OpenFolder,
            "save_file" or "savefile" => ClientFilePickerMode.SaveFile,
            _ => ClientFilePickerMode.OpenFile
        };
}
