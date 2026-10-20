namespace SdrCapture;

static class RecordingFrameSelection
{
    // Stay closest to the CFR deadline while advancing in source order. The
    // recording epoch is aligned to a real source frame; the bounded lookahead
    // avoids repeat/drop pairs when a presentation arrives after its deadline.
    internal static int Index(ReadOnlySpan<long> times,long due,long previous,long tolerance)
    {
        if(times.IsEmpty)return -1;
        if(tolerance>0&&previous>=0)
        {
            int best=-1;long distance=long.MaxValue;
            for(int i=0;i<times.Length;i++)
                if(times[i]>previous&&times[i]>=due-tolerance&&times[i]<=due+tolerance)
                {
                    long error=Math.Abs(times[i]-due);
                    if(error<distance){distance=error;best=i;}
                }
            if(best>=0)return best;
        }
        for(int i=times.Length-1;i>=0;i--)if(times[i]<=due)return i;
        return 0;
    }
}
