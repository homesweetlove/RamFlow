using RamFlow.Core;

static class UiHandoffTests
{
    public static void Run(Action<bool,string> check) {
        using var engine=new Engine(new MockResourceProvider(),persist:false);
        bool refused=false;try{engine.Detach();}catch(InvalidOperationException){refused=true;}
        check(refused,"Local engine owner was allowed to detach and lose restoration ownership");
        check(!engine.IsRemote,"Mock owner incorrectly reported remote");
        var name=UiHandoff.NewReadyName();check(UiHandoff.ValidReadyName(name),"Ready event name invalid");
        check(!UiHandoff.ValidReadyName("Global\\unrelated-event"),"Arbitrary event accepted as ready channel");
        Console.WriteLine("UI handoff lifecycle regressions passed.");
    }
}
