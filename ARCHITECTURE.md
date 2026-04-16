# Architecture Overview

This document describes the full architecture of the **Jellyfin Helper** plugin — layers, inheritance, interfaces, dependency injection, data flow, and design patterns.

> **Audience:** Contributors who want to understand the codebase before making changes.

---

## Table of Contents

- [High-Level Layer Architecture](#high-level-layer-architecture)
- [Plugin Core — Inheritance & Interfaces](#plugin-core--inheritance--interfaces)
- [Dependency Injection Registrations](#dependency-injection-registrations)
- [Service Layer — Interfaces & Implementations](#service-layer--interfaces--implementations)
- [Service Dependency Graph](#service-dependency-graph)
- [API Controllers — Dependencies](#api-controllers--dependencies)
- [Scheduled Tasks — Inheritance & Orchestration](#scheduled-tasks--inheritance--orchestration)
- [Task Execution Flow](#task-execution-flow)
- [Data Models / DTOs](#data-models--dtos)
- [Frontend Dashboard Architecture](#frontend-dashboard-architecture)
- [Static Utility Classes](#static-utility-classes)
- [Test Architecture](#test-architecture)
- [Design Patterns](#design-patterns)

---

## High-Level Layer Architecture

```mermaid
graph TB
    subgraph JELLYFIN["Jellyfin Server"]
        JF_API["MediaBrowser API\n(ILibraryManager, IApplicationPaths,\nIFileSystem, IXmlSerializer)"]
        JF_PLUGIN["Plugin System\n(BasePlugin, IPluginServiceRegistrator,\nIScheduledTask, IHasWebPages)"]
        JF_DI["DI Container\n(IServiceCollection)"]
    end

    subgraph PLUGIN["Jellyfin Helper Plugin"]
        direction TB
        CORE["Plugin Core\nPlugin.cs\nPluginServiceRegistrator.cs"]

        subgraph API_LAYER["API Layer — REST Controllers"]
            direction LR
            C_CONFIG["ConfigurationController"]
            C_STATS["MediaStatisticsController"]
            C_CLEANUP["CleanupStatisticsController"]
            C_TRASH["TrashController"]
            C_LOGS["LogsController"]
            C_TIMELINE["GrowthTimelineController"]
            C_BACKUP["BackupController"]
            C_ARR["ArrIntegrationController"]
            C_TRANS["TranslationsController"]
        end

        subgraph SERVICE_LAYER["Service Layer"]
            direction LR
            S_CLEANUP["Cleanup Services"]
            S_STATS["Statistics Services"]
            S_TIMELINE["Timeline Services"]
            S_BACKUP["Backup Services"]
            S_ARR["Arr Integration"]
            S_LOG["PluginLog Service"]
            S_CONFIG["ConfigAccess Service"]
            S_STRM["STRM Repair Service"]
            S_I18N["I18N Service"]
        end

        subgraph TASK_LAYER["Scheduled Tasks"]
            T_MASTER["HelperCleanupTask"]
            T_TRICKPLAY["CleanTrickplayTask"]
            T_EMPTY["CleanEmptyMediaFoldersTask"]
            T_SUBS["CleanOrphanedSubtitlesTask"]
            T_STRM["RepairStrmFilesTask"]
        end

        CONFIG["Configuration\nPluginConfiguration"]
        HELPERS["Static Utilities\nMediaExtensions, PathValidator,\nFileSystemHelper, LibraryPathResolver"]
    end

    subgraph FRONTEND["Frontend — Dashboard"]
        FE_MAIN["main.js"]
        FE_PAGES["Overview · Settings · Codecs\nHealth · Logs · Trends\nArrIntegration"]
        FE_SHARED["shared.js"]
    end

    subgraph EXTERNAL["External Systems"]
        RADARR["Radarr API"]
        SONARR["Sonarr API"]
        FILESYSTEM["File System\n(Media Libraries)"]
    end

    JF_PLUGIN --> CORE
    JF_DI --> CORE
    CORE --> API_LAYER
    CORE --> SERVICE_LAYER
    CORE --> TASK_LAYER
    API_LAYER --> SERVICE_LAYER
    TASK_LAYER --> SERVICE_LAYER
    SERVICE_LAYER --> CONFIG
    SERVICE_LAYER --> HELPERS
    SERVICE_LAYER --> JF_API
    S_ARR --> RADARR
    S_ARR --> SONARR
    S_CLEANUP --> FILESYSTEM
    S_STATS --> FILESYSTEM
    S_TIMELINE --> FILESYSTEM
    S_STRM --> FILESYSTEM
    FRONTEND --> API_LAYER
```

---

## Plugin Core — Inheritance & Interfaces

```mermaid
classDiagram
    class BasePlugin_PluginConfiguration {
        <<Jellyfin Framework>>
        +string Name
        +Guid Id
        +PluginConfiguration Configuration
        +SaveConfiguration()
    }

    class IHasWebPages {
        <<interface>>
        +GetPages() IEnumerable~PluginPageInfo~
    }

    class IPluginServiceRegistrator {
        <<interface>>
        +RegisterServices(IServiceCollection, IServerApplicationHost)
    }

    class BasePluginConfiguration {
        <<Jellyfin Framework>>
    }

    class Plugin {
        +string Name = "Jellyfin Helper"
        +Guid Id
        +string Description
        +static Plugin? Instance
        +GetPages() IEnumerable~PluginPageInfo~
    }

    class PluginServiceRegistrator {
        +RegisterServices(IServiceCollection, IServerApplicationHost)
    }

    class PluginConfiguration {
        +string IncludedLibraries
        +string ExcludedLibraries
        +int OrphanMinAgeDays
        +TaskMode TrickplayTaskMode
        +TaskMode EmptyMediaFolderTaskMode
        +TaskMode OrphanedSubtitleTaskMode
        +TaskMode StrmRepairTaskMode
        +int TrashRetentionDays
        +string PluginLogLevel
        +List~ArrInstanceConfig~ RadarrInstances
        +List~ArrInstanceConfig~ SonarrInstances
    }

    class TaskMode {
        <<enum>>
        Activate
        Deactivate
        DryRun
    }

    class ArrInstanceConfig {
        +string Name
        +string Url
        +string ApiKey
    }

    BasePlugin_PluginConfiguration <|-- Plugin : inherits
    IHasWebPages <|.. Plugin : implements
    IPluginServiceRegistrator <|.. PluginServiceRegistrator : implements
    BasePluginConfiguration <|-- PluginConfiguration : inherits
    Plugin --> PluginConfiguration : configured by
    PluginConfiguration --> TaskMode : uses
    PluginConfiguration --> ArrInstanceConfig : contains list of
```

---

## Dependency Injection Registrations

All services are registered as **Singletons** inside `PluginServiceRegistrator.RegisterServices()`:

| Interface | Implementation | Notes |
|---|---|---|
| `ICleanupConfigHelper` | `CleanupConfigHelper` | |
| `ICleanupTrackingService` | `CleanupTrackingService` | |
| `ITrashService` | `TrashService` | |
| `IPluginConfigurationService` | `PluginConfigurationService` | |
| `IPluginLogService` | `PluginLogService` | |
| `IMediaStatisticsService` | `MediaStatisticsService` | |
| `IStatisticsCacheService` | `StatisticsCacheService` | |
| `IGrowthTimelineService` | `GrowthTimelineService` | also `IDisposable` |
| `IBackupService` | `BackupService` | |
| `IStrmRepairService` | `StrmRepairService` | |
| `IArrIntegrationService` | `ArrIntegrationService` | |
| Named `HttpClient` | `"ArrIntegration"` | 15 s timeout |

---

## Service Layer — Interfaces & Implementations

```mermaid
classDiagram
    class ICleanupConfigHelper {
        <<interface>>
        +GetConfig() PluginConfiguration
        +GetTrickplayTaskMode() TaskMode
        +GetEmptyMediaFolderTaskMode() TaskMode
        +GetOrphanedSubtitleTaskMode() TaskMode
        +GetStrmRepairTaskMode() TaskMode
        +IsDryRunTrickplay() bool
        +IsDryRunEmptyMediaFolders() bool
        +IsDryRunOrphanedSubtitles() bool
        +IsDryRunStrmRepair() bool
        +GetFilteredLibraryLocations() IReadOnlyList
        +IsOldEnoughForDeletion(string) bool
        +GetTrashPath(string) string
    }

    class ICleanupTrackingService {
        <<interface>>
        +RecordCleanup(long, int, ILogger)
        +GetStatistics() tuple
    }

    class ITrashService {
        <<interface>>
        +MoveToTrash(string, string, ILogger, DateTime?)
        +MoveFileToTrash(string, string, ILogger, DateTime?)
        +PurgeExpiredTrash() tuple
        +GetTrashSummary(string) tuple
        +GetTrashContents(string, int) IReadOnlyList
    }

    class IPluginConfigurationService {
        <<interface>>
        +GetConfiguration() PluginConfiguration
        +SaveConfiguration()
    }

    class IPluginLogService {
        <<interface>>
        +LogDebug / LogInfo / LogWarning / LogError
        +GetEntries() ReadOnlyCollection
        +GetCount() int
        +Clear()
        +ExportAsText() string
    }

    class IMediaStatisticsService {
        <<interface>>
        +CalculateStatistics() MediaStatisticsResult
    }

    class IStatisticsCacheService {
        <<interface>>
        +SaveLatestResult(MediaStatisticsResult)
        +LoadLatestResult() MediaStatisticsResult?
    }

    class IGrowthTimelineService {
        <<interface>>
        +ComputeTimelineAsync(CancellationToken) Task
        +LoadTimelineAsync(CancellationToken) Task
    }

    class IBackupService {
        <<interface>>
        +CreateBackup() BackupData
        +RestoreBackup(BackupData) BackupRestoreSummary
    }

    class IStrmRepairService {
        <<interface>>
        +RepairStrmFiles() StrmRepairResult
    }

    class IArrIntegrationService {
        <<interface>>
        +TestConnectionAsync() Task
        +GetRadarrMoviesAsync() Task
        +GetSonarrSeriesAsync() Task
    }

    class CleanupConfigHelper
    class CleanupTrackingService
    class TrashService
    class PluginConfigurationService
    class PluginLogService
    class MediaStatisticsService
    class StatisticsCacheService
    class GrowthTimelineService
    class BackupService
    class StrmRepairService
    class ArrIntegrationService

    ICleanupConfigHelper <|.. CleanupConfigHelper
    ICleanupTrackingService <|.. CleanupTrackingService
    ITrashService <|.. TrashService
    IPluginConfigurationService <|.. PluginConfigurationService
    IPluginLogService <|.. PluginLogService
    IMediaStatisticsService <|.. MediaStatisticsService
    IStatisticsCacheService <|.. StatisticsCacheService
    IGrowthTimelineService <|.. GrowthTimelineService
    IBackupService <|.. BackupService
    IStrmRepairService <|.. StrmRepairService
    IArrIntegrationService <|.. ArrIntegrationService
```

---

## Service Dependency Graph

Shows which Jellyfin APIs and internal services each implementation depends on.

```mermaid
graph TD
    subgraph Services
        CCH["CleanupConfigHelper"]
        CTS["CleanupTrackingService"]
        TS["TrashService"]
        PCS["PluginConfigurationService"]
        PLS["PluginLogService"]
        MSS["MediaStatisticsService"]
        SCS["StatisticsCacheService"]
        GTS["GrowthTimelineService"]
        BS["BackupService"]
        SRS["StrmRepairService"]
        AIS["ArrIntegrationService"]
    end

    subgraph Jellyfin_APIs["Jellyfin APIs"]
        ILM["ILibraryManager"]
        IAP["IApplicationPaths"]
        IFS["IFileSystem"]
        IHCF["IHttpClientFactory"]
    end

    subgraph Plugin_Core["Plugin Core"]
        PI["Plugin.Instance"]
        PC["PluginConfiguration"]
    end

    CCH --> PI
    CCH --> PC
    PCS --> PI
    PCS --> PC

    MSS --> CCH
    MSS --> PLS
    MSS --> ILM
    MSS --> IFS

    GTS --> CCH
    GTS --> PLS
    GTS --> ILM
    GTS --> IAP
    GTS --> IFS

    BS --> PCS
    BS --> GTS
    BS --> IAP
    BS --> PLS

    SRS --> IFS
    SRS --> PLS

    AIS --> IHCF
    AIS --> PLS

    TS --> IFS

    SCS --> IAP
```

---

## API Controllers — Dependencies

All controllers inherit from `ControllerBase` and require admin authorization (`RequiresElevation`).

| Controller | Route | Injected Dependencies |
|---|---|---|
| `ConfigurationController` | `/JellyfinHelper/Configuration` | `IArrIntegrationService`, `ICleanupConfigHelper`, `IPluginConfigurationService`, `IPluginLogService` |
| `MediaStatisticsController` | `/JellyfinHelper/MediaStatistics` | `IMediaStatisticsService`, `IStatisticsCacheService`, `IPluginLogService`, `IMemoryCache` |
| `CleanupStatisticsController` | `/JellyfinHelper/CleanupStatistics` | `ICleanupTrackingService` |
| `TrashController` | `/JellyfinHelper/Trash` | `ITrashService`, `ICleanupConfigHelper`, `IPluginLogService`, `ILibraryManager` |
| `LogsController` | `/JellyfinHelper/Logs` | `IPluginLogService` |
| `GrowthTimelineController` | `/JellyfinHelper/GrowthTimeline` | `IGrowthTimelineService` |
| `BackupController` | `/JellyfinHelper/Backup` | `IBackupService`, `IPluginLogService` |
| `ArrIntegrationController` | `/JellyfinHelper/ArrIntegration` | `IArrIntegrationService`, `ICleanupConfigHelper`, `IPluginLogService`, `ILibraryManager`, `IFileSystem` |
| `TranslationsController` | `/JellyfinHelper/Translations` | `ICleanupConfigHelper` |

```mermaid
graph LR
    subgraph Controllers["API Controllers : ControllerBase"]
        CC["ConfigurationController"]
        MSC["MediaStatisticsController"]
        CSC["CleanupStatisticsController"]
        TC["TrashController"]
        LC["LogsController"]
        GTC["GrowthTimelineController"]
        BC["BackupController"]
        AIC["ArrIntegrationController"]
        TRC["TranslationsController"]
    end

    subgraph Interfaces["Injected Services"]
        I_AIS["IArrIntegrationService"]
        I_CCH["ICleanupConfigHelper"]
        I_PCS["IPluginConfigurationService"]
        I_PLS["IPluginLogService"]
        I_CTS["ICleanupTrackingService"]
        I_TS["ITrashService"]
        I_MSS["IMediaStatisticsService"]
        I_SCS["IStatisticsCacheService"]
        I_GTS["IGrowthTimelineService"]
        I_BS["IBackupService"]
    end

    subgraph JellyfinAPIs["Jellyfin APIs"]
        ILM2["ILibraryManager"]
        IFS2["IFileSystem"]
        IMC["IMemoryCache"]
    end

    CC --> I_AIS
    CC --> I_CCH
    CC --> I_PCS
    CC --> I_PLS

    MSC --> I_MSS
    MSC --> I_SCS
    MSC --> I_PLS
    MSC --> IMC

    CSC --> I_CTS

    TC --> I_TS
    TC --> I_CCH
    TC --> I_PLS
    TC --> ILM2

    LC --> I_PLS

    GTC --> I_GTS

    BC --> I_BS
    BC --> I_PLS

    AIC --> I_AIS
    AIC --> I_CCH
    AIC --> I_PLS
    AIC --> ILM2
    AIC --> IFS2

    TRC --> I_CCH
```

---

## Scheduled Tasks — Inheritance & Orchestration

```mermaid
classDiagram
    class IScheduledTask {
        <<Jellyfin interface>>
        +ExecuteAsync(IProgress, CancellationToken) Task
        +GetDefaultTriggers() IEnumerable
        +string Name
        +string Key
        +string Description
        +string Category
    }

    class BaseLibraryCleanupTask {
        <<abstract>>
        #ILibraryManager LibraryManager
        #IFileSystem FileSystem
        #IPluginLogService PluginLog
        #ILogger Logger
        #ICleanupConfigHelper ConfigHelper
        #ICleanupTrackingService TrackingService
        #ITrashService TrashService
        +ExecuteAsync(IProgress, CancellationToken) Task
        #IsDryRun()* bool
        #ProcessLocation()* tuple
    }

    class CleanTrickplayTask {
        #IsDryRun() bool
        #ProcessLocation() tuple
    }

    class CleanEmptyMediaFoldersTask {
        #IsDryRun() bool
        #ProcessLocation() tuple
        -AnalyzeDirectoryRecursive()
    }

    class CleanOrphanedSubtitlesTask {
        #IsDryRun() bool
        #ProcessLocation() tuple
        +GetSubtitleBaseName(string) string
    }

    class RepairStrmFilesTask {
        -IStrmRepairService _strmRepairService
        -ICleanupConfigHelper _configHelper
        +ExecuteAsync(IProgress, CancellationToken) Task
    }

    class HelperCleanupTask {
        -ILibraryManager
        -IFileSystem
        -IApplicationPaths
        -IPluginLogService
        -ILoggerFactory
        -IMediaStatisticsService
        -IStatisticsCacheService
        -IGrowthTimelineService
        -ICleanupConfigHelper
        -ICleanupTrackingService
        -ITrashService
        -IStrmRepairService
        +ExecuteAsync(IProgress, CancellationToken) Task
    }

    IScheduledTask <|.. HelperCleanupTask : implements
    BaseLibraryCleanupTask <|-- CleanTrickplayTask : inherits
    BaseLibraryCleanupTask <|-- CleanEmptyMediaFoldersTask : inherits
    BaseLibraryCleanupTask <|-- CleanOrphanedSubtitlesTask : inherits

    HelperCleanupTask ..> CleanTrickplayTask : creates & orchestrates
    HelperCleanupTask ..> CleanEmptyMediaFoldersTask : creates & orchestrates
    HelperCleanupTask ..> CleanOrphanedSubtitlesTask : creates & orchestrates
    HelperCleanupTask ..> RepairStrmFilesTask : creates & orchestrates
```

---

## Task Execution Flow

`HelperCleanupTask` is the single registered `IScheduledTask`. It orchestrates all sub-tasks sequentially:

```mermaid
sequenceDiagram
    participant JF as Jellyfin Scheduler
    participant HCT as HelperCleanupTask
    participant CTT as CleanTrickplayTask
    participant CEMF as CleanEmptyMediaFoldersTask
    participant COST as CleanOrphanedSubtitlesTask
    participant RSFT as RepairStrmFilesTask
    participant MSS as MediaStatisticsService
    participant GTS as GrowthTimelineService

    JF->>HCT: ExecuteAsync()

    Note over HCT: Phase 1 — Trickplay Cleanup
    HCT->>CTT: ExecuteAsync()
    CTT-->>HCT: (deleted, bytesFreed)

    Note over HCT: Phase 2 — Empty Folders Cleanup
    HCT->>CEMF: ExecuteAsync()
    CEMF-->>HCT: (deleted, bytesFreed)

    Note over HCT: Phase 3 — Orphaned Subtitles
    HCT->>COST: ExecuteAsync()
    COST-->>HCT: (deleted, bytesFreed)

    Note over HCT: Phase 4 — STRM Repair
    HCT->>RSFT: ExecuteAsync()
    RSFT-->>HCT: StrmRepairResult

    Note over HCT: Phase 5 — Statistics Scan
    HCT->>MSS: CalculateStatistics()
    MSS-->>HCT: MediaStatisticsResult

    Note over HCT: Phase 6 — Growth Timeline
    HCT->>GTS: ComputeTimelineAsync()
    GTS-->>HCT: GrowthTimelineResult
```

---

## Data Models / DTOs

```mermaid
classDiagram
    class MediaStatisticsResult {
        +List~LibraryStatistics~ Libraries
        +Dictionary Totals
    }
    class LibraryStatistics {
        +string Name
        +int MovieCount
        +int EpisodeCount
        +long TotalSizeBytes
        +Dictionary Codecs
        +Dictionary Resolutions
    }
    class GrowthTimelineResult {
        +List~GrowthTimelinePoint~ DataPoints
        +string Granularity
    }
    class GrowthTimelinePoint {
        +DateTime Date
        +long TotalBytes
        +int TotalFiles
    }
    class GrowthTimelineBaseline {
        +List~BaselineDirectoryEntry~ Directories
        +DateTime CreatedAt
    }
    class BaselineDirectoryEntry {
        +string Path
        +long SizeBytes
        +int FileCount
    }
    class BackupData {
        +int BackupVersion
        +string PluginVersion
        +DateTime CreatedAt
        +GrowthTimelineResult? GrowthTimeline
        +GrowthTimelineBaseline? GrowthBaseline
        +List~BackupArrInstance~? RadarrInstances
        +List~BackupArrInstance~? SonarrInstances
    }
    class BackupArrInstance {
        +string Name
        +string Url
        +string ApiKey
    }
    class BackupRestoreSummary {
        +bool ConfigurationRestored
        +bool TimelineRestored
        +bool BaselineRestored
    }
    class BackupValidationResult {
        +bool IsValid
        +List Errors
        +List Warnings
    }
    class StrmRepairResult {
        +int TotalFiles
        +int Repaired
        +int Failed
        +List~StrmFileResult~ Files
    }
    class StrmFileResult {
        +string Path
        +string Status
        +string? NewTarget
    }
    class ArrComparisonResult {
        +List InBoth
        +List OnlyInArr
        +List OnlyInJellyfin
    }

    MediaStatisticsResult --> LibraryStatistics
    GrowthTimelineResult --> GrowthTimelinePoint
    GrowthTimelineBaseline --> BaselineDirectoryEntry
    BackupData --> GrowthTimelineResult
    BackupData --> GrowthTimelineBaseline
    BackupData --> BackupArrInstance
    StrmRepairResult --> StrmFileResult
```

---

## Frontend Dashboard Architecture

The plugin page is served via `IHasWebPages`. `main.js` acts as a router, loading tab modules on demand. All modules share `shared.js` for API calls, formatting, and i18n.

```mermaid
graph TD
    subgraph HTML["configPage.html (IHasWebPages)"]
        PAGE["Embedded Plugin Page"]
    end

    subgraph JS["JavaScript Modules (docs/js/)"]
        MAIN["main.js — Router & Tab Management"]
        SHARED["shared.js — API Helper, Formatting, i18n"]
        OV["Overview.js"]
        SET["Settings.js"]
        COD["Codecs.js"]
        HLT["Health.js"]
        LOG["Logs.js"]
        TRD["Trends.js"]
        ARR["ArrIntegration.js"]
    end

    subgraph API["REST API Endpoints"]
        E1["/JellyfinHelper/Configuration"]
        E2["/JellyfinHelper/MediaStatistics"]
        E3["/JellyfinHelper/CleanupStatistics"]
        E4["/JellyfinHelper/Trash"]
        E5["/JellyfinHelper/Logs"]
        E6["/JellyfinHelper/GrowthTimeline"]
        E7["/JellyfinHelper/Backup"]
        E8["/JellyfinHelper/ArrIntegration"]
        E9["/JellyfinHelper/Translations"]
    end

    PAGE --> MAIN
    MAIN --> SHARED
    MAIN --> OV
    MAIN --> SET
    MAIN --> COD
    MAIN --> HLT
    MAIN --> LOG
    MAIN --> TRD
    MAIN --> ARR

    OV --> SHARED
    SET --> SHARED
    COD --> SHARED
    HLT --> SHARED
    LOG --> SHARED
    TRD --> SHARED
    ARR --> SHARED

    OV -->|fetch| E2
    OV -->|fetch| E3
    SET -->|fetch| E1
    SET -->|fetch| E7
    COD -->|fetch| E2
    HLT -->|fetch| E4
    LOG -->|fetch| E5
    TRD -->|fetch| E6
    ARR -->|fetch| E8
    MAIN -->|fetch| E9
```

---

## Static Utility Classes

These classes are **not registered in DI** — they are static helpers used directly by services and tasks.

| Class | Purpose | Used by |
|---|---|---|
| `MediaExtensions` | Video, audio, subtitle, image, NFO file extension sets | `StrmRepairService`, `CleanOrphanedSubtitlesTask`, `MediaStatisticsService` |
| `PathValidator` | `IsSafePath()`, `SanitizeFileName()` — path traversal protection | `TrashService`, `StrmRepairService` |
| `FileSystemHelper` | `CalculateDirectorySize()`, `IncrementCount()` | `MediaStatisticsService` |
| `LibraryPathResolver` | `GetDistinctLibraryLocations()` | `GrowthTimelineService` |
| `TimelineAggregator` | Bucket calculation, date-range aggregation | `GrowthTimelineService` |
| `I18NService` | `GetTranslations()` — loads embedded JSON resources | `TranslationsController` |
| `JsonDefaults` | Shared `JsonSerializerOptions` | `BackupService`, `StatisticsCacheService` |
| `ConfigurationRequestValidator` | Validates incoming configuration requests | `ConfigurationController` |
| `BackupValidator` | Validates backup JSON structure before restore | `BackupController` |
| `BackupSanitizer` | Sanitizes imported backup data | `BackupController` |

---

## Test Architecture

```
Jellyfin.Plugin.JellyfinHelper.Tests/
├── Api/                          # Controller tests
├── Configuration/                # Configuration model tests
├── PluginPages/                  # Page registration tests
├── ScheduledTasks/               # Task execution tests
├── Services/
│   ├── Backup/
│   │   └── BackupServicePerformanceTests.cs
│   ├── Cleanup/
│   │   └── TrashServiceSecurityTests.cs
│   ├── Strm/
│   │   └── StrmRepairSecurityTests.cs
│   └── Timeline/
│       └── GrowthTimelineServiceTests.cs
└── TestFixtures/                 # Shared test helpers
```

---

## Design Patterns

| Pattern | Where | Description |
|---|---|---|
| **Template Method** | `BaseLibraryCleanupTask` | Abstract base defines execution order; concrete tasks implement `IsDryRun()` and `ProcessLocation()` only |
| **Dependency Injection** | `PluginServiceRegistrator` | All services registered as singletons via interfaces |
| **Singleton** | `Plugin.Instance` | Static plugin instance for global configuration access |
| **Strategy** | `TaskMode` enum | Runtime behavior (Activate / DryRun / Deactivate) configurable per cleanup task |
| **Facade** | `HelperCleanupTask` | Orchestrates all sub-tasks behind a single `IScheduledTask` |
| **Repository / Cache** | `StatisticsCacheService` | Persists scan results as JSON files |
| **Validator** | `BackupValidator`, `ConfigurationRequestValidator` | Separate validation classes for input data |
| **Sanitizer** | `BackupSanitizer` | Cleanses imported data before processing |