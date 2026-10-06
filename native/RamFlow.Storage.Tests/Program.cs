using System.Net;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using RamFlow.Storage;

// 임의의 사용자 파일/외부 서버를 사용하지 않습니다. 모든 실제 전송은 이 임시 디렉터리 안에서만 수행됩니다.
var temporary = Path.Combine(Path.GetTempPath(), "RamFlow.Storage.SelfTests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporary);
var passed = 0;
try
{
    await Test("기본 DryRun은 디렉터리와 파일을 만들지 않음", async () =>
    {
        var root = Path.Combine(temporary, "dry"); Directory.CreateDirectory(root);
        var file = Path.Combine(root, "model.bin"); await File.WriteAllTextAsync(file, "test");
        var backend = new FolderColdStorageBackend(Path.Combine(temporary, "dry-cold"), "dry");
        var service = new ColdStorageService(root, Path.Combine(temporary, "dry-meta"), backend);
        var result = await service.ArchiveAsync(file);
        Assert(result.DryRun && result.ManifestId is null && File.Exists(file), "DryRun 결과");
        Assert(!Directory.Exists(backend.LocalRoot) && !Directory.Exists(service.ManifestDirectory), "DryRun 디스크 변경");
        await Expect(StorageError.ConfirmationRequired, () => service.ArchiveAsync(file, new() { DryRun = false }));
    });

    var sourceRoot = Path.Combine(temporary, "sources"); Directory.CreateDirectory(sourceRoot);
    var cold = new FolderColdStorageBackend(Path.Combine(temporary, "cold"), "test-folder");
    var storage = new ColdStorageService(sourceRoot, Path.Combine(temporary, "metadata"), cold);
    var source = Path.Combine(sourceRoot, "model.bin");
    var bytes = new byte[3 * 1024 * 1024 + 17]; RandomNumberGenerator.Fill(bytes);
    await File.WriteAllBytesAsync(source, bytes);
    ArchiveManifest archive = null!;

    await Test("스트리밍 보관 및 SHA256/크기 메타데이터, 원본 유지", async () =>
    {
        archive = (await storage.ArchiveAsync(source, new() { DryRun = false, Confirmed = true })).Manifest!;
        Assert(archive.State == ArchiveState.Verified && archive.Size == bytes.Length, "아카이브 상태");
        Assert(archive.Sha256 == Convert.ToHexString(SHA256.HashData(bytes)), "해시");
        Assert(File.Exists(source) && archive.BackendKey == archive.Id.ToString("N") + ".blob", "원본 유지");
        Assert((await storage.GetManifestAsync(archive.Id)).Sha256 == archive.Sha256, "메타데이터 읽기");
        Assert((await storage.ListManifestsAsync()).Count == 1, "메타데이터 목록");
    });

    await Test("복원/삭제/복구 기본 DryRun", async () =>
    {
        var path = Path.Combine(sourceRoot, "dry-restore.bin");
        Assert((await storage.RestoreAsync(archive.Id, new() { DestinationPath = path })).DryRun, "복원 DryRun");
        Assert((await storage.RemoveLocalAsync(archive.Id)).DryRun, "삭제 DryRun");
        Assert((await storage.RecoverAsync(archive.Id)).DryRun, "복구 DryRun");
        Assert(!File.Exists(path) && File.Exists(source), "DryRun 변경 없음");
        await Expect(StorageError.ConfirmationRequired, () => storage.RemoveLocalAsync(archive.Id, new() { DryRun = false, Confirmed = true }));
    });

    await Test("폴더 키 충돌/경로 이동/루트 충돌 차단", async () =>
    {
        await using var input = new MemoryStream(bytes);
        await Expect(StorageError.Collision, () => cold.UploadAsync(archive.BackendKey, input, new(bytes.Length, archive.Sha256)));
        await Expect(StorageError.InvalidPath, () => cold.VerifyAsync("../bad.blob"));
        await Expect(StorageError.InvalidPath, () => storage.ArchiveAsync(Path.Combine(sourceRoot, "..", "outside.bin")));
        await Expect(StorageError.InvalidPath, () => storage.RestoreAsync(archive.Id, new() { DestinationPath = Path.Combine(temporary, "outside.bin") }));
        ExpectSync(StorageError.InvalidPath, () => new ColdStorageService(sourceRoot, storage.ManifestDirectory, new FolderColdStorageBackend(sourceRoot, "invalid")));
    });

    await Test("사용 중인 파일 차단", async () =>
    {
        using var held = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Expect(StorageError.LiveFile, () => storage.ArchiveAsync(source, new() { DryRun = false, Confirmed = true }));
    });

    await Test("복원은 같은 디렉터리의 검증된 임시 파일에서 공개하고 충돌 시 덮어쓰지 않음", async () =>
    {
        var path = Path.Combine(sourceRoot, "restored", "model.bin");
        await storage.RestoreAsync(archive.Id, new() { DryRun = false, Confirmed = true, DestinationPath = path });
        Assert((await File.ReadAllBytesAsync(path)).SequenceEqual(bytes), "복원 바이트");
        await Expect(StorageError.Collision, () => storage.RestoreAsync(archive.Id, new() { DryRun = false, Confirmed = true, DestinationPath = path }));
        Assert((await File.ReadAllBytesAsync(path)).SequenceEqual(bytes), "충돌 원본 유지");
        Assert((await storage.EnsureRestoredAsync(archive.Id)).Action == "AlreadyRestored", "기존 모델 확인");
    });

    await Test("손상된 백엔드 복원 차단 및 임시 파일 정리", async () =>
    {
        var blob = Path.Combine(cold.LocalRoot, archive.BackendKey);
        await File.WriteAllTextAsync(blob, "broken");
        var path = Path.Combine(sourceRoot, "bad-restore.bin");
        await Expect(StorageError.HashMismatch, () => storage.RestoreAsync(archive.Id, new() { DryRun = false, Confirmed = true, DestinationPath = path }));
        Assert(!File.Exists(path) && !Directory.EnumerateFiles(sourceRoot, ".ramflow-restore-*").Any(), "손상 파일 공개 없음");
        await File.WriteAllBytesAsync(blob, bytes);
    });

    await Test("HOT/WARM/COLD 후보와 손상 메타데이터를 안전하게 스캔", async () =>
    {
        var hot = Path.Combine(sourceRoot, "hot.bin"); await File.WriteAllTextAsync(hot, "hot");
        var warm = Path.Combine(sourceRoot, "warm.bin"); await File.WriteAllTextAsync(warm, "warm"); File.SetLastWriteTimeUtc(warm, DateTime.UtcNow.AddDays(-10));
        var candidate = Path.Combine(sourceRoot, "old.bin"); await File.WriteAllTextAsync(candidate, "old"); File.SetLastWriteTimeUtc(candidate, DateTime.UtcNow.AddDays(-40));
        await File.WriteAllTextAsync(Path.Combine(storage.ManifestDirectory, "broken.json"), "broken");
        var scan = await storage.ScanAsync();
        Assert(scan.Single(x => x.Path == hot).Tier == StorageTier.Hot, "HOT");
        Assert(scan.Single(x => x.Path == warm).Tier == StorageTier.Warm, "WARM");
        Assert(scan.Single(x => x.Path == candidate).Tier == StorageTier.Cold, "COLD 후보");
        Assert(scan.Any(x => x.Warning is not null && x.Path.EndsWith("broken.json")), "손상 기록 경고");
    });

    await Test("취소 토큰은 원본과 메타데이터를 변경하지 않음", async () =>
    {
        using var cancel = new CancellationTokenSource(); cancel.Cancel();
        try { await storage.ArchiveAsync(source, new() { DryRun = false, Confirmed = true }, cancel.Token); throw new Exception("취소가 차단되지 않음"); }
        catch (OperationCanceledException) { }
        Assert(File.Exists(source), "취소 후 원본 유지");
    });

    await Test("응답 유실/부분 실패는 GUID 기록으로 복구하고 자동 삭제하지 않음", async () =>
    {
        var failing = new FaultBackend(new FolderColdStorageBackend(Path.Combine(temporary, "fault-cold"), "fault"));
        var service = new ColdStorageService(sourceRoot, Path.Combine(temporary, "fault-meta"), failing);
        try { await service.ArchiveAsync(source, new() { DryRun = false, Confirmed = true }); throw new Exception("의도한 실패 없음"); }
        catch (IOException ex) when (ex.Message == "injected lost response") { }
        var failed = (await service.ListManifestsAsync()).Single().Manifest!;
        Assert(failed.State == ArchiveState.Failed && File.Exists(source), "실패 기록과 원본 유지");
        var recovered = await service.RecoverAsync(failed.Id, new() { DryRun = false, Confirmed = true });
        Assert(recovered.Manifest!.State == ArchiveState.Verified && File.Exists(source), "재검증 복구");
    });

    await Test("초과 길이의 다운로드를 즉시 차단", async () =>
    {
        var fault = new FaultBackend(cold) { FailUpload = false, OversizeDownload = true };
        var service = new ColdStorageService(sourceRoot, storage.ManifestDirectory, fault);
        var target = Path.Combine(sourceRoot, "oversize.bin");
        await Expect(StorageError.HashMismatch, () => service.RestoreAsync(archive.Id, new() { DryRun = false, Confirmed = true, DestinationPath = target }));
        Assert(!File.Exists(target), "초과 응답 공개 금지");
    });

    await Test("메타데이터 식별자/백엔드/경로 위조를 차단", async () =>
    {
        var manifestPath = Path.Combine(storage.ManifestDirectory, archive.Id.ToString("N") + ".json");
        var original = await File.ReadAllTextAsync(manifestPath);
        try
        {
            var json = JsonNode.Parse(original)!; json["BackendKey"] = "../outside.blob";
            await File.WriteAllTextAsync(manifestPath, json.ToJsonString());
            await Expect(StorageError.InvalidManifest, () => storage.GetManifestAsync(archive.Id));
            json = JsonNode.Parse(original)!; json["BackendId"] = "other-backend";
            await File.WriteAllTextAsync(manifestPath, json.ToJsonString());
            await Expect(StorageError.BackendMismatch, () => storage.GetManifestAsync(archive.Id));
            json = JsonNode.Parse(original)!; json["SourcePath"] = Path.Combine(temporary, "outside.bin");
            await File.WriteAllTextAsync(manifestPath, json.ToJsonString());
            await Expect(StorageError.InvalidPath, () => storage.RestoreAsync(archive.Id));
        }
        finally { await File.WriteAllTextAsync(manifestPath, original); }
    });

    await Test("진행 중인 작업의 메타데이터 잠금을 다른 작업이 침범하지 않음", async () =>
    {
        using var held = new FileStream(Path.Combine(storage.ManifestDirectory, ".operation.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        await Expect(StorageError.Busy, () => storage.ArchiveAsync(source, new() { DryRun = false, Confirmed = true }));
    });

    await Test("메타데이터 스냅샷을 읽는 동안 원자적 교체 허용", async () =>
    {
        var manifestPath = Path.Combine(storage.ManifestDirectory, archive.Id.ToString("N") + ".json");
        using var reader = new FileStream(manifestPath, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        var recovered = await storage.RecoverAsync(archive.Id, new() { DryRun = false, Confirmed = true });
        Assert(recovered.Manifest!.State == ArchiveState.Verified, "읽기와 최종 저장 병행");
        using var text = new StreamReader(reader);
        var snapshot = JsonNode.Parse(await text.ReadToEndAsync())!;
        Assert(snapshot["Id"]!.GetValue<Guid>() == archive.Id, "기존 읽기 스냅샷 일관성");
        Assert((await storage.GetManifestAsync(archive.Id)).State == ArchiveState.Verified, "새 스냅샷 읽기");
    });

    await Test("제거 대기 상태는 복구 전 재삭제 금지, 교체된 원본은 복구 시 차단", async () =>
    {
        var manifestPath = Path.Combine(storage.ManifestDirectory, archive.Id.ToString("N") + ".json");
        var original = await File.ReadAllTextAsync(manifestPath);
        try
        {
            var json = JsonNode.Parse(original)!; json["State"] = "RemovalPending";
            await File.WriteAllTextAsync(manifestPath, json.ToJsonString());
            await Expect(StorageError.InvalidManifest, () => storage.RemoveLocalAsync(archive.Id, Removal()));
            await File.WriteAllTextAsync(source, "replacement");
            var recovered = await storage.RecoverAsync(archive.Id, new() { DryRun = false, Confirmed = true });
            Assert(recovered.Manifest!.State == ArchiveState.Verified && recovered.Manifest.SourceMatchesArchiveAtLastCheck == false, "백업 유효성과 원본 변경 분리");
            await Expect(StorageError.FileChanged, () => storage.RemoveLocalAsync(archive.Id, Removal()));
            var backupTarget = Path.Combine(sourceRoot, "replacement-backup.bin");
            await storage.RestoreAsync(archive.Id, new() { DryRun = false, Confirmed = true, DestinationPath = backupTarget });
            Assert((await File.ReadAllBytesAsync(backupTarget)).SequenceEqual(bytes), "변경된 원본의 이전 백업 복원");
            Assert((await File.ReadAllTextAsync(source)) == "replacement", "교체 원본 유지");
        }
        finally { await File.WriteAllBytesAsync(source, bytes); await File.WriteAllTextAsync(manifestPath, original); }
    });

    await Test("외부 통신 없이 HTTPS WebDAV 설정 검증", () =>
    {
        ExpectSync(StorageError.InvalidPath, () => new WebDavColdStorageBackend(new Uri("http://example.invalid/dav/"), "dav", new NetworkCredential("a", "b")));
        ExpectSync(StorageError.InvalidPath, () => new WebDavColdStorageBackend(new Uri("https://a:b@example.invalid/dav/"), "dav", new NetworkCredential("a", "b")));
        using var dav = new WebDavColdStorageBackend(new Uri("https://example.invalid/dav/"), "dav", new NetworkCredential("a", "b"));
        Assert(dav.HasRemoteServerAcknowledgement && dav.LocalRoot is null, "WebDAV 공급자");
        return Task.CompletedTask;
    });

    if (OperatingSystem.IsWindows())
    {
        await Test("원본 변경 및 백엔드 변경 시 수동 삭제도 차단", async () =>
        {
            await File.WriteAllTextAsync(source, "modified");
            await Expect(StorageError.FileChanged, () => storage.RemoveLocalAsync(archive.Id, Removal()));
            Assert(File.Exists(source), "변경 원본 보존");
            await File.WriteAllBytesAsync(source, bytes);
            var blob = Path.Combine(cold.LocalRoot, archive.BackendKey); await File.WriteAllTextAsync(blob, "corrupt");
            await Expect(StorageError.HashMismatch, () => storage.RemoveLocalAsync(archive.Id, Removal()));
            Assert(File.Exists(source), "콜드 손상 시 원본 보존");
            await File.WriteAllBytesAsync(blob, bytes);
        });

        await Test("읽기 전용/하드 링크/ADS 원본 제거 차단", async () =>
        {
            File.SetAttributes(source, FileAttributes.ReadOnly);
            try { await ExpectAnyStorage(() => storage.RemoveLocalAsync(archive.Id, Removal())); }
            finally { File.SetAttributes(source, FileAttributes.Normal); }
            var link = Path.Combine(sourceRoot, "hardlink.bin");
            Assert(Native.CreateHardLink(link, source, IntPtr.Zero), "테스트 하드 링크 생성");
            try { await Expect(StorageError.Unsupported, () => storage.RemoveLocalAsync(archive.Id, Removal())); }
            finally { File.Delete(link); }
            await File.WriteAllTextAsync(source + ":secret", "unarchived stream");
            try { await Expect(StorageError.Unsupported, () => storage.RemoveLocalAsync(archive.Id, Removal())); }
            finally { File.Delete(source + ":secret"); }
            Assert(File.Exists(source), "속성/추가 내용 파일 보존");
        });

        await Test("백엔드 검증 중 늦게 만들어진 ADS도 제거 차단", async () =>
        {
            var created = false;
            var service = new ColdStorageService(sourceRoot, storage.ManifestDirectory, new FaultBackend(cold)
            {
                FailUpload = false,
                BeforeVerify = () =>
                {
                    using var late = new FileStream(source + ":late", FileMode.CreateNew, FileAccess.Write, FileShare.Delete);
                    late.Write(Encoding.UTF8.GetBytes("late stream")); created = true;
                }
            });
            try
            {
                await Expect(StorageError.Unsupported, () => service.RemoveLocalAsync(archive.Id, Removal()));
                Assert(created && File.Exists(source), "늦은 ADS와 원본 보존");
            }
            finally
            {
                if (created) File.Delete(source + ":late");
                await storage.RecoverAsync(archive.Id, new() { DryRun = false, Confirmed = true });
            }
        });

        await Test("명시적 수동 제거와 COLD 기록, 복원 후 해시 일치", async () =>
        {
            var removed = await storage.RemoveLocalAsync(archive.Id, Removal());
            Assert(!File.Exists(source) && removed.Manifest!.State == ArchiveState.Cold && removed.Warning is not null, "원본 제거");
            Assert((await storage.ScanAsync()).Any(x => x.Path == source && x.Tier == StorageTier.Cold && x.ManifestId == archive.Id), "실제 COLD 기록");
            await storage.EnsureRestoredAsync(archive.Id, new() { DryRun = false, Confirmed = true });
            Assert((await File.ReadAllBytesAsync(source)).SequenceEqual(bytes), "재복원");
        });

        await Test("전송 중 원본의 부모 디렉터리 교체 차단", async () =>
        {
            var parent = Path.Combine(temporary, "race-source"); Directory.CreateDirectory(parent);
            var file = Path.Combine(parent, "model.bin"); await File.WriteAllTextAsync(file, "race");
            var blocked = false;
            var fault = new FaultBackend(new FolderColdStorageBackend(Path.Combine(temporary, "race-cold"), "race"))
            {
                FailUpload = false,
                BeforeUpload = () => { try { Directory.Move(parent, parent + "-moved"); } catch (IOException) { blocked = true; } }
            };
            var service = new ColdStorageService(parent, Path.Combine(temporary, "race-meta"), fault);
            await service.ArchiveAsync(file, new() { DryRun = false, Confirmed = true });
            Assert(blocked && File.Exists(file) && !Directory.Exists(parent + "-moved"), "부모 교체 거부");
        });

        await Test("정션을 통한 스캔/보관/복원/백엔드 경로 이탈 차단", async () =>
        {
            var target = Path.Combine(temporary, "junction-target"); Directory.CreateDirectory(target);
            await File.WriteAllTextAsync(Path.Combine(target, "outside.bin"), "never followed");
            var junction = Path.Combine(sourceRoot, "junction");
            Native.CreateJunction(junction, target);
            try
            {
                await Expect(StorageError.LinkNotAllowed, () => storage.ArchiveAsync(Path.Combine(junction, "outside.bin")));
                await Expect(StorageError.LinkNotAllowed, () => storage.RestoreAsync(archive.Id, new() { DestinationPath = Path.Combine(junction, "restored.bin") }));
                ExpectSync(StorageError.LinkNotAllowed, () => new FolderColdStorageBackend(junction, "junction"));
                var scan = await storage.ScanAsync();
                Assert(scan.Any(x => x.Path == junction && x.Warning is not null), "정션 경고");
                Assert(!scan.Any(x => x.Path == Path.Combine(junction, "outside.bin")), "정션 내부 미탐색");
            }
            finally { Directory.Delete(junction); }
        });

        await Test("스파스 백엔드 파일은 검증과 삭제에서 거부", async () =>
        {
            var blob = Path.Combine(cold.LocalRoot, archive.BackendKey);
            using (var handle = File.OpenHandle(blob, FileMode.Open, FileAccess.Write, FileShare.None))
                Assert(Native.DeviceIoControl(handle, 0x000900C4, IntPtr.Zero, 0, IntPtr.Zero, 0, out _, IntPtr.Zero), "테스트 스파스 속성 설정");
            await Expect(StorageError.PlaceholderNotAllowed, () => cold.VerifyAsync(archive.BackendKey));
            await Expect(StorageError.PlaceholderNotAllowed, () => storage.RemoveLocalAsync(archive.Id, Removal()));
            Assert(File.Exists(source), "스파스 대상이면 원본 보존");
        });
    }

    await Test("기본 LaunchModel DryRun과 ArgumentList용 인수", async () =>
    {
        var executable = Environment.ProcessPath!;
        var result = await storage.LaunchModelAsync(archive.Id, new() { ExecutablePath = executable, Arguments = ["--never-executed", "space ; & argument"] });
        Assert(result.DryRun && result.ProcessId is null && result.ModelPath == source, "실행 DryRun");
    });

    await Test("드라이브 루트 선택과 플랫폼에 맞는 경로 대소문자 경계", async () =>
    {
        var driveRoot = Path.GetPathRoot(sourceRoot)!;
        var service = new ColdStorageService(driveRoot, storage.ManifestDirectory, cold);
        Assert((await service.ArchiveAsync(source)).DryRun, "드라이브 루트 하위 경로 허용");
        var differentCase = Path.Combine(Path.GetDirectoryName(sourceRoot)!, Path.GetFileName(sourceRoot).ToUpperInvariant(), "model.bin");
        if (OperatingSystem.IsWindows()) Assert((await storage.ArchiveAsync(differentCase)).DryRun, "일반 Windows 경로의 대소문자 별칭 허용");
        else await Expect(StorageError.InvalidPath, () => storage.ArchiveAsync(differentCase));
    });

    if (OperatingSystem.IsWindows())
    {
        await Test("일반 Windows 디렉터리의 대소문자 별칭은 루트 충돌/내부 파일/스캔에서 일관되게 제외", async () =>
        {
            var root = Path.Combine(temporary, "CaseAliasRoot"); Directory.CreateDirectory(root);
            var aliasRoot = Path.Combine(temporary, "casealiasroot");
            Assert(Directory.Exists(aliasRoot), "같은 일반 디렉터리의 다른 표기");
            var meta = Path.Combine(temporary, "case-alias-meta");
            ExpectSync(StorageError.InvalidPath, () => new ColdStorageService(root, meta, new FolderColdStorageBackend(aliasRoot, "same-root")));
            ExpectSync(StorageError.InvalidPath, () => new ColdStorageService(aliasRoot, meta, new FolderColdStorageBackend(root, "reverse-root")));
            ExpectSync(StorageError.InvalidPath, () => new ColdStorageService(root, aliasRoot, cold));
            var nestedCold = Path.Combine(root, "ColdData"); Directory.CreateDirectory(nestedCold);
            var nestedMeta = Path.Combine(root, "MetaData"); Directory.CreateDirectory(nestedMeta);
            var internalFile = Path.Combine(nestedCold, "cold.bin"); await File.WriteAllTextAsync(internalFile, "not a removal candidate");
            var internalMetaFile = Path.Combine(nestedMeta, "local.bin"); await File.WriteAllTextAsync(internalMetaFile, "not a source");
            var service = new ColdStorageService(root, Path.Combine(aliasRoot, "metadata"), new FolderColdStorageBackend(Path.Combine(aliasRoot, "colddata"), "nested"));
            await Expect(StorageError.InvalidPath, () => service.ArchiveAsync(internalFile));
            await Expect(StorageError.InvalidPath, () => service.ArchiveAsync(internalMetaFile));
            Assert(!(await service.ScanAsync()).Any(x => x.Path == internalFile || x.Path == internalMetaFile), "내부 파일을 제거 후보로 표시하지 않음");
            ExpectSync(StorageError.InvalidPath, () => new ColdStorageService(sourceRoot, nestedMeta, new FolderColdStorageBackend(Path.Combine(aliasRoot, "metadata"), "same-meta")));
            ExpectSync(StorageError.InvalidPath, () => new ColdStorageService(sourceRoot, Path.Combine(aliasRoot, "metadata", "nested"), new FolderColdStorageBackend(nestedMeta, "parent-meta")));
            Assert(await File.ReadAllTextAsync(internalFile) == "not a removal candidate", "기존 파일 변경 없음");
            var sensitive = Path.Combine(root, "Sensitive"); Directory.CreateDirectory(sensitive);
            Native.SetDirectoryCaseSensitivity(sensitive, true);
            try
            {
                ExpectSync(StorageError.Unsupported, () => new FolderColdStorageBackend(sensitive, "sensitive"));
                ExpectSync(StorageError.Unsupported, () => new ColdStorageService(sensitive, meta, cold));
                await Expect(StorageError.Unsupported, () => service.ArchiveAsync(Path.Combine(sensitive, "model.bin")));
                Assert((await service.ScanAsync()).Any(x => x.Path == sensitive && x.Warning is not null), "대소문자 구분 디렉터리는 따라가지 않고 경고");
            }
            finally { Native.SetDirectoryCaseSensitivity(sensitive, false); }
        });
    }

    Console.WriteLine($"PASS: {passed} tests; no external uploads or user files touched.");
}
finally
{
    // 생성한 경로만 제거합니다. 루트 및 접두사를 확인한 뒤 한 셸/API 안에서 정리합니다.
    if (Path.GetDirectoryName(temporary) == Path.TrimEndingDirectorySeparator(Path.GetTempPath()) && Path.GetFileName(temporary).StartsWith("RamFlow.Storage.SelfTests-", StringComparison.Ordinal))
        Directory.Delete(temporary, true);
}

async Task Test(string name, Func<Task> run) { await run(); passed++; Console.WriteLine($"PASS {passed}: {name}"); }
static void Assert(bool condition, string message) { if (!condition) throw new Exception("ASSERT: " + message); }
static async Task Expect(StorageError code, Func<Task> run)
{
    try { await run(); } catch (StorageException ex) when (ex.Code == code) { return; }
    throw new Exception("예상 오류 없음: " + code);
}
static async Task ExpectAnyStorage(Func<Task> run) { try { await run(); } catch (StorageException) { return; } throw new Exception("제거 차단 없음"); }
static void ExpectSync(StorageError code, Action run) { try { run(); } catch (StorageException ex) when (ex.Code == code) { return; } throw new Exception("예상 오류 없음: " + code); }
static RemoveLocalOptions Removal() => new() { DryRun = false, Confirmed = true, AllowUnconfirmedRemoteUploadRemoval = true };

sealed class FaultBackend(IColdStorageBackend inner) : IColdStorageBackend
{
    public bool FailUpload { get; set; } = true;
    public bool OversizeDownload { get; set; }
    public Action? BeforeUpload { get; init; }
    public Action? BeforeVerify { get; init; }
    public string BackendId => inner.BackendId;
    public string? LocalRoot => inner.LocalRoot;
    public bool HasRemoteServerAcknowledgement => inner.HasRemoteServerAcknowledgement;
    public async Task UploadAsync(string key, Stream source, TransferDigest expected, CancellationToken cancellationToken = default) { BeforeUpload?.Invoke(); await inner.UploadAsync(key, source, expected, cancellationToken); if (FailUpload) throw new IOException("injected lost response"); }
    public Task<TransferDigest> VerifyAsync(string key, CancellationToken cancellationToken = default) { BeforeVerify?.Invoke(); return inner.VerifyAsync(key, cancellationToken); }
    public async Task DownloadAsync(string key, Stream destination, CancellationToken cancellationToken = default)
    {
        await inner.DownloadAsync(key, destination, cancellationToken);
        if (OversizeDownload) await destination.WriteAsync(new byte[] { 1 }, cancellationToken);
    }
}

static class Native
{
    internal static void SetDirectoryCaseSensitivity(string path, bool enabled)
    {
        using var handle = CreateFile(path, 0x100, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("테스트 디렉터리 속성 핸들 열기 실패: " + Marshal.GetLastWin32Error());
        var flags = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            Marshal.WriteInt32(flags, enabled ? 1 : 0);
            if (!SetFileInformationByHandle(handle, 23, flags, sizeof(uint))) throw new IOException("테스트 대소문자 구분 속성 설정 실패: " + Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(flags); }
    }
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle handle, int infoClass, IntPtr information, int size);
    internal static void CreateJunction(string junction, string target)
    {
        Directory.CreateDirectory(junction);
        using var handle = CreateFile(junction, 0x40000000, 0, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("테스트 정션 디렉터리 열기 실패: " + Marshal.GetLastWin32Error());
        var substitute = Encoding.Unicode.GetBytes("\\??\\" + target);
        var printable = Encoding.Unicode.GetBytes(target);
        var buffer = new byte[16 + substitute.Length + 2 + printable.Length + 2];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer, 0xA0000003);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), checked((ushort)(buffer.Length - 8)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(10), checked((ushort)substitute.Length));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(12), checked((ushort)(substitute.Length + 2)));
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(14), checked((ushort)printable.Length));
        substitute.CopyTo(buffer, 16); printable.CopyTo(buffer, 18 + substitute.Length);
        var memory = Marshal.AllocHGlobal(buffer.Length);
        try
        {
            Marshal.Copy(buffer, 0, memory, buffer.Length);
            if (!DeviceIoControl(handle, 0x000900A4, memory, (uint)buffer.Length, IntPtr.Zero, 0, out _, IntPtr.Zero)) throw new IOException("테스트 정션 설정 실패: " + Marshal.GetLastWin32Error());
        }
        finally { Marshal.FreeHGlobal(memory); }
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")] private static extern Microsoft.Win32.SafeHandles.SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateHardLinkW")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool CreateHardLink(string newName, string existingName, IntPtr security);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool DeviceIoControl(Microsoft.Win32.SafeHandles.SafeFileHandle handle, uint code, IntPtr input, uint inputSize, IntPtr output, uint outputSize, out uint returned, IntPtr overlapped);
}
