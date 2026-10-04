using System.Diagnostics;
using NAudio.CoreAudioApi;
namespace SdrCapture;
partial class AppSettingsForm
{
    void BuildDiscordPage(Settings value)
    {
        var page=Page("Discord · приём","Экран игрового ПК в Discord · 1080p60");
        discordEnabled.Text="Принимать изображение игрового ПК";discordEnabled.Checked=value.Discord.Enabled;Row(page,"Камера",discordEnabled,38);
        discordName.Text=CameraInstallation.Name(value.Discord.CameraName);Row(page,"Имя в Discord",discordName,38);
        discordSource.Text=value.Discord.Source;
        var sourceRow=new TableLayoutPanel{ColumnCount=2};sourceRow.ColumnStyles.Add(new(SizeType.Percent,100));sourceRow.ColumnStyles.Add(new(SizeType.Absolute,92));
        discordSource.Dock=DockStyle.Fill;sourceRow.Controls.Add(discordSource);
        var find=new Button{Text="Найти",Dock=DockStyle.Fill,Margin=new(6,0,0,0)};sourceRow.Controls.Add(find);Row(page,"Игровой ПК",sourceRow,42);
        find.Click+=async(_,_)=>{find.Enabled=false;try{var sources=await Task.Run(NdiDiscovery.Sources);if(IsDisposed)return;string selected=discordSource.Text;discordSource.Items.Clear();discordSource.Items.AddRange(sources);discordSource.Text=selected;if(selected.Length==0&&sources.Length==1)discordSource.Text=sources[0];}catch(Exception e){if(!IsDisposed)MessageBox.Show(this,e.Message,"Поиск экрана");}finally{if(!IsDisposed)find.Enabled=role.SelectedIndex!=1;}};
        Set(discordMode,["Звук игрового ПК → кабель","Вход этого ПК / микшер","Silent — без звука"],value.Discord.AudioMode switch{"Local"=>"Вход этого ПК / микшер","Silent"=>"Silent — без звука",_=>"Звук игрового ПК → кабель"});
        Row(page,"Откуда брать звук",discordMode,42);
        Row(page,"Выход в кабель",discordOutput,40);Row(page,"Аудиовход Discord",discordInput,40);
        discordVolume.Value=value.Discord.Volume;
        var volumeRow=new TableLayoutPanel{ColumnCount=2};volumeRow.ColumnStyles.Add(new(SizeType.Percent,100));volumeRow.ColumnStyles.Add(new(SizeType.Absolute,50));
        var percent=new Label{Text=discordVolume.Value+"%",AutoSize=true,Anchor=AnchorStyles.Right};discordVolume.Dock=DockStyle.Fill;volumeRow.Controls.Add(discordVolume);volumeRow.Controls.Add(percent);discordVolume.ValueChanged+=(_,_)=>percent.Text=discordVolume.Value+"%";Row(page,"Громкость передачи",volumeRow,46);
        var hint=Note(page,"");
        ReplaceChoices(discordOutput,[new("","Silent — без звука")],value.Discord.AudioDevice,"Сохранённый выход · загружается…");
        ReplaceChoices(discordInput,[new("","Выберите вход для Discord…")],value.Discord.CaptureDevice,"Сохранённый вход · загружается…");
        discordOutput.SelectedIndexChanged+=(_,_)=>{if(!updatingAudioChoices&&discordMode.SelectedIndex==0&&discordOutput.SelectedItem is AudioChoice output)try{string input=DiscordDevices.Pair(output.Id,audioEndpoints).Capture.Id;discordInput.SelectedItem=discordInput.Items.Cast<AudioChoice>().FirstOrDefault(x=>x.Id==input);}catch(IOException){}};
        var pair=new Button{Text="Настроить устройства…",AutoSize=true,Height=34};
        var refreshAudio=new Button{Text="Обновить список",AutoSize=true,Height=34};refreshAudio.Click+=async(_,_)=>{refreshAudio.Enabled=false;try{await RefreshAudioDevices();}finally{if(!IsDisposed)refreshAudio.Enabled=role.SelectedIndex!=1;}};
        var actions=new FlowLayoutPanel{WrapContents=false};actions.Controls.AddRange([pair,refreshAudio]);Row(page,"Подготовка Discord",actions,46);
        pair.Click+=async(_,_)=>
        {
            string receiverRole=role.SelectedIndex==1?"Host":"Client",name=discordName.Text.Trim(),input=(discordInput.SelectedItem as AudioChoice)?.Id??"";
            pair.Enabled=false;
            try
            {
                CameraInstallation.Install(receiverRole,name);
                if(discordMode.SelectedIndex!=2&&input.Length>0)await DiscordDevices.PairInput(receiverRole,input,name);
                await RefreshAudioDevices();
                if(!IsDisposed){MessageBox.Show(this,"Камера готова. Полностью закройте Discord через значок в трее и откройте снова.\n\nВыберите нашу камеру во вкладке «Устройства» и 60 FPS. Если Discord запомнил другой звук, выберите указанный аудиовход вручную.\n\nУстройства Windows по умолчанию не менялись.","Discord");}
            }
            catch(Exception e){if(!IsDisposed)MessageBox.Show(this,e.Message,"Настройка устройств");}
            finally{if(!IsDisposed)pair.Enabled=role.SelectedIndex!=1;}
        };
        var installAudio=UiStyle.Button("Установить VB-CABLE…",()=>{});
        installAudio.Click+=async(_,_)=>
        {
            if(role.SelectedIndex==1)return;
            if(MessageBox.Show(this,"Установить подписанный драйвер VB-CABLE на этом ПК?\n\nWindows запросит права администратора. В установщике нажмите Install Driver. Может потребоваться перезагрузка. Прежние устройства по умолчанию будут восстановлены.\n\nVB-CABLE — donationware VB-Audio.","Звук для Discord",MessageBoxButtons.OKCancel,MessageBoxIcon.Information)!=DialogResult.OK)return;
            installAudio.Enabled=false;
            try{await DiscordSetup.InstallCable("Client");await RefreshAudioDevices();if(!IsDisposed){var cable=audioEndpoints.FirstOrDefault(e=>e.Flow==DataFlow.Render&&DiscordDevices.IsCable(e));if(cable!=null)discordOutput.SelectedItem=discordOutput.Items.Cast<AudioChoice>().FirstOrDefault(c=>c.Id==cable.Id);MessageBox.Show(this,"Драйвер установлен. При необходимости перезагрузите ПК, затем нажмите «Настроить устройства» и «Сохранить».","VB-CABLE");}}
            catch(Exception e){if(!IsDisposed)MessageBox.Show(this,e.Message,"Установка кабеля");}finally{if(!IsDisposed)installAudio.Enabled=role.SelectedIndex!=1;}
        };
        var installerRow=new FlowLayoutPanel{WrapContents=false};installerRow.Controls.Add(installAudio);installerRow.Controls.Add(UiStyle.Button("Условия VB-Audio",()=>Process.Start(new ProcessStartInfo("https://vb-audio.com/Services/licensing.htm"){UseShellExecute=true})));Row(page,"Если нет кабеля",installerRow,46);
        Note(page,"«Настроить устройства» создаёт камеру и переименовывает только выбранный аудиовход: имя камеры + Audio. Windows запросит права администратора. Сохранённый выбор звука в Discord иногда требуется изменить вручную.");
        void RefreshMode()
        {
            bool receiver=role.SelectedIndex!=1,network=discordMode.SelectedIndex==0,sound=discordMode.SelectedIndex!=2;
            foreach(var item in new Control[]{discordEnabled,discordName,discordSource,discordOutput,discordInput,discordMode,discordVolume,pair,installAudio,find,refreshAudio})item.Enabled=receiver;
            if(!receiver)discordEnabled.Checked=false;
            ShowRow(discordOutput,network);ShowRow(discordInput,sound);ShowRow(volumeRow,network);ShowRow(installerRow,network);
            hint.Text=!receiver?"Этот раздел настраивается на стрим-ПК, где открыт Discord. На игровом ПК виртуальные устройства не нужны.":network?"Игровой ПК → выбранный выход кабеля → его парный аудиовход в Discord. Для другого кабеля выберите оба конца вручную. Выход на колонки будет воспроизводить звук локально.":sound?"Выберите вход микшера, например RØDECaster, или любой другой вход этого ПК. Discord берёт звук из него напрямую. Кабель и звук NDI в этом режиме не используются.":"Передаётся только изображение. В Discord выключите звук демонстрации; выбор звука самого Discord программа не меняет.";
        }
        discordMode.SelectedIndexChanged+=(_,_)=>RefreshMode();role.SelectedIndexChanged+=(_,_)=>RefreshMode();RefreshMode();
    }
}
