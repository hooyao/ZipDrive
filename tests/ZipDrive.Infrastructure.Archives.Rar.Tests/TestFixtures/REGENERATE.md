# Regenerating RAR Test Fixtures

The `.rar` files in this directory were created with WinRAR's `rar.exe` command-line tool.
`rar.exe` is proprietary software (RARLAB) and is **not** included in this repository.

## Prerequisites

Install [WinRAR 6.24 x64](https://www.win-rar.com/fileadmin/winrar-versions/winrar/winrar-x64-624.exe)
and ensure `rar.exe` is on your PATH (typically `C:\Program Files\WinRAR\rar.exe`).

The original fixtures were generated with WinRAR 6.24.
Any WinRAR 6.x+ that supports `-ma5` (RAR5 format) should produce compatible output.

## Commands

Run from this directory (`TestFixtures/`):

```bat
rar a -ep1 -r -ma5 -s-  nonsolid-rar5.rar _src\*
rar a -ep1 -r -ma5 -s   solid-rar5.rar    _src\*
rar a -ep1 -r -ma4 -s-  nonsolid-rar4.rar _src\*
rar a -ep1 -r -ma4 -s   solid-rar4.rar    _src\*
```

| Flag | Meaning |
|------|---------|
| `-ep1` | Exclude base `_src\` prefix from stored paths |
| `-r` | Recurse into subdirectories |
| `-ma5` / `-ma4` | RAR5 / RAR4 format |
| `-s` / `-s-` | Solid / non-solid archive |
