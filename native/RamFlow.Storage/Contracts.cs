namespace RamFlow.Storage;

public enum StorageTier { Hot, Warm, Cold }
public enum ArchiveState { Preparing, Verified, RemovalPending, Cold, Restored, Failed }
public enum StorageError { InvalidPath, LinkNotAllowed, PlaceholderNotAllowed, LiveFile, FileChanged, Collision, HashMismatch, ConfirmationRequired, BackendMismatch, InvalidManifest, Busy, Unsupported, TransferFailed }

public sealed class StorageException(StorageError code, string message, Exception? inner = null) : IOException(message, inner)
{
    public StorageError Code { get; } = code;
}

public sealed record TransferDigest(long Size, string Sha256);

/// <summary>서버 저장 완료 보장은 폴더 공급자에서 항상 false입니다.</summary>
public interface IColdStorageBackend
{
    string BackendId { get; }
    string? LocalRoot { get; }
    bool HasRemoteServerAcknowledgement { get; }
    Task UploadAsync(string key, Stream source, TransferDigest expected, CancellationToken cancellationToken = default);
    Task<TransferDigest> VerifyAsync(string key, CancellationToken cancellationToken = default);
    Task DownloadAsync(string key, Stream destination, CancellationToken cancellationToken = default);
}

public sealed record ArchiveManifest
{
    public int Version { get; init; } = 1;
    public Guid Id { get; init; }
    public required string SourcePath { get; init; }
    public required string BackendId { get; init; }
    public required string BackendKey { get; init; }
    public required string Sha256 { get; init; }
    public long Size { get; init; }
    public DateTime SourceLastWriteUtc { get; init; }
    public DateTime CreatedUtc { get; init; }
    public DateTime UpdatedUtc { get; init; }
    public ArchiveState State { get; init; }
    public string? RestoredPath { get; init; }
    public string? LastError { get; init; }
    /// <summary>최근 검사 시점의 원본 일치 여부입니다. 삭제 시에는 이 값과 무관하게 다시 해시/스트림을 검증합니다.</summary>
    public bool? SourceMatchesArchiveAtLastCheck { get; init; }
}

public sealed record ArchiveOptions
{
    public bool DryRun { get; init; } = true;
    public bool Confirmed { get; init; }
}

public sealed record RemoveLocalOptions
{
    public bool DryRun { get; init; } = true;
    public bool Confirmed { get; init; }
    /// <summary>폴더 동기화의 원격 업로드 완료는 확인할 수 없습니다. UI가 경고한 뒤 사용자 수동 확인 시에만 true를 설정하세요.</summary>
    public bool AllowUnconfirmedRemoteUploadRemoval { get; init; }
}

public sealed record RestoreOptions
{
    public bool DryRun { get; init; } = true;
    public bool Confirmed { get; init; }
    /// <summary>생략하면 최초 원본 경로에 복원합니다. 항상 선택한 원본 루트 안이어야 합니다.</summary>
    public string? DestinationPath { get; init; }
}

public sealed record OperationResult(bool DryRun, string Action, string SourcePath, Guid? ManifestId, ArchiveManifest? Manifest, string? Warning = null);
public sealed record ScanOptions
{
    public TimeSpan WarmAfter { get; init; } = TimeSpan.FromDays(7);
    public TimeSpan ColdAfter { get; init; } = TimeSpan.FromDays(30);
    public DateTime? NowUtc { get; init; }
}
public sealed record StorageScanEntry(string Path, StorageTier Tier, long Size, DateTime? LastWriteUtc, Guid? ManifestId = null, string? Warning = null);
public sealed record ManifestReadResult(string Path, ArchiveManifest? Manifest, string? Error);
public sealed record LaunchOptions
{
    public bool DryRun { get; init; } = true;
    public bool Confirmed { get; init; }
    public required string ExecutablePath { get; init; }
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string? WorkingDirectory { get; init; }
    public bool AppendModelPath { get; init; } = true;
}
public sealed record LaunchResult(bool DryRun, string ModelPath, int? ProcessId);
