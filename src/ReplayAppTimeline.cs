namespace SdrCapture;

// Folder selection describes the exported interval, rather than whichever
// launcher/desktop happened to have focus when Save was pressed.
sealed class ReplayAppTimeline
{
    readonly object gate=new();
    readonly List<(double At,string App)> changes=[];
    public string Last{get{lock(gate)return changes.Count==0?"Desktop":changes[^1].App;}}
    public void Observe(string app,double at)
    {
        if(!double.IsFinite(at)||string.IsNullOrWhiteSpace(app))return;
        lock(gate)
        {
            if(changes.Count>0&&at<changes[^1].At)return;
            if(changes.Count==0||!changes[^1].App.Equals(app,StringComparison.OrdinalIgnoreCase))changes.Add((at,app));
            while(changes.Count>1&&changes[1].At<at-7260)changes.RemoveAt(0);
        }
    }
    public string Resolve(double start,double end)
    {
        if(!double.IsFinite(start)||!double.IsFinite(end)||end<=start)return "Desktop";
        lock(gate)
        {
            var weights=new Dictionary<string,(double Seconds,double Last)>(StringComparer.OrdinalIgnoreCase);
            if(changes.Count==0)return "Desktop";
            double unknown=Math.Max(0,Math.Min(end,changes[0].At)-start);
            if(unknown>0)weights["Desktop"]=(unknown,start);
            for(int i=0;i<changes.Count;i++)
            {
                var item=changes[i];double until=i+1<changes.Count?changes[i+1].At:end;
                double seconds=Math.Min(end,until)-Math.Max(start,item.At);if(seconds<=0)continue;
                var prior=weights.GetValueOrDefault(item.App);weights[item.App]=(prior.Seconds+seconds,item.At);
            }
            return weights.OrderByDescending(p=>p.Value.Seconds).ThenByDescending(p=>p.Value.Last).Select(p=>p.Key).FirstOrDefault()??"Desktop";
        }
    }
}
