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
            Require(selected==observed[frame],"Available jittered source image was discarded at an output boundary.");
            prior=selected;legacyPrior=legacy;
        }
        Require(repeats==0&&legacyWindowRepeats==1,"Real 120 FPS trace did not reproduce and eliminate the narrow-window repeat.");
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
        Program.Write("recording-cadence-tests.json",new{Pass=true,Fps=new[]{60,120},LegacyArtificialRepeats=legacyRepeats,NewArtificialRepeats=actualRepeats,NarrowWindowRepeats=legacyWindowRepeats,RealTraceRepeats=repeats,LegacyUnevenMotionSteps=legacyUneven,NewUnevenMotionSteps=integerUneven,NoAccumulatedPacingDrift=true,SourceOrderPreserved=true,RealSourceGapsNotInvented=true});
    }
}
