namespace SdrCapture;

static class RecordingFrameSelection
{
    // Oversampled input can choose the nearest picture. Near-output-rate input
    // must conserve chronological images instead of skipping one and holding
    // the next. Density is measured from distinct, already-buffered pictures.
    internal static int Index(ReadOnlySpan<long> times,long due,long previous,long tolerance,long period=0)
    {
        if(times.IsEmpty)return -1;
        if(tolerance>0&&previous>=0)
        {
            if(period<=0)period=tolerance;
            long end=Math.Min(times[^1],due+period*8);
            int first=0,last=times.Length-1;
            while(first<times.Length&&times[first]<due)first++;
            while(last>first&&times[last]>end)last--;
            bool dense=last-first>=4&&(long)(last-first)*period*4>=(times[last]-times[first])*7;
            // A short burst is not sustained oversampling. Choosing its nearest
            // image may consume the only image for a following deadline. Only
            // skip chronological candidates when the buffered continuation can
            // supply the buffered output slots within the bound. Use only
            // fully buffered deadlines: requiring an unavailable fixed horizon
            // would toggle nearest/chronological pacing as readback age varies.
            int lookahead=(int)Math.Clamp((times[^1]-due-tolerance)/period,0,8);
            dense&=lookahead>=2;
            int best=-1;long distance=long.MaxValue;
            for(int i=0;i<times.Length;i++)
                if(times[i]>previous&&times[i]>=due-tolerance&&times[i]<=due+tolerance)
                {
                    if(!dense)return i;
                    long error=Math.Abs(times[i]-due);
                    if(error<distance&&ContinuationFits(times,i,due,tolerance,period,lookahead)){distance=error;best=i;}
                }
            if(best>=0)return best;
            for(int i=0;i<times.Length;i++)
                if(times[i]>previous&&times[i]>=due-tolerance&&times[i]<=due+tolerance)return i;
        }
        for(int i=times.Length-1;i>=0;i--)if(times[i]<=due)return i;
        return 0;
    }
    static bool ContinuationFits(ReadOnlySpan<long> times,int at,long due,long tolerance,long period,int slots)
    {
        for(int slot=1;slot<=slots;slot++)
        {
            long target=due+period*slot;
            do{at++;}while(at<times.Length&&times[at]<target-tolerance);
            if(at==times.Length||times[at]>target+tolerance)return false;
        }
        return true;
    }
}
