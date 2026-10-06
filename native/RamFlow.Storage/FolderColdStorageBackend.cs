namespace RamFlow.Storage;

/// <summary>
/// 이미 연결된 Google Drive/OneDrive 폴더 또는 네트워크 디스크를 사용합니다. OAuth를 사용하지 않습니다.
/// 로컬 파일의 전체 읽기와 해시만 확인하며 동기화 서버의 수신 완료를 보장하지 않습니다.
/// </summary>
public sealed class FolderColdStorageBackend : IColdStorageBackend
{
    public string BackendId { get; }
    public string LocalRoot { get; }
    public bool HasRemoteServerAcknowledgement => false;
    public const string RemovalWarning = "마운트 폴더의 SHA256 검증은 서버 업로드 완료를 보장하지 않습니다. 클라우드 자리표시자를 임의로 퇴거하지 않습니다. 원격 업로드와 별도 백업을 직접 확인한 뒤 원본 삭제를 수동 승인하세요.";

    public FolderColdStorageBackend(string folderPath, string backendId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(backendId);
        LocalRoot = Safety.FullPath(folderPath);
        Safety.CheckPath(LocalRoot);
        BackendId = backendId;
    }

    private string ObjectPath(string key) => Safety.Under(Path.Combine(LocalRoot, Safety.Key(key)), LocalRoot);

    public async Task UploadAsync(string key, Stream source, TransferDigest expected, CancellationToken cancellationToken = default)
    {
        var target = ObjectPath(key);
        using var ancestors = Safety.LeaseDirectories(LocalRoot, true);
        Safety.CheckPath(LocalRoot);
        Directory.CreateDirectory(LocalRoot);
        using var rootLease = Safety.LeaseDirectories(LocalRoot, true);
        Safety.CheckPath(LocalRoot);
        if (File.Exists(target) || Directory.Exists(target)) throw new StorageException(StorageError.Collision, "백엔드 키가 이미 존재합니다.");
        var stage = target + ".upload-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var output = Safety.Open(stage, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
            {
                var digest = await Safety.DigestAsync(source, output, cancellationToken).ConfigureAwait(false);
                Safety.Match(expected, digest);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(true);
                output.Position = 0;
                Safety.Match(expected, await Safety.DigestAsync(output, ct: cancellationToken).ConfigureAwait(false));
            }
            cancellationToken.ThrowIfCancellationRequested();
            Safety.CheckPath(stage, true);
            Safety.CheckPath(target);
            File.Move(stage, target, false);
        }
        finally
        {
            // 충돌한 대상이나 기존 아카이브는 절대 지우지 않습니다. 이번 전송의 임시 파일만 정리합니다.
            if (File.Exists(stage)) { Safety.CheckPath(stage); File.Delete(stage); }
        }
    }

    public async Task<TransferDigest> VerifyAsync(string key, CancellationToken cancellationToken = default)
    {
        var target = ObjectPath(key);
        using var lease = Safety.LeaseDirectories(LocalRoot, true);
        await using var input = Safety.Open(target, FileMode.Open, FileAccess.Read, FileShare.None, true);
        var digest = await Safety.DigestAsync(input, ct: cancellationToken).ConfigureAwait(false);
        Safety.CheckPath(target, true);
        return digest;
    }

    public async Task DownloadAsync(string key, Stream destination, CancellationToken cancellationToken = default)
    {
        var target = ObjectPath(key);
        using var lease = Safety.LeaseDirectories(LocalRoot, true);
        await using var input = Safety.Open(target, FileMode.Open, FileAccess.Read, FileShare.None, true);
        await input.CopyToAsync(destination, 128 * 1024, cancellationToken).ConfigureAwait(false);
        Safety.CheckPath(target, true);
    }
}
