using System.Diagnostics;
namespace SdrCapture;

sealed class UpdatesForm:Form
{
    readonly UpdateCoordinator updater;
    readonly Func<KvmService?> network;
    readonly Label status=new(){AutoSize=true,Dock=DockStyle.Top,Padding=new(0,8,0,8)};
    readonly TextBox details=new(){Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Vertical,Dock=DockStyle.Fill};
    readonly Button check=new(){Text="Проверить",AutoSize=true},here=new(){Text="Обновить этот ПК",AutoSize=true},all=new(){Text="Обновить все подключённые ПК",AutoSize=true};
    public UpdatesForm(UpdateCoordinator updater,Func<KvmService?> network)
    {
        this.updater=updater;this.network=network;
        Text="ScreenCapture · Обновления";ClientSize=new(760,460);MinimumSize=new(680,400);Font=new("Segoe UI",10);Padding=new(18);StartPosition=FormStartPosition.CenterScreen;
        Icon=Icon.ExtractAssociatedIcon(Environment.ProcessPath!);
        var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=48,WrapContents=false};buttons.Controls.AddRange([check,here,all]);
        var heading=new LinkLabel{Text=$"ScreenCapture {Updates.VersionText} · GitHub Releases",AutoSize=true,Dock=DockStyle.Top,Padding=new(0,0,0,10)};
        heading.LinkClicked+=(_,_)=>Process.Start(new ProcessStartInfo(Updates.RepositoryUrl+"/releases"){UseShellExecute=true});
        Controls.Add(details);Controls.Add(status);Controls.Add(heading);Controls.Add(buttons);
        updater.Changed+=RefreshState;FormClosed+=(_,_)=>updater.Changed-=RefreshState;
        check.Click+=async(_,_)=>{try{await updater.Check();}catch(Exception e){updater.SetStatus("Не удалось проверить обновления: "+e.Message);}};
        here.Click+=async(_,_)=>await updater.InstallHere();all.Click+=async(_,_)=>await updater.InstallAll();
        Shown+=async(_,_)=>{RefreshState();if(!updater.Busy)try{await updater.Check();}catch(Exception e){updater.SetStatus(e.Message);}};
        RefreshState();
    }
    void RefreshState()
    {
        if(IsDisposed)return;status.Text=updater.Status;
        check.Enabled=!updater.Busy;here.Enabled=!updater.Busy&&updater.Latest!=null&&Updates.ParseVersion(updater.Latest.Manifest.Version)>Updates.Current;
        all.Enabled=!updater.Busy&&network()?.Options.Role=="Host"&&updater.Latest!=null;
        var computers=new List<string>{Environment.MachineName+" · "+Updates.VersionText+" · этот ПК"};
        foreach(var peer in network()?.Peers??[])
            computers.Add(updater.Peers.TryGetValue(peer.Id,out string? state)?state:$"{peer.Name} · {(peer.Version.Length==0?"0.5.x":peer.Version)} · {(peer.UpdateProtocol>0?"поддерживает обновления":"нужна первая установка 0.6 вручную")}");
        details.Text=string.Join("\r\n",computers)+"\r\n\r\n"+(updater.Latest?.Notes??"Пакеты загружаются из публичного GitHub и проверяются по цифровой подписи. Настройки, KVM-код и записи сохраняются. Перед перезапуском сохраняется текущий повтор.");
    }
}
