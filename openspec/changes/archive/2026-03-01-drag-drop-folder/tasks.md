## 1. Bare Arg Rewriting

- [x] 1.1 Add arg preprocessing in `Program.cs`: detect `args[0]` not starting with `--`, prepend `--Mount:ArchiveDirectory=<value>` to args array before `Host.CreateDefaultBuilder(args)`

## 2. Directory Validation

- [x] 2.1 In `DokanHostedService.ExecuteAsync`, add `Directory.Exists()` check after the existing empty-string validation — if `ArchiveDirectory` is not an existing directory, log error with the invalid path
- [x] 2.2 Add press-any-key UX: on any `ArchiveDirectory` validation failure (empty or not a directory), print "Press any key to exit..." and call `Console.ReadKey()` before `StopApplication()`

## 3. Tests

- [x] 3.1 Unit test: bare arg `["D:\folder"]` is rewritten to include `--Mount:ArchiveDirectory=D:\folder`
- [x] 3.2 Unit test: args starting with `--` are passed through unchanged
- [x] 3.3 Unit test: empty args are passed through unchanged
- [x] 3.4 Unit test: bare arg + explicit `--Mount:ArchiveDirectory` — explicit wins (last-wins)
