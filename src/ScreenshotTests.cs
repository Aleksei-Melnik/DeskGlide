using System.Drawing.Imaging;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Vortice.DXGI;
using System.Diagnostics;

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
    internal static void Latency()
    {
        var tile=Sdr(new(0,0,2560,1440),(x,y)=>Color.FromArgb(x%256,y%256,(x*13+y*17)%256));
        var watch=Stopwatch.StartNew();
        using var frame=new ScreenshotFrame([tile]);
        double previewMs=watch.Elapsed.TotalMilliseconds;
        var document=new ScreenshotDocument(frame){Selection=new(0,0,2560,1440)};
        watch.Restart();using var image=document.Render();double renderMs=watch.Elapsed.TotalMilliseconds;
        watch.Restart();using var png=new MemoryStream();ScreenshotFiles.SavePng(png,image);double pngMs=watch.Elapsed.TotalMilliseconds;
        watch.Restart();var payload=ScreenshotClipboard.Prepare(image);double dataMs=watch.Elapsed.TotalMilliseconds;
        using var decoded=Image.FromStream(new MemoryStream(payload.Png));Require(decoded.Size==image.Size,"Fast clipboard PNG changed dimensions.");
        Require(Pixel(payload.Png,128,22,out var metadata)==image.GetPixel(128,22)&&metadata?.GetQuery("/sRGB/RenderingIntent") is byte intent&&intent==0,"Fast clipboard PNG changed colors/tag.");
        // Decode every pixel through WIC, including the flattened opaque redact.
        var pixels=new byte[image.Width*image.Height*4];
        using(var input=new MemoryStream(payload.Png))
        {
            var decoder=new PngBitmapDecoder(input,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad);
            var rgba=new FormatConvertedBitmap(decoder.Frames[0],System.Windows.Media.PixelFormats.Bgra32,null,0);rgba.CopyPixels(pixels,image.Width*4,0);
        }
        Require(pixels.AsSpan().SequenceEqual(payload.Dib.AsSpan(40)),"Native DIB and PNG pixels differ.");
        Program.Write("screenshot-latency-test.json",new{Pass=true,PreviewMs=previewMs,RenderMs=renderMs,WicPngMs=pngMs,ClipboardPreparationMs=dataMs,PngBytes=png.Length,ClipboardWritten=false,NativeWindowCreated=false});
    }
    static void GpuColors()
    {
        using var device=Vortice.Direct3D11.D3D11.D3D11CreateDevice(Vortice.Direct3D.DriverType.Hardware,Vortice.Direct3D11.DeviceCreationFlags.BgraSupport,Vortice.Direct3D.FeatureLevel.Level_11_0);
        using var context=device.ImmediateContext;int maximumError=0,cases=0;
        foreach(var rotation in new[]{ModeRotation.Identity,ModeRotation.Rotate90,ModeRotation.Rotate180,ModeRotation.Rotate270})
        foreach(bool hdr in new[]{false,true})foreach(double white in new[]{80d,200d,480d})
        {
            const int width=17,height=9;byte[] bytes=new byte[width*height*8];var random=new Random(71);
            for(int i=0;i<width*height*4;i++)
            {
                float value=i%4==3?1:i<8?new[]{float.NaN,float.PositiveInfinity,float.NegativeInfinity,0f,.1f,.3f,.75f,1f}[i]:(float)(random.NextDouble()*4-.2)*(float)(white/80);
                BitConverter.TryWriteBytes(bytes.AsSpan(i*2,2),BitConverter.HalfToUInt16Bits((Half)value));
            }
            bool quarter=rotation is ModeRotation.Rotate90 or ModeRotation.Rotate270;var size=new Size(quarter?height:width,quarter?width:height);
            using var texture=device.CreateTexture2D(new Vortice.Direct3D11.Texture2DDescription(Format.R16G16B16A16_Float,width,height,1,1,Vortice.Direct3D11.BindFlags.ShaderResource));
            context.UpdateSubresource(bytes,texture,0,width*8);
            using var converter=new ScreenshotGpuReadback(device,context,size.Width,size.Height);
            var converted=converter.Read(texture,rotation,hdr,white);
            using var expected=new ScreenshotFrame([new(new(Point.Empty,size),width,height,rotation,true,hdr,white,bytes)]);
            using var actual=new ScreenshotFrame([new(new(Point.Empty,size),size.Width,size.Height,ModeRotation.Identity,false,hdr,white,converted,converted:true)]);
            using(var wire=new MemoryStream())
            {using(var writer=new BinaryWriter(wire,System.Text.Encoding.UTF8,leaveOpen:true))ScreenshotCaptureWorker.Write(writer,actual.Tiles);wire.Position=0;using var reader=new BinaryReader(wire);using var transferred=ScreenshotCaptureWorker.Read(reader);Require(SamePixels(actual.Preview,transferred.Preview),"The worker converted HDR pixels twice.");}
            for(int y=0;y<size.Height;y++)for(int x=0;x<size.Width;x++)
            {
                var a=actual.Preview.GetPixel(x,y);var b=expected.Preview.GetPixel(x,y);
                maximumError=Math.Max(maximumError,Math.Max(Math.Abs(a.R-b.R),Math.Max(Math.Abs(a.G-b.G),Math.Abs(a.B-b.B))));
                Require(a.A==255&&maximumError<=1,"GPU screenshot tone mapping/rotation differs from the CPU reference.");
            }
            cases++;
        }
        Program.Write("screenshot-gpu-tests.json",new{Pass=true,Cases=cases,AllRotations=true,HdrAndLinearSdr=true,NonfiniteAndNegative=true,MaximumCodeValueError=maximumError,NativeResolution=true});
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
            WorkerTests();
            GpuColors();
            Latency();
            RenderDamage(800,500);
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
            foreach(var screen in new[]{new Rectangle(0,0,1280,720),new(0,0,1080,1920),new(0,0,640,480),new(-1000,-500,1280,720),new(0,0,320,240)})
            foreach(var selection in new[]{screen,new(screen.Left,screen.Top,30,30),new(screen.Right-30,screen.Bottom-30,30,30),new(screen.Left+60,screen.Top+60,200,100)})
            {
                var toolSize=screen.Height<400?new Size(52,196):new Size(28,220);var actionSize=new Size(100,28);
                var place=ScreenshotBarLayout.Place(selection,screen,toolSize,actionSize,7);
                var toolsRect=new Rectangle(place.Tools,toolSize);var actionsRect=new Rectangle(place.Actions,actionSize);
                Require(screen.Contains(toolsRect)&&screen.Contains(actionsRect)&&!toolsRect.IntersectsWith(actionsRect),"Compact screenshot bars overlap or escape a monitor edge.");
                Require(toolsRect.Right==actionsRect.Right&&toolsRect.Bottom+7==actionsRect.Top,"Screenshot controls split across different selection corners.");
                if(selection==screen)
                    Require(actionsRect.Right>=screen.Right-7&&actionsRect.Bottom>=screen.Bottom-7,"Full-screen screenshot controls moved away from the bottom-right corner.");
            }
            var normal=ScreenshotBarLayout.Place(new(100,100,700,400),new(0,0,1600,900),new(28,220),new(100,28),5);
            Require(normal.Tools.X==805&&normal.Actions.Y==505,"Screenshot controls are not right/below the selection.");
            PointerCursors();
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
                    foreach(var selection in new[]{new Rectangle(0,0,1600,900),new(0,0,60,40),new(1530,850,70,50)})
                    {using var edge=editor.RenderDiagnostic(selection);editor.CheckToolbar();edge.Save(Path.Combine(previews,language+"-"+selection.X+"-"+selection.Width+".png"),ImageFormat.Png);}
                    editor.Document.Selection=new(0,0,1600,900);using var exported=editor.Document.Render();Require(exported.GetPixel(1200,20).ToArgb()==frame.Preview.GetPixel(1200,20).ToArgb()&&exported.GetPixel(0,0).ToArgb()==frame.Preview.GetPixel(0,0).ToArgb(),"Editor bars or crop border leaked into the exported PNG.");
                    Require(!editor.Visible,"Diagnostic editor stole focus.");
                }
            }
            Program.Write("screenshot-tests.json",new{Pass=true,IsolatedWorkerRoundTrip=true,WorkerErrorAndCancellation=true,BoundedPixelProtocol=true,SdrRoundTrip=true,HdrWhiteLevels=new[]{80,200,480},HighlightAndNonfiniteChecks=true,MixedDisplays=true,AllRotations=true,CropAndAnnotations=true,FlattenedRedaction=true,UndoRedo=true,AtomicSave=true,LegacyPreferences=true,ToolbarArrowCursor=true,CanvasToolAndResizeCursors=true,BottomRightToolbarCluster=true,FullScreenDragDirections=true,NoMoreButton=true,OffscreenEditorPreviews=previews,GlobalKeysRegistered=false,DesktopCaptured=false,ClipboardWritten=false,InteractivePrintScreenTest=false});
        }
        finally{SynchronizationContext.SetSynchronizationContext(previousContext);if(Directory.Exists(folder))Directory.Delete(folder,true);}
    }
    static void PointerCursors()
    {
        using var frame=new ScreenshotFrame([Sdr(new(0,0,1000,700),(_,_)=>Color.DarkSlateBlue)]);
        using var editor=new ScreenshotEditor(frame,new(),_=>{}){SyntheticPointer=true};
        editor.TestDrag(new(100,100),new(600,450));
        var bars=editor.Controls.OfType<FlowLayoutPanel>().ToArray();
        Require(bars.Length==2&&bars.All(bar=>bar.Controls.Count>0),"Screenshot cursor test did not find both toolbars.");
        Require(bars.All(bar=>bar.ContextMenuStrip!=null)&&!bars.SelectMany(bar=>bar.Controls.Cast<Control>()).Any(button=>Equals(button.Tag,"More")),"Screenshot editor still needs a More button instead of the toolbar context menu.");
        foreach(int dpi in new[]{96,144})
        foreach(var tool in new[]{ScreenshotTool.Select,ScreenshotTool.Pen,ScreenshotTool.Text})
        {
            editor.TestDpi(dpi);editor.TestTool(tool);
            foreach(var (point,expected) in new (Point,Cursor)[]{
                (new(100,100),Cursors.SizeNWSE),(new(600,100),Cursors.SizeNESW),
                (new(600,450),Cursors.SizeNWSE),(new(100,450),Cursors.SizeNESW),
                (new(350,100),Cursors.SizeNS),(new(600,275),Cursors.SizeWE),
                (new(350,450),Cursors.SizeNS),(new(100,275),Cursors.SizeWE),
                (new(350,275),tool==ScreenshotTool.Select?Cursors.SizeAll:Cursors.Cross),
                (new(50,50),Cursors.Cross)})
            {
                editor.TestPointerMove(point);
                Require(editor.Cursor==expected,$"Canvas cursor changed for {tool} at {point} ({dpi} DPI).");
                foreach(var bar in bars)
                {
                    Require(bar.Cursor==Cursors.Default,$"Screenshot toolbar padding inherited the {editor.Cursor} canvas cursor.");
                    foreach(Control button in bar.Controls)
                        Require(button.Cursor==Cursors.Default,$"Screenshot button {button.Tag} inherited the {editor.Cursor} canvas cursor.");
                }
            }
        }
        foreach(int dpi in new[]{96,144})
        foreach(var (from,to) in new (Point,Point)[]{(new(0,0),new(999,699)),(new(999,699),new(0,0)),(new(999,0),new(0,699)),(new(0,699),new(999,0))})
        {
            editor.Document.Selection=Rectangle.Empty;editor.TestDpi(dpi);editor.TestDrag(from,to);editor.CheckToolbar();
            var actions=bars.Single(bar=>bar.FlowDirection==FlowDirection.LeftToRight);
            Require(editor.Document.Selection==new Rectangle(0,0,999,699)&&actions.Right>=994&&actions.Bottom>=694,"A full-screen drag did not keep the controls at the bottom right.");
        }
        Require(!editor.Visible,"Screenshot cursor test activated the editor.");
    }
    internal static void Capture()
    {
        using var lifetime=new CancellationTokenSource(TimeSpan.FromSeconds(25));using var frame=ScreenshotCaptureWorker.Capture(lifetime.Token);
        using var output=new MemoryStream();ScreenshotFiles.SavePng(output,frame.Preview);
        using var decoded=System.Drawing.Image.FromStream(new MemoryStream(output.ToArray()));
        Require(decoded.Size==frame.Bounds.Size,"Live screenshot PNG dimensions changed.");
        int nonzero=0,samples=0;for(int y=0;y<frame.Preview.Height;y+=Math.Max(1,frame.Preview.Height/24))for(int x=0;x<frame.Preview.Width;x+=Math.Max(1,frame.Preview.Width/32))
        {var pixel=frame.Preview.GetPixel(x,y);if(pixel.R>0||pixel.G>0||pixel.B>0)nonzero++;samples++;}
        Require(frame.Tiles.All(t=>t.DesktopPresentTime>0),"A pointer-only/uninitialized texture became a screenshot.");
        Program.Write("screenshot-capture-test.json",new{Pass=true,frame.Bounds,frame.HasHdr,Tiles=frame.Tiles.Select(t=>new{t.Bounds,t.Hdr,t.WhiteNits,t.DesktopPresentTime,t.ProtectedContent,t.PointerOnlySkipped}),PngBytes=output.Length,NonzeroPreviewSamples=nonzero,PreviewSamples=samples,SavedDesktopImage=false,WindowsActivated=false,ClipboardWritten=false});
    }
    static unsafe bool SamePixels(Bitmap a,Bitmap b)
    {
        var bounds=new Rectangle(Point.Empty,a.Size);var left=a.LockBits(bounds,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
        try
        {
            var right=b.LockBits(bounds,ImageLockMode.ReadOnly,PixelFormat.Format32bppArgb);
            try
            {
                for(int y=0;y<a.Height;y++)
                {
                    var actual=new ReadOnlySpan<byte>((byte*)left.Scan0+y*left.Stride,a.Width*4);var expected=new ReadOnlySpan<byte>((byte*)right.Scan0+y*right.Stride,a.Width*4);
                    if(actual.SequenceEqual(expected))continue;
                    for(int at=0;at<actual.Length;at++)if(actual[at]!=expected[at]){Program.Write("screenshot-render-mismatch.json",new{X=at/4,Y=y,Channel=at%4,Actual=actual[at],Expected=expected[at]});break;}
                    return false;
                }
                return true;
            }
            finally{b.UnlockBits(right);}
        }
        finally{a.UnlockBits(left);}
    }
    internal static void RenderPerformance()=>RenderDamage(2560,1440);
    static void RenderDamage(int width,int height)
    {
        using var frame=new ScreenshotFrame([Sdr(new(0,0,width,height),(x,y)=>Color.FromArgb(x%256,y%256,(x+y)%256))]);
        using var editor=new ScreenshotEditor(frame,new(),_=>{}){SyntheticPointer=true};
        using(var blit=new Bitmap(width,height))
        {
            using(var g=Graphics.FromImage(blit)){g.CompositingMode=System.Drawing.Drawing2D.CompositingMode.SourceCopy;g.InterpolationMode=System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;g.PixelOffsetMode=System.Drawing.Drawing2D.PixelOffsetMode.None;var patch=new Rectangle(76,76,48,48);g.DrawImage(frame.Preview,patch,patch,GraphicsUnit.Pixel);}
            Require(blit.GetPixel(100,99)==frame.Preview.GetPixel(100,99),"Opaque native-pixel patch was filtered or shifted.");
        }
        foreach(int dpi in new[]{96,120,144,192,288})
        {editor.TestDpi(dpi);using var preview=editor.RenderDiagnostic(new(40,40,width-80,height-80));editor.CheckToolbar();Require(editor.LargestButton.Width<=26&&editor.LargestButton.Height<=26,"DPI made the compact editor buttons oversized.");}
        editor.TestDpi(96);editor.Document.Selection=new(40,40,width-80,height-80);editor.TestTool(ScreenshotTool.Pen);
        using var incremental=new Bitmap(width,height);using var reference=new Bitmap(width,height);var area=new Rectangle(0,0,width,height);
        var damage=new List<Rectangle>();editor.DamageObserved=damage.Add;
        editor.TestPointerDown(new(100,100));editor.PaintCanvas(incremental,area);
        var elapsed=new List<double>();long damagedPixels=0;
        for(int i=0;i<96;i++)
        {
            damage.Clear();editor.TestPointerMove(new(100+i*3,100+(int)(Math.Sin(i*.15)*25)));
            Require(damage.Count>0&&damage.All(r=>(long)r.Width*r.Height<(long)width*height/4),"Pen movement invalidated the entire frozen desktop.");
            var watch=Stopwatch.StartNew();foreach(var rect in damage){editor.PaintCanvas(incremental,rect);damagedPixels+=(long)rect.Width*rect.Height;}elapsed.Add(watch.Elapsed.TotalMilliseconds);
            if(i%12==0){editor.PaintCanvas(reference,area);Require(SamePixels(incremental,reference),"Incremental pen rendering left stale pixels or clipped a stroke.");}
        }
        editor.TestPointerUp(new(385,100));editor.TestTool(ScreenshotTool.Select);
        foreach(var pair in new[]{(new Point(40,40),new Point(55,55)),(new Point(200,200),new Point(220,215))})
        {
            editor.TestPointerDown(pair.Item1);editor.PaintCanvas(incremental,area);damage.Clear();editor.TestPointerMove(pair.Item2);
            foreach(var rect in damage)editor.PaintCanvas(incremental,rect);
            editor.PaintCanvas(reference,area);Require(SamePixels(incremental,reference),"Moving/resizing the selection left a shade, border or size-label trail.");editor.TestPointerUp(pair.Item2);
        }
        // Export must use original pixels, never the cached dimmed background.
        using var crop=frame.Crop(new(20,20,20,20));Require(crop.GetPixel(0,0)==frame.Preview.GetPixel(20,20)&&frame.ShadedPreview.GetPixel(20,20)!=crop.GetPixel(0,0),"The display shade contaminated the saved screenshot.");
        Require(!editor.Visible&&!editor.IsHandleCreated,"Offscreen rendering tests created a native input window.");
        elapsed.Sort();Program.Write("screenshot-render-tests.json",new{Pass=true,width,height,Frames=elapsed.Count,MedianPaintMs=elapsed[elapsed.Count/2],P95PaintMs=elapsed[(int)(elapsed.Count*.95)],MaximumPaintMs=elapsed[^1],MeanDamagedPixelRatio=damagedPixels/(double)(width*height*elapsed.Count),IncrementalPixelsMatchFullPaint=true,CompactDpiValues=new[]{96,120,144,192,288},MaximumButtonPixels=26,NativeWindowCreated=false,OsInputInjected=false});
    }
    internal static IReadOnlyList<ScreenshotTile> WorkerFixture(string mode,CancellationToken cancel)
    {
        if(mode=="wait"){cancel.WaitHandle.WaitOne();cancel.ThrowIfCancellationRequested();}
        if(mode=="error")throw new IOException("Synthetic screenshot worker failure.");
        if(mode!="pixels")throw new IOException("Invalid screenshot fixture.");
        return [Sdr(new(-8,-2,8,4),(x,y)=>Color.FromArgb(x*31,y*63,128)),Hdr(new(0,-2,8,4),240,(x,y)=>(x/2f,y/2f,1))];
    }
    static void WorkerTests()
    {
        using var expected=new ScreenshotFrame(WorkerFixture("pixels",default));
        using(var actual=ScreenshotCaptureWorker.Capture(fixture:"pixels"))
        {
            Require(expected.Bounds==actual.Bounds&&actual.HasHdr&&actual.Tiles.Count==2,"Screenshot worker lost monitor metadata.");
            for(int y=0;y<expected.Bounds.Height;y++)for(int x=0;x<expected.Bounds.Width;x++)
                Require(expected.Preview.GetPixel(x,y)==actual.Preview.GetPixel(x,y),"Screenshot worker changed SDR/HDR pixels.");
        }
        using(var ready=new ScreenshotCaptureWorker.Ready(default,"pixels"))
        using(var actual=ready.Capture(default))
        {Require(SamePixels(expected.Preview,actual.Preview),"A prewarmed screenshot worker changed pixels.");}
        // Disposing a ready worker before its request must stop it without ever
        // acquiring a desktop; cancellation must stop an active request too.
        using(var unused=new ScreenshotCaptureWorker.Ready(default,"pixels")){Require(unused.TestPipeClosure(),"Closing the parent request pipe left a screenshot worker alive.");}
        using(var ready=new ScreenshotCaptureWorker.Ready(default,"wait"))
        using(var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(200)))
        {
            bool canceled=false;try{using var ignored=ready.Capture(cancel.Token);}catch(OperationCanceledException){canceled=true;}
            Require(canceled,"Cancellation left a prewarmed worker running.");
        }
        bool rejected=false;try{using var bad=ScreenshotCaptureWorker.Capture(fixture:"error");}
        catch(IOException e){rejected=e.Message=="Synthetic screenshot worker failure.";}Require(rejected,"Screenshot worker swallowed the capture failure.");
        using(var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(1)))
        {
            var watch=Stopwatch.StartNew();rejected=false;
            try{using var ignored=ScreenshotCaptureWorker.Capture(cancel.Token,"wait");}catch(OperationCanceledException){rejected=true;}
            Require(rejected&&watch.Elapsed<TimeSpan.FromSeconds(8),"Screenshot cancellation leaked a waiting worker.");
        }
        using var wire=new MemoryStream();using(var writer=new BinaryWriter(wire,System.Text.Encoding.UTF8,leaveOpen:true))ScreenshotCaptureWorker.Write(writer,WorkerFixture("pixels",default));
        var bytes=wire.ToArray();
        foreach(var invalid in new[]{bytes[..^1],bytes.Concat(new byte[]{1}).ToArray(),Mutate(5,int.MaxValue),Mutate(17,int.MaxValue),Mutate(47,int.MaxValue)})
        {
            rejected=false;try{using var reader=new BinaryReader(new MemoryStream(invalid));using var bad=ScreenshotCaptureWorker.Read(reader);}catch(IOException){rejected=true;}
            Require(rejected,"Unbounded, truncated or trailing screenshot data was accepted.");
        }
        byte[] Mutate(int offset,int value){var copy=bytes.ToArray();BitConverter.TryWriteBytes(copy.AsSpan(offset,4),value);return copy;}
        // The same all-black pixels must be rejected without a desktop present,
        // and accepted for a genuinely presented black screen. Do not guess
        // validity from brightness or exclude dark screenshots.
        foreach(long presented in new[]{0L,1L})
        {
            using var message=new MemoryStream();using(var writer=new BinaryWriter(message,System.Text.Encoding.UTF8,leaveOpen:true))
                ScreenshotCaptureWorker.Write(writer,[new(new(0,0,1,1),1,1,ModeRotation.Identity,false,false,80,[0,0,0,255],presented)]);
            message.Position=0;rejected=false;
            try{using var reader=new BinaryReader(message,System.Text.Encoding.UTF8,leaveOpen:true);using var black=ScreenshotCaptureWorker.Read(reader);Require(black.Preview.GetPixel(0,0).R==0,"A valid black desktop was altered.");}
            catch(IOException){rejected=true;}
            Require(rejected==(presented==0),"Uninitialized and legitimately black screenshots were confused.");
        }
    }
    internal static void ConcurrentCapture()
    {
        // Hold the real replay/NDI duplication in this process while requesting
        // the production screenshot path. No windows, input or image files.
        var display=DisplayInfo.All().First();using var capture=new DesktopCapture(display,true){FrameWaitMilliseconds=10};
        bool collision=false;
        try{using var forbidden=ScreenshotFrame.Capture();}
        catch(SharpGen.Runtime.SharpGenException e) when(e.ResultCode.Code==unchecked((int)0x80070057)){collision=true;}
        Require(collision,"Could not reproduce the original in-process DXGI duplication conflict.");
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        long reads=0,frames=0;var task=Task.Run(()=>ScreenshotCaptureWorker.Capture(timeout.Token),timeout.Token);
        while(!task.IsCompleted){using var image=capture.Next();reads++;if(image!=null)frames++;}
        using var frame=task.GetAwaiter().GetResult();using var after=capture.Next();
        Require(frame.Bounds.Width>0&&reads>0,"Screenshot did not run alongside continuous capture.");
        using var output=new MemoryStream();ScreenshotFiles.SavePng(output,frame.Preview);
        using var decoded=Image.FromStream(new MemoryStream(output.ToArray()));Require(decoded.Size==frame.Bounds.Size,"Concurrent PNG dimensions changed.");
        Program.Write("screenshot-concurrent-capture-test.json",new{Pass=true,OriginalDxgiConflictReproduced=collision,IsolatedScreenshotSucceeded=true,CaptureReadsDuringScreenshot=reads,FramesDuringScreenshot=frames,ContinuousCaptureStillUsable=true,frame.Bounds,frame.HasHdr,PngBytes=output.Length,SavedDesktopImage=false,WindowsActivated=false,ClipboardWritten=false});
    }
    static void ClipboardRoundTrip()
    {
        using var image=new Bitmap(127,63);using(var g=Graphics.FromImage(image)){g.Clear(Color.CornflowerBlue);g.FillRectangle(Brushes.Black,12,8,30,20);}
        var payload=ScreenshotClipboard.Prepare(image);var owner=new NativeWindow();owner.CreateHandle(new CreateParams{Parent=new IntPtr(-3)});
        var watch=Stopwatch.StartNew();try{ScreenshotClipboard.Publish(owner.Handle,payload);}finally{owner.DestroyHandle();}
        double publishMs=watch.Elapsed.TotalMilliseconds;
        using var pasted=Clipboard.GetImage();Require(pasted!=null&&pasted.Size==image.Size,"Native screenshot bitmap is unavailable after editor close; formats="+string.Join(",",Clipboard.GetDataObject()?.GetFormats()??[])+"; size="+pasted?.Size);
        using var decoded=new Bitmap(pasted!);Require(decoded.GetPixel(0,0).ToArgb()==Color.CornflowerBlue.ToArgb()&&decoded.GetPixel(15,12).ToArgb()==Color.Black.ToArgb(),"Clipboard changed colors or exposed redacted pixels.");
        var data=Clipboard.GetDataObject()??throw new Exception("Clipboard is empty.");
        using var png=data.GetData("PNG",false) as MemoryStream;Require(png!=null&&Pixel(png.ToArray(),15,12,out _).ToArgb()==Color.Black.ToArgb(),"Clipboard PNG did not survive its owner window.");
        using var transferred=KvmClipboardImage.Capture(data);Require(transferred!=null,"KVM cannot read the screenshot clipboard.");
        Program.Write("screenshot-clipboard-tests.json",new{Pass=true,NativePublishMs=publishMs,OwnerDestroyedBeforePaste=true,WindowsBitmap=true,Png=true,Redaction=true,KvmCompatible=true});
    }
    internal static void WarmCapture()
    {
        using var cancel=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var watch=Stopwatch.StartNew();using var cold=ScreenshotCaptureWorker.Capture(cancel.Token);double coldMs=watch.Elapsed.TotalMilliseconds;
        watch.Restart();using var ready=new ScreenshotCaptureWorker.Ready(cancel.Token);double initializationMs=watch.Elapsed.TotalMilliseconds;
        watch.Restart();using var warm=ready.Capture(cancel.Token);double warmMs=watch.Elapsed.TotalMilliseconds;
        Require(cold.Bounds==warm.Bounds&&warm.Tiles.All(t=>t.DesktopPresentTime>0),"A warmed worker lost desktop presentation metadata.");
        Program.Write("screenshot-warm-capture-test.json",new{Pass=true,ColdCaptureMs=coldMs,WarmInitializationMs=initializationMs,WarmRequestMs=warmMs,warm.Bounds,warm.HasHdr,Metadata=warm.Tiles.Select(t=>new{t.DesktopPresentTime,t.PointerOnlySkipped}),DesktopImageSaved=false,WindowsActivated=false,ClipboardWritten=false});
    }
    internal static void Ui()
    {
        if(EventWaitHandle.TryOpenExisting("Local\\SdrCapture.OpenKvm",out var running))
        {running.Dispose();throw new InvalidOperationException("Close DeskGlide before screenshot UI tests; they activate a synthetic test window.");}
        ClipboardRoundTrip();
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
                    editor.Refresh();Require(editor.PaintCount>0,"The shown screenshot editor never painted its native window.");
                    editor.TestAction("Undo");Require(!editor.Document.CanUndo&&editor.Document.CanRedo,"Editor Undo button failed.");
                    var copy=editor.Controls.OfType<FlowLayoutPanel>().SelectMany(bar=>bar.Controls.Cast<Control>()).Single(button=>Equals(button.Tag,"Copy"));
                    SendMessage(copy.Handle,0x007b,copy.Handle,-1);Require(editor.MoreVisible&&!editor.IsDisposed,"The toolbar context menu failed to open or closed the editor.");
                    editor.TestAction("Redo");editor.CloseMore();Application.DoEvents();Require(!editor.IsDisposed&&editor.Document.CanUndo,"Editor Redo menu action failed or dismissed the screenshot.");
                    Require(editor.TestKey(Keys.Control|Keys.Z)&&!editor.Document.CanUndo&&editor.Document.CanRedo,"Ctrl+Z did not undo the annotation while a toolbar had focus.");
                    Require(!editor.TestKey(Keys.Control|Keys.S)&&!editor.TestKey(Keys.Control|Keys.C),"Removed copy/save key combination still executes an action.");
                    editor.TestTool(ScreenshotTool.Pen);editor.TestDrag(new(80,90),new(70,80));Require(editor.Document.Selection==new Rectangle(70,80,740,430),"Crop handles stopped resizing when a drawing tool was selected.");
                    editor.CheckToolbar();
                    editor.TestAction("Redo");editor.TestTool(ScreenshotTool.Text);editor.TestDrag(new(200,200),new(200,200));
                    Require(editor.EditingText&&!editor.TestKey(Keys.Control|Keys.Z)&&editor.Document.CanUndo,"Ctrl+Z during text entry intercepted native text undo or removed a drawing.");
                    editor.TestKey(Keys.Escape);Require(!editor.IsDisposed,"Escape during text editing closed the capture.");
                }
                catch(Exception e){failure=e;}
                finally{editor.Close();}
            });
            Application.Run(editor);if(failure!=null)throw failure;
        }
        Program.Write("screenshot-ui-tests.json",new{Pass=true,Languages=new[]{"en","ru"},NativeWindowShown=true,NativeWindowPainted=true,SelectionAndResize=true,AnnotationAndUndoButtons=true,ToolbarContextMenu=true,CtrlZUndo=true,RemovedCopySaveChords=true,ResizeWhileDrawing=true,TextCancel=true,ToolbarBounds=true,DesktopCaptured=false,OsInputInjected=false,ClipboardWritten=false,PrintScreenPressed=false});
    }
    [System.Runtime.InteropServices.DllImport("user32.dll",EntryPoint="SendMessageW")] static extern nint SendMessage(nint window,int message,nint wParam,nint lParam);
}
