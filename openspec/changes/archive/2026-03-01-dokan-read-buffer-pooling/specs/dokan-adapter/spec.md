## MODIFIED Requirements

### Requirement: ReadFile uses pooled buffers
The DokanFileSystemAdapter SHALL use `ArrayPool<byte>.Shared` to obtain a pooled buffer for read operations instead of allocating a new `byte[]` on each call. Only valid bytes (`bytesRead`) SHALL be copied to the native buffer to avoid leaking stale pool data.

#### Scenario: Successful read with pooled buffer
- **WHEN** Dokan invokes ReadFile with a `NativeMemory<byte>` buffer
- **THEN** the adapter rents a buffer from `ArrayPool<byte>.Shared`, passes it to `ReadFileAsync`, copies only `bytesRead` bytes to the native buffer, and returns the array to the pool

#### Scenario: Read returns zero bytes
- **WHEN** `ReadFileAsync` returns 0 bytes (EOF or empty file)
- **THEN** the adapter skips the copy to native buffer, sets `bytesRead` to 0, and returns the array to the pool

#### Scenario: Read throws exception
- **WHEN** `ReadFileAsync` throws an exception
- **THEN** the rented buffer is still returned to `ArrayPool<byte>.Shared` via `finally` block to prevent pool exhaustion
