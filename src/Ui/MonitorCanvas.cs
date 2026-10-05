using W=System.Windows;
using C=System.Windows.Controls;
using I=System.Windows.Input;

namespace SdrCapture.Ui;

sealed class MonitorCanvas:C.StackPanel
{
    readonly C.Canvas canvas=new(){Height=310,Background=Kit.Brush("#0B0B13"),ClipToBounds=true};
    readonly C.TextBlock label=Kit.Text("Select a display",13,"#B0A9BC");
    readonly C.TextBox x=new(){Width=68},y=new(){Width=68};
    readonly C.ComboBox shortcut=new(){Width=145};
    readonly List<MonitorPlacement> monitors;
    readonly KvmOptions original;
    readonly HashSet<string> remoteOnly;
    readonly Dictionary<MonitorPlacement,C.Border> blocks=[];
    MonitorPlacement? selected;
    bool updating,dragging;
    W.Point dragStart;int startX,startY;
    double scale=1,offsetX,offsetY;
    public List<MonitorPlacement> Result=>monitors.Concat(original.Layout.Where(m=>remoteOnly.Contains(m.Peer))).DistinctBy(m=>m.Key).Select(m=>m with{}).ToList();
    public List<string> RemoteOnlyPeers=>remoteOnly.ToList();
    public MonitorCanvas(KvmOptions options,List<MonitorPlacement> source)
    {
        original=options.Copy();remoteOnly=options.RemoteOnlyPeers.ToHashSet();monitors=source.Select(m=>m with{}).ToList();
        Children.Add(canvas);label.Margin=new(0,16,0,12);Children.Add(label);
        shortcut.Items.Add("No shortcut");for(int i=1;i<=12;i++)shortcut.Items.Add("Ctrl+Alt+F"+i);
        var tools=new C.WrapPanel();foreach(var item in new W.FrameworkElement[]{Kit.Text("X"),x,Kit.Text("Y"),y,shortcut,Kit.Button("Place left",()=>Place(-1)),Kit.Button("Place right",()=>Place(1)),Kit.Button("Remote only",ExcludeSelected)}){item.Margin=new(0,0,8,8);item.VerticalAlignment=W.VerticalAlignment.Center;tools.Children.Add(item);}Children.Add(tools);
        x.LostKeyboardFocus+=(_,_)=>MoveFromInputs();y.LostKeyboardFocus+=(_,_)=>MoveFromInputs();shortcut.SelectionChanged+=(_,_)=>{if(!updating&&selected!=null){selected.Hotkey=Math.Max(0,shortcut.SelectedIndex);Draw();}};
        canvas.SizeChanged+=(_,_)=>Draw();canvas.MouseMove+=(_,e)=>
        {
            if(!dragging||selected==null)return;var p=e.GetPosition(canvas);selected.X=Math.Clamp(startX+(int)((p.X-dragStart.X)/scale),-100000,100000);selected.Y=Math.Clamp(startY+(int)((p.Y-dragStart.Y)/scale),-100000,100000);Draw();UpdateSelection();
        };
        canvas.MouseLeftButtonUp+=(_,_)=>{if(!dragging)return;dragging=false;canvas.ReleaseMouseCapture();Snap();Draw();UpdateSelection();};
        canvas.LostMouseCapture+=(_,_)=>dragging=false;
        selected=monitors.FirstOrDefault();UpdateSelection();
    }
    void UpdateSelection()
    {
        updating=true;label.Text=selected?.Name??"Select a display to arrange it";x.IsEnabled=y.IsEnabled=shortcut.IsEnabled=selected!=null;
        if(selected!=null){x.Text=selected.X.ToString();y.Text=selected.Y.ToString();shortcut.SelectedIndex=Math.Clamp(selected.Hotkey,0,12);}updating=false;
    }
    void MoveFromInputs(){if(selected==null||updating)return;if(int.TryParse(x.Text,out int px)&&int.TryParse(y.Text,out int py)){selected.X=Math.Clamp(px,-100000,100000);selected.Y=Math.Clamp(py,-100000,100000);Draw();}UpdateSelection();}
    internal void SelectMonitor(string device){selected=monitors.FirstOrDefault(m=>m.Device==device);UpdateSelection();Draw();}
    internal void ExcludeSelected()
    {
        if(selected==null||selected.Peer==original.Id)return;string peer=selected.Peer;remoteOnly.Add(peer);original.Layout.RemoveAll(m=>m.Peer==peer);original.Layout.AddRange(monitors.Where(m=>m.Peer==peer).Select(m=>m with{}));monitors.RemoveAll(m=>m.Peer==peer);selected=monitors.FirstOrDefault();Draw();UpdateSelection();
    }
    internal void Place(int direction)
    {
        var center=monitors.FirstOrDefault(m=>m.Peer==original.Id&&KvmScreen.Local().Any(s=>s.Device==m.Device&&s.Primary))??monitors.FirstOrDefault(m=>m.Peer==original.Id);
        if(selected==null||center==null||selected==center)return;selected.X=direction<0?center.X-selected.Width:center.X+center.Width;selected.Y=center.Y;Draw();UpdateSelection();
    }
    void Snap()
    {
        if(selected==null)return;int tolerance=(int)(18/Math.Max(scale,.001));
        foreach(var other in monitors.Where(m=>m!=selected))
        {
            if(Math.Abs(selected.X-(other.X+other.Width))<tolerance)selected.X=other.X+other.Width;
            if(Math.Abs(selected.X+selected.Width-other.X)<tolerance)selected.X=other.X-selected.Width;
            if(Math.Abs(selected.Y-other.Y)<tolerance)selected.Y=other.Y;
            if(Math.Abs(selected.Y-(other.Y+other.Height))<tolerance)selected.Y=other.Y+other.Height;
            if(Math.Abs(selected.Y+selected.Height-other.Y)<tolerance)selected.Y=other.Y-selected.Height;
        }
    }
    void Draw()
    {
        canvas.Children.Clear();blocks.Clear();if(monitors.Count==0||canvas.ActualWidth<=0)return;
        int minX=monitors.Min(m=>m.X),minY=monitors.Min(m=>m.Y),maxX=monitors.Max(m=>m.X+m.Width),maxY=monitors.Max(m=>m.Y+m.Height);
        if(!dragging){scale=Math.Min(Math.Max(1,canvas.ActualWidth-44)/Math.Max(1,maxX-minX),Math.Max(1,canvas.Height-44)/Math.Max(1,maxY-minY));offsetX=(canvas.ActualWidth-(maxX-minX)*scale)/2-minX*scale;offsetY=(canvas.Height-(maxY-minY)*scale)/2-minY*scale;}
        foreach(var monitor in monitors)
        {
            bool local=monitor.Peer==original.Id;double width=monitor.Width*scale,height=monitor.Height*scale;
            var title=Kit.Text(monitor.Name.Split(" · ")[0],13);title.FontWeight=W.FontWeights.SemiBold;title.TextAlignment=W.TextAlignment.Center;
            var details=Kit.Text($"{monitor.Device.Replace(@"\\.\","")}\n{monitor.Width} × {monitor.Height}"+(monitor.Hotkey>0?$"\nCtrl+Alt+F{monitor.Hotkey}":""),11,"#C8BED5");details.TextAlignment=W.TextAlignment.Center;details.Margin=new(0,7,0,0);
            var content=Kit.Stack(title,details);content.VerticalAlignment=W.VerticalAlignment.Center;
            var block=new C.Border{Width=Math.Max(1,width-2),Height=Math.Max(1,height-2),CornerRadius=new(8),BorderThickness=new(monitor==selected?2:1),BorderBrush=Kit.Brush(monitor==selected?"#31D1DB":"#685576"),Background=Kit.Brush(local?"#402341":"#242337"),Padding=new(10),Child=content,Cursor=I.Cursors.SizeAll,ClipToBounds=true};
            C.Canvas.SetLeft(block,offsetX+monitor.X*scale+1);C.Canvas.SetTop(block,offsetY+monitor.Y*scale+1);canvas.Children.Add(block);blocks[monitor]=block;
            block.MouseLeftButtonDown+=(_,e)=>{selected=monitor;dragStart=e.GetPosition(canvas);startX=monitor.X;startY=monitor.Y;dragging=true;canvas.CaptureMouse();UpdateSelection();Draw();e.Handled=true;};
        }
    }
}
