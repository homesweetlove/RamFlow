using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Reflection;
using RamFlow.Core;

namespace ResearchLab;

// Per-record OS-visible flush; fsync at lifecycle boundaries. This does not
// promise survival of every sample across power loss or physical disk failure.
sealed class EvidenceJournal : IDisposable
{
    readonly string root;
    readonly FileStream file;
    readonly StreamWriter writer;
    readonly object gate=new();
    readonly long epoch=Stopwatch.GetTimestamp();
    long sequence;
    bool closed,sealedRun;
    public string RunId { get; }=Guid.NewGuid().ToString("N");
    public bool Faulted { get; private set; }
    public double WriteMilliseconds { get; private set; }
    public long Records => sequence;
    public EvidenceJournal(string directory,string[] args) {
        root=Path.GetFullPath(directory);
        using(var self=Process.GetCurrentProcess()) AtomicNew(Path.Combine(root,"run-manifest.json"),new {
            evidence_schema_version=1,run_id=RunId,created_utc=DateTimeOffset.UtcNow,args,
            host_pid=self.Id,host_creation_filetime=self.StartTime.ToFileTimeUtc(),
            runtime=Environment.Version.ToString(),os=Environment.OSVersion.ToString(),logical_processors=Environment.ProcessorCount,
            controller_binary=Assembly.GetEntryAssembly()?.Location,
            controller_sha256=Assembly.GetEntryAssembly() is {} assembly?FileResearchAuditSink.FileHash(assembly.Location):null,
            core_sha256=FileResearchAuditSink.FileHash(typeof(Engine).Assembly.Location),
            monotonic_frequency=Stopwatch.Frequency,monotonic_epoch=epoch,
            durability="Per-record OS buffer flush; durable boundary flush; unfinalized runs are never marked complete",
            scope="Research evidence lifecycle; not a physical UI input latency measurement" });
        file=new(Path.Combine(root,"trial-events.jsonl"),FileMode.CreateNew,FileAccess.Write,FileShare.Read);
        writer=new(file,new UTF8Encoding(false),4096,leaveOpen:true) { AutoFlush=true };
        Write("run-start",new { args },true);
    }
    public void Write(string kind,object? data=null,bool durable=false) {
        lock(gate) {
            var clock=Stopwatch.StartNew();
            try {
                if(closed) throw new ObjectDisposedException(nameof(EvidenceJournal));
                string line=JsonSerializer.Serialize(new { run_id=RunId,sequence=sequence+1,time_utc=DateTimeOffset.UtcNow,
                    monotonic_ticks=Stopwatch.GetTimestamp()-epoch,kind,data });
                writer.WriteLine(line); if(durable) file.Flush(true); sequence++;
            } catch { Faulted=true;throw; }
            finally { WriteMilliseconds+=clock.Elapsed.TotalMilliseconds; }
        }
    }
    public bool TryWrite(string kind,object? data=null) {
        try { Write(kind,data,true);return true; } catch { return false; }
    }
    public void Seal(string status,bool cleanupCompleted) {
        lock(gate) {
            if(sealedRun || closed) throw new InvalidOperationException("Evidence cannot be resealed.");
            if(status=="completed" && (Faulted || !cleanupCompleted)) throw new InvalidOperationException("Incomplete evidence/cleanup cannot be successful.");
            Write("terminal",new { status,cleanup_completed=cleanupCompleted,logging_fault=Faulted },true);
            Close();
            AtomicNew(Path.Combine(root,"completion.json"),new { evidence_schema_version=1,run_id=RunId,status,
                cleanup_completed=cleanupCompleted,logging_fault=Faulted,records=sequence,
                log_write_ms=WriteMilliseconds,log_cost_scope="Event serialization/write/flush wall time, not CPU time; detailed logging changes measurement overhead",
                finished_utc=DateTimeOffset.UtcNow });
            var inventory=new List<object>();var directories=new Stack<string>();directories.Push(root);
            while(directories.Count>0) foreach(string path in Directory.EnumerateFileSystemEntries(directories.Pop())) {
                var attributes=File.GetAttributes(path);
                if((attributes&FileAttributes.ReparsePoint)!=0) throw new IOException("Evidence index refuses linked files/directories.");
                if((attributes&FileAttributes.Directory)!=0) { directories.Push(path);continue; }
                string name=Path.GetRelativePath(root,path).Replace('\\','/');
                inventory.Add(new { path=name,bytes=new FileInfo(path).Length,sha256=FileResearchAuditSink.FileHash(path),
                    retention=name.Split('/').Contains("fixture-bin")?"hash-only-binary":"required" });
            }
            AtomicNew(Path.Combine(root,"artifact-index.json"),new { evidence_schema_version=1,run_id=RunId,
                files=inventory,scope="Index sealed after outcome and all cleanup/stream closes; hashes detect changes, not scientific validity" });
            sealedRun=true;
        }
    }
    internal static void AtomicNew<T>(string path,T value) {
        if(File.Exists(path)||Directory.Exists(path)) throw new IOException("Refusing existing evidence: "+path);
        string temporary=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            using(var stream=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None)) {
                JsonSerializer.Serialize(stream,value,new JsonSerializerOptions { WriteIndented=true });stream.Flush(true);
            }
            File.Move(temporary,path,overwrite:false);
        } finally { if(File.Exists(temporary)) File.Delete(temporary); }
    }
    internal static void AssertSealedForTraining(string directory,bool requiresEvidence=false) {
        string root=Path.GetFullPath(directory);
        // Historical rows keep their original provenance; workload IDs isolate
        // them from the new evidence-v1 protocol. Do not manufacture a seal.
        if(!File.Exists(Path.Combine(root,"run-manifest.json"))) {
            if(requiresEvidence)throw new InvalidDataException("Required evidence manifest is missing.");
            return;
        }
        using var manifest=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"run-manifest.json")));
        using var done=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"completion.json")));
        using var index=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"artifact-index.json")));
        using var outcome=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"outcome.json")));
        string? id=manifest.RootElement.GetProperty("run_id").GetString();
        if(done.RootElement.GetProperty("run_id").GetString()!=id || index.RootElement.GetProperty("run_id").GetString()!=id ||
            done.RootElement.GetProperty("status").GetString()!="completed" || !done.RootElement.GetProperty("cleanup_completed").GetBoolean() ||
            done.RootElement.GetProperty("logging_fault").GetBoolean() || outcome.RootElement.GetProperty("status").GetString()!="completed" ||
            !outcome.RootElement.GetProperty("restored").GetBoolean()) throw new InvalidDataException("Training refuses unfinalized, failed or unrestored evidence.");
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var row in index.RootElement.GetProperty("files").EnumerateArray()) {
            string name=row.GetProperty("path").GetString()!,path=Path.GetFullPath(Path.Combine(root,name));
            if(!names.Add(name) || !path.StartsWith(root+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Unsafe evidence index.");
            if(!File.Exists(path) && row.GetProperty("retention").GetString()=="hash-only-binary")continue;
            string part=root;
            foreach(string component in Path.GetRelativePath(root,path).Split(Path.DirectorySeparatorChar)) {
                part=Path.Combine(part,component);if((File.GetAttributes(part)&FileAttributes.ReparsePoint)!=0)throw new InvalidDataException("Linked evidence is not trusted.");
            }
            if(new FileInfo(path).Length!=row.GetProperty("bytes").GetInt64() || FileResearchAuditSink.FileHash(path)!=row.GetProperty("sha256").GetString())
                throw new InvalidDataException("Evidence hash mismatch: "+name);
        }
        foreach(string required in new[] { "run-manifest.json","completion.json","outcome.json","trial-events.jsonl","cost-observation.json" })
            if(!names.Contains(required))throw new InvalidDataException("Required evidence is not indexed: "+required);
    }
    void Close() { if(closed)return;writer.Flush();file.Flush(true);writer.Dispose();file.Dispose();closed=true; }
    public void Dispose() { lock(gate) Close(); }
}
