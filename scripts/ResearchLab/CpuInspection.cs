using System.Diagnostics;
using System.Runtime.InteropServices;
using RamFlow.Core;

namespace ResearchLab;

// Read-only, explicitly enabled fixture telemetry. It is never policy evidence
// and cannot change scheduling, affinity, QoS, or the system power plan.
static class CpuInspection
{
    [StructLayout(LayoutKind.Sequential)] internal struct ProcessorNumber { public ushort Group;public byte Number,Reserved; }
    [StructLayout(LayoutKind.Sequential)] struct CpuRate { public uint Flags,Rate; }
    [StructLayout(LayoutKind.Sequential)] struct Throttle { public uint Version,Control,State; }
    [DllImport("kernel32.dll")] internal static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll")] static extern void GetCurrentProcessorNumberEx(out ProcessorNumber value);
    [DllImport("kernel32.dll")] static extern uint GetActiveProcessorCount(ushort group);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool QueryInformationJobObject(IntPtr job,int kind,out CpuRate value,uint size,IntPtr returned);
    [DllImport("kernel32.dll",SetLastError=true)] static extern IntPtr OpenThread(uint access,bool inherit,uint tid);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool GetThreadInformation(IntPtr thread,int kind,ref Throttle value,uint size);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr value);
    public static uint Processor() { GetCurrentProcessorNumberEx(out var p);return ((uint)p.Group<<8)|p.Number; }
    static object Power(uint tid) {
        IntPtr handle=OpenThread(0x40,false,tid);
        if(handle==IntPtr.Zero)return new { available=false,error=Marshal.GetLastWin32Error() };
        try {
            var value=new Throttle {Version=1};bool ok=GetThreadInformation(handle,3,ref value,12);
            return new { available=ok,error=ok?0:Marshal.GetLastWin32Error(),control=ok?(uint?)value.Control:null,state=ok?(uint?)value.State:null,
                meaning="Explicit API state; not the effective heuristic QoS classification" };
        }finally {CloseHandle(handle);}
    }
    public static object Capture(int active,uint[] ids,long[] completed,uint[] processors,Task[] tasks) {
        var clock=Stopwatch.StartNew();using var process=Process.GetCurrentProcess();using var provider=new WindowsResourceProvider();
        var threads=new List<object>();
        foreach(ProcessThread thread in process.Threads) using(thread) {
            try {
                var state=thread.ThreadState;
                threads.Add(new {tid=thread.Id,cpu_ms=thread.TotalProcessorTime.TotalMilliseconds,state=state.ToString(),
                    wait_reason=state==System.Diagnostics.ThreadState.Wait?thread.WaitReason.ToString():null,
                    base_priority=thread.BasePriority,current_priority=thread.CurrentPriority,power=Power((uint)thread.Id)});
            }catch(Exception error) {threads.Add(new {tid=thread.Id,status="unavailable",error=error.Message});}
        }
        bool jobOk=QueryInformationJobObject(IntPtr.Zero,15,out var job,8,IntPtr.Zero);int jobError=jobOk?0:Marshal.GetLastWin32Error();
        var workers=Enumerable.Range(0,ids.Length).Select(i=> {
            uint at=Volatile.Read(ref processors[i]);
            return new {index=i,tid=Volatile.Read(ref ids[i]),completed_units=Interlocked.Read(ref completed[i]),
                sampled_last_group=at==uint.MaxValue?(uint?)null:at>>8,sampled_last_processor=at==uint.MaxValue?(uint?)null:at&255,
                status=i<tasks.Length?tasks[i].Status.ToString():"not-created"};
        }).ToArray();
        return new {pid=process.Id,creation_filetime=process.StartTime.ToFileTimeUtc(),active_threads=active,workers,threads,
            process_cpu_ms=process.TotalProcessorTime.TotalMilliseconds,affinity_mask=process.ProcessorAffinity.ToInt64().ToString("x"),
            priority_class=process.PriorityClass.ToString(),managed_logical_processors=Environment.ProcessorCount,
            system_logical_processors=GetActiveProcessorCount(ushort.MaxValue),values=provider.ReadValues(process.Id),topology=provider.Topology(),
            immediate_job=new {available=jobOk,error=jobError,flags=jobOk?(uint?)job.Flags:null,rate=jobOk?(uint?)job.Rate:null,
                scope="Current/immediate job only; does not rule out ancestor limits"},
            control_writes=0,inspection_ms=clock.Elapsed.TotalMilliseconds,
            scope="Instrumented disposable fixture diagnostic; sampled last CPU is not an affinity mask or continuous residency trace"};
    }
}
