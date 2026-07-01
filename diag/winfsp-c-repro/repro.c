/*
 * winfsp-c-repro — pure-C WinFsp minimal filesystem to isolate the same-file
 * read-serialization hang, with NO winfsp-native (.NET) binding involved.
 *
 * Purpose (see diag/out/*.md): confirm whether the "a slow read of video.bin
 * blocks a concurrent OPEN/read of the SAME file" behavior lives in the WinFsp
 * FSD/I-O-Manager layer itself (reproduces in pure C) or in the .NET binding
 * (does NOT reproduce in pure C). Also lets us toggle:
 *   - STATUS_PENDING async reads (like memfs slowio / winfsp-native) vs blocking
 *   - FileInfoTimeout = 0 (no kernel cache, ZipDrive's setting) vs > 0 (cached)
 * to see if the kernel-cache read path (FspFsvolReadCached, which releases the
 * FileNode lock inside the FSD instead of holding it across the user-mode round
 * trip) avoids the serialization.
 *
 * SAFETY: mounts via UNC/net prefix (\\winfsp-crepro\share) by default — NEVER a
 * drive letter. In-memory, read-only. A crash cannot wedge a drive letter.
 * A --dir=<empty existing dir> flag is provided as a safe fallback (directory
 * mount point / reparse point, still no drive letter).
 *
 * Layout (read-only):
 *   \video.bin  (64 MB)  reads at offset >= TAIL_START block/pend for the tail
 *                        delay; other offsets are instant.
 *   \other.bin  (64 MB)  always instant.
 *
 * Build: see build.cmd (uses FspLoad delay-loading of winfsp-x64.dll).
 *
 * Run:
 *   winfsp-c-repro.exe [--pending] [--timeout=MILLIS] [--tailDelayMs=MS]
 *                      [--debug] [--dir=PATH]
 */

#include <winfsp/winfsp.h>
#include <sddl.h>
#include <strsafe.h>
#include <stdio.h>

#define FILE_SIZE           (64ULL * 1024 * 1024)
#define TAIL_START          (32ULL * 1024 * 1024)
#define ALLOC_UNIT          4096

/* ── diagnostic logging (gated on --debug) ── */
static BOOLEAN gVerbose = FALSE;
static ULONGLONG gT0;
#define LOG(...) do { if (gVerbose) { \
    fprintf(stderr, "[%6llums t%-5lu] ", \
        (unsigned long long)(GetTickCount64() - gT0), (unsigned long)GetCurrentThreadId()); \
    fprintf(stderr, __VA_ARGS__); fputc('\n', stderr); fflush(stderr); } } while (0)

typedef struct
{
    FSP_FILE_SYSTEM *FileSystem;
    ULONG TailDelayMs;      /* how long a tail read blocks / pends for */
    BOOLEAN UsePending;     /* if TRUE, tail reads return STATUS_PENDING + async thread */
    BOOLEAN SlowAll;        /* if TRUE, ALL video.bin reads are slow (scenario C control) */
} REPRO;

/* Two virtual files. FileContext is a small tag: 1 = video.bin, 2 = other.bin, 0 = root dir. */
#define CTX_ROOT   ((PVOID)0)
#define CTX_VIDEO  ((PVOID)1)
#define CTX_OTHER  ((PVOID)2)

/* Shared root/file security descriptor (self-relative), built once at startup. */
static PSECURITY_DESCRIPTOR gSD = 0;
static DWORD gSDLen = 0;

static const char *CtxName(PVOID ctx)
{
    if (ctx == CTX_VIDEO) return "video.bin";
    if (ctx == CTX_OTHER) return "other.bin";
    return "<root>";
}

static VOID FillFileInfo(FSP_FSCTL_FILE_INFO *FileInfo, BOOLEAN IsDir)
{
    memset(FileInfo, 0, sizeof *FileInfo);
    if (IsDir)
    {
        FileInfo->FileAttributes = FILE_ATTRIBUTE_DIRECTORY;
        FileInfo->FileSize = 0;
        FileInfo->AllocationSize = 0;
    }
    else
    {
        FileInfo->FileAttributes = FILE_ATTRIBUTE_ARCHIVE | FILE_ATTRIBUTE_READONLY;
        FileInfo->FileSize = FILE_SIZE;
        FileInfo->AllocationSize = (FILE_SIZE + ALLOC_UNIT - 1) / ALLOC_UNIT * ALLOC_UNIT;
    }
}

static BOOLEAN NameIsVideo(PWSTR n) { return 0 == wcscmp(n, L"\\video.bin"); }
static BOOLEAN NameIsOther(PWSTR n) { return 0 == wcscmp(n, L"\\other.bin"); }
static BOOLEAN NameIsRoot(PWSTR n)  { return 0 == wcscmp(n, L"\\"); }

static NTSTATUS GetVolumeInfo(FSP_FILE_SYSTEM *FileSystem, FSP_FSCTL_VOLUME_INFO *VolumeInfo)
{
    (void)FileSystem;
    memset(VolumeInfo, 0, sizeof *VolumeInfo);
    VolumeInfo->TotalSize = 1ULL << 31;
    VolumeInfo->FreeSize = 0;
    StringCbCopyW(VolumeInfo->VolumeLabel, sizeof VolumeInfo->VolumeLabel, L"CRepro");
    VolumeInfo->VolumeLabelLength = (UINT16)(wcslen(VolumeInfo->VolumeLabel) * sizeof(WCHAR));
    return STATUS_SUCCESS;
}

/* Copy the shared SD into caller buffer honoring the WinFsp size-query contract. */
static NTSTATUS CopyOutSD(PSECURITY_DESCRIPTOR SecurityDescriptor, SIZE_T *PSecurityDescriptorSize)
{
    if (0 != PSecurityDescriptorSize)
    {
        if (gSDLen > *PSecurityDescriptorSize)
        {
            *PSecurityDescriptorSize = gSDLen;
            return STATUS_BUFFER_OVERFLOW;
        }
        *PSecurityDescriptorSize = gSDLen;
        if (0 != SecurityDescriptor)
            memcpy(SecurityDescriptor, gSD, gSDLen);
    }
    return STATUS_SUCCESS;
}

static NTSTATUS GetSecurityByName(FSP_FILE_SYSTEM *FileSystem,
    PWSTR FileName, PUINT32 PFileAttributes,
    PSECURITY_DESCRIPTOR SecurityDescriptor, SIZE_T *PSecurityDescriptorSize)
{
    (void)FileSystem;
    BOOLEAN exists = NameIsRoot(FileName) || NameIsVideo(FileName) || NameIsOther(FileName);
    LOG("GetSecurityByName '%S' exists=%d", FileName, exists);
    if (!exists)
        return STATUS_OBJECT_NAME_NOT_FOUND;

    if (0 != PFileAttributes)
        *PFileAttributes = NameIsRoot(FileName)
            ? FILE_ATTRIBUTE_DIRECTORY
            : (FILE_ATTRIBUTE_ARCHIVE | FILE_ATTRIBUTE_READONLY);

    return CopyOutSD(SecurityDescriptor, PSecurityDescriptorSize);
}

static NTSTATUS GetSecurity(FSP_FILE_SYSTEM *FileSystem, PVOID FileContext,
    PSECURITY_DESCRIPTOR SecurityDescriptor, SIZE_T *PSecurityDescriptorSize)
{
    (void)FileSystem;
    LOG("GetSecurity %s", CtxName(FileContext));
    return CopyOutSD(SecurityDescriptor, PSecurityDescriptorSize);
}

static NTSTATUS Open(FSP_FILE_SYSTEM *FileSystem,
    PWSTR FileName, UINT32 CreateOptions, UINT32 GrantedAccess,
    PVOID *PFileContext, FSP_FSCTL_FILE_INFO *FileInfo)
{
    (void)FileSystem; (void)CreateOptions; (void)GrantedAccess;
    LOG("Open '%S'", FileName);
    if (NameIsRoot(FileName))  { *PFileContext = CTX_ROOT;  FillFileInfo(FileInfo, TRUE);  return STATUS_SUCCESS; }
    if (NameIsVideo(FileName)) { *PFileContext = CTX_VIDEO; FillFileInfo(FileInfo, FALSE); return STATUS_SUCCESS; }
    if (NameIsOther(FileName)) { *PFileContext = CTX_OTHER; FillFileInfo(FileInfo, FALSE); return STATUS_SUCCESS; }
    return STATUS_OBJECT_NAME_NOT_FOUND;
}

static VOID Close(FSP_FILE_SYSTEM *FileSystem, PVOID FileContext)
{
    (void)FileSystem;
    LOG("Close %s", CtxName(FileContext));
}

static NTSTATUS GetFileInfo(FSP_FILE_SYSTEM *FileSystem, PVOID FileContext, FSP_FSCTL_FILE_INFO *FileInfo)
{
    (void)FileSystem;
    LOG("GetFileInfo %s", CtxName(FileContext));
    FillFileInfo(FileInfo, FileContext == CTX_ROOT);
    return STATUS_SUCCESS;
}

/* Read-only stubs. FspFileSystemOpCreate() requires Create/CreateEx AND
 * Overwrite/OverwriteEx to be non-NULL or it rejects EVERY create IRP (even
 * FILE_OPEN of an existing file) with STATUS_INVALID_DEVICE_REQUEST before it
 * ever dispatches to Open(). We never create/overwrite (ReadOnlyVolume=1), so
 * these just deny — existing-file opens are served by Open() above. */
static NTSTATUS Create(FSP_FILE_SYSTEM *FileSystem,
    PWSTR FileName, UINT32 CreateOptions, UINT32 GrantedAccess, UINT32 FileAttributes,
    PSECURITY_DESCRIPTOR SecurityDescriptor, UINT64 AllocationSize,
    PVOID *PFileContext, FSP_FSCTL_FILE_INFO *FileInfo)
{
    (void)FileSystem; (void)CreateOptions; (void)GrantedAccess; (void)FileAttributes;
    (void)SecurityDescriptor; (void)AllocationSize; (void)PFileContext; (void)FileInfo;
    LOG("Create '%S' -> ACCESS_DENIED (read-only)", FileName);
    return STATUS_ACCESS_DENIED;
}

static NTSTATUS Overwrite(FSP_FILE_SYSTEM *FileSystem,
    PVOID FileContext, UINT32 FileAttributes, BOOLEAN ReplaceFileAttributes,
    UINT64 AllocationSize, FSP_FSCTL_FILE_INFO *FileInfo)
{
    (void)FileSystem; (void)FileContext; (void)FileAttributes; (void)ReplaceFileAttributes;
    (void)AllocationSize; (void)FileInfo;
    LOG("Overwrite -> ACCESS_DENIED (read-only)");
    return STATUS_ACCESS_DENIED;
}

/* ── the read under test ── */
static VOID DoFill(PVOID Buffer, UINT64 Offset, ULONG Count)
{
    (void)Offset;
    memset(Buffer, 0xCD, Count);
}

typedef struct
{
    FSP_FILE_SYSTEM *FileSystem;
    PVOID Buffer;
    UINT64 Offset;
    ULONG Length;
    UINT64 Hint;
    ULONG DelayMs;
} PENDING_READ;

static DWORD WINAPI PendingReadThread(PVOID Param)
{
    PENDING_READ *pr = (PENDING_READ *)Param;
    FSP_FSCTL_TRANSACT_RSP Rsp;

    LOG("  [pending worker] sleeping %lums for offset=%llu", pr->DelayMs, (unsigned long long)pr->Offset);
    Sleep(pr->DelayMs); /* simulate slow sequential extraction */

    UINT64 endOff = pr->Offset + pr->Length;
    if (endOff > FILE_SIZE) endOff = FILE_SIZE;
    ULONG xfer = (ULONG)(endOff - pr->Offset);
    DoFill(pr->Buffer, pr->Offset, xfer);

    memset(&Rsp, 0, sizeof Rsp);
    Rsp.Size = sizeof Rsp;
    Rsp.Kind = FspFsctlTransactReadKind;
    Rsp.Hint = pr->Hint;
    Rsp.IoStatus.Status = STATUS_SUCCESS;
    Rsp.IoStatus.Information = xfer;
    LOG("  [pending worker] SendResponse offset=%llu xfer=%lu", (unsigned long long)pr->Offset, xfer);
    FspFileSystemSendResponse(pr->FileSystem, &Rsp);

    free(pr);
    return 0;
}

static NTSTATUS Read(FSP_FILE_SYSTEM *FileSystem,
    PVOID FileContext, PVOID Buffer, UINT64 Offset, ULONG Length, PULONG PBytesTransferred)
{
    REPRO *r = (REPRO *)FileSystem->UserContext;

    if (Offset >= FILE_SIZE)
        return STATUS_END_OF_FILE;

    BOOLEAN isVideo = (FileContext == CTX_VIDEO);
    BOOLEAN isTail = (Offset >= TAIL_START);
    /* Normally only the tail is slow; --slowAll makes EVERY video.bin read slow
     * (scenario C: does any cache-miss slow read block OPEN, or only tail reads?). */
    BOOLEAN slow = isVideo && (isTail || r->SlowAll) && r->TailDelayMs > 0;

    LOG("Read ENTER %s offset=%llu len=%lu %s", CtxName(FileContext),
        (unsigned long long)Offset, Length, slow ? "(SLOW)" : "");

    if (slow)
    {
        if (r->UsePending)
        {
            /* Async model (like winfsp-native / memfs slowio): return STATUS_PENDING,
             * complete from a worker thread. NOTE: per the FSD analysis, the FileNode
             * lock is STILL held until SendResponse — this tests whether pending vs
             * blocking changes the same-file serialization at all. */
            PENDING_READ *pr = (PENDING_READ *)malloc(sizeof *pr);
            if (0 != pr)
            {
                pr->FileSystem = FileSystem;
                pr->Buffer = Buffer;
                pr->Offset = Offset;
                pr->Length = Length;
                pr->Hint = FspFileSystemGetOperationContext()->Request->Hint;
                pr->DelayMs = r->TailDelayMs;
                HANDLE h = CreateThread(0, 0, PendingReadThread, pr, 0, 0);
                if (0 != h)
                {
                    CloseHandle(h);
                    LOG("Read PENDING %s offset=%llu (returned STATUS_PENDING)",
                        CtxName(FileContext), (unsigned long long)Offset);
                    return STATUS_PENDING;
                }
                free(pr); /* fall through to blocking on failure */
            }
        }
        /* Blocking model (what ZipDrive's ChunkedStream effectively does today). */
        Sleep(r->TailDelayMs);
    }

    UINT64 endOff = Offset + Length;
    if (endOff > FILE_SIZE) endOff = FILE_SIZE;
    ULONG xfer = (ULONG)(endOff - Offset);
    DoFill(Buffer, Offset, xfer);
    *PBytesTransferred = xfer;
    LOG("Read EXIT  %s offset=%llu xfer=%lu", CtxName(FileContext),
        (unsigned long long)Offset, xfer);
    return STATUS_SUCCESS;
}

static NTSTATUS AddDirInfoEntry(PWSTR Name, PVOID Buffer, ULONG Length, PULONG PBytesTransferred)
{
    union
    {
        UINT8 B[sizeof(FSP_FSCTL_DIR_INFO) + MAX_PATH * sizeof(WCHAR)];
        FSP_FSCTL_DIR_INFO D;
    } u;
    memset(&u.D, 0, sizeof u.D);
    size_t nameLen = wcslen(Name) * sizeof(WCHAR);
    u.D.Size = (UINT16)(sizeof(FSP_FSCTL_DIR_INFO) + nameLen);
    FillFileInfo(&u.D.FileInfo, FALSE);
    memcpy(u.D.FileNameBuf, Name, nameLen);
    return FspFileSystemAddDirInfo(&u.D, Buffer, Length, PBytesTransferred) ? STATUS_SUCCESS : STATUS_BUFFER_OVERFLOW;
}

static NTSTATUS ReadDirectory(FSP_FILE_SYSTEM *FileSystem,
    PVOID FileContext, PWSTR Pattern, PWSTR Marker, PVOID Buffer, ULONG Length, PULONG PBytesTransferred)
{
    (void)FileSystem; (void)FileContext; (void)Pattern;
    LOG("ReadDirectory marker=%S len=%lu", Marker ? Marker : L"(none)", Length);

    /* Two small entries. Emit only entries lexicographically after Marker so a
     * continuation call cannot loop or double-add. Names: "other.bin", "video.bin". */
    static const PWSTR names[] = { L"other.bin", L"video.bin" };
    for (int i = 0; i < 2; i++)
    {
        if (0 != Marker && wcscmp(names[i], Marker) <= 0)
            continue; /* already returned in a previous chunk */
        if (STATUS_SUCCESS != AddDirInfoEntry(names[i], Buffer, Length, PBytesTransferred))
            /* Buffer full: stop WITHOUT the end marker so the FSD asks again with a Marker. */
            return STATUS_SUCCESS;
    }
    /* All remaining entries fit: finalize with the end marker. */
    FspFileSystemAddDirInfo(0, Buffer, Length, PBytesTransferred);
    return STATUS_SUCCESS;
}

static FSP_FILE_SYSTEM_INTERFACE ReproInterface =
{
    .GetVolumeInfo = GetVolumeInfo,
    .GetSecurityByName = GetSecurityByName,
    .GetSecurity = GetSecurity,
    .Open = Open,
    .Close = Close,
    .Create = Create,
    .Overwrite = Overwrite,
    .Read = Read,
    .GetFileInfo = GetFileInfo,
    .ReadDirectory = ReadDirectory,
};

static PWSTR GetStrArg(int argc, wchar_t **argv, const wchar_t *key)
{
    size_t klen = wcslen(key);
    for (int i = 1; i < argc; i++)
        if (0 == wcsncmp(argv[i], key, klen))
            return argv[i] + klen;
    return 0;
}
static int GetIntArg(int argc, wchar_t **argv, const wchar_t *key, int dflt)
{
    PWSTR v = GetStrArg(argc, argv, key);
    return v ? _wtoi(v) : dflt;
}
static BOOLEAN HasFlag(int argc, wchar_t **argv, const wchar_t *key)
{
    for (int i = 1; i < argc; i++)
        if (0 == wcscmp(argv[i], key))
            return TRUE;
    return FALSE;
}

int wmain(int argc, wchar_t **argv)
{
    NTSTATUS Result;
    PVOID Module;

    gT0 = GetTickCount64();

    Result = FspLoad(&Module);
    if (!NT_SUCCESS(Result))
    {
        fwprintf(stderr, L"FspLoad failed 0x%lx — is WinFsp installed?\n", (unsigned long)Result);
        return 1;
    }

    /* Build a permissive self-relative security descriptor once (Everyone: full access).
     * Without a real SD, opens over the net redirector can fail access checks. */
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
            L"O:BAG:BAD:P(A;;FA;;;WD)", SDDL_REVISION_1, &gSD, &gSDLen))
    {
        fwprintf(stderr, L"ConvertStringSecurityDescriptor failed %lu\n", GetLastError());
        return 1;
    }

    REPRO repro;
    memset(&repro, 0, sizeof repro);
    repro.TailDelayMs = (ULONG)GetIntArg(argc, argv, L"--tailDelayMs=", 3000);
    repro.UsePending = HasFlag(argc, argv, L"--pending");
    repro.SlowAll = HasFlag(argc, argv, L"--slowAll");
    gVerbose = HasFlag(argc, argv, L"--debug");
    int timeoutMs = GetIntArg(argc, argv, L"--timeout=", 0); /* FileInfoTimeout; 0 = no kernel cache */
    /* --infinite-cache (or --timeout=-1) sets FileInfoTimeout = FspTimeoutInfinity32
     * (0xFFFFFFFF). This is the ONLY value that enables the FSD cached read path
     * (FspFsvolReadCached asserts FileInfoTimeout == FspTimeoutInfinity32, read.c:248).
     * Any finite timeout (e.g. 1000) does NOT enable kernel data caching. */
    BOOLEAN infiniteCache = HasFlag(argc, argv, L"--infinite-cache") || timeoutMs == -1;
    PWSTR dirMount = GetStrArg(argc, argv, L"--dir="); /* optional safe directory mount fallback */
    PWSTR prefixArg = GetStrArg(argc, argv, L"--prefix="); /* override UNC prefix, e.g. \\host\share */

    UINT32 fileInfoTimeout = infiniteCache ? 0xFFFFFFFFu : (UINT32)timeoutMs;

    FSP_FSCTL_VOLUME_PARAMS vp;
    memset(&vp, 0, sizeof vp);
    vp.Version = sizeof vp;
    vp.SectorSize = ALLOC_UNIT;
    vp.SectorsPerAllocationUnit = 1;
    vp.VolumeCreationTime = 0;
    vp.VolumeSerialNumber = 0x43524550;
    vp.FileInfoTimeout = fileInfoTimeout;
    vp.CaseSensitiveSearch = 0;
    vp.CasePreservedNames = 1;
    vp.UnicodeOnDisk = 1;
    vp.PersistentAcls = 1;
    vp.ReadOnlyVolume = 1;
    vp.PostCleanupWhenModifiedOnly = 1;
    vp.UmFileContextIsUserContext2 = 0;
    /* Required for the net redirector/MUP to open files in kernel mode while
     * probing the share; without this the UNC path fails to resolve. memfs-net
     * sets this. */
    vp.AllowOpenInKernelMode = 1;

    PWSTR devicePath;
    if (0 != dirMount)
    {
        /* Directory mount into an existing empty dir — safe, no drive letter. */
        devicePath = L"" FSP_FSCTL_DISK_DEVICE_NAME;
    }
    else
    {
        /* UNC/net prefix mount — SAFE, no drive letter. Default \\winfsp-crepro\share,
         * overridable with --prefix=\\host\share to dodge redirector negative-cache. */
        StringCbCopyW(vp.Prefix, sizeof vp.Prefix,
            (0 != prefixArg && L'\0' != prefixArg[0]) ? prefixArg : L"\\winfsp-crepro\\share");
        devicePath = L"" FSP_FSCTL_NET_DEVICE_NAME;
    }
    StringCbCopyW(vp.FileSystemName, sizeof vp.FileSystemName, L"NTFS");

    FSP_FILE_SYSTEM *fs;
    Result = FspFileSystemCreate(devicePath, &vp, &ReproInterface, &fs);
    if (!NT_SUCCESS(Result))
    {
        fwprintf(stderr, L"FspFileSystemCreate failed 0x%lx\n", (unsigned long)Result);
        return 1;
    }
    fs->UserContext = &repro;
    repro.FileSystem = fs;

    if (gVerbose)
    {
        FspDebugLogSetHandle(GetStdHandle(STD_ERROR_HANDLE));
        FspFileSystemSetDebugLog(fs, -1); /* FSD-level request/response tracing to stderr */
    }

    /* Mount point:
     *  - UNC/net mode: DO NOT call SetMountPoint. The volume is reachable at
     *    \\winfsp-crepro\share via the WinFsp network provider (memfs-net does this).
     *  - --dir mode: mount into the given existing empty directory (reparse point). */
    if (0 != dirMount)
    {
        Result = FspFileSystemSetMountPoint(fs, dirMount);
        if (!NT_SUCCESS(Result))
        {
            fwprintf(stderr, L"SetMountPoint('%s') failed 0x%lx\n", dirMount, (unsigned long)Result);
            FspFileSystemDelete(fs);
            return 1;
        }
    }

    Result = FspFileSystemStartDispatcher(fs, 0);
    if (!NT_SUCCESS(Result))
    {
        fwprintf(stderr, L"StartDispatcher failed 0x%lx\n", (unsigned long)Result);
        FspFileSystemRemoveMountPoint(fs);
        FspFileSystemDelete(fs);
        return 1;
    }

    PWSTR mp = FspFileSystemMountPoint(fs);
    /* Access root: for net mode it's \\<prefix-with-leading-backslash-doubled>. The
     * VolumeParams.Prefix already begins with a single backslash (\host\share); the
     * UNC form just needs one more leading backslash. */
    WCHAR accessBuf[260];
    PWSTR access;
    if (0 != dirMount)
        access = dirMount;
    else
    {
        StringCbPrintfW(accessBuf, sizeof accessBuf, L"\\%s", vp.Prefix);
        access = accessBuf;
    }
    wprintf(L"MOUNTED at: %s\n", mp ? mp : access);
    wprintf(L"access root: %s\n", access);
    wprintf(L"mode: %s  tailDelayMs=%lu  slowAll=%d  FileInfoTimeout=%s(0x%08lX)  debug=%d\n",
        repro.UsePending ? L"PENDING(async)" : L"BLOCKING(sync)",
        repro.TailDelayMs, repro.SlowAll,
        infiniteCache ? L"INFINITE" : L"finite",
        (unsigned long)fileInfoTimeout, gVerbose);
    wprintf(L"kernel cached-read path: %s\n",
        infiniteCache ? L"ENABLED (FileInfoTimeout==FspTimeoutInfinity32)"
                      : L"DISABLED (finite timeout -> always non-cached)");
    wprintf(L"files: %s\\video.bin  %s\\other.bin\n", access, access);
    wprintf(L"Press Ctrl+C to unmount.\n");
    fflush(stdout);

    /* Idle until Ctrl+C / kill. The reader/probe driver runs from a separate
       process (the .NET probe) against this mount. */
    for (;;) Sleep(1000);

    FspFileSystemStopDispatcher(fs);
    FspFileSystemRemoveMountPoint(fs);
    FspFileSystemDelete(fs);
    return 0;
}
