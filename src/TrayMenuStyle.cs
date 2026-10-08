using System.Drawing.Drawing2D;
namespace SdrCapture;
static class TrayMenuStyle
{
    sealed class Palette:ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground=>Color.FromArgb(17,17,20);
        public override Color ImageMarginGradientBegin=>ToolStripDropDownBackground;
        public override Color ImageMarginGradientMiddle=>ToolStripDropDownBackground;
        public override Color ImageMarginGradientEnd=>ToolStripDropDownBackground;
        public override Color MenuBorder=>Color.FromArgb(48,43,62);
        public override Color MenuItemSelected=>Color.FromArgb(49,33,55);
        public override Color MenuItemBorder=>Color.FromArgb(83,48,90);
        public override Color SeparatorDark=>Color.FromArgb(48,43,62);
        public override Color SeparatorLight=>ToolStripDropDownBackground;
        public override Color CheckBackground=>ToolStripDropDownBackground;
        public override Color CheckSelectedBackground=>MenuItemSelected;
        public override Color CheckPressedBackground=>MenuItemSelected;
    }
    sealed class Renderer:ToolStripProfessionalRenderer
    {
        public Renderer():base(new Palette()){RoundedEdges=true;}
        protected override void OnRenderItemCheck(ToolStripItemImageRenderEventArgs e)
        {
            using var pen=new Pen(Color.FromArgb(206,147,245),1.5f);e.Graphics.SmoothingMode=SmoothingMode.AntiAlias;
            var r=e.ImageRectangle;int cy=r.Top+r.Height/2;e.Graphics.DrawLines(pen,[new(r.Left+3,cy),new(r.Left+6,cy+3),new(r.Left+12,cy-4)]);
        }
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e){e.TextColor=e.Item.Enabled?Color.FromArgb(248,247,251):Color.FromArgb(123,117,137);base.OnRenderItemText(e);}
    }
    public static void Apply(ContextMenuStrip menu){menu.Renderer=new Renderer();menu.Font=new Font("Segoe UI",9);menu.Padding=new(3);menu.ShowImageMargin=false;menu.ShowCheckMargin=true;menu.BackColor=Color.FromArgb(17,17,20);menu.ForeColor=Color.FromArgb(248,247,251);}
}
