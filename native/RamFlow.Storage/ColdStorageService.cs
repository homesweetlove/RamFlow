using System.Diagnostics;

namespace RamFlow.Storage;

/// <summary>
/// 사용자가 선택한 원본 루트, 콜드 저장소 및 메타데이터 폴더를 연결하는 UI용 API입니다.
/// 모든 변경/실행 메서드는 DryRun 기본값이 true이며 실제 수행에는 명시적인 확인이 필요합니다.
/// 메타데이터와 루트는 사용자가 신뢰하는 디렉터리에 두세요. 외부 프로세스의 파일 내용 변조를 인증하지는 않습니다.
/// </summary>
public sealed class ColdStorageService
{
    private readonly IColdStorageBackend backend;
    private readonly ManifestStore store;
    public string SourceRoot { get; }
    public string ManifestDirectory => store.Root;
    public string BackendId => backend.BackendId;
    public string? LocalRemovalWarning => backend.HasRemoteServerAcknowledgement ? null : FolderColdStorageBackend.RemovalWarning;

    public ColdStorageService(string sourceRoot, string manifestDirectory, IColdStorageBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        this.backend = backend;
        SourceRoot = Safety.FullPath(sourceRoot);
        Safety.CheckPath(SourceRoot);
        store = new ManifestStore(manifestDirectory);
        if (Safety.Within(SourceRoot, store.Root)) throw new StorageException(StorageError.InvalidPath, "원본 루트는 메타데이터 폴더 내부에 둘 수 없습니다.");
        if (backend.LocalRoot is string local && (Safety.Within(SourceRoot, local) || Safety.Within(local, store.Root) || Safety.Within(store.Root, local)))
            throw new StorageException(StorageError.InvalidPath, "원본, 콜드 저장소 및 메타데이터 경로가 충돌합니다.");
    }

    private string Source(string path)
    {
        var full = Safety.Under(path, SourceRoot);
        if (full.Equals(SourceRoot, Safety.PathComparison) || Safety.Within(full, store.Root) || (backend.LocalRoot is string root && Safety.Within(full, root)))
            throw new StorageException(StorageError.InvalidPath, "루트 자체 또는 저장소 내부 파일은 원본으로 선택할 수 없습니다.");
        return full;
    }

    private static void Confirm(bool dryRun, bool confirmed)
    {
        if (!dryRun && !confirmed) throw new StorageException(StorageError.ConfirmationRequired, "작업 대상과 경고를 확인한 사용자의 명시적 승인이 필요합니다.");
    }

    private async Task<ArchiveManifest> ManifestAsync(Guid id, CancellationToken ct)
    {
        var m = await store.ReadAsync(id, ct).ConfigureAwait(false);
        if (m.BackendId != backend.BackendId) throw new StorageException(StorageError.BackendMismatch, "선택한 백엔드가 아카이브의 백엔드와 다릅니다.");
        Source(m.SourcePath);
        return m;
    }

    public Task<IReadOnlyList<ManifestReadResult>> ListManifestsAsync(CancellationToken cancellationToken = default) => store.ListAsync(cancellationToken);
    public Task<ArchiveManifest> GetManifestAsync(Guid id, CancellationToken cancellationToken = default) => ManifestAsync(id, cancellationToken);

    /// <summary>로컬 파일은 수정 시각 기준 HOT/WARM/COLD 후보로 분류하며, 없는 보관 파일은 기록 기준 COLD로 표시합니다. 원격 재검증은 수행하지 않습니다.</summary>
    public async Task<IReadOnlyList<StorageScanEntry>> ScanAsync(ScanOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        if (options.WarmAfter < TimeSpan.Zero || options.ColdAfter < options.WarmAfter)
            throw new ArgumentException("COLD 기준은 WARM 기준 이상이어야 하며 기간은 음수일 수 없습니다.", nameof(options));
        var now = options.NowUtc ?? DateTime.UtcNow;
        var manifests = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        var valid = new List<ArchiveManifest>();
        var entries = new List<StorageScanEntry>();
        foreach (var item in manifests)
        {
            if (item.Manifest is not { } m) { entries.Add(new(item.Path, StorageTier.Warm, 0, null, Warning: item.Error)); continue; }
            if (m.BackendId != backend.BackendId) continue;
            try { Source(m.SourcePath); valid.Add(m); }
            catch (StorageException ex) { entries.Add(new(item.Path, StorageTier.Warm, 0, null, m.Id, ex.Message)); }
        }
        var byPath = valid.GroupBy(m => m.SourcePath, Safety.PathComparer)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.UpdatedUtc).First(), Safety.PathComparer);
        var seen = new HashSet<string>(Safety.PathComparer);
        var directories = new Stack<string>();
        if (Directory.Exists(SourceRoot)) directories.Push(SourceRoot);
        while (directories.TryPop(out var directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var lease = Safety.LeaseDirectories(directory, true);
                foreach (var child in Directory.EnumerateFileSystemEntries(directory))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (Safety.Within(child, store.Root) || (backend.LocalRoot is string root && Safety.Within(child, root))) continue;
                    try
                    {
                        Source(child);
                        var attrs = File.GetAttributes(child);
                        if ((attrs & FileAttributes.Directory) != 0) { directories.Push(child); continue; }
                        Safety.CheckPath(child, true);
                        var info = new FileInfo(child);
                        byPath.TryGetValue(child, out var manifest);
                        var age = now - info.LastWriteTimeUtc;
                        var tier = age >= options.ColdAfter ? StorageTier.Cold : age >= options.WarmAfter ? StorageTier.Warm : StorageTier.Hot;
                        entries.Add(new(child, tier, info.Length, info.LastWriteTimeUtc, manifest?.Id, tier == StorageTier.Cold ? "COLD 보관 후보: 원본 파일이 로컬에 남아 있습니다." : null));
                        seen.Add(child);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    { entries.Add(new(child, StorageTier.Warm, 0, null, Warning: ex.Message)); seen.Add(child); }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { entries.Add(new(directory, StorageTier.Warm, 0, null, Warning: ex.Message)); }
        }
        foreach (var m in valid)
        {
            if (seen.Contains(m.SourcePath) || File.Exists(m.SourcePath)) continue;
            if (m.State is ArchiveState.Verified or ArchiveState.Cold or ArchiveState.Restored)
                entries.Add(new(m.SourcePath, StorageTier.Cold, m.Size, m.SourceLastWriteUtc, m.Id, "아카이브 기록 기준입니다. 원격 데이터의 현재 존재 여부는 복원/재검증 시 확인합니다."));
            else entries.Add(new(m.SourcePath, StorageTier.Warm, m.Size, m.SourceLastWriteUtc, m.Id, "부분 실패/대기 기록: RecoverAsync로 재검증하세요."));
        }
        return entries;
    }

    public async Task<OperationResult> ArchiveAsync(string sourcePath, ArchiveOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        Confirm(options.DryRun, options.Confirmed);
        var source = Source(sourcePath);
        Safety.CheckPath(source, true);
        if (!File.Exists(source)) throw new FileNotFoundException("보관할 원본 파일이 없습니다.", source);
        if (options.DryRun) return new(true, "Archive", source, null, null, LocalRemovalWarning);
        using var lease = Safety.LeaseDirectories(source);
        using var operation = store.AcquireLock();
        await using var input = Safety.Open(source, FileMode.Open, FileAccess.Read, FileShare.None, true);
        var before = new FileInfo(source);
        var size = input.Length;
        var writeTime = before.LastWriteTimeUtc;
        var digest = await Safety.DigestAsync(input, ct: cancellationToken).ConfigureAwait(false);
        if (digest.Size != size) throw new StorageException(StorageError.FileChanged, "해시 계산 중 원본 파일의 크기가 변경되었습니다.");
        input.Position = 0;
        var id = Guid.NewGuid();
        var m = new ArchiveManifest
        {
            Id = id, SourcePath = source, BackendId = backend.BackendId, BackendKey = id.ToString("N") + ".blob",
            Sha256 = digest.Sha256, Size = digest.Size, SourceLastWriteUtc = writeTime,
            CreatedUtc = DateTime.UtcNow, UpdatedUtc = DateTime.UtcNow, State = ArchiveState.Preparing
        };
        await store.SaveAsync(m, cancellationToken).ConfigureAwait(false);
        try
        {
            await backend.UploadAsync(m.BackendKey, input, digest, cancellationToken).ConfigureAwait(false);
            Safety.Match(digest, await backend.VerifyAsync(m.BackendKey, cancellationToken).ConfigureAwait(false));
            var after = new FileInfo(source);
            if (input.Length != size || after.Length != size || after.LastWriteTimeUtc != writeTime)
                throw new StorageException(StorageError.FileChanged, "전송 중 원본 파일이 변경되었습니다. 원본은 유지합니다.");
            input.Position = 0;
            var finalDigest = await Safety.DigestAsync(input, ct: cancellationToken).ConfigureAwait(false);
            if (!Safety.SameDigest(finalDigest, digest)) throw new StorageException(StorageError.FileChanged, "전송 중 원본 내용이 변경되었습니다.");
            m = m with { State = ArchiveState.Verified, UpdatedUtc = DateTime.UtcNow, SourceMatchesArchiveAtLastCheck = true };
            await store.SaveAsync(m, cancellationToken).ConfigureAwait(false);
            return new(false, "Archive", source, id, m, LocalRemovalWarning);
        }
        catch (Exception ex)
        {
            // 키/해시를 남겨 전송 응답 유실과 최종 메타데이터 저장 실패도 수동 복구할 수 있습니다.
            await SaveFailureBestEffortAsync(m, ex).ConfigureAwait(false);
            throw;
        }
    }

    public async Task<OperationResult> RemoveLocalAsync(Guid id, RemoveLocalOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        Confirm(options.DryRun, options.Confirmed);
        var m = await ManifestAsync(id, cancellationToken).ConfigureAwait(false);
        if (options.DryRun) return new(true, "RemoveLocal", m.SourcePath, id, m, LocalRemovalWarning);
        if (!backend.HasRemoteServerAcknowledgement && !options.AllowUnconfirmedRemoteUploadRemoval)
            throw new StorageException(StorageError.ConfirmationRequired, FolderColdStorageBackend.RemovalWarning);
        using var lease = Safety.LeaseDirectories(m.SourcePath);
        using var operation = store.AcquireLock();
        m = await ManifestAsync(id, cancellationToken).ConfigureAwait(false);
        if (m.State is not (ArchiveState.Verified or ArchiveState.Restored))
            throw new StorageException(StorageError.InvalidManifest, "검증 완료 상태에서만 원본을 제거할 수 있습니다. 대기/실패 상태는 먼저 복구하세요.");
        await using (var source = Safety.OpenForRemoval(m.SourcePath))
        {
            var digest = new TransferDigest(m.Size, m.Sha256);
            var actual = await Safety.DigestAsync(source, ct: cancellationToken).ConfigureAwait(false);
            if (!Safety.SameDigest(actual, digest)) throw new StorageException(StorageError.FileChanged, "보관 후 원본 파일 내용이 변경되어 제거를 차단했습니다.");
            Safety.Match(digest, await backend.VerifyAsync(m.BackendKey, cancellationToken).ConfigureAwait(false));
            m = m with { State = ArchiveState.RemovalPending, UpdatedUtc = DateTime.UtcNow, LastError = null };
            await store.SaveAsync(m, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Safety.DeleteHeldFile(source);
        }
        // 삭제 후에는 취소로 최종 기록을 생략하지 않습니다. 실패해도 RemovalPending이 복구 근거가 됩니다.
        m = m with { State = ArchiveState.Cold, UpdatedUtc = DateTime.UtcNow };
        await store.SaveAsync(m, CancellationToken.None).ConfigureAwait(false);
        return new(false, "RemoveLocal", m.SourcePath, id, m, LocalRemovalWarning);
    }

    public async Task<OperationResult> RestoreAsync(Guid id, RestoreOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        Confirm(options.DryRun, options.Confirmed);
        var m = await ManifestAsync(id, cancellationToken).ConfigureAwait(false);
        var destination = Source(options.DestinationPath ?? m.SourcePath);
        if (options.DryRun) return new(true, "Restore", destination, id, m);
        using var operation = store.AcquireLock();
        m = await ManifestAsync(id, cancellationToken).ConfigureAwait(false);
        if (m.State is ArchiveState.Preparing or ArchiveState.Failed or ArchiveState.RemovalPending)
            throw new StorageException(StorageError.InvalidManifest, "전송/제거 대기 또는 실패 기록은 먼저 RecoverAsync로 확인하세요.");
        using var parents = Safety.LeaseDirectories(destination);
        if (File.Exists(destination) || Directory.Exists(destination)) throw new StorageException(StorageError.Collision, "복원 대상이 이미 존재합니다. 덮어쓰지 않습니다.");
        var parent = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(parent);
        using var lease = Safety.LeaseDirectories(parent, true);
        var stage = Path.Combine(parent, ".ramflow-restore-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var output = Safety.Open(stage, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                // 예상 크기를 초과하는 원격 응답은 즉시 중단하여 디스크 고갈을 막습니다.
                using var bounded = new BoundedWriteStream(output, m.Size);
                await backend.DownloadAsync(m.BackendKey, bounded, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(true);
                output.Position = 0;
                Safety.Match(new(m.Size, m.Sha256), await Safety.DigestAsync(output, ct: cancellationToken).ConfigureAwait(false));
            }
            cancellationToken.ThrowIfCancellationRequested();
            Safety.CheckPath(destination);
            Safety.CheckPath(stage, true);
            File.Move(stage, destination, false); // 동일 폴더에서 검증된 파일만 원자적으로 공개합니다.
            m = m with { State = ArchiveState.Restored, RestoredPath = destination, UpdatedUtc = DateTime.UtcNow, LastError = null };
            await store.SaveAsync(m, CancellationToken.None).ConfigureAwait(false);
            return new(false, "Restore", destination, id, m);
        }
        finally { if (File.Exists(stage)) { Safety.CheckPath(stage); File.Delete(stage); } }
    }

    /// <summary>부분 실패를 재검증해 기록만 정리합니다. 원본 삭제나 재업로드는 수행하지 않습니다.</summary>
    public async Task<OperationResult> RecoverAsync(Guid id, ArchiveOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        Confirm(options.DryRun, options.Confirmed);
        var m = await ManifestAsync(id, cancellationToken).ConfigureAwait(false);
        if (options.DryRun) return new(true, "Recover", m.SourcePath, id, m, LocalRemovalWarning);
        using var operation = store.AcquireLock();
        m = await ManifestAsync(id, cancellationToken).ConfigureAwait(false);
        Safety.Match(new(m.Size, m.Sha256), await backend.VerifyAsync(m.BackendKey, cancellationToken).ConfigureAwait(false));
        using var lease = Safety.LeaseDirectories(m.SourcePath);
        // 아카이브 유효성과 현재 원본 일치 여부를 분리합니다. 교체된 원본도 삭제하지 않고 다른 경로에 백업을 복원할 수 있습니다.
        var sourceExists = File.Exists(m.SourcePath);
        bool? sourceMatches = null;
        if (sourceExists)
        {
            await using var input = Safety.Open(m.SourcePath, FileMode.Open, FileAccess.Read, FileShare.None, true);
            var actual = await Safety.DigestAsync(input, ct: cancellationToken).ConfigureAwait(false);
            sourceMatches = Safety.SameDigest(actual, new TransferDigest(m.Size, m.Sha256));
        }
        m = m with { State = sourceExists ? ArchiveState.Verified : ArchiveState.Cold, UpdatedUtc = DateTime.UtcNow, LastError = sourceMatches == false ? "SourceChanged" : null, SourceMatchesArchiveAtLastCheck = sourceMatches };
        await store.SaveAsync(m, cancellationToken).ConfigureAwait(false);
        var warning = sourceMatches == false ? "백업 해시는 유효하지만 현재 원본은 다른 내용입니다. 원본을 보존하며 다른 경로로 복원할 수 있습니다. " + LocalRemovalWarning : LocalRemovalWarning;
        return new(false, "Recover", m.SourcePath, id, m, warning);
    }

    /// <summary>원본 경로에 파일이 있으면 해시를 확인하고, 없으면 안전하게 복원합니다.</summary>
    public async Task<OperationResult> EnsureRestoredAsync(Guid id, RestoreOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new();
        var m = await ManifestAsync(id, cancellationToken).ConfigureAwait(false);
        var target = Source(options.DestinationPath ?? m.SourcePath);
        if (!File.Exists(target)) return await RestoreAsync(id, options, cancellationToken).ConfigureAwait(false);
        using var lease = Safety.LeaseDirectories(target);
        await using var input = Safety.Open(target, FileMode.Open, FileAccess.Read, FileShare.None, true);
        Safety.Match(new(m.Size, m.Sha256), await Safety.DigestAsync(input, ct: cancellationToken).ConfigureAwait(false));
        return new(options.DryRun, "AlreadyRestored", target, id, m);
    }

    public async Task<LaunchResult> LaunchModelAsync(Guid id, LaunchOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        Confirm(options.DryRun, options.Confirmed);
        var executable = Safety.FullPath(options.ExecutablePath);
        Safety.CheckPath(executable, true);
        if (!File.Exists(executable)) throw new FileNotFoundException("모델 실행 프로그램이 없습니다.", executable);
        if (OperatingSystem.IsWindows() && !Path.GetExtension(executable).Equals(".exe", StringComparison.OrdinalIgnoreCase))
            throw new StorageException(StorageError.InvalidPath, "셸 스크립트 대신 실행 파일(.exe)을 지정하세요.");
        var restored = await EnsureRestoredAsync(id, new RestoreOptions { DryRun = options.DryRun, Confirmed = options.Confirmed }, cancellationToken).ConfigureAwait(false);
        if (options.DryRun) return new(true, restored.SourcePath, null);
        var working = options.WorkingDirectory is null ? Path.GetDirectoryName(executable)! : Safety.FullPath(options.WorkingDirectory);
        Safety.CheckPath(working);
        if (!Directory.Exists(working)) throw new DirectoryNotFoundException("실행 작업 디렉터리가 없습니다.");
        using var modelLease = Safety.LeaseDirectories(restored.SourcePath);
        using var exeLease = Safety.LeaseDirectories(executable);
        using var model = Safety.Open(restored.SourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, true);
        var m = await ManifestAsync(id, cancellationToken).ConfigureAwait(false);
        Safety.Match(new(m.Size, m.Sha256), await Safety.DigestAsync(model, ct: cancellationToken).ConfigureAwait(false));
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo { FileName = executable, UseShellExecute = false, WorkingDirectory = working };
        foreach (var arg in options.Arguments) start.ArgumentList.Add(arg);
        if (options.AppendModelPath) start.ArgumentList.Add(restored.SourcePath);
        using var process = Process.Start(start) ?? throw new StorageException(StorageError.TransferFailed, "모델 프로세스를 시작할 수 없습니다.");
        return new(false, restored.SourcePath, process.Id);
    }

    private async Task SaveFailureBestEffortAsync(ArchiveManifest m, Exception error)
    {
        // 공급자의 예외 문자열에는 URL/인증 정보가 있을 수 있어 타입/오류 코드만 기록합니다.
        var safeError = error is StorageException se ? se.Code.ToString() : error is OperationCanceledException ? "Canceled" : error.GetType().Name;
        try { await store.SaveAsync(m with { State = ArchiveState.Failed, UpdatedUtc = DateTime.UtcNow, LastError = safeError }, CancellationToken.None).ConfigureAwait(false); }
        catch (IOException) { /* 기존 Preparing 기록을 유지해 수동 복구합니다. */ }
        catch (UnauthorizedAccessException) { }
    }

    private sealed class BoundedWriteStream(Stream inner, long limit) : Stream
    {
        private long written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => written;
        public override long Position { get => written; set => throw new NotSupportedException(); }
        private void Check(int count) { if (count > limit - written) throw new StorageException(StorageError.HashMismatch, "원격 응답이 기록된 파일 크기를 초과했습니다."); }
        public override void Write(byte[] buffer, int offset, int count) { Check(count); inner.Write(buffer, offset, count); written += count; }
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default) { Check(buffer.Length); await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false); written += buffer.Length; }
        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => inner.Flush();
        public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
