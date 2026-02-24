# telemetry Specification

## Purpose
OpenTelemetry instrumentation layer for ZipDrive. Defines metrics instruments, activity sources, and size bucket classification for cache, ZIP extraction, and Dokan read operations.

## Requirements
### Requirement: Cache metrics via System.Diagnostics.Metrics

The caching subsystem SHALL emit metrics using `System.Diagnostics.Metrics` with a `Meter` named `"ZipDrive.Caching"`. All counter and histogram recordings SHALL include a `tier` tag identifying the cache instance (e.g., `"memory"`, `"disk"`, `"structure"`).

#### Scenario: Cache hit increments counter

- **WHEN** a cache lookup finds a valid, non-expired entry
- **THEN** the `cache.hits` Counter\<long\> SHALL be incremented by 1
- **AND** the recording SHALL include tag `tier` set to the cache instance name

#### Scenario: Cache miss increments counter

- **WHEN** a cache lookup does not find a valid entry (miss or expired)
- **THEN** the `cache.misses` Counter\<long\> SHALL be incremented by 1
- **AND** the recording SHALL include tag `tier` set to the cache instance name

#### Scenario: Cache eviction increments counter

- **WHEN** an entry is evicted from the cache
- **THEN** the `cache.evictions` Counter\<long\> SHALL be incremented by 1
- **AND** the recording SHALL include tag `tier` and tag `reason` set to one of `"policy"`, `"expired"`, or `"manual"`

#### Scenario: Materialization duration recorded as histogram

- **WHEN** a cache miss triggers factory execution (materialization)
- **THEN** the `cache.materialization.duration` Histogram\<double\> SHALL record the elapsed milliseconds
- **AND** the recording SHALL include tags `tier` and `size_bucket`

---

### Requirement: Cache observable gauges

The caching subsystem SHALL expose observable gauges via `System.Diagnostics.Metrics` that report current cache state when polled.

#### Scenario: Cache size bytes gauge

- **WHEN** the metrics collector polls `cache.size_bytes`
- **THEN** the ObservableGauge\<long\> SHALL return the current `CurrentSizeBytes` for each cache instance
- **AND** each measurement SHALL include the `tier` tag

#### Scenario: Cache entry count gauge

- **WHEN** the metrics collector polls `cache.entry_count`
- **THEN** the ObservableGauge\<int\> SHALL return the current `EntryCount` for each cache instance
- **AND** each measurement SHALL include the `tier` tag

#### Scenario: Cache utilization gauge

- **WHEN** the metrics collector polls `cache.utilization`
- **THEN** the ObservableGauge\<double\> SHALL return `CurrentSizeBytes / CapacityBytes` (0.0 to 1.0) for each cache instance
- **AND** each measurement SHALL include the `tier` tag

---

### Requirement: ZIP extraction metrics

The ZIP reader subsystem SHALL emit metrics using a `Meter` named `"ZipDrive.Zip"`.

#### Scenario: Extraction duration recorded with size bucket

- **WHEN** a file is extracted from a ZIP archive
- **THEN** the `zip.extraction.duration` Histogram\<double\> SHALL record the elapsed milliseconds
- **AND** the recording SHALL include tag `size_bucket` categorized as: `"tiny"` (< 1 KB), `"small"` (1 KB – 1 MB), `"medium"` (1 MB – 10 MB), `"large"` (10 MB – 50 MB), `"xlarge"` (50 MB – 500 MB), `"huge"` (> 500 MB)
- **AND** the recording SHALL include tag `compression` set to `"store"` or `"deflate"`

#### Scenario: Bytes extracted counter

- **WHEN** a file is extracted from a ZIP archive
- **THEN** the `zip.bytes_extracted` Counter\<long\> SHALL be incremented by the uncompressed file size in bytes
- **AND** the recording SHALL include tag `compression`

---

### Requirement: Dokan read latency metric

The Dokan file system adapter SHALL emit a read latency metric using a `Meter` named `"ZipDrive.Dokan"`.

#### Scenario: Read latency recorded

- **WHEN** a `ReadFile` request is processed by the Dokan adapter
- **THEN** the `dokan.read.duration` Histogram\<double\> SHALL record the total elapsed milliseconds from method entry to return
- **AND** the recording SHALL include tag `result` set to `"success"` or `"error"`

---

### Requirement: Cache tracing via ActivitySource

The caching subsystem SHALL create trace spans using an `ActivitySource` named `"ZipDrive.Caching"`.

#### Scenario: Borrow operation creates span

- **WHEN** `BorrowAsync` is called on a cache instance
- **THEN** an Activity named `"cache.borrow"` SHALL be started
- **AND** the Activity SHALL include tags `tier`, and `result` set to `"hit"` or `"miss"`

#### Scenario: Materialization creates child span

- **WHEN** a cache miss triggers materialization
- **THEN** an Activity named `"cache.materialize"` SHALL be started as a child of the `"cache.borrow"` Activity
- **AND** the Activity SHALL include tags `tier`, `size_bucket`, and `size_bytes`

#### Scenario: Eviction batch creates span

- **WHEN** eviction is triggered (by capacity pressure or manual call)
- **THEN** an Activity named `"cache.evict"` SHALL be started
- **AND** the Activity SHALL include tags `tier`, `reason`, `evicted_count`, and `evicted_bytes`

---

### Requirement: Static telemetry class per subsystem

Each infrastructure project SHALL define an `internal static` telemetry class containing the `Meter`, `ActivitySource`, and all instrument definitions as `static readonly` fields.

#### Scenario: CacheTelemetry class in Caching project

- **WHEN** the Caching infrastructure project is compiled
- **THEN** it SHALL contain a `CacheTelemetry` class with a `Meter` named `"ZipDrive.Caching"` and an `ActivitySource` named `"ZipDrive.Caching"`
- **AND** all cache metric instruments SHALL be defined as static fields on this class

#### Scenario: ZipTelemetry class in Archives.Zip project

- **WHEN** the Archives.Zip infrastructure project is compiled
- **THEN** it SHALL contain a `ZipTelemetry` class with a `Meter` named `"ZipDrive.Zip"`
- **AND** all ZIP extraction instruments SHALL be defined as static fields on this class

#### Scenario: DokanTelemetry class in FileSystem project

- **WHEN** the FileSystem infrastructure project is compiled
- **THEN** it SHALL contain a `DokanTelemetry` class with a `Meter` named `"ZipDrive.Dokan"`
- **AND** the Dokan read latency instrument SHALL be defined as a static field on this class

---

### Requirement: Size bucket classification

Metrics that include a `size_bucket` tag SHALL classify file sizes using a fixed set of buckets.

#### Scenario: Size bucket boundaries

- **WHEN** a file size is classified for metric tagging
- **THEN** the `size_bucket` tag SHALL be set to:
  - `"tiny"` for files < 1,024 bytes
  - `"small"` for files >= 1,024 bytes and < 1,048,576 bytes
  - `"medium"` for files >= 1,048,576 bytes and < 10,485,760 bytes
  - `"large"` for files >= 10,485,760 bytes and < 52,428,800 bytes
  - `"xlarge"` for files >= 52,428,800 bytes and < 524,288,000 bytes
  - `"huge"` for files >= 524,288,000 bytes

---

### Requirement: Zero OTel package dependencies in infrastructure

Infrastructure projects (Caching, Archives.Zip, FileSystem) SHALL NOT reference any OpenTelemetry NuGet packages. All instrumentation SHALL use only `System.Diagnostics.Metrics` and `System.Diagnostics.Activity` from the .NET runtime.

#### Scenario: No OTel packages in infrastructure csproj files

- **WHEN** the infrastructure projects are inspected
- **THEN** their `.csproj` files SHALL NOT contain any `PackageReference` with `Include` starting with `"OpenTelemetry"`
- **AND** instrumentation SHALL compile without any OpenTelemetry SDK present
