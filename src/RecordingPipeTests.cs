using System.Diagnostics;
using System.IO.Pipes;

namespace SdrCapture;
static class RecordingPipeTests
{
    public static void Run()
    {
        foreach(string scenario in new[]{"transfer","cancel","disconnect","dispose"})
        {
            string name="DeskGlide-pipe-test-"+Guid.NewGuid().ToString("N");
            using var server=new NamedPipeServerStream(name,PipeDirection.Out,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,0,1024);
            using var client=new NamedPipeClientStream(".",name,PipeDirection.In);
            var connection=server.WaitForConnectionAsync();client.Connect(2000);connection.GetAwaiter().GetResult();
            using var stop=new CancellationTokenSource();
            byte[] sent=new byte[3*1024*1024],received=new byte[sent.Length];new Random(81).NextBytes(sent);
            var write=Task.Factory.StartNew(()=>{using var writer=new RecordingPipeWriter(server);writer.Write(sent,sent.Length,stop.Token);},CancellationToken.None,TaskCreationOptions.LongRunning,TaskScheduler.Default);
            if(scenario=="transfer")
            {
                client.ReadExactly(received);write.GetAwaiter().GetResult();
                if(!sent.AsSpan().SequenceEqual(received))throw new Exception("Media pipe changed or truncated bytes");
            }
            else
            {
                Thread.Sleep(50);if(write.IsCompleted)throw new Exception("Expected a blocked pipe write");
                var clock=Stopwatch.StartNew();
                if(scenario=="disconnect")client.Dispose();
                else{stop.Cancel();if(scenario=="dispose")server.Dispose();}
                if(!SpinWait.SpinUntil(()=>write.IsCompleted,2000))throw new Exception("Media pipe shutdown hung: "+scenario);
                bool expected=false;
                try{write.GetAwaiter().GetResult();}
                catch(OperationCanceledException)when(scenario!="disconnect"){expected=true;}
                catch(IOException)when(scenario=="disconnect"){expected=true;}
                if(!expected||clock.ElapsedMilliseconds>1000)throw new Exception("Unexpected media pipe cancellation: "+scenario);
            }
        }
        Program.Write("recording-pipe-test.json",new{Pass=true,ByteExact=true,BlockedWriteCancelled=true,DisconnectHandled=true,ConcurrentDisposeHandled=true});
    }
}
