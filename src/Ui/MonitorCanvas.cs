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
    readonly HashSet<string> excluded;
    readonly Dictionary<string,MonitorPlacement> available=[];
    readonly HashSet<string> serverPeers=[];
    readonly C.WrapPanel pool=new(){AllowDrop=false,Margin=new(0,10,0,0)};
    readonly Dictionary<MonitorPlacement,C.Border> blocks=[];
    MonitorPlacement? selected;
    bool updating,dragging;
    W.Point dragStart;int startX,startY;
    double scale=1,offsetX,offsetY;
    public List<MonitorPlacement> Result=>monitors.Concat(available.Values).DistinctBy(m=>m.Key).Select(m=>m with{}).ToList();
    public List<string> RemoteOnlyPeers=>remoteOnly.ToList();
    public List<string> ExcludedMonitors=>excluded.ToList();
    public MonitorCanvas(KvmOptions options,List<MonitorPlacement> source,IEnumerable<KvmPeerInfo>? peers=null)
    {
        original=options.Copy();remoteOnly=options.RemoteOnlyPeers.ToHashSet();excluded=options.ExcludedMonitors.ToHashSet();monitors=source.Select(m=>m with{}).ToList();
        foreach(var old in options.Layout.Where(m=>!monitors.Any(n=>n.Key==m.Key)))available[old.Key]=old with{};
        foreach(var peer in peers??[]){if(peer.RemoteViewOnly)serverPeers.Add(peer.Id);foreach(var screen in peer.Screens){string key=peer.Id+"|"+screen.Device;if(monitors.Any(m=>m.Key==key)||available.ContainsKey(key))continue;available[key]=new(){Peer=peer.Id,Name=peer.Name+" · "+screen.Device,Device=screen.Device,Width=screen.Width,Height=screen.Height};}}
        Children.Add(canvas);label.Margin=new(0,16,0,12);Children.Add(label);
        shortcut.ItemTemplate=Kit.ChoiceTemplate();shortcut.Items.Add(new SettingsWindow.Choice("0","No shortcut"));for(int i=1;i<=12;i++)shortcut.Items.Add(new SettingsWindow.Choice(i.ToString(),"Ctrl+Alt+F"+i));
        var tools=new C.WrapPanel();foreach(var item in new W.FrameworkElement[]{Kit.Text("X"),x,Kit.Text("Y"),y,shortcut}){item.Margin=new(0,0,8,8);item.VerticalAlignment=W.VerticalAlignment.Center;tools.Children.Add(item);}Children.Add(tools);
        Children.Add(Kit.Text("Available monitors",13,"#B0A9BC"));Children.Add(pool);RenderPool();
        canvas.AllowDrop=true;canvas.DragOver+=(_,e)=>{e.Effects=e.Data.GetDataPresent("SC.Monitor")?W.DragDropEffects.Move:W.DragDropEffects.None;e.Handled=true;};
        canvas.Drop+=(_,e)=>{if(e.Data.GetData("SC.Monitor") is string key&&available.ContainsKey(key)){var point=e.GetPosition(canvas);AddMonitor(key,(int)((point.X-offsetX)/scale),(int)((point.Y-offsetY)/scale));e.Handled=true;}};
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
        updating=true;label.Text=selected?.Name??UiStrings.T("Select a display to arrange it");x.IsEnabled=y.IsEnabled=shortcut.IsEnabled=selected!=null;
        if(selected!=null){x.Text=selected.X.ToString();y.Text=selected.Y.ToString();shortcut.SelectedIndex=Math.Clamp(selected.Hotkey,0,12);}updating=false;
    }
    void MoveFromInputs(){if(selected==null||updating)return;if(int.TryParse(x.Text,out int px)&&int.TryParse(y.Text,out int py)){selected.X=Math.Clamp(px,-100000,100000);selected.Y=Math.Clamp(py,-100000,100000);Draw();}UpdateSelection();}
    internal void SelectMonitor(string device){selected=monitors.FirstOrDefault(m=>m.Device==device);UpdateSelection();Draw();}
    internal void ExcludeSelected()
    {
        if(selected==null||selected.Peer==original.Id)return;available[selected.Key]=selected;excluded.Add(selected.Key);monitors.Remove(selected);selected=monitors.FirstOrDefault();Draw();UpdateSelection();RenderPool();
    }
    internal void AddMonitor(string key,int px,int py)
    {
        if(!available.TryGetValue(key,out var monitor)||serverPeers.Contains(monitor.Peer))return;
        if(remoteOnly.Remove(monitor.Peer))foreach(var other in available.Values.Where(m=>m.Peer==monitor.Peer&&m.Key!=key))excluded.Add(other.Key);
        available.Remove(key);excluded.Remove(key);monitor.X=Math.Clamp(px,-100000,100000);monitor.Y=Math.Clamp(py,-100000,100000);monitors.Add(monitor);selected=monitor;Snap();UpdateSelection();Draw();RenderPool();
    }
    void RenderPool()
    {
        pool.Children.Clear();
        if(available.Count==0){pool.Children.Add(Kit.Text("All available monitors are in the layout.",12,"#8C829D"));return;}
        foreach(var item in available.Values)
        {
            var text=Kit.Text(item.Name+"\n"+item.Width+" × "+item.Height,12);var block=new C.Border{Child=text,Padding=new(12),Margin=new(0,0,8,8),CornerRadius=new(7),Background=Kit.Brush("#1B1A27"),MaxWidth=290,Cursor=I.Cursors.Hand};
            if(serverPeers.Contains(item.Peer)){text.Text+="\n"+UiStrings.T("Remote desktop only");block.IsEnabled=false;}
            else block.MouseMove+=(_,e)=>{if(e.LeftButton==I.MouseButtonState.Pressed){var data=new W.DataObject("SC.Monitor",item.Key);W.DragDrop.DoDragDrop(block,data,W.DragDropEffects.Move);}};
            pool.Children.Add(block);
        }
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
            var container=new C.Grid();container.Children.Add(content);
            if(!local){var remove=Kit.Button("×",()=>{selected=monitor;ExcludeSelected();});remove.Padding=new(4,0,4,0);remove.MinHeight=20;remove.Width=22;remove.Height=22;remove.HorizontalAlignment=W.HorizontalAlignment.Right;remove.VerticalAlignment=W.VerticalAlignment.Top;remove.ToolTip=UiStrings.T("Remove from layout");remove.PreviewMouseLeftButtonDown+=(_,e)=>e.Handled=true;remove.PreviewMouseLeftButtonUp+=(_,e)=>{selected=monitor;ExcludeSelected();e.Handled=true;};container.Children.Add(remove);}
            var block=new C.Border{Width=Math.Max(1,width-2),Height=Math.Max(1,height-2),CornerRadius=new(8),BorderThickness=new(monitor==selected?2:1),BorderBrush=Kit.Brush(monitor==selected?"#31D1DB":"#685576"),Background=Kit.Brush(local?"#402341":"#242337"),Padding=new(10),Child=container,Cursor=I.Cursors.SizeAll,ClipToBounds=true};
            C.Canvas.SetLeft(block,offsetX+monitor.X*scale+1);C.Canvas.SetTop(block,offsetY+monitor.Y*scale+1);canvas.Children.Add(block);blocks[monitor]=block;
            block.MouseLeftButtonDown+=(_,e)=>{selected=monitor;dragStart=e.GetPosition(canvas);startX=monitor.X;startY=monitor.Y;dragging=true;canvas.CaptureMouse();UpdateSelection();Draw();e.Handled=true;};
        }
    }
}
