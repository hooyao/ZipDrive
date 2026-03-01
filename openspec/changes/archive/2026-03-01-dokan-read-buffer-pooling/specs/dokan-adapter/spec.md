## MODIFIED Requirements

### Requirement: ReadFile uses pooled buffers
The DokanFileSystemAdapter SHALL use `NativeMemory<byte>.RentArray()` to obtain a pooled buffer for read operations instead of allocating a new `byte[]` on each call. The buffer SHALL be returned via `ReturnArray()` after the read completes.

#### Scenario: Successful read with pooled buffer
- **WHEN** Dokan invokes ReadFile with a `NativeMemory<byte>` buffer
- **THEN** the adapter rents a buffer from the pool via `RentArray()`, passes it to `ReadFileAsync`, and returns it via `ReturnArray(array, copyBack: true)`

#### Scenario: Read returns zero bytes
- **WHEN** `ReadFileAsync` returns 0 bytes (EOF or empty file)
- **THEN** the adapter returns the buffer via `ReturnArray(array, copyBack: false)` to skip the unnecessary copy

#### Scenario: Read throws exception
- **WHEN** `ReadFileAsync` throws an exception
- **THEN** the rented buffer is still returned to the pool (via finally or catch) to prevent pool exhaustion
