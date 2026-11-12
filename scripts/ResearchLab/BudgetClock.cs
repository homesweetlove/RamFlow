using System.Text.Json;

namespace ResearchLab;

static class BudgetClock
{
    public static (double Seconds,bool Active) Exposure(double start,double end,IEnumerable<(double Time,bool On)> writes) {
        double total=0,cursor=start;bool active=false;
        foreach(var row in writes.Where(x=>x.Time>=start&&x.Time<=end).OrderBy(x=>x.Time)) {
            if(active)total+=row.Time-cursor;
            active=row.On;cursor=row.Time;
        }
        if(active)total+=end-cursor;
        return (total,active);
    }
    public static double NextDue(bool selected,int used,double actual,double tickEnd,double onSeconds) {
        if(!double.IsFinite(actual)||!double.IsFinite(tickEnd)||!double.IsFinite(onSeconds)||tickEnd<actual||onSeconds<0||used<0||used>9)throw new ArgumentException("Invalid dose clock input.");
        // Debit completed restore overshoot from the next ON credit. Never
        // delay restoration or remove the original-value journal for a match.
        return selected?tickEnd+Math.Max(0,used*.5-onSeconds):Math.Max(tickEnd,actual+.5);
    }
    public static int Replay(string[] args) {
        using var doc=JsonDocument.Parse(File.ReadAllText(Options.Get(args,"--input","")));var input=doc.RootElement;
        double now=0;int used=0;bool active=false;var writes=new List<(double,bool)>();var rows=new List<object>();
        foreach(var row in input.GetProperty("epochs").EnumerateArray()) {
            bool selected=row.GetProperty("selected").GetBoolean();double actual=now;
            if(selected!=active){now+=row.GetProperty("transition_delay").GetDouble();writes.Add((now,selected));active=selected;}
            if(selected)used++;
            double on=Exposure(0,now,writes).Seconds;double next=NextDue(selected,used,actual,now,on);
            rows.Add(new {actual_seconds=actual,tick_end_seconds=now,on_seconds=on,next_due_seconds=next});now=next;
        }
        if(active){now+=input.GetProperty("final_restore_delay").GetDouble();writes.Add((now,false));}
        Options.WriteNew(Options.Get(args,"--output",""),new {scope="SYNTHETIC actuator-clock replay; no OS calls",actual_on_seconds=Exposure(0,now,writes).Seconds,end_seconds=now,rows});return 0;
    }
}
