namespace SdrCapture;

// Used by both physical keyboards and software keys from Stream Deck.
static class KvmShortcut
{
    public static void Validate(uint modifiers,uint key)
    {if(key!=0&&(modifiers is 0 or >7||key>254))throw new ArgumentException("Горячая клавиша KVM: выберите Ctrl, Alt или Shift и основную клавишу.");}
    public static uint Modifiers(IEnumerable<int> keys)
    {
        uint result=0;
        foreach(int key in keys)result|=key switch{17 or 162 or 163=>2u,18 or 164 or 165=>1u,16 or 160 or 161=>4u,91 or 92=>8u,_=>0u};
        return result;
    }
    public static bool Matches(uint configuredModifiers,uint configuredKey,uint modifiers,int key)=>configuredKey!=0&&configuredKey==key&&configuredModifiers==modifiers;
}

sealed class ShortcutEditor:FlowLayoutPanel
{
    readonly CheckBox ctrl=new(){Text="Ctrl",AutoSize=true},alt=new(){Text="Alt",AutoSize=true},shift=new(){Text="Shift",AutoSize=true};
    readonly ComboBox key=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=110};
    public uint Modifiers=>(uint)((ctrl.Checked?2:0)|(alt.Checked?1:0)|(shift.Checked?4:0));
    public uint Key=>key.SelectedItem is KeyChoice choice?choice.Code:0;
    sealed record KeyChoice(uint Code,string Label){public override string ToString()=>Label;}
    public ShortcutEditor(uint modifiers,uint code)
    {
        WrapContents=false;ctrl.Checked=(modifiers&2)!=0;alt.Checked=(modifiers&1)!=0;shift.Checked=(modifiers&4)!=0;
        key.Items.Add(new KeyChoice(0,"Выключено"));
        foreach(var k in Enumerable.Range((int)Keys.A,26).Concat(Enumerable.Range((int)Keys.D0,10)).Concat(Enumerable.Range((int)Keys.F1,12)).Append((int)Keys.Pause))key.Items.Add(new KeyChoice((uint)k,((Keys)k).ToString()));
        key.SelectedItem=key.Items.Cast<KeyChoice>().FirstOrDefault(k=>k.Code==code)??key.Items[0];
        Controls.AddRange([ctrl,alt,shift,key]);
    }
}
