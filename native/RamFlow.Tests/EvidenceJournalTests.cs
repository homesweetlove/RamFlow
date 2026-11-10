using ResearchLab;
using System.Text.Json;

static class EvidenceJournalTests
{
    public static void Run(Action<bool,string> check) {
        string root=Path.Combine(Path.GetTempPath(),"RamFlow.Evidence-"+Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        using(var evidence=new EvidenceJournal(root,["trial","--condition","A"])) {
            evidence.Write("system-sample",new { CpuPercent=12.5 });
            using var live=new StreamReader(new FileStream(Path.Combine(root,"trial-events.jsonl"),FileMode.Open,FileAccess.Read,FileShare.ReadWrite));
            var events=live.ReadToEnd().Split('\n',StringSplitOptions.RemoveEmptyEntries);
            check(events.Length==2,"Incremental evidence was buffered until completion");
            using var first=JsonDocument.Parse(events[0]);using var last=JsonDocument.Parse(events[1]);
            check(first.RootElement.GetProperty("sequence").GetInt64()==1 && last.RootElement.GetProperty("sequence").GetInt64()==2,"Evidence sequence is not monotonic");
            check(last.RootElement.GetProperty("monotonic_ticks").GetInt64()>=first.RootElement.GetProperty("monotonic_ticks").GetInt64(),"Evidence clock moved backwards");
            File.WriteAllText(Path.Combine(root,"outcome.json"),"{\"status\":\"failed\"}");
            evidence.Seal("failed",true);
        }
        check(File.Exists(Path.Combine(root,"completion.json")) && File.Exists(Path.Combine(root,"artifact-index.json")),"Failed trials lost their final evidence inventory");
        using(var done=JsonDocument.Parse(File.ReadAllText(Path.Combine(root,"completion.json"))))
            check(done.RootElement.GetProperty("status").GetString()=="failed","Evidence seal rewrote failure into success");
        bool overwrite=false;try { using var again=new EvidenceJournal(root,[]); }catch(IOException){overwrite=true;}
        check(overwrite,"Evidence journal overwrote an existing run");
        string partial=Path.Combine(root,"partial");Directory.CreateDirectory(partial);
        using(var open=new EvidenceJournal(partial,[])) { open.Write("request-begin",new { id=1 }); }
        check(File.Exists(Path.Combine(partial,"trial-events.jsonl")) && !File.Exists(Path.Combine(partial,"completion.json")),"Incomplete run was falsely marked complete");
        string complete=Path.Combine(root,"training");Directory.CreateDirectory(complete);
        using(var evidence=new EvidenceJournal(complete,[])) {
            EvidenceJournal.AtomicNew(Path.Combine(complete,"outcome.json"),new { status="completed",restored=true,label="SYNTHETIC FORMAT TEST" });
            EvidenceJournal.AtomicNew(Path.Combine(complete,"cost-observation.json"),new { label="SYNTHETIC FORMAT TEST" });
            evidence.Seal("completed",true);
        }
        EvidenceJournal.AssertSealedForTraining(complete);check(true,"Sealed evidence rejected before training");
        File.AppendAllText(Path.Combine(complete,"cost-observation.json")," ");
        bool tampered=false;try { EvidenceJournal.AssertSealedForTraining(complete); }catch(InvalidDataException){tampered=true;}
        check(tampered,"Tampered cost data reached model training");
        Console.WriteLine("Incremental evidence, failure seal, monotonic clock and overwrite regressions passed.");
    }
}
