namespace SdrCapture;

// Used by both physical keyboards and software keys from Stream Deck.
static class KvmShortcut
{
    public static void Validate(uint modifiers,uint key)
    {if(key!=0&&(modifiers is 0 or >7||key>254))throw new ArgumentException("Choose Ctrl, Alt or Shift plus a key for the KVM shortcut.");}
    public static uint Modifiers(IEnumerable<int> keys)
    {
        uint result=0;
        foreach(int key in keys)result|=key switch{17 or 162 or 163=>2u,18 or 164 or 165=>1u,16 or 160 or 161=>4u,91 or 92=>8u,_=>0u};
        return result;
    }
    public static bool Matches(uint configuredModifiers,uint configuredKey,uint modifiers,int key)=>configuredKey!=0&&configuredKey==key&&configuredModifiers==modifiers;
}
