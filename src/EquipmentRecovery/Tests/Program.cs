using System;
using LocalDinkumRecovery;
class Program
{
    static int count;
    static void Check(bool value) { count++; if (!value) throw new Exception("Test failed: " + count); }
    static void Main()
    {
        for (int map = 0; map < 3; map++) for (int current = 0; current < 3; current++)
        {
            Check(RecoveryRules.SameArea(map, current, false, -1,-1,false,-1,-1) == (map == current));
            Check(RecoveryRules.SameArea(map, current, true, 10,20,true,10,20) == (map == current));
            Check(!RecoveryRules.SameArea(map, current, true,10,20,true,10,21));
            Check(!RecoveryRules.SameArea(map, current, true,10,20,false,-1,-1));
            Check(!RecoveryRules.SameArea(map, current, false,-1,-1,true,10,20));
        }
        for(int level = -1; level <= 21; level++) for(int condition=-1;condition<=101;condition++)
            Check(RecoveryRules.ValidGear(level,condition,100) == (level>=1 && level<=20 && condition>0 && condition<=100));
        Check(!RecoveryRules.ValidGear(1,1,0));
        Check(RecoveryRules.Confirmed(1,1,4,8,true));
        Check(!RecoveryRules.Confirmed(1,2,4,8,true));
        Check(!RecoveryRules.Confirmed(0,0,4,8,true));
        Check(!RecoveryRules.Confirmed(1,1,8,8,true));
        Check(!RecoveryRules.Confirmed(1,1,4,8,false));
        Check(!RecoveryRules.Confirmed(1,1,double.NaN,8,true));
        Console.WriteLine("PASS: " + count + " rule checks. Not an in-game/network test.");
    }
}
