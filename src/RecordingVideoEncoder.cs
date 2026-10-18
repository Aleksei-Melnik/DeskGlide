using System.Diagnostics;
using System.IO.Pipes;

namespace SdrCapture;

// Encode video independently of the delayed WASAPI input. The muxer can wait
// for audio with a small queue of compressed packets, rather than raw frames.
// This is still one hardware encoder; the second process only copies video.
sealed class RecordingVideoEncoder:IDisposable
{
    readonly Process process;
    readonly NamedPipeServerStream output;
    readonly CancellationTokenSource stop;
    readonly Task transport,stderr;
    readonly Queue<string> errors=[];
    int disposed;
    internal string PipeName {get;}="DeskGlide-encoded-"+Guid.NewGuid().ToString("N");
    internal RecordingVideoEncoder(string rawPipe,int sourceWidth,int sourceHeight,int width,int height,int fps,RecordingEncoder encoder,CancellationToken token)
    {
        stop=CancellationTokenSource.CreateLinkedTokenSource(token);
        output=new(PipeName,PipeDirection.Out,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,0,1024*1024);
        var args=new List<string>{"-hide_banner","-loglevel","warning","-nostdin","-y",
            "-thread_queue_size","8","-threads","1","-probesize","32","-analyzeduration","0"};
        args.AddRange(RecordingSdr.RawInput(rawPipe,sourceWidth,sourceHeight,fps));
        args.AddRange(["-map","0:v","-an"]);
        args.AddRange(encoder.Arguments);
        args.AddRange(RecordingScale.Arguments(encoder,sourceWidth,sourceHeight,width,height));
        args.AddRange(RecordingSdr.Tags);
        args.AddRange(["-f","nut","-flush_packets","1","pipe:1"]);
        try{process=Process.Start(ReplayTools.StartInfo(ReplayTools.Ffmpeg,args))??throw new IOException("Video encoder did not start.");}
        catch{output.Dispose();stop.Dispose();throw;}
        try{process.PriorityClass=ProcessPriorityClass.AboveNormal;}catch(System.ComponentModel.Win32Exception){}
        stderr=Task.Run(async()=>{while(await process.StandardError.ReadLineAsync() is string line){lock(errors){errors.Enqueue(line);while(errors.Count>12)errors.Dequeue();}}});
        transport=Task.Factory.StartNew(()=>
        {
            Thread.CurrentThread.Priority=ThreadPriority.AboveNormal;
            using var writer=new RecordingPipeWriter(output);
            try
            {
                output.WaitForConnectionAsync(stop.Token).GetAwaiter().GetResult();
                byte[] data=new byte[65536];int count;
                while((count=process.StandardOutput.BaseStream.Read(data))>0)writer.Write(data,count,stop.Token);
            }
            finally{output.Dispose();}
        },CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
    }
    internal void Check()
    {
        if(process.HasExited){lock(errors)throw new IOException("Video encoder: "+string.Join(" ",errors));}
        if(transport.IsCompleted)transport.GetAwaiter().GetResult();
    }
    public void Dispose()
    {
        if(Interlocked.Exchange(ref disposed,1)!=0)return;
        stop.Cancel();output.Dispose();
        // Closing the raw input normally ends FFmpeg. Bound a stalled driver
        // too, so transport's stdout read cannot keep the recorder alive.
        if(!process.WaitForExit(1500))
        {
            try{process.Kill(true);}catch(Exception e)when(e is InvalidOperationException or System.ComponentModel.Win32Exception){Log.Write("Video encoder stop: "+e.Message);}
            process.WaitForExit(1500);
        }
        MediaShutdown.Drain([transport,stderr],()=>{process.Dispose();stop.Dispose();},2000);
    }
}
