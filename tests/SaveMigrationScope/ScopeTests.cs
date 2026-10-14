using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LocalDinkumMods;
static class ScopeTests
{
    static int checks;
    static void Check(bool condition,string name) { if(!condition) throw new Exception(name); checks++; Console.WriteLine("PASS "+name); }
    static void Reject(Action action,string name) { try { action(); } catch(InvalidDataException) { Check(true,name); return; } throw new Exception("Expected rejection: "+name); }
    static void Main()
    {
        string root=Path.GetFullPath("scope-fixture-root");
        Func<string,string> p=s=>Path.Combine(root,s.Replace('/',Path.DirectorySeparatorChar));
        string[] relatives={"Slot0/Player.es3","Slot0/Player.es3.bak","Slot0/Player.es3.enhancement-backup","Slot0/Container.es3","Slot0/playerInfo.dat","Slot0/playerInfo.es3","Slot0/Chests/chest-1+-1.dat","Slot0/npc.dat","Slot0/npc.es3","Slot1/Player.es3","Slot1/Container.es3","Slot1/Container.es3.bac","Slot1/Player.es3.bak","Slot2/playerInfo.dat","Slot2/playerInfo.es3"};
        var files=relatives.Select(p).ToArray(); string[] slots={"Slot0","Slot1","Slot2"};
        var modes=new Dictionary<string,int>{{"Slot0",1},{"Slot1",1},{"Slot2",4}};
        var selected=EnhancementMigrationScope.Select(root,files,slots,modes);
        Check(selected.Count==8,"mixed legacy/modern plus skipped legacy-only slot selects eight modern files");
        Check(!selected.Any(s=>s.EndsWith(".dat") || s.EndsWith("playerInfo.es3") || s.EndsWith("npc.es3")),"legacy remnants never selected");
        modes["Slot0"]=4;
        Check(EnhancementMigrationScope.Select(root,files,slots,modes).All(s=>s.StartsWith(p("Slot1/"))),"skipped modern slot unchanged");
        modes["Slot1"]=4; Reject(()=>EnhancementMigrationScope.Select(root,files,slots,modes),"all skipped rejected");
        modes["Slot0"]=1; modes["Slot2"]=1; Reject(()=>EnhancementMigrationScope.Select(root,files,slots,modes),"legacy-only selected refused, not silently dropped");
        modes["Slot2"]=0; Reject(()=>EnhancementMigrationScope.Select(root,files,slots,modes),"unknown slot requires deliberate decision");
        modes.Remove("Slot2"); Reject(()=>EnhancementMigrationScope.Select(root,files,slots,modes),"missing slot choice refused");
        modes["Slot2"]=4; modes["Slot0"]=9; Reject(()=>EnhancementMigrationScope.Select(root,files,slots,modes),"invalid enum refused");
        modes["Slot0"]=1;
        Reject(()=>EnhancementMigrationScope.Select(root,files.Concat(new[]{Path.GetFullPath("outside.es3")}).ToArray(),slots,modes),"outside-root file rejected");
        Reject(()=>EnhancementMigrationScope.Select(root,files,new[]{"../Slot0"},modes),"invalid slot rejected");
        foreach(int mode in new[]{1,2,3}) {modes["Slot0"]=mode;Check(EnhancementMigrationScope.Select(root,files,slots,modes).Count==4,"valid selected mode "+mode);}
        foreach(string name in new[]{"Player.es3","Container.es3","Player.es3.bak","Container.es3.bac","Player.es3.enhancement-backup"}) Check(EnhancementMigrationScope.ModernFile("Slot12/"+name),"supported file "+name);
        foreach(string name in new[]{"Player.es3.tmp","Player.es3.old","playerInfo.es3","npc.dat","Chests/Player.es3","Player.es3/extra","../Slot0/Player.es3"}) Check(!EnhancementMigrationScope.ModernFile("Slot0/"+name),"excluded file "+name);
        Console.WriteLine("ALL PASS: "+checks+" pure selection checks; no real save I/O");
    }
}
