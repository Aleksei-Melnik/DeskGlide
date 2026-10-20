using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace SdrCapture;

enum ScreenshotTool {Select,Pen,Line,Arrow,Rectangle,Highlight,Text,Redact}
sealed record ScreenshotMark(ScreenshotTool Tool,Color Color,float Width,Point[] Points,string Text="");

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
        if(mark.Points.Length==0)return;
        g.SmoothingMode=SmoothingMode.AntiAlias;
        var first=mark.Points[0];var last=mark.Points[^1];var rect=Between(first,last);
        using var pen=new Pen(mark.Color,mark.Width){StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round};
        switch(mark.Tool)
        {
            case ScreenshotTool.Pen:
                if(mark.Points.Length>1)g.DrawLines(pen,mark.Points);
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
    readonly FlowLayoutPanel tools=new(){AutoSize=true,WrapContents=false};
    readonly FlowLayoutPanel actions=new(){AutoSize=true,WrapContents=false};
    readonly Panel toolbar=new(){BackColor=Color.FromArgb(24,22,34)};
    readonly ToolTip tips=new();
    readonly Dictionary<ScreenshotTool,Button> toolButtons=[];
    readonly Label status=new(){AutoSize=true,ForeColor=Color.FromArgb(205,200,220)};
    readonly List<Point> points=[];
    readonly float scale;
    ScreenshotTool tool;
    Color ink=Color.FromArgb(244,67,116);
    float stroke=3;
    Point anchor;
    Rectangle priorSelection;
    int hit;
    bool dragging;
    bool copyOnSelect;
    bool dialogActive;
    ScreenshotMark? pending;
    TextBox? textInput;
    Button? undoButton,redoButton,colorButton;
    internal ScreenshotDocument Document=>document;
    internal ScreenshotEditor(ScreenshotFrame frame,ScreenshotOptions options,Action<string> saved)
    {
        this.frame=frame;this.options=options;this.saved=saved;document=new(frame);
        AutoScaleMode=AutoScaleMode.None;FormBorderStyle=FormBorderStyle.None;StartPosition=FormStartPosition.Manual;
        Bounds=frame.Bounds;TopMost=true;ShowInTaskbar=false;KeyPreview=true;DoubleBuffered=true;
        Text="DeskGlide · "+UiStrings.T("Screenshot");Cursor=Cursors.Cross;
        scale=Math.Clamp(DeviceDpi/96f,1,2);
        Font=new("Segoe UI",12*scale,FontStyle.Regular,GraphicsUnit.Pixel);
        toolbar.Font=Font;
        BuildToolbar();
        Deactivate+=(_,_)=>{if(!dialogActive)Close();};
    }
    int S(int value)=>(int)Math.Round(value*scale);
    Button Action(FlowLayoutPanel row,string label,System.Action action,string hint)
    {
        var button=new Button{Text=UiStrings.T(label),AutoSize=true,AutoSizeMode=AutoSizeMode.GrowAndShrink,Height=S(30),FlatStyle=FlatStyle.Flat,ForeColor=Color.White,BackColor=toolbar.BackColor,Padding=new(S(7),S(3),S(7),S(3)),Margin=new(S(2)),TabStop=false};
        button.FlatAppearance.BorderColor=Color.FromArgb(65,57,80);button.Click+=(_,_)=>Run(action);tips.SetToolTip(button,UiStrings.T(hint));row.Controls.Add(button);return button;
    }
    void BuildToolbar()
    {
        toolbar.Visible=false;toolbar.Padding=new(S(10));Controls.Add(toolbar);toolbar.Controls.Add(tools);toolbar.Controls.Add(actions);toolbar.Controls.Add(status);
        foreach(var pair in new[]{(ScreenshotTool.Select,"Select"),(ScreenshotTool.Pen,"Pen"),(ScreenshotTool.Line,"Line"),(ScreenshotTool.Arrow,"Arrow"),(ScreenshotTool.Rectangle,"Rectangle"),(ScreenshotTool.Highlight,"Highlight"),(ScreenshotTool.Text,"Text"),(ScreenshotTool.Redact,"Redact")})
        {
            var selected=pair.Item1;toolButtons[selected]=Action(tools,pair.Item2,()=>SetTool(selected),pair.Item2);
        }
        colorButton=Action(actions,"Color",()=>{CommitText();using var dialog=new ColorDialog{Color=ink,FullOpen=true};if(ShowDialog(dialog)==DialogResult.OK){ink=dialog.Color;colorButton!.ForeColor=ink;}},"Choose annotation color");colorButton.ForeColor=ink;
        Action(actions,"−",()=>stroke=Math.Max(1,stroke-1),"Thinner stroke");Action(actions,"+",()=>stroke=Math.Min(16,stroke+1),"Thicker stroke");
        undoButton=Action(actions,"Undo",()=>{CommitText();document.Undo();RefreshActions();},"Undo · Ctrl+Z");
        redoButton=Action(actions,"Redo",()=>{document.Redo();RefreshActions();},"Redo · Ctrl+Y");
        Action(actions,"Copy",Copy,"Copy · Ctrl+C / Enter");Action(actions,"Save PNG",()=>Save(false),"Save · Ctrl+S");
        Action(actions,"Save as…",()=>Save(true),"Save as · Ctrl+Shift+S");Action(actions,"Print",Print,"Print · Ctrl+P");
        Action(actions,"Close",Close,"Cancel · Escape");
        tools.Location=new(S(10),S(10));tools.PerformLayout();tools.Size=tools.PreferredSize;
        actions.Location=new(S(10),tools.Bottom+S(5));actions.PerformLayout();actions.Size=actions.PreferredSize;
        status.Location=new(S(13),actions.Bottom+S(7));
        toolbar.Size=new(Math.Max(tools.Width,actions.Width)+S(20),status.Bottom+S(12));
        SetTool(ScreenshotTool.Select);
    }
    void Run(System.Action action)
    {try{action();Invalidate();}catch(Exception e){status.Text=UiStrings.T("Screenshot failed: ")+e.Message;status.ForeColor=Color.LightPink;Invalidate();}}
    DialogResult ShowDialog(CommonDialog dialog)
    {dialogActive=true;try{return dialog.ShowDialog(this);}finally{dialogActive=false;}}
    void SetTool(ScreenshotTool value)
    {
        CommitText();tool=value;Cursor=tool==ScreenshotTool.Select?Cursors.SizeAll:Cursors.Cross;
        foreach(var pair in toolButtons)pair.Value.BackColor=pair.Key==tool?Color.FromArgb(99,49,118):toolbar.BackColor;
        RefreshActions();
    }
    void RefreshActions()
    {
        if(undoButton!=null)undoButton.Enabled=document.CanUndo;
        if(redoButton!=null)redoButton.Enabled=document.CanRedo;
        var r=document.Selection;
        status.ForeColor=Color.FromArgb(205,200,220);status.Text=$"{r.Width} × {r.Height} px · PNG · "+UiStrings.T(frame.HasHdr?"HDR → SDR":"SDR")+" · "+UiStrings.T("Enter: copy · Esc: cancel");
        Invalidate();
    }
    void PositionToolbar()
    {
        var r=document.Selection;bool selected=r.Width>0&&r.Height>0;toolbar.Visible=selected;if(!selected)return;
        var center=new Point(frame.Bounds.X+r.Left+r.Width/2,frame.Bounds.Y+r.Top+r.Height/2);
        var screen=Screen.FromPoint(center).Bounds;screen.Offset(-frame.Bounds.X,-frame.Bounds.Y);
        screen=Rectangle.Intersect(screen,new(Point.Empty,frame.Bounds.Size));
        int width=Math.Max(S(200),screen.Width-S(24));
        tools.WrapContents=actions.WrapContents=true;
        tools.MaximumSize=actions.MaximumSize=new(width-S(20),0);
        tools.Size=tools.GetPreferredSize(new(width-S(20),0));
        actions.Location=new(S(10),tools.Bottom+S(5));actions.Size=actions.GetPreferredSize(new(width-S(20),0));
        status.Location=new(S(13),actions.Bottom+S(7));
        toolbar.Size=new(Math.Max(tools.Width,actions.Width)+S(20),status.Bottom+S(12));
        int x=Math.Clamp(r.Right-toolbar.Width,screen.Left,Math.Max(screen.Left,screen.Right-toolbar.Width));
        int y=r.Bottom+S(12);
        if(y+toolbar.Height>screen.Bottom)y=r.Top-toolbar.Height-S(12);
        y=Math.Clamp(y,screen.Top,Math.Max(screen.Top,screen.Bottom-toolbar.Height));
        toolbar.Location=new(x,y);toolbar.BringToFront();RefreshActions();
    }
    protected override void OnShown(EventArgs e){base.OnShown(e);Bounds=frame.Bounds;Activate();Focus();}
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {e.Cancel=true;base.OnDpiChanged(e);Bounds=frame.Bounds;}
    protected override void OnPaint(PaintEventArgs e)
    {
        var g=e.Graphics;g.DrawImageUnscaled(frame.Preview,0,0);
        var r=document.Selection;
        using(var shade=new SolidBrush(Color.FromArgb(145,7,7,15)))
        {
            using var region=new Region(ClientRectangle);if(r.Width>0&&r.Height>0)region.Exclude(r);g.FillRegion(shade,region);
        }
        if(r.Width>0&&r.Height>0)
        {
            var state=g.Save();g.SetClip(r);document.Paint(g,pending);g.Restore(state);
            using var border=new Pen(Color.FromArgb(49,209,219),1);g.DrawRectangle(border,r.X,r.Y,r.Width-1,r.Height-1);
            if(tool==ScreenshotTool.Select)foreach(var point in Handles(r))g.FillRectangle(Brushes.White,point.X-S(3),point.Y-S(3),S(6),S(6));
            string size=$"{r.Width} × {r.Height}";var measured=g.MeasureString(size,Font);
            var label=new RectangleF(r.Left,Math.Max(0,r.Top-measured.Height-S(9)),measured.Width+S(14),measured.Height+S(6));
            using var fill=new SolidBrush(toolbar.BackColor);g.FillRectangle(fill,label);g.DrawString(size,Font,Brushes.White,label.Left+S(7),label.Top+S(3));
        }
        else
        {
            var screen=Screen.FromPoint(Cursor.Position).Bounds;screen.Offset(-frame.Bounds.X,-frame.Bounds.Y);
            var box=new Rectangle(screen.Left+S(28),screen.Top+S(28),S(560),S(90));using var fill=new SolidBrush(toolbar.BackColor);g.FillRectangle(fill,box);
            using var heading=new Font("Segoe UI",S(18),FontStyle.Bold,GraphicsUnit.Pixel);
            g.DrawString("DeskGlide · "+UiStrings.T("Screenshot"),heading,Brushes.White,box.Left+S(18),box.Top+S(13));
            g.DrawString(UiStrings.T("Drag to select · Ctrl+A: full screen · Esc: cancel"),Font,Brushes.LightGray,box.Left+S(18),box.Top+S(51));
        }
        base.OnPaint(e);
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
        CommitText();Focus();anchor=Limit(e.Location);priorSelection=document.Selection;hit=Hit(anchor);
        copyOnSelect=(tool==ScreenshotTool.Select||hit==0)&&(ModifierKeys&Keys.Control)!=0;
        if(tool==ScreenshotTool.Text&&document.Selection.Contains(anchor)){BeginText(anchor);return;}
        if(tool!=ScreenshotTool.Select&&document.Selection.Contains(anchor))
        {points.Clear();points.Add(anchor);pending=new(tool,ink,stroke,points.ToArray());}
        else {tool=ScreenshotTool.Select;toolbar.Visible=false;}
        dragging=true;Capture=true;Invalidate();
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);if(!dragging){if(tool==ScreenshotTool.Select)Cursor=Hit(e.Location) is >=1 and <=8?Cursors.SizeNWSE:Hit(e.Location)==9?Cursors.SizeAll:Cursors.Cross;return;}
        var p=Limit(e.Location);
        if(pending!=null)
        {
            if(pending.Tool==ScreenshotTool.Pen){if(points.Count<65536&&points[^1]!=p)points.Add(p);pending=pending with{Points=points.ToArray()};}
            else pending=pending with{Points=[anchor,p]};
        }
        else if(hit==9)document.Selection=ScreenshotDocument.Clamp(new(priorSelection.X+p.X-anchor.X,priorSelection.Y+p.Y-anchor.Y,priorSelection.Width,priorSelection.Height),frame.Bounds.Size);
        else if(hit>0)
        {
            int l=priorSelection.Left,t=priorSelection.Top,r=priorSelection.Right,b=priorSelection.Bottom;
            if(hit is 1 or 4 or 8)l=p.X;if(hit is 2 or 3 or 6)r=p.X;if(hit is 1 or 2 or 5)t=p.Y;if(hit is 3 or 4 or 7)b=p.Y;
            document.Selection=ScreenshotDocument.Between(new(l,t),new(r,b));
        }
        else document.Selection=ScreenshotDocument.Between(anchor,p);
        Invalidate();
    }
    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);if(!dragging||e.Button!=MouseButtons.Left)return;
        OnMouseMove(e);dragging=false;Capture=false;
        if(pending!=null){document.Add(pending);pending=null;}
        PositionToolbar();if(document.Selection.Width>0&&document.Selection.Height>0&&copyOnSelect)Run(Copy);
    }
    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if(!dragging||Capture)return;
        dragging=false;pending=null;document.Selection=priorSelection;PositionToolbar();
    }
    void BeginText(Point p)
    {
        textInput=new(){Multiline=true,AcceptsReturn=true,ForeColor=ink,BackColor=Color.FromArgb(24,22,34),BorderStyle=BorderStyle.FixedSingle,Font=new Font("Segoe UI",Math.Max(14,stroke*6),FontStyle.Bold,GraphicsUnit.Pixel),Location=p,Size=new(Math.Min(S(340),document.Selection.Right-p.X),Math.Min(S(110),document.Selection.Bottom-p.Y))};
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
        if(textInput!=null){if(keyData==(Keys.Control|Keys.Enter)){CommitText();Focus();return true;}return base.ProcessCmdKey(ref msg,keyData);}
        if(keyData==(Keys.Control|Keys.A))
        {
            var r=Screen.FromPoint(Cursor.Position).Bounds;r.Offset(-frame.Bounds.X,-frame.Bounds.Y);document.Selection=Rectangle.Intersect(r,new(Point.Empty,frame.Bounds.Size));PositionToolbar();return true;
        }
        if(keyData==(Keys.Control|Keys.Z)){document.Undo();RefreshActions();return true;}
        if(keyData==(Keys.Control|Keys.Y)||keyData==(Keys.Control|Keys.Shift|Keys.Z)){document.Redo();RefreshActions();return true;}
        if(document.Selection.Width>0&&document.Selection.Height>0)
        {
            if(keyData==(Keys.Control|Keys.C)||keyData==Keys.Enter){Run(Copy);return true;}
            if(keyData==(Keys.Control|Keys.S)){Run(()=>Save(false));return true;}
            if(keyData==(Keys.Control|Keys.Shift|Keys.S)){Run(()=>Save(true));return true;}
            if(keyData==(Keys.Control|Keys.P)){Run(Print);return true;}
            var key=keyData&Keys.KeyCode;int step=(keyData&Keys.Shift)!=0?10:1;
            if(key is Keys.Left or Keys.Right or Keys.Up or Keys.Down)
            {var r=document.Selection;r.Offset(key==Keys.Left?-step:key==Keys.Right?step:0,key==Keys.Up?-step:key==Keys.Down?step:0);document.Selection=ScreenshotDocument.Clamp(r,frame.Bounds.Size);PositionToolbar();return true;}
        }
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
    internal bool TestKey(Keys key){var message=new Message();return ProcessCmdKey(ref message,key);}
    internal void CheckToolbar()
    {
        var area=new Rectangle(Point.Empty,frame.Bounds.Size);
        if(!area.Contains(toolbar.Bounds))throw new Exception("Screenshot toolbar escaped the frozen desktop.");
        foreach(var row in new[]{tools,actions})foreach(Control child in row.Controls)
            if(!row.ClientRectangle.Contains(child.Bounds))throw new Exception("Screenshot action clipped by toolbar: "+child.Text);
    }
    protected override void WndProc(ref Message m)
    {
        // Geometry or HDR topology changes invalidate the frozen physical canvas.
        if(m.Msg==0x7e&&Visible){Close();return;}
        base.WndProc(ref m);
    }
    protected override void Dispose(bool disposing)
    {
        if(disposing){tips.Dispose();textInput?.Font.Dispose();}
        var font=Font;base.Dispose(disposing);if(disposing)font.Dispose();
    }
    internal Bitmap RenderDiagnostic(Rectangle selection)
    {
        document.Selection=selection;PositionToolbar();var bitmap=new Bitmap(frame.Bounds.Width,frame.Bounds.Height);
        DrawToBitmap(bitmap,new(Point.Empty,bitmap.Size));
        // DrawToBitmap skips children of an invisible top-level window. Render
        // the detached child control separately; never Show/activate a test form.
        var location=toolbar.Location;Controls.Remove(toolbar);
        try
        {
            toolbar.Visible=true;using var bar=new Bitmap(toolbar.Width,toolbar.Height);toolbar.DrawToBitmap(bar,new(Point.Empty,bar.Size));
            using var g=Graphics.FromImage(bitmap);g.DrawImageUnscaled(bar,location);
        }
        finally{Controls.Add(toolbar);toolbar.Location=location;}
        return bitmap;
    }
}
