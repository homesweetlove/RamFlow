using System.Text.Json;
using System.Text.Json.Serialization;

namespace RamFlow.Storage;

internal sealed class ManifestStore
{
    internal string Root { get; }
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    internal ManifestStore(string root) { Root = Safety.FullPath(root); Safety.CheckPath(Root); }
    private string ManifestPath(Guid id) => Safety.Under(Path.Combine(Root, id.ToString("N") + ".json"), Root);

    internal IDisposable AcquireLock()
    {
        var ancestors = Safety.LeaseDirectories(Root, true);
        IDisposable? rootLease = null;
        try
        {
            Directory.CreateDirectory(Root);
            rootLease = Safety.LeaseDirectories(Root, true);
            var file = Safety.Open(Path.Combine(Root, ".operation.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new StoreLock(file, rootLease, ancestors);
        }
        catch (IOException ex) { rootLease?.Dispose(); ancestors.Dispose(); throw new StorageException(StorageError.Busy, "다른 아카이브 작업이 진행 중이거나 메타데이터를 잠글 수 없습니다.", ex); }
        catch { rootLease?.Dispose(); ancestors.Dispose(); throw; }
    }

    private sealed class StoreLock(params IDisposable[] resources) : IDisposable
    {
        public void Dispose() { foreach (var resource in resources) resource.Dispose(); }
    }

    internal async Task SaveAsync(ArchiveManifest manifest, CancellationToken ct)
    {
        Validate(manifest);
        var target = ManifestPath(manifest.Id);
        var temporary = target + ".tmp-" + Guid.NewGuid().ToString("N");
        try
        {
            await using (var file = Safety.Open(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await JsonSerializer.SerializeAsync(file, manifest, Json, ct).ConfigureAwait(false);
                await file.FlushAsync(ct).ConfigureAwait(false);
                file.Flush(true);
            }
            ct.ThrowIfCancellationRequested();
            Safety.CheckPath(target);
            Safety.CheckPath(temporary);
            // Windows MoveFileEx 덮어쓰기는 열린 대상에 실패할 수 있습니다. ReplaceFile은 삭제 공유를 허용한 읽기 스냅샷과 함께 동작합니다.
            if (File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target, false);
        }
        finally { if (File.Exists(temporary)) { Safety.CheckPath(temporary); File.Delete(temporary); } }
    }

    internal async Task<ArchiveManifest> ReadAsync(Guid id, CancellationToken ct)
    {
        var path = ManifestPath(id);
        using var lease = Safety.LeaseDirectories(Root, true);
        await using var file = Safety.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length > 1024 * 1024) throw new StorageException(StorageError.InvalidManifest, "메타데이터 크기가 허용 범위를 초과했습니다.");
        try
        {
            var manifest = await JsonSerializer.DeserializeAsync<ArchiveManifest>(file, Json, ct).ConfigureAwait(false)
                ?? throw new StorageException(StorageError.InvalidManifest, "메타데이터가 비어 있습니다.");
            Validate(manifest);
            if (manifest.Id != id) throw new StorageException(StorageError.InvalidManifest, "메타데이터 식별자가 파일명과 다릅니다.");
            return manifest;
        }
        catch (JsonException ex) { throw new StorageException(StorageError.InvalidManifest, "메타데이터 JSON을 읽을 수 없습니다.", ex); }
    }

    internal async Task<IReadOnlyList<ManifestReadResult>> ListAsync(CancellationToken ct)
    {
        Safety.CheckPath(Root);
        if (!Directory.Exists(Root)) return [];
        using var lease = Safety.LeaseDirectories(Root, true);
        var list = new List<ManifestReadResult>();
        foreach (var path in Directory.EnumerateFiles(Root, "*.json", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!Guid.TryParseExact(Path.GetFileNameWithoutExtension(path), "N", out var id)) throw new StorageException(StorageError.InvalidManifest, "알 수 없는 메타데이터 파일명입니다.");
                list.Add(new(path, await ReadAsync(id, ct).ConfigureAwait(false), null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            { list.Add(new(path, null, ex.Message)); }
        }
        return list;
    }

    private static void Validate(ArchiveManifest m)
    {
        if (m.Version != 1 || m.Id == Guid.Empty || m.Size < 0 || string.IsNullOrWhiteSpace(m.BackendId) || m.BackendKey != m.Id.ToString("N") + ".blob" || string.IsNullOrEmpty(m.Sha256) || m.Sha256.Length != 64 || !m.Sha256.All(Uri.IsHexDigit) || !Enum.IsDefined(m.State))
            throw new StorageException(StorageError.InvalidManifest, "아카이브 메타데이터 형식이 잘못되었습니다.");
        Safety.FullPath(m.SourcePath);
        if (m.RestoredPath is not null) Safety.FullPath(m.RestoredPath);
    }
}
