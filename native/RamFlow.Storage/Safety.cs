using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace RamFlow.Storage;

internal static class Safety
{
    // Windows의 대소문자 별칭은 동일 경로로 취급합니다. 대소문자 구분 디렉터리는 아래 검사에서 보수적으로 차단합니다.
    internal static readonly StringComparison PathComparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    internal static readonly StringComparer PathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private const FileAttributes NonResident = FileAttributes.SparseFile | FileAttributes.Offline | (FileAttributes)0x00400000 | (FileAttributes)0x00040000;

    internal static string FullPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) || path.StartsWith("\\\\?", StringComparison.Ordinal) || path.StartsWith("\\\\.", StringComparison.Ordinal))
            throw new StorageException(StorageError.InvalidPath, "일반 절대 경로를 지정해야 합니다.");
        var root = Path.GetPathRoot(path)!;
        foreach (var part in path[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.Contains(':') || part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new StorageException(StorageError.InvalidPath, "경로 이동, 대체 데이터 스트림 또는 모호한 파일명은 허용되지 않습니다.");
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '1' and <= '9'))
                throw new StorageException(StorageError.InvalidPath, "장치 이름은 파일 경로로 사용할 수 없습니다.");
        }
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }

    internal static bool Within(string path, string root) => path.Equals(root, PathComparison) || path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar, PathComparison);
    internal static string Under(string path, string root)
    {
        var full = FullPath(path);
        if (!Within(full, root)) throw new StorageException(StorageError.InvalidPath, "사용자가 선택한 루트 밖의 경로입니다.");
        CheckPath(full);
        return full;
    }

    // 모든 기존 부모를 검사합니다. 선택한 디렉터리는 신뢰할 수 있어야 하며 외부 프로세스가 부모를 교체하면 안 됩니다.
    internal static void CheckPath(string path, bool residentFile = false)
    {
        for (string? current = FullPath(path); current is not null; current = Path.GetDirectoryName(current))
        {
            FileAttributes attrs;
            try { attrs = File.GetAttributes(current); }
            catch (FileNotFoundException) { continue; }
            catch (DirectoryNotFoundException) { continue; }
            if ((attrs & FileAttributes.ReparsePoint) != 0)
                throw new StorageException(StorageError.LinkNotAllowed, "정션, 심볼릭 링크 및 클라우드 재분석 지점은 따라가지 않습니다.");
            if (OperatingSystem.IsWindows() && (attrs & FileAttributes.Directory) != 0)
            {
                using var handle = CreateFile(current, 0x80, 7, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
                if (handle.IsInvalid) throw new StorageException(StorageError.Unsupported, "디렉터리의 대소문자 구분 설정을 확인할 수 없습니다.");
                CheckDirectoryCaseSensitivity(handle);
            }
            if (residentFile && current.Equals(path, PathComparison) && (attrs & NonResident) != 0)
                throw new StorageException(StorageError.PlaceholderNotAllowed, "스파스 파일 또는 완전히 내려받지 않은 파일은 검증할 수 없습니다.");
        }
    }

    private static void CheckDirectoryCaseSensitivity(SafeFileHandle handle)
    {
        var flags = Marshal.AllocHGlobal(sizeof(uint));
        try
        {
            // FileCaseSensitiveInfo(23), FILE_CS_FLAG_CASE_SENSITIVE_DIR(1).
            if (!GetFileInformationByHandleEx(handle, 23, flags, sizeof(uint)))
                throw new StorageException(StorageError.Unsupported, "대소문자 구분 설정을 확인할 수 없는 파일 시스템은 안전하게 사용할 수 없습니다.");
            if ((Marshal.ReadInt32(flags) & 1) != 0)
                throw new StorageException(StorageError.Unsupported, "Windows 대소문자 구분 디렉터리는 경로 별칭의 안전한 구분을 위해 지원하지 않습니다.");
        }
        finally { Marshal.FreeHGlobal(flags); }
    }

    internal static FileStream Open(string path, FileMode mode, FileAccess access, FileShare share, bool resident = false)
    {
        CheckPath(path, resident);
        FileStream stream;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var desiredAccess = (access.HasFlag(FileAccess.Read) ? 0x80000000u : 0u) | (access.HasFlag(FileAccess.Write) ? 0x40000000u : 0u);
                var creation = mode switch { FileMode.CreateNew => 1u, FileMode.Open => 3u, FileMode.OpenOrCreate => 4u, _ => throw new NotSupportedException("안전한 파일 열기 모드가 아닙니다.") };
                // 링크 자체를 열되 따라가지 않습니다. 내용을 읽거나 쓰기 전에 열린 객체의 속성을 확인합니다.
                var handle = CreateFile(path, desiredAccess, (uint)share, IntPtr.Zero, creation, 0x40000000 | 0x08000000 | 0x00200000, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    var error = Marshal.GetLastWin32Error(); handle.Dispose();
                    if (error is 32 or 33) throw new StorageException(StorageError.LiveFile, "사용 중인 파일을 잠글 수 없습니다.");
                    if (error is 80 or 183) throw new StorageException(StorageError.Collision, "파일이 이미 존재합니다.");
                    if (error is 2 or 3) throw new FileNotFoundException("파일 또는 부모 디렉터리가 없습니다.", path);
                    if (error == 5) throw new UnauthorizedAccessException("파일 접근 권한이 없습니다.");
                    throw new StorageException(StorageError.TransferFailed, $"파일 열기에 실패했습니다. Windows 오류: {error}");
                }
                try
                {
                    if (!GetFileInformationByHandle(handle, out var info)) throw new StorageException(StorageError.InvalidPath, "열린 파일의 정보를 확인할 수 없습니다.");
                    if ((info.Attributes & ((uint)FileAttributes.Directory | (uint)FileAttributes.ReparsePoint)) != 0)
                        throw new StorageException(StorageError.LinkNotAllowed, "디렉터리나 링크는 파일로 열 수 없습니다.");
                    if (resident && (info.Attributes & (uint)NonResident) != 0) throw new StorageException(StorageError.PlaceholderNotAllowed, "완전히 내려받은 일반 파일만 읽을 수 있습니다.");
                    stream = new FileStream(handle, access, 128 * 1024, true);
                }
                catch { handle.Dispose(); throw; }
            }
            else stream = new FileStream(path, mode, access, share, 128 * 1024, FileOptions.Asynchronous | FileOptions.SequentialScan);
        }
        catch (StorageException) { throw; }
        catch (IOException ex) when (mode == FileMode.Open && File.Exists(path))
        { throw new StorageException(StorageError.LiveFile, "사용 중인 파일을 잠글 수 없습니다.", ex); }
        try { CheckPath(path, resident); CheckHandle(stream.SafeFileHandle, path); return stream; }
        catch { stream.Dispose(); throw; }
    }

    internal static void CheckHandle(SafeFileHandle handle, string path, bool openedName = false)
    {
        if (!OperatingSystem.IsWindows()) return;
        var buffer = new StringBuilder(32768);
        var count = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, openedName ? 8u : 0u);
        if (count == 0 || count >= buffer.Capacity) throw new StorageException(StorageError.InvalidPath, $"파일 핸들의 실제 경로를 확인할 수 없습니다. Windows 오류: {Marshal.GetLastWin32Error()}, 경로: {path}");
        var final = buffer.ToString();
        if (final.StartsWith("\\\\?\\UNC\\", StringComparison.OrdinalIgnoreCase)) final = "\\\\" + final[8..];
        else if (final.StartsWith("\\\\?\\", StringComparison.Ordinal)) final = final[4..];
        if (!Path.GetFullPath(final).Equals(path, PathComparison)) throw new StorageException(StorageError.LinkNotAllowed, "파일의 실제 위치가 요청 경로와 다릅니다.");
    }

    internal static string Key(string key)
    {
        if (key.Length != 37 || !key.EndsWith(".blob", StringComparison.Ordinal) || !Guid.TryParseExact(key[..32], "N", out _))
            throw new StorageException(StorageError.InvalidPath, "백엔드 키는 아카이브 GUID.blob 형식이어야 합니다.");
        return key;
    }

    internal static async Task<TransferDigest> DigestAsync(Stream input, Stream? output = null, CancellationToken ct = default, long maximumBytes = long.MaxValue)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[128 * 1024];
        long size = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, ct).ConfigureAwait(false);
            if (read == 0) break;
            if (read > maximumBytes - size) throw new StorageException(StorageError.HashMismatch, "파일 읽기가 허용된 최대 크기를 초과했습니다.");
            sha.AppendData(buffer, 0, read);
            size = checked(size + read);
            if (output is not null) await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
        }
        return new TransferDigest(size, Convert.ToHexString(sha.GetHashAndReset()));
    }

    internal static void Match(TransferDigest expected, TransferDigest actual)
    {
        if (!SameDigest(expected, actual))
            throw new StorageException(StorageError.HashMismatch, "크기 또는 SHA256 검증에 실패했습니다. 원본은 삭제하지 않습니다.");
    }

    internal static bool SameDigest(TransferDigest expected, TransferDigest actual) => expected.Size == actual.Size && expected.Sha256.Equals(actual.Sha256, StringComparison.OrdinalIgnoreCase);

    internal static FileStream OpenForRemoval(string path)
    {
        if (!OperatingSystem.IsWindows()) throw new StorageException(StorageError.Unsupported, "안전한 원본 제거는 Windows 파일 핸들에서만 지원됩니다.");
        CheckPath(path, true);
        // DELETE 권한과 공유 금지로 경로 교체/쓰기 경쟁 없이 검증한 핸들 자체를 제거합니다.
        var handle = CreateFile(path, 0x80000000 | 0x00010000, 0, IntPtr.Zero, 3, 0x08000000 | 0x00200000, IntPtr.Zero);
        if (handle.IsInvalid) { var error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new StorageException(StorageError.LiveFile, $"원본을 독점 잠금할 수 없습니다. Windows 오류: {error}"); }
        try
        {
            CheckPath(path, true); CheckHandle(handle, path);
            if (!GetFileInformationByHandle(handle, out var info) || info.NumberOfLinks != 1 || (info.Attributes & ((uint)FileAttributes.ReparsePoint | (uint)FileAttributes.Directory | (uint)FileAttributes.ReadOnly | (uint)NonResident)) != 0)
                throw new StorageException(StorageError.Unsupported, "읽기 전용, 링크, 스파스 파일 또는 지원되지 않는 원본은 제거하지 않습니다.");
            RejectAlternateStreams(path);
            return new FileStream(handle, FileAccess.Read, 128 * 1024, false);
        }
        catch { handle.Dispose(); throw; }
    }

    internal static void DeleteHeldFile(FileStream file)
    {
        ValidateRemovalStreams(file.SafeFileHandle);
        var disposition = new FileDisposition { DeleteFile = 1 };
        if (!SetFileInformationByHandle(file.SafeFileHandle, 4, ref disposition, (uint)Marshal.SizeOf<FileDisposition>()))
            throw new StorageException(StorageError.TransferFailed, $"검증한 원본 핸들의 제거가 실패했습니다. Windows 오류: {Marshal.GetLastWin32Error()}");
        try
        {
            // 삭제 대기 상태는 새 스트림 열기를 차단합니다. 그 상태에서 같은 핸들로 다시 확인해 늦게 생긴 ADS도 보존합니다.
            ValidateRemovalStreams(file.SafeFileHandle, true);
        }
        catch
        {
            disposition.DeleteFile = 0;
            if (!SetFileInformationByHandle(file.SafeFileHandle, 4, ref disposition, (uint)Marshal.SizeOf<FileDisposition>()))
                throw new StorageException(StorageError.TransferFailed, "제거 대기 상태의 취소가 실패했습니다. 메타데이터의 RemovalPending 기록을 확인하세요.");
            throw;
        }
    }

    private static void ValidateRemovalStreams(SafeFileHandle handle, bool deletePending = false)
    {
        if (!GetFileInformationByHandle(handle, out var info) || (deletePending ? info.NumberOfLinks > 1 : info.NumberOfLinks != 1) || (info.Attributes & ((uint)FileAttributes.ReparsePoint | (uint)FileAttributes.Directory | (uint)FileAttributes.ReadOnly | (uint)NonResident)) != 0)
            throw new StorageException(StorageError.Unsupported, "제거 직전에 원본의 링크/속성이 변경되었습니다.");
        const int capacity = 64 * 1024;
        var memory = Marshal.AllocHGlobal(capacity);
        try
        {
            if (!GetFileInformationByHandleEx(handle, 7, memory, capacity))
                throw new StorageException(StorageError.Unsupported, "같은 원본 핸들에서 전체 스트림을 확인할 수 없어 제거를 차단했습니다.");
            var bytes = new byte[capacity]; Marshal.Copy(memory, bytes, 0, capacity);
            var offset = 0;
            while (true)
            {
                var next = BitConverter.ToUInt32(bytes, offset);
                var nameLength = BitConverter.ToUInt32(bytes, offset + 4);
                if (nameLength > capacity - offset - 24 || (nameLength & 1) != 0)
                    throw new StorageException(StorageError.Unsupported, "원본 스트림 정보가 올바르지 않습니다.");
                if (Encoding.Unicode.GetString(bytes, offset + 24, (int)nameLength) != "::$DATA")
                    throw new StorageException(StorageError.Unsupported, "보관되지 않은 대체 데이터 스트림이 있어 원본 제거를 차단했습니다.");
                if (next == 0) break;
                if (next < 24 + nameLength || next > capacity - offset - 24)
                    throw new StorageException(StorageError.Unsupported, "원본 스트림 목록이 허용 범위를 초과했습니다.");
                offset += (int)next;
            }
        }
        finally { Marshal.FreeHGlobal(memory); }
    }

    // BOOLEAN은 1바이트입니다(Win32 BOOL 반환값과 다름).
    [StructLayout(LayoutKind.Sequential)] private struct FileDisposition { public byte DeleteFile; }
    [StructLayout(LayoutKind.Sequential)] private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME Creation, Access, Write;
        public uint VolumeSerial, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StreamData
    {
        public long Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 296)] public string Name;
    }

    private static void RejectAlternateStreams(string path)
    {
        var find = FindFirstStream(path, 0, out var data, 0);
        if (find == new IntPtr(-1))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == 38) return; // 스트림 없음
            throw new StorageException(StorageError.Unsupported, "원본의 대체 데이터 스트림을 안전하게 확인할 수 없습니다.");
        }
        try
        {
            do
            {
                if (data.Name != "::$DATA") throw new StorageException(StorageError.Unsupported, "대체 데이터 스트림이 있는 파일은 기본 스트림만 보관한 뒤 삭제할 수 없습니다.");
            } while (FindNextStream(find, out data));
            if (Marshal.GetLastWin32Error() != 38) throw new StorageException(StorageError.Unsupported, "원본 스트림 열거에 실패했습니다.");
        }
        finally { FindClose(find); }
    }

    // Windows에서는 부모 디렉터리 핸들에서 DELETE 공유를 막아 검사 이후의 정션/부모 교체를 방지합니다.
    internal static IDisposable LeaseDirectories(string path, bool includeLeaf = false)
    {
        CheckPath(path);
        var lease = new DirectoryLease();
        if (!OperatingSystem.IsWindows()) return lease;
        var parents = new Stack<string>();
        for (string? p = includeLeaf ? path : Path.GetDirectoryName(path); p is not null; p = Path.GetDirectoryName(p)) parents.Push(p);
        try
        {
            foreach (var p in parents)
            {
                if (!Directory.Exists(p)) continue;
                var handle = CreateFile(p, 0x80, 3, IntPtr.Zero, 3, 0x02000000 | 0x00200000, IntPtr.Zero);
                if (handle.IsInvalid) { handle.Dispose(); throw new StorageException(StorageError.Busy, "부모 디렉터리의 안전 잠금에 실패했습니다."); }
                lease.Handles.Add(handle);
                CheckHandle(handle, p, true);
                if (!GetFileInformationByHandle(handle, out var info) || (info.Attributes & (uint)FileAttributes.ReparsePoint) != 0)
                    throw new StorageException(StorageError.LinkNotAllowed, "부모 디렉터리에 링크가 있습니다.");
                CheckDirectoryCaseSensitivity(handle);
            }
            CheckPath(path);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private sealed class DirectoryLease : IDisposable
    {
        internal List<SafeFileHandle> Handles { get; } = [];
        public void Dispose() { foreach (var handle in Handles) handle.Dispose(); }
    }

    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetFileInformationByHandleEx(SafeFileHandle file, int infoClass, IntPtr information, int size);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "FindFirstStreamW")] private static extern IntPtr FindFirstStream(string path, int level, out StreamData data, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "FindNextStreamW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindNextStream(IntPtr find, out StreamData data);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool FindClose(IntPtr find);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern uint GetFinalPathNameByHandle(SafeFileHandle hFile, StringBuilder path, uint size, uint flags);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")] private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetFileInformationByHandle(SafeFileHandle file, int infoClass, ref FileDisposition info, uint size);
}
