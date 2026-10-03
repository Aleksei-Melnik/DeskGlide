using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SdrCapture;

// Brief fade of the previous page; the real controls stay native and accessible.
sealed class PageTransition:Control
{
    readonly System.Windows.Forms.Timer timer=new(){Interval=15};
    Bitmap? previous,next;
    long started;
    public PageTransition()
    {
        SetStyle(ControlStyles.UserPaint|ControlStyles.AllPaintingInWmPaint|ControlStyles.OptimizedDoubleBuffer,true);
        TabStop=false;Visible=false;
        timer.Tick+=(_,_)=>{if(Environment.TickCount64-started>=160)Finish();else Invalidate();};
    }
    public void CapturePage(Control host)
    {
        Finish();
        if(!SystemParametersInfo(0x1042,0,out bool enabled,0)||!enabled||!host.Visible||host.Width<1||host.Height<1)return;
        Bounds=host.ClientRectangle;
        previous=new Bitmap(host.Width,host.Height);host.DrawToBitmap(previous,host.ClientRectangle);
    }
    public void Play(Control host){if(previous==null)return;next=new Bitmap(host.Width,host.Height);host.DrawToBitmap(next,host.ClientRectangle);started=Environment.TickCount64;Visible=true;BringToFront();timer.Start();}
    public void Finish(){timer.Stop();Visible=false;previous?.Dispose();next?.Dispose();previous=null;next=null;}
    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);if(previous==null)return;
        float opacity=1-Math.Clamp((Environment.TickCount64-started)/160f,0,1);
        using var attributes=new ImageAttributes();attributes.SetColorMatrix(new ColorMatrix{Matrix33=opacity});
        e.Graphics.Clear(Color.White);
        if(next!=null)e.Graphics.DrawImage(next,ClientRectangle);
        e.Graphics.DrawImage(previous,ClientRectangle,0,0,previous.Width,previous.Height,GraphicsUnit.Pixel,attributes);
    }
    protected override void Dispose(bool disposing){if(disposing){timer.Dispose();previous?.Dispose();next?.Dispose();}base.Dispose(disposing);}
    [DllImport("user32.dll",EntryPoint="SystemParametersInfoW")]
    [return:MarshalAs(UnmanagedType.Bool)] static extern bool SystemParametersInfo(uint action,uint parameter,[MarshalAs(UnmanagedType.Bool)] out bool value,uint flags);
}

static class UiStyle
{
    public static Button Button(string text,Action clicked,bool primary=false)
    {
        var button=new Button{Text=text,AutoSize=true,MinimumSize=new(110,36),Padding=new(12,4,12,4),FlatStyle=FlatStyle.Flat,BackColor=primary?Color.FromArgb(33,105,211):Color.White,ForeColor=primary?Color.White:Color.FromArgb(36,52,75),Cursor=Cursors.Hand};
        button.FlatAppearance.BorderColor=Color.FromArgb(210,220,233);button.FlatAppearance.BorderSize=primary?0:1;
        button.FlatAppearance.MouseOverBackColor=primary?Color.FromArgb(24,89,184):Color.FromArgb(231,239,250);
        button.Click+=(_,_)=>clicked();return button;
    }
}
