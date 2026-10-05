namespace SdrCapture;
static class UiStyle
{
    public static void FixedWindow(Form form)
    {
        form.FormBorderStyle=FormBorderStyle.FixedSingle;form.MaximizeBox=false;form.MinimumSize=Size.Empty;
        form.Shown+=(_,_)=>
        {
            if(!form.TopLevel)return; // Embedded video surfaces are sized by the WPF host.
            var work=Screen.FromControl(form).WorkingArea;
            form.Size=new(Math.Min(form.Width,work.Width-24),Math.Min(form.Height,work.Height-24));
            form.Location=new(Math.Max(work.Left,Math.Min(form.Left,work.Right-form.Width)),Math.Max(work.Top,Math.Min(form.Top,work.Bottom-form.Height)));
        };
    }
    public static Button Button(string text,Action clicked,bool primary=false)
    {
        var button=new Button{Text=text,AutoSize=true,MinimumSize=new(110,36),Padding=new(12,4,12,4),FlatStyle=FlatStyle.Flat,BackColor=primary?Color.FromArgb(33,105,211):Color.White,ForeColor=primary?Color.White:Color.FromArgb(36,52,75),Cursor=Cursors.Hand};
        button.FlatAppearance.BorderColor=Color.FromArgb(210,220,233);button.FlatAppearance.BorderSize=primary?0:1;
        button.FlatAppearance.MouseOverBackColor=primary?Color.FromArgb(24,89,184):Color.FromArgb(231,239,250);
        button.Click+=(_,_)=>clicked();return button;
    }
}
