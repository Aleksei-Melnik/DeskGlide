using System.Drawing.Drawing2D;

namespace SdrCapture;
sealed class MonitorLayoutEditor:UserControl
{
    sealed class Canvas:Panel {public Canvas(){DoubleBuffered=true;ResizeRedraw=true;}}
    readonly Canvas canvas=new(){Dock=DockStyle.Fill,BackColor=Color.FromArgb(241,245,250)};
    readonly NumericUpDown x=new(){Minimum=-100000,Maximum=100000,Width=90},y=new(){Minimum=-100000,Maximum=100000,Width=90};
    readonly ComboBox hotkey=new(){DropDownStyle=ComboBoxStyle.DropDownList,Width=130};
    readonly Label selectedName=new(){AutoSize=true};
    readonly List<MonitorPlacement> monitors;
    readonly string localId;
    readonly KvmOptions original;
    readonly HashSet<string> remoteOnly;
    readonly Dictionary<MonitorPlacement,RectangleF> boxes=[];
    MonitorPlacement? selected;
    Point dragOrigin,nodeOrigin;
    bool dragging,updating;
    float scale=1;
    public List<MonitorPlacement> Result=>monitors.Concat(original.Layout.Where(m=>remoteOnly.Contains(m.Peer))).DistinctBy(m=>m.Key).Select(m=>m with{}).ToList();
    public List<string> RemoteOnlyPeers=>remoteOnly.ToList();
    public MonitorLayoutEditor(KvmOptions options,List<MonitorPlacement> monitors)
    {
        localId=options.Id;original=options.Copy();remoteOnly=options.RemoteOnlyPeers.ToHashSet();this.monitors=monitors.Select(m=>m with{}).ToList();
        var controls=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=85,Padding=new(0,6,0,0),WrapContents=true};
        selectedName.Width=600;selectedName.AutoSize=false;selectedName.Height=24;controls.Controls.Add(selectedName);controls.SetFlowBreak(selectedName,true);
        controls.Controls.AddRange([new Label{Text="X",AutoSize=true},x,new Label{Text="Y",AutoSize=true},y,hotkey]);
        Button left=new(){Text="Слева",AutoSize=true},right=new(){Text="Справа",AutoSize=true};
        controls.Controls.AddRange([left,right]);Controls.Add(canvas);Controls.Add(controls);
        var remove=new Button{Text="Только KVM",AutoSize=true};controls.Controls.Add(remove);
        remove.Click+=(_,_)=>{if(selected!=null&&selected.Peer!=localId){string peer=selected.Peer;remoteOnly.Add(peer);original.Layout.RemoveAll(m=>m.Peer==peer);original.Layout.AddRange(this.monitors.Where(m=>m.Peer==peer).Select(m=>m with{}));this.monitors.RemoveAll(m=>m.Peer==peer);selected=this.monitors.FirstOrDefault();RefreshSelection();}};
        hotkey.Items.Add("Без клавиши");for(int i=1;i<=12;i++)hotkey.Items.Add("Ctrl+Alt+F"+i);
        x.ValueChanged+=(_,_)=>Change();y.ValueChanged+=(_,_)=>Change();hotkey.SelectedIndexChanged+=(_,_)=>Change();
        left.Click+=(_,_)=>Place(-1);right.Click+=(_,_)=>Place(1);
        canvas.Paint+=PaintCanvas;
        canvas.MouseDown+=(_,e)=>{selected=boxes.LastOrDefault(p=>p.Value.Contains(e.Location)).Key;RefreshSelection();if(selected==null)return;dragging=true;dragOrigin=e.Location;nodeOrigin=new(selected.X,selected.Y);canvas.Capture=true;};
        canvas.MouseMove+=(_,e)=>{if(!dragging||selected==null)return;selected.X=nodeOrigin.X+(int)((e.X-dragOrigin.X)/scale);selected.Y=nodeOrigin.Y+(int)((e.Y-dragOrigin.Y)/scale);RefreshSelection();};
        canvas.MouseUp+=(_,_)=>{if(dragging){dragging=false;canvas.Capture=false;Snap();RefreshSelection();}};
        selected=this.monitors.FirstOrDefault();RefreshSelection();
    }
    void Place(int direction)
    {
        var center=monitors.FirstOrDefault(m=>m.Peer==localId&&KvmScreen.Local().Any(s=>s.Device==m.Device&&s.Primary))??monitors.FirstOrDefault(m=>m.Peer==localId);
        if(selected==null||center==null||selected==center)return;
        selected.X=direction<0?center.X-selected.Width:center.X+center.Width;selected.Y=center.Y;RefreshSelection();
    }
    void Change(){if(updating||selected==null)return;selected.X=(int)x.Value;selected.Y=(int)y.Value;selected.Hotkey=Math.Max(0,hotkey.SelectedIndex);canvas.Invalidate();}
    void RefreshSelection()
    {
        updating=true;
        selectedName.Text=selected?.Name??"Выберите монитор";
        x.Enabled=y.Enabled=hotkey.Enabled=selected!=null;
        if(selected!=null){x.Value=Math.Clamp(selected.X,-100000,100000);y.Value=Math.Clamp(selected.Y,-100000,100000);hotkey.SelectedIndex=Math.Clamp(selected.Hotkey,0,12);}
        updating=false;canvas.Invalidate();
    }
    void Snap()
    {
        if(selected==null)return;
        int tolerance=(int)(18/Math.Max(scale,.001f));
        foreach(var other in monitors.Where(m=>m!=selected))
        {
            if(Math.Abs(selected.X-(other.X+other.Width))<tolerance)selected.X=other.X+other.Width;
            if(Math.Abs(selected.X+selected.Width-other.X)<tolerance)selected.X=other.X-selected.Width;
            if(Math.Abs(selected.Y-other.Y)<tolerance)selected.Y=other.Y;
            if(Math.Abs(selected.Y-(other.Y+other.Height))<tolerance)selected.Y=other.Y+other.Height;
            if(Math.Abs(selected.Y+selected.Height-other.Y)<tolerance)selected.Y=other.Y-selected.Height;
        }
    }
    void PaintCanvas(object? sender,PaintEventArgs e)
    {
        e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;boxes.Clear();
        if(monitors.Count==0)return;
        int minX=monitors.Min(m=>m.X),minY=monitors.Min(m=>m.Y),maxX=monitors.Max(m=>m.X+m.Width),maxY=monitors.Max(m=>m.Y+m.Height);
        if(!dragging)scale=Math.Min(Math.Max(1,canvas.Width-50)/(float)Math.Max(1,maxX-minX),Math.Max(1,canvas.Height-60)/(float)Math.Max(1,maxY-minY));
        float offsetX=(canvas.Width-(maxX-minX)*scale)/2-minX*scale,offsetY=(canvas.Height-(maxY-minY)*scale)/2-minY*scale;
        foreach(var monitor in monitors)
        {
            var rect=new RectangleF(offsetX+monitor.X*scale,offsetY+monitor.Y*scale,monitor.Width*scale,monitor.Height*scale);
            boxes[monitor]=rect;
            using var brush=new SolidBrush(monitor.Peer==localId?Color.FromArgb(48,105,179):Color.FromArgb(73,86,110));
            e.Graphics.FillRectangle(brush,rect);
            using var border=new Pen(monitor==selected?Color.FromArgb(31,174,231):Color.White,monitor==selected?4:2);
            e.Graphics.DrawRectangle(border,rect.X+1,rect.Y+1,Math.Max(1,rect.Width-2),Math.Max(1,rect.Height-2));
            string name=monitor.Name.Split(" · ")[0];
            var textRect=Rectangle.Round(rect);textRect.Inflate(-8,-6);
            TextRenderer.DrawText(e.Graphics,$"{name}\n{monitor.Device.Replace(@"\\.\","")}\n{monitor.Width} × {monitor.Height}"+(monitor.Hotkey>0?$"\nCtrl+Alt+F{monitor.Hotkey}":""),Font,textRect,Color.White,TextFormatFlags.HorizontalCenter|TextFormatFlags.VerticalCenter|TextFormatFlags.WordBreak|TextFormatFlags.EndEllipsis);
        }
    }
}
