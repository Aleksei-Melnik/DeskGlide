using System.Text.Json;
namespace SdrCapture;
internal sealed record RecordingExport(ReplayResult Result,double SourceEnd);
static class ReplayClipExporter
{
    internal static async Task<RecordingExport> ExportAsync(ReplayChunk[] selected,ReplayOptions config,double wantedStart,double pressed,string? appFolder,string prefix)
    {
        if(selected.Length==0)throw new IOException("No completed recording segments are available.");
        if(selected.Any(c=>c.Codec!=selected[0].Codec||c.Fps!=selected[0].Fps||c.Width!=selected[0].Width||c.Height!=selected[0].Height))throw new IOException("Recording format changed; completed segments remain available for recovery.");
        string? listPath=null,partialPath=null;
        try
        {
            string exports=Path.Combine(ReplayTools.Root,"saved");Directory.CreateDirectory(exports);
            string name=$"{prefix}-{DateTime.Now:yyyy-MM-dd-HH-mm-ss}-{Guid.NewGuid().ToString("N")[..6]}.mp4";
            string output=Path.Combine(exports,name),partial=output+".partial";partialPath=partial;
            listPath=output+".ffconcat";
            // AAC preroll can give a chunk a negative container start time.
            // Anchor concat to video time zero so it cannot shift the frame grid.
            File.WriteAllLines(listPath,selected.SelectMany(c=>new[]{"file '"+c.Path.Replace("\\","/").Replace("'","'\\''")+"'","inpoint 0","duration "+ReplayTools.Number(c.End-c.Start)}));
            double offset=0;
            foreach(var chunk in selected)
            {
                if(wantedStart>=chunk.End)offset+=chunk.End-chunk.Start;
                else{offset+=Math.Max(0,wantedStart-chunk.Start);break;}
            }
            // Snap both sides to the same frame grid. MP4 edit lists retain the
            // keyframe preroll for decoding but hide already exported frames.
            int fps=selected[0].Fps;
            offset=ReplaySaveTime.Boundary(offset,fps);
            double end=selected.Sum(c=>c.End-c.Start)-Math.Max(0,selected[^1].End-pressed);
            end=ReplaySaveTime.Boundary(end,fps);
            double duration=end-offset;
            if(duration<1.0/fps-.001)throw new InvalidOperationException("Буфер ещё пуст. Подождите несколько секунд после включения записи.");
            using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(180,selected.Sum(c=>c.Bytes)/(16.0*1024*1024)+60)));
            var exportArgs=new List<string>{"-hide_banner","-loglevel","error","-nostdin","-y","-ss",ReplayTools.Number(ReplaySaveTime.Packet(offset)),"-f","concat","-safe","0","-i",listPath,"-t",ReplayTools.Number(ReplaySaveTime.Packet(end)-ReplaySaveTime.Packet(offset)-.001),"-map","0","-c","copy","-avoid_negative_ts","disabled","-use_editlist","1","-movflags","+faststart"};
            if(selected[0].Codec=="HEVC")exportArgs.AddRange(["-tag:v","hvc1"]);
            if(config.IsSilent)exportArgs.Add("-an");
            exportArgs.AddRange(["-f","mp4",partial]);
            await ReplayTools.RunAsync(ReplayTools.Ffmpeg,exportArgs,timeout.Token);
            if(new FileInfo(partial).Length<1024)throw new IOException("Пустой файл повтора.");
            File.Move(partial,output);
            appFolder??=ReplayAppContext.ForInterval(selected[0].Start+offset,selected[0].Start+end);
            string destination=ReplayAppContext.Destination(config,appFolder,name);
            File.WriteAllText(output+".delivery.tmp",JsonSerializer.Serialize(new DeliveryJob(output,destination,duration)));
            File.Move(output+".delivery.tmp",output+".delivery.json");
            double sourceEnd=selected[^1].End-(selected.Sum(c=>c.End-c.Start)-end);
            return new(new ReplayResult(output,destination,duration,"Video is ready; delivery to the configured folder is queued."),sourceEnd);
        }
        finally
        {foreach(string? file in new[]{listPath,partialPath})if(file!=null)try{File.Delete(file);}catch(IOException){}catch(UnauthorizedAccessException){}}
    }
}
