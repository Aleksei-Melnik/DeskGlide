namespace SdrCapture;

static class RecordingCadenceTests
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    public static void Run()
    {
        int legacyRepeats=0,actualRepeats=0;
        foreach(int fps in new[]{60,120})foreach(long phase in new long[]{0,1000,4000,7000,10000,15000})
        {
            long period=1000000/fps;
            long[] source=Enumerable.Range(0,600).Select(i=>1000000L+i*period+(i%2==0?-1200:1200)).ToArray();
            long oldPrevious=-1,newPrevious=-1;int oldIndex=-1,newIndex=-1;
            for(int frame=10;frame<580;frame++)
            {
                long due=1000000L+frame*period+phase;
                int a=RecordingFrameSelection.Index(source,due,oldPrevious,0);
                int b=RecordingFrameSelection.Index(source,due,newPrevious,period);
                if(a==oldIndex)legacyRepeats++;if(b==newIndex)actualRepeats++;
                Require(b>=newIndex,"Jitter recovery reordered source frames.");
                oldPrevious=source[a];newPrevious=source[b];oldIndex=a;newIndex=b;
            }
        }
        Require(legacyRepeats>100&&actualRepeats==0,"Jitter resampling did not remove artificial repeat/drop pairs.");
        // Relative times from a real 120 FPS desktop/NDI trace: the 3/4-period
        // window discarded the -8.689 ms image and repeated its successor.
        // All three images were available before encoding (100 ms playout).
        long[] observed=[991311,1000000,1012438,1020425];
        long prior=980000,legacyPrior=prior;int repeats=0,legacyWindowRepeats=0;
        for(int frame=0;frame<3;frame++)
        {
            long due=997751+frame*8333;
            long selected=observed[RecordingFrameSelection.Index(observed,due,prior,8333)];
            long legacy=observed[RecordingFrameSelection.Index(observed,due,legacyPrior,6249)];
            if(selected==prior)repeats++;if(legacy==legacyPrior)legacyWindowRepeats++;
            Require(selected>prior&&Math.Abs(selected-due)<=8333,"Selector repeated/reordered an available image or exceeded its timing bound.");
            prior=selected;legacyPrior=legacy;
        }
        Require(repeats==0&&legacyWindowRepeats==1,"Real 120 FPS trace did not reproduce and eliminate the narrow-window repeat.");
        foreach(int fps in new[]{60,120})
        {
            long period=1000000/fps;
            long[] source=Enumerable.Range(0,600).Select(i=>1000000L+i*period+(i%2==0?-1200:1200)).ToArray();
            long previous=-1;
            for(int frame=20;frame<580;frame++)
            {
                long due=source[20]+(frame-20)*period;
                int at=RecordingFrameSelection.Index(source,due,previous,period);
                Require(at==frame,"Source-aligned output discarded an available equal-rate jittered image.");
                previous=source[at];
            }
        }
        int legacyUneven=0,integerUneven=0;
        foreach(int fps in new[]{60,120})foreach(long phase in new long[]{1000,11000,27000})
        {
            const long frequency=12000000;long period=frequency/fps;
            foreach(bool integer in new[]{false,true})
            {
                int rate=integer?StreamEngine.RecordingCaptureRate(fps):fps*6/5;
                long[] times=Enumerable.Range(0,rate*10).Select(n=>(long)n*240/rate*(frequency/240)).ToArray();
                long previous=-1;int last=-1;
                for(int frame=0;frame<fps*9;frame++)
                {
                    int at=RecordingFrameSelection.Index(times,frame*period+phase,previous,period);
                    int id=(int)(times[at]/(frequency/240));
                    if(frame>20&&id-last!=240/fps){if(integer)integerUneven++;else legacyUneven++;}
                    previous=times[at];last=id;
                }
            }
        }
        Require(legacyUneven>100&&integerUneven==0,"Acquisition/output beat still adds irregular motion to a uniformly moving source.");
        foreach(int rate in new[]{60,120,240})
        {
            const long origin=73;
            Require(Pacer.Deadline(origin,rate*3600L,rate)==origin+System.Diagnostics.Stopwatch.Frequency*3600L,"Pacing clock accumulated tick-rounding drift over an hour.");
            long[] intervals=Enumerable.Range(0,rate*2).Select(i=>Pacer.Deadline(origin,i+1,rate)-Pacer.Deadline(origin,i,rate)).ToArray();
            Require(intervals.Max()-intervals.Min()<=1,"Rational pacing introduced a discontinuous deadline.");
        }
        Require(RecordingFrameSelection.Index([],100,-1,20)==-1,"Empty source history accepted.");
        Require(RecordingFrameSelection.Index([10,20,100],50,20,10)==1,"Missing source footage was replaced with a distant future frame.");
        long[] upcoming=[0,8,14,23,39,47,55,63,71,79,87,95];
        Require(RecordingFrameSelection.Index(upcoming,20,8,8)==2,"Nearest selection skipped an available image and caused an avoidable next-slot repeat.");
        Require(RecordingFrameSelection.Index(upcoming,28,14,8)==3,"Lookahead did not preserve the next distinct source image.");
        long[] burst=[85,94,100,102,104,106,108,110,112,114,116,118,120,122,124,126,190,200,210];
        Require(RecordingFrameSelection.Index(burst,100,80,20,10)==0,"A short source burst consumed an image needed across the following render gap.");
        Require(RecordingFrameSelection.Index([85,94,100,102,104,106],100,80,20,10)==0,"An incomplete lookahead was mistaken for sustained oversampling.");
        Require(RecordingFrameSelection.Index(Enumerable.Range(0,50).Select(i=>(long)i*5).ToArray(),100,90,20,10)==20,"Sustained oversampling lost nearest-image motion pacing.");
        Require(RecordingFrameSelection.Index(Enumerable.Range(0,32).Select(i=>(long)i*5).ToArray(),100,90,20,10)==20,"Readback age changed motion phase despite sufficient buffered continuation.");
        VariableCaptureCadence();
        NearRateJitter();
        WideWindowJitter();
        BufferedGapWithRecovery();
        Program.Write("recording-cadence-tests.json",new{Pass=true,Fps=new[]{60,120},LegacyArtificialRepeats=legacyRepeats,NewArtificialRepeats=actualRepeats,NarrowWindowRepeats=legacyWindowRepeats,RealTraceRepeats=repeats,LegacyUnevenMotionSteps=legacyUneven,NewUnevenMotionSteps=integerUneven,NoAccumulatedPacingDrift=true,SourceOrderPreserved=true,RealSourceGapsNotInvented=true,VariableCapturePhaseStable=true});
    }
    static void VariableCaptureCadence()
    {
        const long frequency=12000000,origin=frequency;
        var results=new List<(int Fps,double ErrorMs,double BoundMs)>();
        foreach(int fps in new[]{60,120})
        {
            long period=frequency/fps,previous=-1,maximumError=0;
            double source=origin;var history=new List<long>();
            for(int frame=0;frame<fps*20;frame++)
            {
                long due=origin+frame*period;
                // Every source interval is shorter than the output interval.
                // Changing capture density must not change the motion phase.
                while(source<=due+frequency/10)
                {
                    history.Add((long)Math.Round(source));
                    int rate=((long)(source-origin)/(frequency*15/100)%2)==0?fps*3/2:fps*2;
                    source+=frequency/(double)rate;
                }
                int at=RecordingFrameSelection.Index(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(history),due,previous,frequency/50,period);
                long selected=history[at];Require(selected>previous,"Changing capture density repeated or reordered an available picture.");
                if(frame>=fps)maximumError=Math.Max(maximumError,Math.Abs(selected-due));
                previous=selected;if(at>0)history.RemoveRange(0,at);
            }
            double bound=frequency/(double)(fps*3/2)/2+2;
            results.Add((fps,maximumError*1000.0/frequency,bound*1000/frequency));
        }
        bool pass=results.All(r=>r.ErrorMs<=r.BoundMs);
        Program.Write("recording-variable-cadence-tests.json",new{Pass=pass,Cases=results.Select(r=>new{r.Fps,MaximumPhaseErrorMs=r.ErrorMs,NearestPictureBoundMs=r.BoundMs})});
        Require(pass,"Variable capture density changed recording motion phase despite buffered pictures: "+string.Join("; ",results.Select(r=>$"{r.Fps} FPS {r.ErrorMs:F3} ms (bound {r.BoundMs:F3} ms)")));
    }
    static void NearRateJitter()
    {
        const long frequency=12000000,origin=frequency;
        foreach(int fps in new[]{60,120})
        {
            long period=frequency/fps,previous=-1;int next=0;var history=new List<long>();
            long[] source=Enumerable.Range(0,80).Select(n=>origin+n*period+(n<10?0:n<12?-period*6/10:n<21?period*18/100:period*6/10)).ToArray();
            for(int frame=0;frame<60;frame++)
            {
                long due=origin+frame*period;
                while(next<source.Length&&source[next]<=due+frequency/10)history.Add(source[next++]);
                int at=RecordingFrameSelection.Index(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(history),due,previous,frequency/50,period);
                Require(history[at]==source[frame],"Near-rate jitter discarded a picture needed by a later output deadline: "+fps+" FPS frame "+frame);
                previous=history[at];if(at>0)history.RemoveRange(0,at);
            }
        }
    }
    static void WideWindowJitter()
    {
        const long frequency=12000000,origin=frequency,period=100000;
        long previous=-1;int next=0;var history=new List<long>();
        long[] source=Enumerable.Range(0,100).Select(n=>origin+n*period+(n<10?0:n==10?-60000:n==11?-120000:n<31?-160000:n<41?-20000:160000)).ToArray();
        for(int frame=0;frame<80;frame++)
        {
            long due=origin+frame*period;
            while(next<source.Length&&source[next]<=due+frequency/10)history.Add(source[next++]);
            int at=RecordingFrameSelection.Index(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(history),due,previous,frequency/50,period);
            Require(history[at]==source[frame],"A multi-period jitter allowance was mistaken for extra source pictures at frame "+frame);
            previous=history[at];if(at>0)history.RemoveRange(0,at);
        }
    }
    static void BufferedGapWithRecovery()
    {
        const long frequency=12000000,origin=frequency,period=100000;
        long[] source=Enumerable.Range(0,20).Select(n=>origin+n*period+(n<9?0:n==9?-30000:-60000))
            .Concat(Enumerable.Range(0,150).Select(n=>origin+2145000+n*50000L)).ToArray();
        long previous=-1;int next=0;var history=new List<long>();
        for(int frame=0;frame<70;frame++)
        {
            long due=origin+frame*period;
            while(next<source.Length&&source[next]<=due+frequency*15/100)history.Add(source[next++]);
            int at=RecordingFrameSelection.Index(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(history),due,previous,frequency/50,period);
            Require(history[at]>previous,"Post-gap pictures authorized an early skip and avoidable repeat at frame "+frame);
            previous=history[at];if(at>0)history.RemoveRange(0,at);
        }
    }
}
