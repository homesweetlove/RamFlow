using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace RamFlow.Core;

public sealed record PipeRequest(string Command, Settings? Settings = null);
public static class EnginePipe
{
    [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; public int Inherit; }
    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptor(string text, uint revision, out IntPtr descriptor, out uint size);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr pointer);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern SafePipeHandle CreateNamedPipe(string name, uint openMode, uint mode, uint instances, uint output, uint input, uint timeout, ref SecurityAttributes security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")] private static extern SafePipeHandle OpenPipe(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);
    public static string? VerificationNamespace { get; private set; }
    private static string NamespaceSuffix => VerificationNamespace is string value ? ".Test." + value : "";
    public static string Name => "RamFlow.Native." + WindowsIdentity.GetCurrent().User!.Value + NamespaceSuffix;
    public static string EngineMutexName => "Local\\RamFlow.Native.Engine." + WindowsIdentity.GetCurrent().User!.Value + NamespaceSuffix;
    public static void ConfigureVerificationNamespace(Guid value)
    {
        if (value == Guid.Empty) throw new ArgumentException("Verification namespace cannot be empty.");
        string next = value.ToString("N");
        if (VerificationNamespace is not null && VerificationNamespace != next) throw new InvalidOperationException("Verification namespace already configured.");
        VerificationNamespace = next;
    }
    private const int Limit = 1048576;
    public static T Request<T>(PipeRequest request, int milliseconds = 3000)
    {
        using var timeout = new CancellationTokenSource(milliseconds);
        using var pipe = Connect(timeout.Token); pipe.ReadMode = PipeTransmissionMode.Message;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(request);
        pipe.WriteAsync(bytes, timeout.Token).AsTask().GetAwaiter().GetResult();
        using var document = JsonDocument.Parse(Read(pipe, timeout.Token).GetAwaiter().GetResult());
        if (!document.RootElement.GetProperty("Ok").GetBoolean()) throw new InvalidOperationException(document.RootElement.GetProperty("Error").GetString());
        return document.RootElement.GetProperty("Data").Deserialize<T>()!;
    }
    private static NamedPipeClientStream Connect(CancellationToken token)
    {
        while (true) {
            token.ThrowIfCancellationRequested();
            // SECURITY_IDENTIFICATION: 관리자 클라이언트의 토큰을 서버가 대신 사용할 수 없다.
            var handle = OpenPipe(@"\\.\pipe\" + Name, 0x120183, 0, IntPtr.Zero, 3, 0x40110000, IntPtr.Zero);
            if (!handle.IsInvalid) return new NamedPipeClientStream(PipeDirection.InOut, true, true, handle);
            int error = Marshal.GetLastWin32Error(); handle.Dispose();
            if (error is not (2 or 231)) throw new System.ComponentModel.Win32Exception(error);
            Task.Delay(20, token).GetAwaiter().GetResult();
        }
    }
    public static async Task ServeAsync(Engine engine, CancellationTokenSource lifetime)
    {
        var clients = new List<Task>();
        using var slots = new SemaphoreSlim(8);
        bool first = true;
        try {
            while (!lifetime.IsCancellationRequested) {
                await slots.WaitAsync(lifetime.Token);
                var pipe = CreateServer(first); first = false;
                try { await pipe.WaitForConnectionAsync(lifetime.Token); pipe.ReadMode = PipeTransmissionMode.Message; }
                catch { pipe.Dispose(); slots.Release(); throw; }
                var client = Task.Run(async () => {
                    bool stopping = false;
                    using (pipe) using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token)) {
                        deadline.CancelAfter(5000);
                        try {
                            var request = JsonSerializer.Deserialize<PipeRequest>(await Read(pipe, deadline.Token)) ?? throw new ArgumentException("IPC 형식 오류");
                            bool admin = false;
                            pipe.RunAsClient(() => { using var identity = WindowsIdentity.GetCurrent(); admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); });
                            object data;
                            switch (request.Command) {
                                case "ping": data = true; break;
                                case "state": data = engine.GetState(); break;
                                case "settings": data = engine.Settings; break;
                                case "apply":
                                    var settings = request.Settings ?? throw new ArgumentException("설정 누락");
                                    if (!admin && !settings.DryRun && !settings.Paused) throw new UnauthorizedAccessException("실제 최적화 활성화에는 관리자 UI가 필요합니다.");
                                    engine.ApplySettings(settings); data = engine.Settings; break;
                                case "stop":
                                    engine.Stop();
                                    if (engine.GetState().PendingRestores > 0) { engine.Start(); throw new IOException("복원 대기 중입니다. 재시도를 유지하며 엔진을 종료하지 않습니다."); }
                                    data = true; stopping = true; break;
                                default: throw new ArgumentException("허용되지 않은 명령");
                            }
                            var response = JsonSerializer.SerializeToUtf8Bytes(new { Ok = true, Data = data });
                            if (response.Length > Limit) throw new IOException("응답 크기 제한 초과");
                            await pipe.WriteAsync(response, deadline.Token);
                        } catch (Exception e) {
                            try { await pipe.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(new { Ok = false, Error = e.Message }), deadline.Token); } catch { }
                        } finally { slots.Release(); }
                    }
                    if (stopping) lifetime.Cancel();
                });
                clients.RemoveAll(x => x.IsCompleted); clients.Add(client);
            }
        } catch (OperationCanceledException) { }
        await Task.WhenAll(clients);
    }
    private static NamedPipeServerStream CreateServer(bool first)
    {
        using var identity = WindowsIdentity.GetCurrent();
        bool admin = new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        string userAccess = admin ? "0x120183" : "GA";
        string sddl = $"D:P(A;;GA;;;SY)(A;;GA;;;BA)(A;;{userAccess};;;{identity.User!.Value})";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptor(sddl, 1, out var descriptor, out _)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try {
            var security = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor };
            // 메시지 모드 · OVERLAPPED · 최초 인스턴스 보호 · 원격 클라이언트 거부.
            var handle = CreateNamedPipe(@"\\.\pipe\" + Name, 3U | 0x40000000U | (first ? 0x80000U : 0), 4U | 2U | 8U, 8, 65536, 65536, 0, ref security);
            if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); throw new System.ComponentModel.Win32Exception(error); }
            return new NamedPipeServerStream(PipeDirection.InOut, true, false, handle);
        } finally { LocalFree(descriptor); }
    }
    private static async Task<byte[]> Read(PipeStream pipe, CancellationToken token)
    {
        using var memory = new MemoryStream(); byte[] buffer = new byte[16384];
        // Request is synchronous on WPF's UI thread: the read continuation must never wait for that thread.
        do { int count = await pipe.ReadAsync(buffer, token).ConfigureAwait(false); if (count == 0) throw new EndOfStreamException(); if (memory.Length + count > Limit) throw new IOException("IPC 크기 제한 초과"); memory.Write(buffer, 0, count); } while (!pipe.IsMessageComplete);
        return memory.ToArray();
    }
}
