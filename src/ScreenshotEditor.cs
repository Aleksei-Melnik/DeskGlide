using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SdrCapture;

enum ScreenshotTool {Select,Pen,Line,Arrow,Rectangle,Highlight,Text,Redact}
sealed record ScreenshotMark(ScreenshotTool Tool,Color Color,float Width,IReadOnlyList<Point> Points,string Text="");

sealed class ScreenshotDocument(ScreenshotFrame frame)
{
    readonly List<ScreenshotMark> marks=[];
    readonly Stack<ScreenshotMark> undone=[];
    internal Rectangle Selection {get;set;}
    internal bool CanUndo=>marks.Count>0;
    internal bool CanRedo=>undone.Count>0;
    internal void Add(ScreenshotMark mark){marks.Add(mark);undone.Clear();}
    internal void Undo(){if(marks.Count>0){undone.Push(marks[^1]);marks.RemoveAt(marks.Count-1);}}
    internal void Redo(){if(undone.TryPop(out var mark))marks.Add(mark);}
    internal static Rectangle Between(Point a,Point b)=>Rectangle.FromLTRB(Math.Min(a.X,b.X),Math.Min(a.Y,b.Y),Math.Max(a.X,b.X),Math.Max(a.Y,b.Y));
    internal static Rectangle Clamp(Rectangle r,Size size)
    {
        int width=Math.Clamp(r.Width,1,size.Width),height=Math.Clamp(r.Height,1,size.Height);
        return new(Math.Clamp(r.X,0,size.Width-width),Math.Clamp(r.Y,0,size.Height-height),width,height);
    }
    internal static void Draw(Graphics g,ScreenshotMark mark)
    {
        if(mark.Points.Count==0)return;
        g.SmoothingMode=SmoothingMode.AntiAlias;
        var first=mark.Points[0];var last=mark.Points[^1];var rect=Between(first,last);
        using var pen=new Pen(mark.Color,mark.Width){StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round};
        switch(mark.Tool)
        {
            case ScreenshotTool.Pen:
                if(mark.Points.Count>1)
                {if(mark.Points is List<Point> list)g.DrawLines(pen,CollectionsMarshal.AsSpan(list));else g.DrawLines(pen,mark.Points as Point[]??mark.Points.ToArray());}
                else{using var dot=new SolidBrush(mark.Color);g.FillEllipse(dot,first.X-mark.Width/2,first.Y-mark.Width/2,mark.Width,mark.Width);}break;
            case ScreenshotTool.Line:g.DrawLine(pen,first,last);break;
            case ScreenshotTool.Arrow:
                using(var arrow=new AdjustableArrowCap(4,5,true)){pen.CustomEndCap=arrow;g.DrawLine(pen,first,last);}break;
            case ScreenshotTool.Rectangle:if(rect.Width>0&&rect.Height>0)g.DrawRectangle(pen,rect);break;
            case ScreenshotTool.Highlight:
                using(var fill=new SolidBrush(Color.FromArgb(85,mark.Color)))g.FillRectangle(fill,rect);break;
            case ScreenshotTool.Redact:
                // Opaque pixels are flattened into the exported PNG. No hidden
                // original image, reversible blur or annotation layer is saved.
                using(var fill=new SolidBrush(Color.Black))g.FillRectangle(fill,rect);break;
            case ScreenshotTool.Text:
                using(var font=new Font("Segoe UI",Math.Max(14,mark.Width*6),FontStyle.Bold,GraphicsUnit.Pixel))
                using(var fill=new SolidBrush(mark.Color))g.DrawString(mark.Text,font,fill,first,StringFormat.GenericTypographic);break;
        }
    }
    internal void Paint(Graphics g,ScreenshotMark? pending=null)
    {
        foreach(var mark in marks)Draw(g,mark);
        if(pending!=null)Draw(g,pending);
    }
    internal Bitmap Render()
    {
        var image=frame.Crop(Selection);
        try
        {
            using var g=Graphics.FromImage(image);g.TranslateTransform(-Selection.X,-Selection.Y);Paint(g);return image;
        }
        catch{image.Dispose();throw;}
    }
}

// One stationary PerMonitorV2 HWND uses physical pixels across the frozen virtual
// desktop. AutoScale is disabled: a crop must never resample image pixels at 125%.
sealed class ScreenshotEditor:Form
{
    readonly ScreenshotFrame frame;
    readonly ScreenshotDocument document;
    readonly ScreenshotOptions options;
    readonly Action<string> saved;
    readonly FlowLayoutPanel tools=new(){BackColor=Color.FromArgb(24,22,34),Padding=new(4),WrapContents=true};
    readonly FlowLayoutPanel actions=new(){BackColor=Color.FromArgb(24,22,34),Padding=new(4),FlowDirection=FlowDirection.TopDown,WrapContents=false};
    readonly Color barColor=Color.FromArgb(24,22,34);
    readonly ToolTip tips=new();
    readonly ContextMenuStrip more=new();
    readonly Dictionary<ScreenshotTool,ScreenshotButton> toolButtons=[];
    string errorMessage="";
    readonly List<Point> points=[];
    float scale=1;
    Font? ownedFont;
    Point mousePoint;
    Region? paintDamage;
    internal int PaintCount {get;private set;}
    ScreenshotTool tool;
    Color ink=Color.FromArgb(244,67,116);
    float stroke=3;
    Point anchor;
    Rectangle priorSelection;
    int hit;
    bool dragging;

    bool dialogActive;
    bool menuActive;
    ScreenshotMark? pending;
    TextBox? textInput;
    ScreenshotButton? undoButton,colorButton;
    internal Action<Rectangle>? DamageObserved;
    internal bool SyntheticPointer;
    internal ScreenshotDocument Document=>document;
    internal ScreenshotEditor(ScreenshotFrame frame,ScreenshotOptions options,Action<string> saved)
    {
        this.frame=frame;this.options=options;this.saved=saved;document=new(frame);
        AutoScaleMode=AutoScaleMode.None;FormBorderStyle=FormBorderStyle.None;StartPosition=FormStartPosition.Manual;
        Bounds=frame.Bounds;TopMost=true;ShowInTaskbar=false;KeyPreview=true;DoubleBuffered=true;
        SetStyle(ControlStyles.Opaque|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer|ControlStyles.UserPaint,true);
        Text="DeskGlide · "+UiStrings.T("Screenshot");Cursor=Cursors.Cross;
        BuildToolbar();
        ApplyMetrics(DeviceDpi);
        Deactivate+=(_,_)=>{if(!dialogActive&&!menuActive)Close();};
    }
    int S(int value)=>(int)Math.Round(value*scale);
    ScreenshotButton Action(FlowLayoutPanel row,string glyph,string label,System.Action action)
    {
        var button=new ScreenshotButton(glyph){Size=new(S(22),S(22)),Margin=new(1),TabStop=true,ForeColor=Color.FromArgb(237,233,245),BackColor=barColor,AccessibleName=UiStrings.T(label),Tag=glyph};
        button.Click+=(_,_)=>Run(action);tips.SetToolTip(button,UiStrings.T(label));row.Controls.Add(button);return button;
    }
    void BuildToolbar()
    {
        tools.Visible=actions.Visible=false;tools.Padding=actions.Padding=new(S(4));Controls.Add(tools);Controls.Add(actions);
        foreach(var pair in new[]{(ScreenshotTool.Select,"Select"),(ScreenshotTool.Pen,"Pen"),(ScreenshotTool.Line,"Line"),(ScreenshotTool.Arrow,"Arrow"),(ScreenshotTool.Rectangle,"Rectangle"),(ScreenshotTool.Highlight,"Highlight"),(ScreenshotTool.Text,"Text")})
        {
            var selected=pair.Item1;toolButtons[selected]=Action(tools,pair.Item2,pair.Item2,()=>SetTool(selected));
        }
        colorButton=Action(tools,"Color","Choose annotation color",()=>{CommitText();using var dialog=new ColorDialog{Color=ink,FullOpen=true};if(ShowDialog(dialog)==DialogResult.OK){ink=dialog.Color;colorButton!.Ink=ink;colorButton.Invalidate();}});colorButton.Ink=ink;
        undoButton=Action(tools,"Undo","Undo",()=>{CommitText();document.Undo();RefreshActions();});
        Action(actions,"Copy","Copy",Copy);Action(actions,"Save","Save PNG",()=>Save(false));
        var extra=Action(actions,"More","More tools",()=>{});
        extra.Click+=(_,_)=>{menuActive=true;more.Show(extra,new Point(extra.Width,0));};
        Action(actions,"Close","Close",Close);
        TrayMenuStyle.Apply(more);more.Closed+=(_,_)=>
        {
            menuActive=false;
            if(!IsDisposed&&!Disposing&&IsHandleCreated)BeginInvoke(()=>{if(!IsDisposed&&!dialogActive&&!ContainsFocus)Close();});
        };
        void Item(string glyph,string label,System.Action action)
        {var item=new ToolStripMenuItem(UiStrings.T(label)){Tag=glyph};item.Click+=(_,_)=>Run(action);more.Items.Add(item);}
        Item("Redact","Redact",()=>SetTool(ScreenshotTool.Redact));
        var widths=new ToolStripMenuItem(UiStrings.T("Stroke width"));
        foreach(int pixels in new[]{1,2,3,5,8,12})
        {var item=new ToolStripMenuItem(UiStrings.F("Stroke width: {0} px",pixels)){Tag=pixels};item.Click+=(_,_)=>{stroke=pixels;};widths.DropDownItems.Add(item);}
        widths.DropDownOpening+=(_,_)=>{foreach(ToolStripMenuItem item in widths.DropDownItems)item.Checked=(int)item.Tag! ==(int)stroke;};
        more.Items.Add(widths);Item("Redo","Redo",()=>{document.Redo();RefreshActions();});more.Items.Add(new ToolStripSeparator());
        Item("SaveAs","Save as…",()=>Save(true));Item("Print","Print",Print);
        SetTool(ScreenshotTool.Select);
    }
    void Run(System.Action action)
    {try{action();Invalidate();}catch(Exception e){errorMessage=UiStrings.T("Screenshot failed: ")+e.Message;Invalidate();}}
    DialogResult ShowDialog(CommonDialog dialog)
    {dialogActive=true;try{return dialog.ShowDialog(this);}finally{dialogActive=false;}}
    void SetTool(ScreenshotTool value)
    {
        CommitText();tool=value;Cursor=tool==ScreenshotTool.Select&&document.Selection.Width>0?Cursors.SizeAll:Cursors.Cross;
        foreach(var pair in toolButtons){pair.Value.Selected=pair.Key==tool;pair.Value.Invalidate();}
        RefreshActions();
    }
    void RefreshActions()
    {
        if(undoButton!=null){undoButton.Enabled=document.CanUndo;undoButton.Invalidate();}
        foreach(var item in more.Items.OfType<ToolStripMenuItem>())if((string?)item.Tag=="Redo")item.Enabled=document.CanRedo;
        errorMessage="";Invalidate();
    }
    void ApplyMetrics(int dpi)
    {
        // This is a physical-pixel canvas, not a DPI-scaled settings window.
        // Cap toolbar scaling and reapply explicit sizes after a DPI transition
        // so WinForms cannot scale our already-scaled buttons a second time.
        scale=Math.Clamp(dpi/96f,1,1.15f);
        Font? previous=null;
        if(ownedFont==null||Math.Abs(ownedFont.Size-11*scale)>.01f)
        {previous=ownedFont;ownedFont=new("Segoe UI",11*scale,FontStyle.Regular,GraphicsUnit.Pixel);}
        Font=tools.Font=actions.Font=ownedFont;
        foreach(var bar in new[]{tools,actions})
        {bar.SuspendLayout();bar.Padding=new(S(2));foreach(Control button in bar.Controls){button.Size=new(S(22),S(22));button.Margin=new(1);}bar.ResumeLayout(true);}
        previous?.Dispose();PositionToolbar();
    }
    void PositionToolbar()
    {
        var r=document.Selection;bool selected=r.Width>0&&r.Height>0;tools.Visible=actions.Visible=selected;if(!selected)return;
        var center=new Point(frame.Bounds.X+r.Left+r.Width/2,frame.Bounds.Y+r.Top+r.Height/2);
        var screen=Screen.FromPoint(center).Bounds;screen.Offset(-frame.Bounds.X,-frame.Bounds.Y);
        screen=Rectangle.Intersect(screen,new(Point.Empty,frame.Bounds.Size));
        int cell=S(22)+2,padding=2*S(2),gap=S(5);
        actions.Size=new(cell+padding,actions.Controls.Count*cell+padding);
        int columns=Math.Min(tools.Controls.Count,Math.Max(1,(screen.Width-actions.Width-3*gap-padding)/cell));
        int rows=(tools.Controls.Count+columns-1)/columns;tools.Size=new(columns*cell+padding,rows*cell+padding);
        var place=ScreenshotBarLayout.Place(r,screen,tools.Size,actions.Size,gap);
        tools.Location=place.Tools;actions.Location=place.Actions;tools.BringToFront();actions.BringToFront();RefreshActions();
    }
    protected override void OnShown(EventArgs e){base.OnShown(e);Bounds=frame.Bounds;mousePoint=PointToClient(Cursor.Position);Activate();Focus();}
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {e.Cancel=true;base.OnDpiChanged(e);Bounds=frame.Bounds;ApplyMetrics(e.DeviceDpiNew);}
    protected override void OnPaintBackground(PaintEventArgs e){} // Every invalid pixel is restored from an opaque frozen bitmap.
    Rectangle LabelBounds(Rectangle r)
    {
        string size=$"{r.Width} × {r.Height}";
        var measured=TextRenderer.MeasureText(size,Font);
        var label=new Rectangle(r.Left,Math.Max(0,r.Top-(int)Math.Ceiling(measured.Height)-S(9)),(int)Math.Ceiling(measured.Width)+S(14),(int)Math.Ceiling(measured.Height)+S(6));
        if(tools.Visible&&label.IntersectsWith(tools.Bounds))label.Y=Math.Min(frame.Bounds.Height-label.Height,r.Bottom+S(7));
        return label;
    }
    Rectangle HintBounds()
    {
        var size=TextRenderer.MeasureText(UiStrings.T("Drag to select an area"),Font);
        var absolute=new Point(frame.Bounds.X+mousePoint.X,frame.Bounds.Y+mousePoint.Y);
        var screen=Screen.FromPoint(absolute).Bounds;screen.Offset(-frame.Bounds.X,-frame.Bounds.Y);screen.Intersect(new(Point.Empty,frame.Bounds.Size));
        int width=size.Width+S(20),height=size.Height+S(12);
        return new(Math.Clamp(mousePoint.X+S(18),screen.Left,Math.Max(screen.Left,screen.Right-width)),Math.Clamp(mousePoint.Y+S(22),screen.Top,Math.Max(screen.Top,screen.Bottom-height)),width,height);
    }
    void InvalidateArea(Rectangle area)
    {area.Intersect(ClientRectangle);if(area.Width>0&&area.Height>0){DamageObserved?.Invoke(area);Invalidate(area);}}
    void InvalidateSelection(Rectangle before,Rectangle after)
    {
        // The overlapping interior is identical frozen content. Restore only
        // newly exposed/shaded strips and the two old/new outlines and labels.
        Difference(before,after);Difference(after,before);Border(before);Border(after);
        InvalidateArea(LabelBounds(before));InvalidateArea(LabelBounds(after));
        void Difference(Rectangle outer,Rectangle inner)
        {
            if(outer.Width<=0||outer.Height<=0)return;var overlap=Rectangle.Intersect(outer,inner);
            if(overlap.Width<=0||overlap.Height<=0){InvalidateArea(outer);return;}
            InvalidateArea(Rectangle.FromLTRB(outer.Left,outer.Top,outer.Right,overlap.Top));
            InvalidateArea(Rectangle.FromLTRB(outer.Left,overlap.Bottom,outer.Right,outer.Bottom));
            InvalidateArea(Rectangle.FromLTRB(outer.Left,overlap.Top,overlap.Left,overlap.Bottom));
            InvalidateArea(Rectangle.FromLTRB(overlap.Right,overlap.Top,outer.Right,overlap.Bottom));
        }
        void Border(Rectangle r)
        {
            if(r.Width<=0||r.Height<=0)return;int edge=S(4);
            InvalidateArea(new(r.Left-edge,r.Top-edge,r.Width+2*edge,2*edge));
            InvalidateArea(new(r.Left-edge,r.Bottom-edge,r.Width+2*edge,2*edge));
            InvalidateArea(new(r.Left-edge,r.Top-edge,2*edge,r.Height+2*edge));
            InvalidateArea(new(r.Right-edge,r.Top-edge,2*edge,r.Height+2*edge));
        }
    }
    protected override void OnPaint(PaintEventArgs e)
    {
        var g=e.Graphics;var dirty=Rectangle.Intersect(e.ClipRectangle,ClientRectangle);if(dirty.IsEmpty)return;
        var paintState=g.Save();g.SetClip(dirty,CombineMode.Intersect);if(paintDamage!=null)g.SetClip(paintDamage,CombineMode.Intersect);
        var r=document.Selection;
        var backdrop=g.Save();g.CompositingMode=CompositingMode.SourceCopy;g.InterpolationMode=InterpolationMode.NearestNeighbor;g.PixelOffsetMode=PixelOffsetMode.None;
        using(var clip=g.Clip)using(var identity=new Matrix())foreach(var scan in clip.GetRegionScans(identity))
        {
            var patch=Rectangle.Intersect(dirty,Rectangle.Ceiling(scan));if(patch.Width<=0||patch.Height<=0)continue;
            g.DrawImage(frame.ShadedPreview,patch,patch,GraphicsUnit.Pixel);
            var exposed=Rectangle.Intersect(patch,r);
            if(exposed.Width>0&&exposed.Height>0)g.DrawImage(frame.Preview,exposed,exposed,GraphicsUnit.Pixel);
        }
        g.Restore(backdrop);
        if(r.Width>0&&r.Height>0)
        {
            var state=g.Save();g.SetClip(r,CombineMode.Intersect);document.Paint(g,pending);g.Restore(state);
            using var border=new Pen(Color.FromArgb(49,209,219),1);g.DrawRectangle(border,r.X,r.Y,r.Width-1,r.Height-1);
            foreach(var point in Handles(r))g.FillRectangle(Brushes.White,point.X-S(3),point.Y-S(3),S(6),S(6));
            string size=$"{r.Width} × {r.Height}";var label=LabelBounds(r);
            using var fill=new SolidBrush(barColor);g.FillRectangle(fill,label);g.DrawString(size,Font,Brushes.White,label.Left+S(7),label.Top+S(3));
        }
        else
        {
            string hint=UiStrings.T("Drag to select an area");var box=HintBounds();
            using var fill=new SolidBrush(barColor);g.FillRectangle(fill,box);g.DrawString(hint,Font,Brushes.White,box.Left+S(10),box.Top+S(6));
        }
        if(errorMessage.Length>0){using var fill=new SolidBrush(barColor);var size=g.MeasureString(errorMessage,Font);g.FillRectangle(fill,8,frame.Bounds.Height-size.Height-20,size.Width+20,size.Height+12);g.DrawString(errorMessage,Font,Brushes.LightPink,18,frame.Bounds.Height-size.Height-14);}
        g.Restore(paintState);PaintCount++;base.OnPaint(e);
    }
    static Point[] Handles(Rectangle r)=>[new(r.Left,r.Top),new(r.Right,r.Top),new(r.Right,r.Bottom),new(r.Left,r.Bottom),new(r.Left+r.Width/2,r.Top),new(r.Right,r.Top+r.Height/2),new(r.Left+r.Width/2,r.Bottom),new(r.Left,r.Top+r.Height/2)];
    int Hit(Point point)
    {
        var r=document.Selection;if(r.Width<=0||r.Height<=0)return 0;
        var handles=Handles(r);for(int i=0;i<handles.Length;i++)if(Math.Abs(handles[i].X-point.X)<=S(7)&&Math.Abs(handles[i].Y-point.Y)<=S(7))return i+1;
        return r.Contains(point)?9:0;
    }
    Point Limit(Point p)=>new(Math.Clamp(p.X,0,frame.Bounds.Width),Math.Clamp(p.Y,0,frame.Bounds.Height));
    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);if(e.Button==MouseButtons.Right){SetTool(ScreenshotTool.Select);return;}if(e.Button!=MouseButtons.Left)return;
        CommitText();if(!SyntheticPointer)Focus();anchor=Limit(e.Location);priorSelection=document.Selection;hit=Hit(anchor);
        if(hit is >=1 and <=8){tools.Visible=actions.Visible=false;dragging=true;if(!SyntheticPointer)Capture=true;Invalidate();return;}
        if(tool==ScreenshotTool.Text&&document.Selection.Contains(anchor)){BeginText(anchor);return;}
        if(tool!=ScreenshotTool.Select&&document.Selection.Contains(anchor))
        {points.Clear();points.Add(anchor);pending=new(tool,ink,stroke,points);}
        else {SetTool(ScreenshotTool.Select);tools.Visible=actions.Visible=false;}
        dragging=true;if(!SyntheticPointer)Capture=true;Invalidate();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);var priorHint=HintBounds();mousePoint=e.Location;
        if(!dragging){if(document.Selection.IsEmpty){InvalidateArea(priorHint);InvalidateArea(HintBounds());}Cursor=Hit(e.Location) switch{1 or 3=>Cursors.SizeNWSE,2 or 4=>Cursors.SizeNESW,5 or 7=>Cursors.SizeNS,6 or 8=>Cursors.SizeWE,9 when tool==ScreenshotTool.Select=>Cursors.SizeAll,_=>Cursors.Cross};return;}
        var p=Limit(e.Location);
        var before=document.Selection;Rectangle inkDirty=default;
        if(pending!=null)
        {
            if(pending.Tool==ScreenshotTool.Pen){inkDirty=ScreenshotDocument.Between(points[^1],p);if(points.Count<65536&&points[^1]!=p)points.Add(p);}
            else{inkDirty=Rectangle.Union(ScreenshotDocument.Between(anchor,pending.Points[^1]),ScreenshotDocument.Between(anchor,p));pending=pending with{Points=[anchor,p]};}
            inkDirty=Rectangle.Inflate(inkDirty,(int)Math.Ceiling(stroke*7)+3,(int)Math.Ceiling(stroke*7)+3);
        }
        else if(hit==9)document.Selection=ScreenshotDocument.Clamp(new(priorSelection.X+p.X-anchor.X,priorSelection.Y+p.Y-anchor.Y,priorSelection.Width,priorSelection.Height),frame.Bounds.Size);
        else if(hit>0)
        {
            int l=priorSelection.Left,t=priorSelection.Top,r=priorSelection.Right,b=priorSelection.Bottom;
            if(hit is 1 or 4 or 8)l=p.X;if(hit is 2 or 3 or 6)r=p.X;if(hit is 1 or 2 or 5)t=p.Y;if(hit is 3 or 4 or 7)b=p.Y;
            document.Selection=ScreenshotDocument.Between(new(l,t),new(r,b));
        }
        else document.Selection=ScreenshotDocument.Between(anchor,p);
        if(pending!=null)InvalidateArea(inkDirty);else{InvalidateArea(priorHint);InvalidateSelection(before,document.Selection);}
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);if(!dragging||e.Button!=MouseButtons.Left)return;
        OnMouseMove(e);dragging=false;if(!SyntheticPointer)Capture=false;
        if(pending!=null){document.Add(pending with{Points=pending.Points.ToArray()});pending=null;}
        PositionToolbar();
    }
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if(SyntheticPointer||!dragging||Capture)return;
        dragging=false;pending=null;document.Selection=priorSelection;PositionToolbar();
    }
    void BeginText(Point p)
    {
        textInput=new(){Multiline=false,ForeColor=ink,BackColor=Color.FromArgb(24,22,34),BorderStyle=BorderStyle.FixedSingle,Font=new Font("Segoe UI",Math.Max(14,stroke*6),FontStyle.Bold,GraphicsUnit.Pixel),Location=p,Size=new(Math.Min(S(340),document.Selection.Right-p.X),Math.Min(S(110),document.Selection.Bottom-p.Y))};
        Controls.Add(textInput);textInput.BringToFront();textInput.Focus();
    }
    void CommitText()
    {
        if(textInput==null)return;var box=textInput;textInput=null;
        if(!string.IsNullOrWhiteSpace(box.Text))document.Add(new(ScreenshotTool.Text,ink,stroke,[box.Location],box.Text));
        Controls.Remove(box);box.Font.Dispose();box.Dispose();RefreshActions();
    }
    protected override bool ProcessCmdKey(ref Message msg,Keys keyData)
    {
        if(keyData==Keys.Escape){if(textInput!=null){var box=textInput;textInput=null;Controls.Remove(box);box.Font.Dispose();box.Dispose();Focus();}else Close();return true;}
        if(textInput!=null&&keyData==Keys.Enter){CommitText();Focus();return true;}
        return base.ProcessCmdKey(ref msg,keyData);
    }
    void Copy()
    {
        CommitText();using var image=document.Render();var data=ClipboardData(image);
        Clipboard.SetDataObject(data,true,5,80);Close();
    }
    internal static DataObject ClipboardData(Bitmap image)
    {
        using var png=new MemoryStream();ScreenshotFiles.SavePng(png,image);
        var data=new DataObject();data.SetData(DataFormats.Bitmap,true,image);data.SetData("PNG",false,new MemoryStream(png.ToArray()));return data;
    }
    void Save(bool choose)
    {
        CommitText();using var image=document.Render();string path;
        if(choose)
        {
            using var dialog=new SaveFileDialog{Title=UiStrings.T("Save screenshot"),Filter="PNG image|*.png",DefaultExt="png",AddExtension=true,InitialDirectory=Directory.Exists(options.Folder)?options.Folder:Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),FileName=$"Screenshot-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}.png"};
            if(ShowDialog(dialog)!=DialogResult.OK)return;path=dialog.FileName;ScreenshotFiles.Write(path,s=>ScreenshotFiles.SavePng(s,image));
        }
        else path=ScreenshotFiles.Save(options.Folder,".png",s=>ScreenshotFiles.SavePng(s,image));
        saved(path);Close();
    }
    void Print()
    {
        CommitText();using var image=document.Render();using var job=new System.Drawing.Printing.PrintDocument();
        job.DocumentName="DeskGlide screenshot";job.PrintPage+=(_,e)=>
        {
            float ratio=Math.Min(e.MarginBounds.Width/(float)image.Width,e.MarginBounds.Height/(float)image.Height);
            e.Graphics!.DrawImage(image,e.MarginBounds.Left,e.MarginBounds.Top,image.Width*ratio,image.Height*ratio);
        };
        using var dialog=new PrintDialog{Document=job,UseEXDialog=true};if(ShowDialog(dialog)==DialogResult.OK)job.Print();
    }
    internal void TestDrag(Point from,Point to)
    {OnMouseDown(new(MouseButtons.Left,1,from.X,from.Y,0));OnMouseMove(new(MouseButtons.Left,0,to.X,to.Y,0));OnMouseUp(new(MouseButtons.Left,1,to.X,to.Y,0));}
    internal void TestTool(ScreenshotTool value)=>SetTool(value);
    internal void TestPointerDown(Point p)=>OnMouseDown(new(MouseButtons.Left,1,p.X,p.Y,0));
    internal void TestPointerMove(Point p)=>OnMouseMove(new(MouseButtons.Left,0,p.X,p.Y,0));
    internal void TestPointerUp(Point p)=>OnMouseUp(new(MouseButtons.Left,1,p.X,p.Y,0));
    internal void TestDpi(int dpi)=>ApplyMetrics(dpi);
    internal Size LargestButton=>tools.Controls.Cast<Control>().Concat(actions.Controls.Cast<Control>()).Select(c=>c.Size).MaxBy(s=>s.Width);
    internal bool MoreVisible=>more.Visible;
    internal void CloseMore()=>more.Close();
    internal void PaintCanvas(Bitmap target,Rectangle clip)
    {using var g=Graphics.FromImage(target);OnPaint(new PaintEventArgs(g,clip));}
    internal bool TestKey(Keys key){var message=new Message();return ProcessCmdKey(ref message,key);}
    internal void TestAction(string glyph)
    {
        var button=actions.Controls.Cast<Control>().Concat(tools.Controls.Cast<Control>()).OfType<ScreenshotButton>().SingleOrDefault(b=>(string?)b.Tag==glyph);
        if(button!=null)button.PerformClick();else more.Items.OfType<ToolStripMenuItem>().Single(i=>(string?)i.Tag==glyph).PerformClick();
    }
    internal void CheckToolbar()
    {
        var area=new Rectangle(Point.Empty,frame.Bounds.Size);
        if(!area.Contains(tools.Bounds)||!area.Contains(actions.Bounds)||tools.Bounds.IntersectsWith(actions.Bounds))throw new Exception("Screenshot bars are outside the desktop or overlap.");
        foreach(var bar in new[]{tools,actions})foreach(Control child in bar.Controls)
            if(!bar.ClientRectangle.Contains(child.Bounds))throw new Exception("Screenshot action clipped by toolbar: "+child.AccessibleName);
    }
    protected override void WndProc(ref Message m)
    {
        // Geometry or HDR topology changes invalidate the frozen physical canvas.
        if(m.Msg==0x7e&&Visible){Close();return;}
        if(m.Msg==0xf)
        {
            // rcPaint is only the bounding box. Preserve the actual Win32
            // update region so opposite thin crop edges do not become a full
            // desktop copy in WinForms' double buffer.
            nint region=CreateRectRgn(0,0,0,0);var previous=paintDamage;
            try{paintDamage=GetUpdateRgn(Handle,region,false)>1?Region.FromHrgn(region):null;base.WndProc(ref m);}
            finally{paintDamage?.Dispose();paintDamage=previous;DeleteObject(region);}
            return;
        }
        base.WndProc(ref m);
    }
    [DllImport("gdi32.dll")] static extern nint CreateRectRgn(int left,int top,int right,int bottom);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint value);
    [DllImport("user32.dll")] static extern int GetUpdateRgn(nint window,nint region,bool erase);
    protected override void Dispose(bool disposing)
    {
        if(disposing){tips.Dispose();more.Dispose();textInput?.Font.Dispose();}
        base.Dispose(disposing);if(disposing)ownedFont?.Dispose();
    }
    internal Bitmap RenderDiagnostic(Rectangle selection)
    {
        document.Selection=selection;PositionToolbar();var bitmap=new Bitmap(frame.Bounds.Width,frame.Bounds.Height);
        using(var g=Graphics.FromImage(bitmap))OnPaint(new PaintEventArgs(g,new(Point.Empty,bitmap.Size)));
        // DrawToBitmap skips children of an invisible top-level window. Render
        // the detached child control separately; never Show/activate a test form.
        foreach(var bar in new[]{tools,actions})
        {
            var location=bar.Location;Controls.Remove(bar);
            try
            {
                bar.Visible=true;using var image=new Bitmap(bar.Width,bar.Height);bar.DrawToBitmap(image,new(Point.Empty,image.Size));
                using var g=Graphics.FromImage(bitmap);g.DrawImageUnscaled(image,location);
            }
            finally{Controls.Add(bar);bar.Location=location;}
        }
        return bitmap;
    }
}
