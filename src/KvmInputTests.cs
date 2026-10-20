using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text.Json;

namespace SdrCapture;

// No hooks, cursor warps, OS input, clipboard writes or live KVM connections.
static class KvmInputTests
{
    static void Require(bool value,string message){if(!value)throw new Exception(message);}
    sealed class Transport:IKvmInputTransport
    {
        public KvmPeerInfo[] Peers {get;set;}=[];
        public bool Online=true;
        public readonly List<KvmMessage> Sent=[];
        public readonly List<(string Peer,KvmMessage Message)> Routed=[];
        public bool Send(string peer,KvmMessage message){Sent.Add(message);Routed.Add((peer,message));return Online;}
    }
    public static void Run()
    {
        KvmInputBlock block=KvmInputBlock.None;
        KvmInputAccess.TestProbe=()=>block;KvmController.TestWarp=_=>true;
        try
        {
            Controller(value=>block=value);ViewerKeyboard(value=>block=value);NativeViewerOwnership();Injection(value=>block=value);Plans();
            Pipes().GetAwaiter().GetResult();
            HelperProtocol().GetAwaiter().GetResult();
            Program.Write("kvm-input-tests.json",new{Pass=true,AdministratorFocusReturnsLocal=true,ProtectedDesktopReturnsLocal=true,
                SuccessfulInjectionTrackedOnly=true,FailedReleasesRetried=true,NoPhantomRightClick=true,FailedWarpStaysLocal=true,
                HelperPlanExcludesSecrets=true,PrivatePipeRoundtrip=true,PipeProcessIdentityChecked=true,MalformedFramesRejected=true,
                PipeCloseStopsChild=true,UnchangedHelperPlanKeepsRemote=true,HostHelperRejectsInjection=true,HelperHeartbeatDeadline=true,
                ViewerSystemKeysSuppressedLocally=true,ViewerScancodesPreserved=true,KeyLifetimesSurviveFocusLoss=true,ViewerWindowOwnershipChecked=true,
                InjectedOsInput=false,InstalledHooks=false,PhysicalTwoPcTest=false});
        }
        finally{block=KvmInputBlock.None;KvmInput.ReleaseAll();KvmInput.TestSend=null;KvmInputAccess.TestProbe=null;KvmController.TestWarp=null;KvmKeyboardFocus.TestOwned=null;KvmKeyboardFocus.TestFocused=null;}
    }
    static void ViewerKeyboard(Action<KvmInputBlock> access)
    {
        access(KvmInputBlock.None);bool focused=true,owned=true;int checkedProcess=0;
        KvmKeyboardFocus.TestOwned=(window,viewport,process)=>{checkedProcess=process;return owned&&window==100&&viewport==101;};
        KvmKeyboardFocus.TestFocused=(_,_)=>focused;
        string peer=Guid.NewGuid().ToString("N"),second=Guid.NewGuid().ToString("N");
        var transport=new Transport{Peers=[new(peer,"Fixture",[]),new(second,"Second",[])]};
        using var controller=new KvmController(transport,new(){Role="Host"},installHooks:false,viewerProcess:12345);
        var target=new KvmViewerKeyboard(peer,100,101);controller.SetViewerKeyboard(target);
        Require(checkedProcess==12345,"Elevated viewer capture checked the helper process instead of its parent.");
        foreach(var chord in new (int Key,int Scan,bool Extended)[][]{
            [(162,29,false),(160,42,false),(27,1,false)], // Ctrl+Shift+Esc
            [(164,56,false),(9,15,false)],             // Alt+Tab
            [(164,56,false),(115,62,false)],           // Alt+F4
            [(91,91,true),(82,19,false)],              // left Win+R
            [(92,92,true),(69,18,false)],              // right Win+E
            [(44,55,true)],                           // PrintScreen stays with the controlled PC
            [(162,29,false),(27,1,false)],             // Ctrl+Esc
            [(160,42,false),(52,5,false)]})            // Shift+4
        {
            int before=transport.Sent.Count;
            foreach(var k in chord)Require(controller.TestKey(k.Key,false,false,k.Scan,k.Extended)==(IntPtr)1,"System key DOWN leaked into the local shell.");
            foreach(var k in chord.Reverse())Require(controller.TestKey(k.Key,true,false,k.Scan,k.Extended)==(IntPtr)1,"System key UP leaked into the local shell.");
            var messages=transport.Sent.Skip(before).ToArray();
            Require(messages.Length==chord.Length*2&&messages.All(m=>m.Type=="key"),"A viewer chord duplicated or lost key messages.");
            for(int i=0;i<chord.Length;i++)Require(messages[i].Code==chord[i].Key&&messages[i].X==chord[i].Scan&&messages[i].Flags==(chord[i].Extended?1:0),"Physical scan code or left/right extended flag changed.");
        }
        int count=transport.Sent.Count;controller.SetViewerKeyboard(target);
        Require(transport.Sent.Count==count,"Refreshing an unchanged viewport released held keys.");
        controller.TestKey(162,false,false,29);controller.TestKey(164,false,false,56);controller.TestKey((int)Keys.Pause,false,false,69);
        controller.TestKey((int)Keys.Pause,true,false,69);controller.TestKey(164,true,false,56);controller.TestKey(162,true,false,29);
        Require(!controller.Seamless&&transport.Sent.Count==count+4,"Locking physical screen edges disabled the viewer keyboard.");
        count=transport.Sent.Count;
        controller.TestKey(65,false,true,30);controller.TestKey(65,false,true,30);controller.TestKey(65,true,true,30);
        Require(transport.Sent.Count==count+3,"Injected accessibility/Stream Deck input or autorepeat was duplicated/lost.");
        count=transport.Sent.Count;controller.TestKey(65,false,true,30,own:true);
        Require(transport.Sent.Count==count,"The KVM's own marked injection fed back into transport.");
        controller.TestKey(91,false,false,91,true);focused=false;
        controller.TestKey(91,false,false,91,true);controller.TestKey(91,true,false,91,true);
        Require(transport.Sent[^1].Type=="release"&&transport.Sent.Count==count+2,"Focus loss retained input or leaked a held Win key into another target.");
        Require(controller.TestKey(91,true,false,91,true)!=(IntPtr)1,"A completed key lifetime stayed trapped locally.");
        focused=true;controller.TestKey(164,false,false,56);
        controller.SetViewerKeyboard(new(second,100,101));count=transport.Sent.Count;
        Require(controller.TestKey(164,false,false,56)==(IntPtr)1&&controller.TestKey(164,true,false,56)==(IntPtr)1,"A held Alt key leaked during viewer handover.");
        Require(transport.Sent.Count==count,"A held key moved to a different server without a fresh DOWN.");
        controller.TestKey(66,false,false,48);controller.TestKey(66,true,false,48);
        Require(transport.Routed.TakeLast(2).All(x=>x.Peer==second&&x.Message.Code==66),"New keys went to the old server after handover.");
        int emergencies=0;controller.EmergencyReturn+=()=>emergencies++;
        controller.TestKey(162,false,false,29);controller.TestKey(164,false,false,56);controller.TestKey(27,false,false,1);
        Require(emergencies==1&&transport.Sent[^1].Type=="release","Ctrl+Alt+Esc did not release viewer control.");
        count=transport.Sent.Count;controller.TestKey(27,true,false,1);controller.TestKey(164,true,false,56);controller.TestKey(162,true,false,29);
        Require(transport.Sent.Count==count,"Emergency return forwarded orphan modifier releases.");
        owned=false;controller.SetViewerKeyboard(target);count=transport.Sent.Count;controller.TestKey(67,false,false,46);controller.TestKey(67,true,false,46);
        Require(transport.Sent.Count==count,"A foreign viewport could capture keyboard input.");
        owned=true;controller.SetViewerKeyboard(target);access(KvmInputBlock.Desktop);controller.TestWatch();
        Require(transport.Sent[^1].Type=="release","Protected desktop did not release the viewer keyboard.");
        access(KvmInputBlock.None);KvmKeyboardFocus.TestOwned=null;KvmKeyboardFocus.TestFocused=null;
    }
    static void NativeViewerOwnership()
    {
        // Create private hidden HWNDs only: no Show, activation, hooks or input.
        var prior=SynchronizationContext.Current;
        try
        {
            using var window=new Form();using var viewport=new PictureBox();window.Controls.Add(viewport);
            long root=window.Handle.ToInt64(),child=viewport.Handle.ToInt64();
            Require(KvmKeyboardFocus.Root(viewport.Handle)==root&&KvmKeyboardFocus.Owned(root,child,Environment.ProcessId),"Native viewer/viewport ownership was rejected.");
            Require(!KvmKeyboardFocus.Owned(root,child,Environment.ProcessId+1)&&!KvmKeyboardFocus.Owned(long.MaxValue,child,Environment.ProcessId),"Foreign/invalid native window ownership was accepted.");
            Require(!KvmKeyboardFocus.Focused(root,child,Environment.ProcessId),"A hidden background viewer could capture keyboard input.");
        }
        // Creating HWNDs auto-installs a WinForms context. This CLI has no UI
        // message loop; subsequent async pipe tests must retain their old context.
        finally{SynchronizationContext.SetSynchronizationContext(prior);}
    }
    static void Controller(Action<KvmInputBlock> access)
    {
        string localId=Guid.NewGuid().ToString("N"),peer=Guid.NewGuid().ToString("N");
        var local=KvmScreen.Local()[0];
        var options=new KvmOptions{Id=localId,Role="Host",Layout=[
            new(){Peer=localId,Device=local.Device,Width=local.Width,Height=local.Height},
            new(){Peer=peer,Device="remote",X=local.Width,Width=1920,Height=1080}]};
        var transport=new Transport{Peers=[new(peer,"Fixture",[new("remote",0,0,1920,1080,true)])]};
        using var controller=new KvmController(transport,options,installHooks:false);
        var target=controller.Monitors.Single(m=>m.Peer==peer);
        KvmController.TestWarp=_=>false;controller.Activate(target);
        Require(controller.ActiveMonitorKey!=target.Key&&!transport.Sent.Any(m=>m.Type=="mouse"),"Failed cursor warp committed remote routing.");
        KvmController.TestWarp=_=>true;controller.Activate(target);
        Require(controller.ActiveMonitorKey==target.Key,"Remote activation failed.");
        controller.Refresh();Require(controller.ActiveMonitorKey==target.Key,"Ordinary inventory refresh canceled remote control.");
        controller.TestMouse(0x201,Point.Empty);controller.TestKey(65,false,false);
        Require(transport.Sent.Any(m=>m.Type=="mouse"&&m.Flags==2)&&transport.Sent.Any(m=>m.Type=="key"&&m.Code==65),"Remote key/click routing failed.");
        access(KvmInputBlock.Administrator);int count=transport.Sent.Count;controller.TestMouse(0x201,Point.Empty);
        Require(controller.ActiveMonitorKey!=target.Key&&transport.Sent.Count==count+1&&transport.Sent.Last().Type=="release","Administrator focus did not release and return local before click routing.");
        count=transport.Sent.Count;controller.TestKey(66,false,false);controller.TestMove(10,5);
        Require(transport.Sent.Count==count,"Blocked input continued to reach another PC.");
        access(KvmInputBlock.None);controller.TestWatch();controller.Activate(target);
        access(KvmInputBlock.Desktop);controller.TestWatch();
        Require(controller.ActiveMonitorKey!=target.Key&&transport.Sent.Last().Type=="release","Protected desktop retained remote ownership.");
        access(KvmInputBlock.None);controller.TestWatch();
        int opens=0;controller.OpenRequested+=()=>opens++;
        controller.TestKey(162,false,false);access(KvmInputBlock.Administrator);controller.TestWatch();
        access(KvmInputBlock.None);controller.TestWatch();controller.TestKey(164,false,false);controller.TestKey((int)Keys.K,false,false);
        Require(opens==0,"Stale modifier survived access loss.");
        controller.TestKey((int)Keys.K,true,false);controller.TestKey(164,true,false);
        controller.TestKey(162,false,false);controller.TestKey(164,false,false);controller.TestKey((int)Keys.K,false,false);
        Require(opens==1,"Shortcut failed after access recovery.");
        controller.TestKey((int)Keys.K,true,false);controller.TestKey(162,true,false);controller.TestKey(164,true,false);
        transport.Online=false;controller.Activate(target);
        Require(controller.ActiveMonitorKey!=target.Key,"Failed peer send retained remote ownership.");
    }
    static void Injection(Action<KvmInputBlock> access)
    {
        var sent=new List<KvmMessage>();bool accept=true;
        KvmInput.TestSend=message=>{sent.Add(message);return accept;};
        KvmInput.ReleaseAll();sent.Clear();accept=false;
        Require(!KvmInput.Inject(new(){Type="key",Code=65}),"Rejected DOWN reported success.");
        accept=true;KvmInput.ReleaseAll();Require(sent.Count==1,"Unsent DOWN caused a phantom UP.");
        sent.Clear();Require(KvmInput.Inject(new(){Type="key",Code=65,X=30}),"Valid DOWN failed.");
        accept=false;Require(!KvmInput.Inject(new(){Type="key",Code=65,X=30,Flags=2}),"Rejected UP reported success.");
        accept=true;Require(KvmInput.Inject(new(){Type="key",Code=66}),"Pending release did not recover.");
        Require(sent[^2].Code==65&&(sent[^2].Flags&2)!=0&&sent[^1].Code==66,"New input preceded the pending release.");
        KvmInput.ReleaseAll();sent.Clear();
        Require(KvmInput.Inject(new(){Type="mouse",Flags=8}),"Right DOWN failed.");
        accept=false;Require(!KvmInput.ReleaseAll(),"Failed button release was lost.");
        accept=true;KvmInput.RetryRelease();int released=sent.Count;KvmInput.ReleaseAll();
        Require(sent.Last().Flags==16&&sent.Count==released,"Button release retry failed or created phantom right click.");
        sent.Clear();Require(KvmInput.Inject(new(){Type="key",Code=17}),"CTRL DOWN failed.");
        access(KvmInputBlock.Desktop);Require(!KvmInput.ReleaseAll()&&!KvmInput.Inject(new(){Type="key",Code=67}),"Protected desktop accepted input.");
        Require(sent.Count==1,"Input was sent to protected desktop.");
        access(KvmInputBlock.None);KvmInput.RetryRelease();Require(sent.Count==2&&sent.Last().Code==17&&sent.Last().Flags==2,"Deferred CTRL release was not recovered.");
        foreach(var invalid in new KvmMessage[]{new(){Type="clipboard-text"},new(){Type="key",Code=0},new(){Type="key",Code=65,Flags=16},new(){Type="mouse",Flags=1},new(){Type="mouse",Flags=128,Code=3}})
            Require(!KvmInput.Inject(invalid),"Non-input or invalid input accepted.");
    }
    static void Plans()
    {
        var host=JsonSerializer.Deserialize<KvmOptions>("{\"Role\":\"Host\"}")!;
        Require(host.UsesAdministratorInput&&host.AdministratorInput==null,"Existing host did not default to administrator input.");
        Require(!(host with{Role="Client"}).UsesAdministratorInput&&!(host with{AdministratorInput=false}).UsesAdministratorInput,"Client unexpectedly required UAC or opt-out ignored.");
        var secret=host with{PairingCode="PRIVATE-PAIRING",Host="private-host",AudioDevice="private-audio"};
        var safe=KvmInputHelper.SafeOptions(secret);string json=JsonSerializer.Serialize(safe);
        Require(!json.Contains("PRIVATE-PAIRING")&&!json.Contains("private-host")&&!json.Contains("private-audio"),"Helper received unrelated private configuration.");
        KvmInputHelper.ValidatePlan(new(){Options=safe,Peers=[]});
        foreach(var invalid in new KvmHelperMessage[]{new(){Options=safe with{Role="Off"},Peers=[]},new(){Options=safe,Peers=[new("bad","x",[])]},new(){Options=safe with{Layout=null!},Peers=[]}})
        {bool rejected=false;try{KvmInputHelper.ValidatePlan(invalid);}catch(Exception e)when(e is IOException or ArgumentException){rejected=true;}Require(rejected,"Invalid helper plan accepted.");}
    }
    static async Task Pipes()
    {
        using var deadline=new CancellationTokenSource(15000);
        string name="DeskGlide.Input.Test."+Guid.NewGuid().ToString("N");
        using var server=KvmInputHelper.CreatePipe(name);
        using(var identity=System.Security.Principal.WindowsIdentity.GetCurrent())
        {
            var security=server.GetAccessControl();
            Require(identity.User!.Equals(security.GetOwner(typeof(System.Security.Principal.SecurityIdentifier))),"Helper pipe is not owned by the actual user SID.");
            var allows=security.GetAccessRules(true,true,typeof(System.Security.Principal.SecurityIdentifier)).Cast<PipeAccessRule>().Where(r=>r.AccessControlType==System.Security.AccessControl.AccessControlType.Allow);
            Require(allows.All(r=>r.IdentityReference.Equals(identity.User)),"Helper pipe grants access to another user/group.");
        }
        var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,WindowStyle=ProcessWindowStyle.Hidden};
        foreach(var argument in new[]{"--kvm-pipe-fixture",name,Environment.ProcessId.ToString()})start.ArgumentList.Add(argument);
        using var child=Process.Start(start)??throw new Exception("Pipe fixture failed to start.");
        try
        {
            await server.WaitForConnectionAsync(deadline.Token);KvmInputHelper.VerifyClient(server,child.Id);
            bool rejected=false;try{KvmInputHelper.VerifyClient(server,Environment.ProcessId);}catch(IOException){rejected=true;}Require(rejected,"Foreign pipe client PID accepted.");
            using(var link=new KvmHelperPipe(server))
            {
                for(int i=0;i<120;i++)Require(link.Post(new(){Type="fixture-ping",Key=i.ToString()}),"IPC queue unexpectedly overflowed.");
                for(int i=0;i<120;i++){var reply=await link.Read(deadline.Token);Require(reply?.Type=="fixture-pong"&&reply.Key==i.ToString(),"Input pipe reordered or corrupted messages.");}
            }
            Require(child.WaitForExit(5000)&&child.ExitCode==0,"Pipe disconnect did not stop child.");
        }
        finally{if(!child.HasExited){child.Kill();child.WaitForExit(5000);}}
        foreach(byte[] frame in new[]{Frame(0),Frame(KvmHelperPipe.MaximumFrame+1),Frame(8),Frame(2,[123,125]),Frame(2,[123,93])})
        {
            using var link=new KvmHelperPipe(new MemoryStream(frame));bool rejected=false;
            try{var message=await link.Read(deadline.Token);if(frame.Length==6&&frame[5]==125){Require(message!=null,"Valid JSON rejected.");continue;}}
            catch(IOException){rejected=true;}
            Require(rejected,"Malformed/truncated helper frame accepted.");
        }
        using var blocked=new KvmHelperPipe(new BlockingStream());bool saturated=false;
        for(int i=0;i<520;i++)if(!blocked.Post(new(){Type="fixture"})){saturated=true;break;}
        Require(saturated,"Blocked pipe had an unbounded queue.");
    }
    static byte[] Frame(int size,byte[]? body=null){byte[] frame=new byte[4+(body?.Length??0)];BinaryPrimitives.WriteInt32LittleEndian(frame,size);body?.CopyTo(frame,4);return frame;}
    static async Task HelperProtocol()
    {
        using var deadline=new CancellationTokenSource(12000);
        string name="DeskGlide.Input.Test."+Guid.NewGuid().ToString("N");
        using var server=KvmInputHelper.CreatePipe(name);
        using var client=KvmInputHelper.ClientPipe(name);
        var connecting=client.ConnectAsync(deadline.Token);await server.WaitForConnectionAsync(deadline.Token);await connecting;
        using var parent=new KvmHelperPipe(server);using var child=new KvmHelperPipe(client);using var process=Process.GetCurrentProcess();
        var worker=KvmInputHelper.RunLoop(child,process,installHooks:false);
        string peer=Guid.NewGuid().ToString("N");var screen=KvmScreen.Local()[0];
        var options=new KvmOptions{Role="Host",Layout=[new(){Peer=peer,Device="remote",X=screen.Width,Width=1920,Height=1080}]};
        var plan=new KvmHelperMessage{Type="configure",Options=options,Peers=[new(peer,"Fixture",[new("remote",0,0,1920,1080,true)])]};
        parent.Post(plan);Require((await parent.Read(deadline.Token))?.Type=="ready","Helper did not configure.");
        string key=peer+"|remote";parent.Post(new(){Type="activate",Key=key});
        Require((await parent.Read(deadline.Token))?.Type=="send","Helper did not emit initial movement.");
        Require((await parent.Read(deadline.Token)) is {Type:"state",Key:var active}&&active==key,"Helper activation did not publish active remote.");
        parent.Post(plan);Require((await parent.Read(deadline.Token))?.Type=="ready","Unchanged helper plan unexpectedly released remote ownership.");
        parent.Post(new(){Type="input",Input=new(){Type="key",Code=65}});
        await worker.WaitAsync(deadline.Token);Require((await parent.Read(deadline.Token)) is {Type:"send",Input.Type:"release"},"Host helper accepted injection or failed to release on invalid command.");
        // An idle/stalled parent must also end the capture loop without a stop command.
        var clock=Stopwatch.StartNew();var idle=KvmInputHelper.RunLoop(child,process,installHooks:false);await idle.WaitAsync(deadline.Token);
        Require(clock.ElapsedMilliseconds>=2500,"Input helper exited without exercising its heartbeat deadline.");
    }
    sealed class BlockingStream:MemoryStream
    {
        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer,CancellationToken token=default)=>await Task.Delay(Timeout.Infinite,token);
    }
    // This fixture only echoes test strings and never constructs a controller or injects input.
    public static int PipeFixture(string name,int parent)
    {
        if(!name.StartsWith("DeskGlide.Input.Test.",StringComparison.Ordinal))throw new IOException("Invalid fixture pipe.");
        using var pipe=KvmInputHelper.ClientPipe(name);
        pipe.Connect(5000);KvmInputHelper.VerifyServer(pipe,parent);
        using var link=new KvmHelperPipe(pipe);using var deadline=new CancellationTokenSource(15000);
        while(true)
        {
            var message=link.Read(deadline.Token).GetAwaiter().GetResult();if(message==null)return 0;
            if(message.Type!="fixture-ping")throw new IOException("Invalid fixture command.");
            if(!link.Post(new(){Type="fixture-pong",Key=message.Key}))return 1;
        }
    }
    // Explicit manual integration test: Windows asks for consent; no remote
    // activation or injected input. Use a self-contained build on an interactive desktop.
    public static void Approval()
    {
        using var deadline=new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var connection=KvmInputHelper.Start(deadline.Token).GetAwaiter().GetResult();
        using var link=connection.Pipe;using var child=connection.Process;
        try
        {
            var options=new KvmOptions{Role="Host",Seamless=false,OpenHotkeyKey=0,ToggleHotkeyKey=0};
            Require(link.Post(new(){Type="configure",Options=options,Peers=[]}),"Could not configure approved helper.");
            Require(link.Read(deadline.Token).GetAwaiter().GetResult()?.Type=="ready","Approved helper did not install its hooks.");
            link.Post(new(){Type="ping"});Require(link.Read(deadline.Token).GetAwaiter().GetResult()?.Type=="pong","Approved helper did not respond.");
            link.Post(new(){Type="stop"});
            Require(child.WaitForExit(5000)&&child.ExitCode==0,"Approved helper did not exit cleanly.");
            Program.Write("kvm-helper-approval-test.json",new{Pass=true,WindowsApproval=true,SameUserCrossIntegrityPipe=true,
                ElevatedDedicatedInputThread=true,ParentAndChildIdentityVerified=true,CleanHelperExit=true,InjectedOsInput=false,PhysicalTwoPcTest=false});
        }
        finally{link.Dispose();if(!child.HasExited)child.WaitForExit(5000);}
    }
}
