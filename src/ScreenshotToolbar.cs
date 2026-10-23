using System.Drawing.Drawing2D;

namespace SdrCapture;

// Original vector glyphs, drawn at the display's UI scale; no third-party assets.
sealed class ScreenshotButton(string glyph):Button
{
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] internal bool Selected {get;set;}
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)] internal Color Ink {get;set;}=Color.FromArgb(244,67,116);
    bool hover;
    protected override void OnMouseEnter(EventArgs e){hover=true;Invalidate();base.OnMouseEnter(e);}
    protected override void OnMouseLeave(EventArgs e){hover=false;Invalidate();base.OnMouseLeave(e);}
    protected override void OnPaint(PaintEventArgs e)
    {
        var g=e.Graphics;using(var background=new SolidBrush(Selected?Color.FromArgb(78,48,97):hover?Color.FromArgb(52,47,66):BackColor))g.FillRectangle(background,ClientRectangle);
        g.SmoothingMode=SmoothingMode.AntiAlias;
        var state=g.Save();float size=Math.Min(Width,Height)*.72f;
        g.TranslateTransform((Width-size)/2,(Height-size)/2);g.ScaleTransform(size/24,size/24);
        var color=Enabled?ForeColor:Color.FromArgb(98,92,114);
        using var pen=new Pen(color,1.7f){StartCap=LineCap.Round,EndCap=LineCap.Round,LineJoin=LineJoin.Round};
        using var brush=new SolidBrush(color);
        switch(glyph)
        {
            case "Select":
                using(var dotted=new Pen(color,1.4f){DashStyle=DashStyle.Dash})g.DrawRectangle(dotted,3,3,15,15);
                g.FillPolygon(brush,[new(12,11),new(20,16),new(16,17),new(14,21)]);break;
            case "Pen":g.DrawPolygon(pen,[new(4,15),new(15,4),new(20,9),new(9,20),new(3,21)]);g.DrawLine(pen,13,6,18,11);g.DrawLine(pen,4,15,9,20);break;
            case "Line":g.DrawLine(pen,4,20,20,4);break;
            case "Arrow":g.DrawLine(pen,4,20,20,4);g.DrawLines(pen,[new(11,4),new(20,4),new(20,13)]);break;
            case "Rectangle":g.DrawRectangle(pen,3,5,18,14);break;
            case "Highlight":g.DrawPolygon(pen,[new(4,13),new(14,3),new(20,9),new(10,19)]);g.DrawLine(pen,3,21,17,21);g.DrawLine(pen,4,13,10,19);break;
            case "Text":g.DrawLine(pen,4,5,20,5);g.DrawLine(pen,12,5,12,20);g.DrawLine(pen,8,20,16,20);g.DrawLine(pen,4,5,4,8);g.DrawLine(pen,20,5,20,8);break;
            case "Redact":g.FillRectangle(brush,3,4,18,5);g.FillRectangle(brush,3,11,14,4);g.FillRectangle(brush,3,17,18,4);break;
            case "Color":using(var ink=new SolidBrush(Ink))g.FillEllipse(ink,3,3,18,18);g.DrawEllipse(pen,3,3,18,18);break;
            case "Stroke":g.DrawLine(pen,4,5,20,5);pen.Width=3;g.DrawLine(pen,4,12,20,12);pen.Width=5;g.DrawLine(pen,4,20,20,20);break;
            case "Undo":g.DrawArc(pen,5,6,15,14,205,260);g.DrawLines(pen,[new(3,5),new(3,12),new(10,12)]);break;
            case "Redo":g.DrawArc(pen,4,6,15,14,75,260);g.DrawLines(pen,[new(21,5),new(21,12),new(14,12)]);break;
            case "Copy":g.DrawRectangle(pen,8,7,12,14);g.DrawLines(pen,[new(5,17),new(3,17),new(3,3),new(15,3),new(15,4)]);break;
            case "Save":g.DrawPolygon(pen,[new(3,3),new(17,3),new(21,7),new(21,21),new(3,21)]);g.DrawRectangle(pen,7,3,9,6);g.DrawRectangle(pen,7,14,10,7);break;
            case "SaveAs":g.DrawLines(pen,[new(11,3),new(3,3),new(3,21),new(14,21)]);g.DrawLine(pen,12,15,21,6);g.DrawLines(pen,[new(16,6),new(21,6),new(21,11)]);break;
            case "Print":g.DrawRectangle(pen,3,8,18,9);g.DrawRectangle(pen,7,14,10,7);g.DrawLines(pen,[new(7,6),new(7,3),new(17,3),new(17,6)]);g.FillEllipse(brush,17,10,2,2);break;
            case "Close":g.DrawLine(pen,5,5,19,19);g.DrawLine(pen,19,5,5,19);break;
            case "More":g.FillEllipse(brush,10,3,4,4);g.FillEllipse(brush,10,10,4,4);g.FillEllipse(brush,10,17,4,4);break;
        }
        g.Restore(state);
        if(Focused&&ShowFocusCues)ControlPaint.DrawFocusRectangle(g,new(2,2,Width-4,Height-4),color,BackColor);
    }
}

internal readonly record struct ScreenshotBarPositions(Point Tools,Point Actions);
static class ScreenshotBarLayout
{
    internal static ScreenshotBarPositions Place(Rectangle selection,Rectangle screen,Size tools,Size actions,int gap)
    {
        // Drawing tools follow the right edge, export actions the bottom edge.
        // Flip at monitor boundaries, then separate the bars even for full-screen
        // and tiny selections where they have to sit inside the frozen image.
        int tx=selection.Right+gap;
        if(tx+tools.Width>screen.Right)tx=selection.Left-tools.Width-gap;
        tx=Math.Clamp(tx,screen.Left,screen.Right-tools.Width);
        int ty=Math.Clamp(selection.Bottom-tools.Height,screen.Top,screen.Bottom-tools.Height);
        int ax=Math.Clamp(selection.Right-actions.Width,screen.Left,screen.Right-actions.Width);
        int ay=selection.Bottom+gap;
        if(ay+actions.Height>screen.Bottom)ay=selection.Top-actions.Height-gap;
        ay=Math.Clamp(ay,screen.Top,screen.Bottom-actions.Height);
        var toolsRect=new Rectangle(tx,ty,tools.Width,tools.Height);
        if(toolsRect.IntersectsWith(new(ax,ay,actions.Width,actions.Height)))
        {
            if(toolsRect.Left-gap-actions.Width>=screen.Left)ax=toolsRect.Left-gap-actions.Width;
            else if(toolsRect.Right+gap+actions.Width<=screen.Right)ax=toolsRect.Right+gap;
            else
            {
                // The compact bars together fit vertically on a small monitor.
                ay=screen.Bottom-actions.Height;
                ty=Math.Min(ty,ay-gap-tools.Height);
                ty=Math.Max(screen.Top,ty);
            }
        }
        return new(new(tx,ty),new(ax,ay));
    }
}
