namespace SdrCapture;

static class TrayMenu
{
    public static ContextMenuStrip Create()=>new(){Renderer=new Renderer(),BackColor=Color.FromArgb(18,18,28),ForeColor=Color.FromArgb(248,247,251),Font=new("Segoe UI",10),ShowImageMargin=false,ShowCheckMargin=true};
    sealed class Colors:ProfessionalColorTable
    {
        public override Color ToolStripDropDownBackground=>Color.FromArgb(18,18,28);
        public override Color MenuBorder=>Color.FromArgb(67,48,76);
        public override Color MenuItemSelected=>Color.FromArgb(48,32,57);
        public override Color MenuItemBorder=>Color.FromArgb(125,60,132);
        public override Color CheckBackground=>Color.FromArgb(118,40,105);
        public override Color CheckSelectedBackground=>Color.FromArgb(167,77,255);
        public override Color SeparatorDark=>Color.FromArgb(48,43,62);
        public override Color SeparatorLight=>Color.FromArgb(48,43,62);
        public override Color ImageMarginGradientBegin=>ToolStripDropDownBackground;
        public override Color ImageMarginGradientMiddle=>ToolStripDropDownBackground;
        public override Color ImageMarginGradientEnd=>ToolStripDropDownBackground;
    }
    sealed class Renderer:ToolStripProfessionalRenderer
    {
        public Renderer():base(new Colors()){}
        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e){e.TextColor=e.Item.Enabled?Color.FromArgb(248,247,251):Color.FromArgb(157,147,172);base.OnRenderItemText(e);}
    }
}
