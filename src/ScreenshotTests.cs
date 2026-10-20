using System.Drawing.Imaging;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Vortice.DXGI;

namespace SdrCapture;

static class ScreenshotTests
{
    static void Require(bool value,string reason){if(!value)throw new Exception(reason);}
    static ScreenshotTile Sdr(Rectangle bounds,Func<int,int,Color> pixel)
    {
        byte[] data=new byte[bounds.Width*bounds.Height*4];
        for(int y=0;y<bounds.Height;y++)for(int x=0;x<bounds.Width;x++)
        {var c=pixel(x,y);int p=(y*bounds.Width+x)*4;data[p]=c.B;data[p+1]=c.G;data[p+2]=c.R;data[p+3]=255;}
        return new(bounds,bounds.Width,bounds.Height,ModeRotation.Identity,false,false,80,data);
    }
    static ScreenshotTile Hdr(Rectangle bounds,double white,Func<int,int,(float R,float G,float B)> pixel)
    {
        byte[] data=new byte[bounds.Width*bounds.Height*8];
        for(int y=0;y<bounds.Height;y++)for(int x=0;x<bounds.Width;x++)
        {
            var c=pixel(x,y);var channels=new[]{c.R,c.G,c.B,1};
            for(int i=0;i<4;i++)BitConverter.TryWriteBytes(data.AsSpan((y*bounds.Width+x)*8+i*2,2),BitConverter.HalfToUInt16Bits((Half)channels[i]));
        }
        return new(bounds,bounds.Width,bounds.Height,ModeRotation.Identity,true,true,white,data);
    }
    static Color Pixel(byte[] png,int x,int y,out BitmapMetadata? metadata)
    {
        using var stream=new MemoryStream(png);var decoder=new PngBitmapDecoder(stream,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
        var image=decoder.Frames[0];metadata=image.Metadata as BitmapMetadata;
        var converted=new FormatConvertedBitmap(image,System.Windows.Media.PixelFormats.Bgra32,null,0);byte[] value=new byte[4];converted.CopyPixels(new(x,y,1,1),value,4,0);return Color.FromArgb(value[3],value[2],value[1],value[0]);
    }
    internal static void Run()
    {
        // No hooks, focus changes, OS input, clipboard writes or live desktop capture.
        var previousContext=SynchronizationContext.Current;
        string folder=Path.Combine(Path.GetTempPath(),"DeskGlide-Screenshots-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
        try
        {
            var legacy=JsonSerializer.Deserialize<Settings>("{}")!;Require(legacy.Screenshots.Enabled,"Legacy settings did not receive the screenshot default.");legacy.Screenshots.Validate();
            var settings=new Settings{Screenshots=new(){Enabled=false,Folder=folder}};
            Require(!settings.Copy().Screenshots.Enabled&&settings.Copy().Screenshots.Folder==folder,"Settings copy lost screenshot preferences.");
            Require(JsonSerializer.Deserialize<Settings>(JsonSerializer.Serialize(settings))!.Screenshots==settings.Screenshots,"Screenshot settings failed round trip.");
            bool rejected=false;try{new ScreenshotOptions{Folder="relative"}.Validate();}catch(ArgumentException){rejected=true;}Require(rejected,"Relative destination accepted.");
            // Every SDR code value must survive the screenshot path exactly.
            using(var frame=new ScreenshotFrame([Sdr(new(0,0,256,1),(x,_)=>Color.FromArgb(x,255-x,x/2))]))
            {
                for(int x=0;x<256;x++)Require(frame.Preview.GetPixel(x,0).ToArgb()==Color.FromArgb(x,255-x,x/2).ToArgb(),"SDR colors changed.");
                using var encoded=new MemoryStream();ScreenshotFiles.SavePng(encoded,frame.Preview);
                var value=Pixel(encoded.ToArray(),128,0,out var metadata);
                Require(value.ToArgb()==Color.FromArgb(128,127,64).ToArgb(),"PNG colors changed.");
                Require(metadata?.GetQuery("/sRGB/RenderingIntent") is byte intent&&intent==0,"PNG has no explicit sRGB interpretation.");
            }
            // SDR reference brightness must be invariant under the Windows HDR
            // brightness slider; highlights must be monotonic, bounded and colored.
            int[] levels=[];
            foreach(double white in new[]{80d,200d,480d})
            {
                using var frame=new ScreenshotFrame([Hdr(new(0,0,8,1),white,(x,_)=>
                {float v=x switch{0=>0,1=>.1f,2=>.3f,3=>.75f,4=>1,5=>2,6=>4,_=>8};return(v*(float)(white/80),v*(float)(white/80),v*(float)(white/80));})]);
                var actual=Enumerable.Range(0,8).Select(x=>(int)frame.Preview.GetPixel(x,0).R).ToArray();
                if(levels.Length==0)levels=actual;else Require(actual.Zip(levels).All(pair=>Math.Abs(pair.First-pair.Second)<=1),"HDR white compensation changed the screenshot.");
                Require(actual[0]==0&&actual[2]>actual[1]&&actual[4]>actual[3]&&actual[5]>actual[4]&&actual[7]<=255,"HDR shoulder clips or inverts highlights.");
            }
            using(var frame=new ScreenshotFrame([Hdr(new(0,0,3,1),80,(x,_)=>x switch{0=>(4,1,.5f),1=>(float.NaN,float.PositiveInfinity,float.NegativeInfinity),_=>(-1,-2,-3)})]))
            {var c=frame.Preview.GetPixel(0,0);Require(c.R>c.G&&c.G>c.B,"HDR highlight hue was lost.");Require(frame.Preview.GetPixel(1,0).R==0&&frame.Preview.GetPixel(2,0).R==0,"Invalid/negative scRGB became a bright pixel.");}
            // Physical crop coordinates across negative origins and two white levels.
            using(var frame=new ScreenshotFrame([Sdr(new(-4,-2,4,3),(_,_)=>Color.Red),Hdr(new(0,-2,4,3),200,(_,_) =>(.75f,.75f,.75f))]))
            {
                Require(frame.Bounds==new Rectangle(-4,-2,8,3),"Negative virtual desktop origin lost.");
                using var crop=frame.Crop(new(3,1,2,1));Require(crop.GetPixel(0,0).R==255&&crop.GetPixel(0,0).G==0&&crop.GetPixel(1,0).G>0,"Mixed HDR/SDR crop seam was misaligned.");
                rejected=false;try{using var bad=frame.Crop(new(-1,0,2,1));}catch(ArgumentOutOfRangeException){rejected=true;}Require(rejected,"Out-of-bounds crop accepted.");
            }
            foreach(var rotation in new[]{ModeRotation.Identity,ModeRotation.Rotate90,ModeRotation.Rotate180,ModeRotation.Rotate270})
            {
                byte[] data=[0,0,1,255,0,0,2,255,0,0,3,255,0,0,4,255,0,0,5,255,0,0,6,255];
                bool rotated=rotation is ModeRotation.Rotate90 or ModeRotation.Rotate270;
                using var frame=new ScreenshotFrame([new(new(0,0,rotated?2:3,rotated?3:2),3,2,rotation,false,false,80,data)]);
                int[] expected=rotation switch{ModeRotation.Identity=>[1,2,3,4,5,6],ModeRotation.Rotate90=>[4,1,5,2,6,3],ModeRotation.Rotate180=>[6,5,4,3,2,1],_=>[3,6,2,5,1,4]};
                for(int i=0;i<6;i++)Require(frame.Preview.GetPixel(i%frame.Bounds.Width,i/frame.Bounds.Width).R==expected[i],"Rotated monitor orientation changed.");
            }
            using(var frame=new ScreenshotFrame([Sdr(new(0,0,400,240),(x,y)=>Color.FromArgb(x%256,y%256,80))]))
            {
                var document=new ScreenshotDocument(frame){Selection=new(10,10,300,200)};
                foreach(var tool in Enum.GetValues<ScreenshotTool>().Where(t=>t!=ScreenshotTool.Select))document.Add(new(tool,Color.Lime,3,[new(30,30),new(110,80)],"DeskGlide"));
                using var rendered=document.Render();Require(rendered.Size==new Size(300,200),"Editor resampled a native-pixel crop.");
                Require(rendered.GetPixel(40,40).ToArgb()==Color.Black.ToArgb(),"Redaction failed to overwrite source pixels.");
                document.Undo();using var undone=document.Render();Require(undone.GetPixel(40,40).ToArgb()!=Color.Black.ToArgb(),"Undo did not restore the previous document.");
                document.Redo();using var redone=document.Render();Require(redone.GetPixel(40,40).ToArgb()==Color.Black.ToArgb(),"Redo lost redaction.");
                using var encoded=new MemoryStream();ScreenshotFiles.SavePng(encoded,redone);Require(Pixel(encoded.ToArray(),40,40,out _).ToArgb()==Color.Black.ToArgb(),"PNG export exposed redacted pixels.");
                var clipboard=ScreenshotEditor.ClipboardData(redone);Require(clipboard.GetDataPresent(DataFormats.Bitmap)&&clipboard.GetDataPresent("PNG"),"Clipboard lacks interoperable bitmap/PNG formats.");
                Require(clipboard.TryGetData<MemoryStream>("PNG",out var clipboardPng),"Clipboard PNG stream is missing.");
                using(clipboardPng)Require(Pixel(clipboardPng!.ToArray(),40,40,out _).ToArgb()==Color.Black.ToArgb(),"Clipboard exposed redacted pixels.");
                document.Undo();document.Add(new(ScreenshotTool.Pen,Color.Red,2,[new(200,100)]));Require(!document.CanRedo,"New annotation retained stale redo history.");
                string file=ScreenshotFiles.Save(folder,".png",s=>ScreenshotFiles.SavePng(s,rendered));Require(File.Exists(file),"PNG not saved in requested directory.");
                byte[] old=File.ReadAllBytes(file);rejected=false;
                try{ScreenshotFiles.Write(file,_=>throw new IOException("Simulated encode failure"));}catch(IOException){rejected=true;}
                Require(rejected&&old.SequenceEqual(File.ReadAllBytes(file))&&Directory.GetFiles(folder,"*.tmp").Length==0,"Failed save damaged an existing file or leaked temp data.");
                Require(ScreenshotDocument.Clamp(new(-5,500,300,200),frame.Bounds.Size)==new Rectangle(0,40,300,200),"Selection move escaped the physical desktop.");
            }
            string previews=Path.Combine(PortableResources.Bundled?Path.Combine(Log.Folder,"Diagnostics"):AppContext.BaseDirectory,"screenshot-preview");Directory.CreateDirectory(previews);
            using(var frame=new ScreenshotFrame([Sdr(new(0,0,1600,900),(x,y)=>Color.FromArgb(15+x/40,20+y/30,42+(x+y)/60))]))
            {
                using(var g=Graphics.FromImage(frame.Preview))
                using(var font=new Font("Segoe UI",36,FontStyle.Bold,GraphicsUnit.Pixel))
                {g.DrawString("DeskGlide",font,Brushes.White,180,170);g.DrawString("Capture. Mark. Save.",font,Brushes.LightSteelBlue,180,230);}
                foreach(string language in new[]{"en","ru"})
                {
                    UiStrings.Shared.Language=language;
                    using var editor=new ScreenshotEditor(frame,new(){Folder=folder},_=>{});
                    editor.Document.Add(new(ScreenshotTool.Arrow,Color.FromArgb(49,209,219),4,[new(260,420),new(640,290)]));
                    editor.Document.Add(new(ScreenshotTool.Rectangle,Color.FromArgb(244,67,116),3,[new(165,155),new(500,285)]));
                    using var image=editor.RenderDiagnostic(new(120,120,1100,450));image.Save(Path.Combine(previews,language+".png"),ImageFormat.Png);editor.CheckToolbar();
                    Require(!editor.Visible,"Diagnostic editor stole focus.");
                }
            }
            Program.Write("screenshot-tests.json",new{Pass=true,SdrRoundTrip=true,HdrWhiteLevels=new[]{80,200,480},HighlightAndNonfiniteChecks=true,MixedDisplays=true,AllRotations=true,CropAndAnnotations=true,FlattenedRedaction=true,UndoRedo=true,AtomicSave=true,LegacyPreferences=true,OffscreenEditorPreviews=previews,GlobalKeysRegistered=false,DesktopCaptured=false,ClipboardWritten=false,InteractivePrintScreenTest=false});
        }
        finally{SynchronizationContext.SetSynchronizationContext(previousContext);if(Directory.Exists(folder))Directory.Delete(folder,true);}
    }
    internal static void Capture()
    {
        using var lifetime=new CancellationTokenSource(TimeSpan.FromSeconds(12));using var frame=ScreenshotFrame.Capture(lifetime.Token);
        using var output=new MemoryStream();ScreenshotFiles.SavePng(output,frame.Preview);
        using var decoded=System.Drawing.Image.FromStream(new MemoryStream(output.ToArray()));
        Require(decoded.Size==frame.Bounds.Size,"Live screenshot PNG dimensions changed.");
        Program.Write("screenshot-capture-test.json",new{Pass=true,frame.Bounds,frame.HasHdr,Tiles=frame.Tiles.Select(t=>new{t.Bounds,t.Hdr,t.WhiteNits}),PngBytes=output.Length,SavedDesktopImage=false,WindowsActivated=false,ClipboardWritten=false});
    }
    internal static void Ui()
    {
        if(EventWaitHandle.TryOpenExisting("Local\\SdrCapture.OpenKvm",out var running))
        {running.Dispose();throw new InvalidOperationException("Close DeskGlide before screenshot UI tests; they activate a synthetic test window.");}
        Application.EnableVisualStyles();using var frame=new ScreenshotFrame([Sdr(new(0,0,1100,760),(_,_)=>Color.DarkSlateBlue)]);
        foreach(string language in new[]{"en","ru"})
        {
            UiStrings.Shared.Language=language;using var editor=new ScreenshotEditor(frame,new(),_=>{});Exception? failure=null;
            editor.Shown+=(_,_)=>editor.BeginInvoke(()=>
            {
                try
                {
                    editor.TestDrag(new(800,500),new(100,100));Require(editor.Document.Selection==new Rectangle(100,100,700,400),"Reverse selection failed.");editor.CheckToolbar();
                    editor.TestDrag(new(450,300),new(460,310));Require(editor.Document.Selection==new Rectangle(110,110,700,400),"Selection drag failed.");
                    editor.TestDrag(new(110,110),new(80,90));Require(editor.Document.Selection==new Rectangle(80,90,730,420),"Corner resize failed.");
                    editor.TestTool(ScreenshotTool.Arrow);editor.TestDrag(new(150,150),new(400,300));Require(editor.Document.CanUndo,"Drawing through the editor lost the mark.");
                    editor.TestKey(Keys.Control|Keys.Z);Require(!editor.Document.CanUndo&&editor.Document.CanRedo,"Editor Undo shortcut failed.");
                    editor.TestKey(Keys.Control|Keys.Y);Require(editor.Document.CanUndo,"Editor Redo shortcut failed.");
                    editor.TestTool(ScreenshotTool.Select);var before=editor.Document.Selection;editor.TestKey(Keys.Shift|Keys.Right);Require(editor.Document.Selection.X==before.X+10,"Pixel nudge failed.");
                    editor.CheckToolbar();
                    editor.TestTool(ScreenshotTool.Text);editor.TestDrag(new(200,200),new(200,200));editor.TestKey(Keys.Escape);Require(!editor.IsDisposed,"Escape during text editing closed the capture.");
                }
                catch(Exception e){failure=e;}
                finally{editor.Close();}
            });
            Application.Run(editor);if(failure!=null)throw failure;
        }
        Program.Write("screenshot-ui-tests.json",new{Pass=true,Languages=new[]{"en","ru"},NativeWindowShown=true,SelectionAndResize=true,AnnotationAndUndo=true,TextCancel=true,ToolbarBounds=true,DesktopCaptured=false,OsInputInjected=false,ClipboardWritten=false,PrintScreenPressed=false});
    }
}
