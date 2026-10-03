using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
namespace SdrCapture;
static class SelfTest
{
    public static void Run()
    {
        using var device = D3D11.D3D11CreateDevice(DriverType.Hardware, DeviceCreationFlags.BgraSupport, FeatureLevel.Level_11_0);
        using var context = device.ImmediateContext;
        using var pipeline = new ColorPipeline(device, context);
        byte[]? reference = null;
        foreach (float scale in new[] { 1f, 2.5f, 6f })
        {
            // FP16 synthetic SDR values after compositor SDR-white multiplication.
            Half[] data = [ (Half)(.1f*scale), (Half)(.3f*scale), (Half)(.8f*scale), (Half)1 ];
            using var texture = device.CreateTexture2D(new Texture2DDescription(Format.R16G16B16A16_Float, 1, 1, 1, 1, BindFlags.ShaderResource));
            context.UpdateSubresource(data, texture, 0, 8);
            var result = pipeline.Convert(texture, true, 1 / scale).ToArray();
            if (reference != null && System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(result).ToArray()
                .Zip(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(reference).ToArray()).Any(p => Math.Abs(p.First - p.Second) > 40))
                throw new Exception("SDR white normalization failed.");
            reference = result;
        }
        foreach (var sample in new[] { new[] { 0f, 0f, 0f }, new[] { 125f, 20f, -2f }, new[] { float.NaN, float.PositiveInfinity, 1f } })
        {
            Half[] data = [(Half)sample[0], (Half)sample[1], (Half)sample[2], (Half)1];
            using var texture = device.CreateTexture2D(new Texture2DDescription(Format.R16G16B16A16_Float, 1, 1, 1, 1, BindFlags.ShaderResource));
            context.UpdateSubresource(data, texture, 0, 8);
            var result = pipeline.Convert(texture, true, 1);
            var codes = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, ushort>(result);
            if (codes[3] != 65535) throw new Exception("Invalid alpha.");
            if (sample[0] == 0 && codes[..3].ToArray().Any(v => v != 0)) throw new Exception("Black level failed.");
            if (sample[0] == 125 && !(codes[0] > codes[1] && codes[1] > codes[2])) throw new Exception("Highlight mapping failed.");
            if (float.IsNaN(sample[0]) && (codes[0] != 0 || codes[1] != 0)) throw new Exception("Nonfinite sanitization failed.");
        }
        using var unsupported = device.CreateTexture2D(new Texture2DDescription(Format.R10G10B10A2_UNorm, 1, 1, 1, 1, BindFlags.ShaderResource));
        bool rejected = false;
        try { pipeline.Convert(unsupported, true, 1); } catch (InvalidOperationException) { rejected = true; }
        if (!rejected) throw new Exception("Unsafe HDR format was accepted.");
        using var block = device.CreateTexture2D(new Texture2DDescription(Format.R16G16B16A16_Float,2,2,1,1,BindFlags.ShaderResource));
        foreach(float level in new[]{0f,1f})
        {
            Half[] values = Enumerable.Range(0,16).Select(i=>(Half)(i%4==3?1:level)).ToArray();
            context.UpdateSubresource(values,block,0,16);
            var p010 = System.Runtime.InteropServices.MemoryMarshal.Cast<byte,ushort>(pipeline.ConvertP010(block,false,1));
            if(p010.Length!=6 || p010[..4].ToArray().Any(v=>v!=(level==0?64:940)*64) || p010[4]!=512*64 || p010[5]!=512*64)
                throw new Exception("GPU P010 black/white/range/neutral-chroma failed.");
        }
        context.UpdateSubresource(new Half[16],block,0,16);
        pipeline.Cursor.UploadShape(new OutduplPointerShapeInfo { Type=(uint)PointerShapeType.Color,Width=1,Height=1,Pitch=4 },[0,0,255,255]);
        pipeline.Cursor.SetTestPosition(1,1,true);
        var pointer=System.Runtime.InteropServices.MemoryMarshal.Cast<byte,ushort>(pipeline.Convert(block,false,1));
        if(pointer[12]!=65535 || pointer[13]!=0 || pointer[14]!=0 || pointer[0]!=0) throw new Exception("Color cursor position/alpha failed.");
        pipeline.Cursor.UploadShape(new OutduplPointerShapeInfo { Type=(uint)PointerShapeType.Monochrome,Width=1,Height=2,Pitch=1 },[0x80,0x80]);
        pointer=System.Runtime.InteropServices.MemoryMarshal.Cast<byte,ushort>(pipeline.Convert(block,false,1));
        if(pointer[12]!=65535 || pointer[13]!=65535 || pointer[14]!=65535) throw new Exception("Monochrome invert cursor failed.");
        pipeline.Cursor.UploadShape(new OutduplPointerShapeInfo { Type=(uint)PointerShapeType.MaskedColor,Width=1,Height=1,Pitch=4 },[255,0,0,255]);
        pointer=System.Runtime.InteropServices.MemoryMarshal.Cast<byte,ushort>(pipeline.Convert(block,false,1));
        if(pointer[12]!=0 || pointer[13]!=0 || pointer[14]!=65535) throw new Exception("Masked color cursor failed.");
        pipeline.Cursor.SetTestPosition(-1,-1,true);
        pointer=System.Runtime.InteropServices.MemoryMarshal.Cast<byte,ushort>(pipeline.Convert(block,false,1));
        if(pointer[12]!=0 || pointer[13]!=0 || pointer[14]!=0) throw new Exception("Off-screen cursor clipping failed.");
        File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "self-test.txt"), "PASS: real GPU shader, synthetic FP16 SDR-white invariance (80/200/480 nits, <=40 RGB16 codes = <1 output 10-bit code), black, highlights, nonfinite handling, unsupported HDR rejection. This does NOT prove mixed HDR/SDR desktop invariance or network performance.");
        File.AppendAllText(Path.Combine(AppContext.BaseDirectory,"self-test.txt"), "\nPASS v0.2: GPU P010 limited black/white and neutral chroma; GPU color, masked and monochrome cursor, position, clipping.");
    }
}
