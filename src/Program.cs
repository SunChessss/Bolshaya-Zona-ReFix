using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace ReFix {
 static class Program {
  [STAThread] static void Main(string[] args) {
   if(args.Length==2 && args[0]=="--apply-self-update") {try{SelfUpdate.Apply(args[1]);}catch(Exception ex){try{File.WriteAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"error.log"),ex.ToString());}catch{}MessageBox.Show("Обновление лаунчера не завершено: "+ex.Message+"\nПредыдущие файлы сохранены в резервных копиях .refix-launcher/transactions рядом с лаунчером.","ReFix — обновление");}return;}
   bool created;
   using(var mutex=new Mutex(true,"Local\\SunChessReFixLauncher",out created)) {
    if(!created){MessageBox.Show("Лаунчер уже запущен.");return;}
    Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
    Application.ThreadException+=(s,e)=>MessageBox.Show(e.Exception.Message,"ReFix",MessageBoxButtons.OK,MessageBoxIcon.Warning);
    string root=args.Length==2 && args[0]=="--game"?args[1]:AppDomain.CurrentDomain.BaseDirectory;
    try{root=Data.Root(root);}catch{}   // 1.7: "--game \"D:\\X\\\"" from an older launcher arrives as D:\X" - strip it
    Application.Run(new MainForm(root));
   }
  }
 }
 public class Choice {
  public string Text,Value;
  public Choice(string text,string value){Text=text;Value=value;}
  public override string ToString(){return Text;}
 }
 public class MainForm:Form {
  static readonly Color Bg=Color.FromArgb(19,23,23), Card=Color.FromArgb(29,35,34), Gold=Color.FromArgb(204,173,100), Ink=Color.FromArgb(229,233,225), Muted=Color.FromArgb(163,175,166);
  Panel page; Label status,location,version,addonsCheck; TextBox notes;
  Button update,install,verify,selfUpdate,play,addonsButton,choose,refresh;
  bool selfAvailable;
  InstallationReport installation;
  ComboBox fontSize,uiScale;
  PreparedPatch prepared;
  GameService game; Catalog catalog; bool busy; string initialRoot;
  ComboBox season,render,resolution,texture,aniso;CheckBox minimal,newHud,textures,fullscreen,vsync;
  public const string TexturesAddon="optional_textures";   // 1.7: offered automatically after the update check
  NumericUpDown weight,maximum,speed,distance,gamma,grass;ComboBox lootDrop;
  ComboBox grassRadius;Button cancelOp;   // 1.9
  bool loading;System.Windows.Forms.Timer saveTimer;   // 1.9: settings are saved right after a change (no "Сохранить" buttons)
  CancellationTokenSource cts;bool writing,closeRequested;   // 1.9: cancellation of long operations
  Dictionary<string,Panel> pages=new Dictionary<string,Panel>();
  public MainForm(string root) {
   initialRoot=root;Text="Большая Зона · ReFix";Size=new Size(1080,780);MinimumSize=new Size(960,730);StartPosition=FormStartPosition.CenterScreen;
   BackColor=Bg;ForeColor=Ink;Font=new Font("Segoe UI",10);AutoScaleMode=AutoScaleMode.Dpi;
   var footer=new Panel {Dock=DockStyle.Bottom,Height=48,BackColor=Card,Padding=new Padding(20,8,20,8)};
   status=Label("Готов к запуску",10,Muted);status.Dock=DockStyle.Fill;footer.Controls.Add(status);
   cancelOp=Button("Отмена",120);cancelOp.Dock=DockStyle.Right;cancelOp.Height=32;cancelOp.Margin=new Padding(0);cancelOp.Visible=false;
   cancelOp.Click+=(s,e)=>{var c=cts;if(c!=null){c.Cancel();cancelOp.Enabled=false;status.Text="Отмена… уже изменённые файлы возвращаются на место.";}};
   footer.Controls.Add(cancelOp);Controls.Add(footer);
   var nav=new FlowLayoutPanel {Dock=DockStyle.Left,Width=200,Padding=new Padding(20,28,14,0),FlowDirection=FlowDirection.TopDown,WrapContents=false,BackColor=Card};
   nav.Controls.Add(Label("☢  ReFix",25,Gold));nav.Controls.Add(Label("БОЛЬШАЯ ЗОНА",10,Muted));
   var spacer=new Panel{Height=34,Width=160};nav.Controls.Add(spacer);
   foreach(string name in new[]{"Играть","Дополнения","Настройки"}) {string n=name;var b=Button(name,160);b.Height=46;b.Click+=(s,e)=>ShowPage(n);nav.Controls.Add(b);}
   nav.Controls.Add(new Panel{Height=30,Width=160});nav.Controls.Add(Label("by SunChess\nЛаунчер "+SelfUpdate.VersionString,10,Muted));Controls.Add(nav);
   page=new Panel {Dock=DockStyle.Fill,Padding=new Padding(28,25,28,15)};Controls.Add(page);page.BringToFront();
   BuildHome();BuildAddons();BuildSettings();ShowPage("Играть");
   Shown+=async(s,e)=> {await SetRoot(initialRoot);if(game!=null)await Check();};
   // 1.9: a running file operation can be interrupted; it is rolled back before the window closes.
   // Read-only work (update check, file verification) does not keep the window open.
   FormClosing+=(s,e)=> {
    // 1.9: a setting changed less than a second ago is still written before the window closes
    if(saveTimer!=null && saveTimer.Enabled && !busy && game!=null) {saveTimer.Stop();try{SaveSettingsSync();}catch(Exception ex){MessageBox.Show(this,"Последнее изменение настроек не сохранено: "+ex.Message,"ReFix");}}
    if(!writing)return;
    e.Cancel=true;
    if(closeRequested)return;
    if(MessageBox.Show(this,"Идёт установка. Прервать её? Уже изменённые файлы будут возвращены, затем лаунчер закроется.","ReFix",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
    closeRequested=true;var c=cts;if(c!=null)c.Cancel();status.Text="Отмена… уже изменённые файлы возвращаются на место.";
   };
  }
  static Label Label(string text,int size,Color color){return new Label{Text=text,Font=new Font("Segoe UI",size),ForeColor=color,AutoSize=true,MaximumSize=new Size(760,0),Margin=new Padding(0,0,0,12)};}
  static Button Button(string text,int width=210) {return new Button{Text=text,Width=width,Height=40,FlatStyle=FlatStyle.Flat,BackColor=Color.FromArgb(43,51,47),ForeColor=Ink,Cursor=Cursors.Hand,Margin=new Padding(0,0,12,12)};}
  static ComboBox Combo(params Choice[] choices) {var c=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=260,BackColor=Card,ForeColor=Ink,FlatStyle=FlatStyle.Flat};c.Items.AddRange(choices);c.SelectedIndex=0;NoWheel(c);return c;}
  static NumericUpDown Number(decimal min,decimal max,decimal step,decimal value,int decimals=0){var n=new NumericUpDown{Minimum=min,Maximum=max,Increment=step,Value=value,DecimalPlaces=decimals,Width=140,BackColor=Card,ForeColor=Ink};NoWheel(n);return n;}
  // 1.9: the mouse wheel scrolls the page, not the value of the field under focus (players scrolled to "Сохранить" and
  // turned 30 kg into 1 kg or switched the resolution without noticing). An open drop-down list still scrolls.
  static void NoWheel(Control c) {
   c.MouseWheel+=(s,e)=> {
    var cb=c as ComboBox;if(cb!=null && cb.DroppedDown)return;
    var h=e as HandledMouseEventArgs;if(h!=null)h.Handled=true;
    for(Control p=c.Parent;p!=null;p=p.Parent){var sc=p as ScrollableControl;if(sc!=null && sc.AutoScroll){int y=Math.Max(0,-sc.AutoScrollPosition.Y-e.Delta);sc.AutoScrollPosition=new Point(-sc.AutoScrollPosition.X,y);break;}}
   };
  }
  Panel NewPage(string name) {var p=new Panel {Dock=DockStyle.Fill,AutoScroll=true};pages[name]=p;page.Controls.Add(p);return p;}
  FlowLayoutPanel Flow(Panel parent){var f=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true};parent.Controls.Add(f);return f;}
  void ShowPage(string name){foreach(var item in pages)item.Value.Visible=item.Key==name;pages[name].BringToFront();if(name=="Дополнения" && game!=null && addonsCheck!=null && addonsCheck.Text=="")addonsCheck.Text=AddonsSummary();}
  string AddonsSummary() {
   int n=game.State.addons.Count;
   return n==0?"Дополнения через лаунчер не установлены.":"Установлено дополнений: "+n+" ("+String.Join(", ",game.State.addons.Select(a=>a.id.Replace("optional_","")))+"). Проверка файлов — кнопкой «Проверить дополнения».";
  }
  // 1.8: add-on files (up to 1.4 GB) are verified only here, not at start-up; 1.9: only by the button and as a regular
  // operation (other buttons wait, "Отмена" stops it), not every time the page is opened.
  async Task CheckAddons() {
   if(game==null || busy || addonsCheck==null)return;
   int n=game.State.addons.Count;if(n==0){addonsCheck.Text="Дополнения через лаунчер не установлены.";return;}
   addonsCheck.Text="Проверка файлов дополнений…";
   await Run(async()=> {
    var token=cts.Token;
    var errors=await Task.Run(()=>Installation.VerifyAddons(game.Root,game.State,token));int files=Installation.AddonFileCount(game.State);
    addonsCheck.Text=errors.Count==0?"Установлено дополнений: "+n+", файлов проверено: "+files+", всё на месте.":"Проблемы ("+errors.Count+"): "+string.Join("; ",errors.Take(5))+(errors.Count>5?" …":"")+". Снимите и поставьте дополнение заново.";
    game.Log("ADDONS_CHECK","addons="+n+"; files="+files+"; errors="+errors.Count);status.Text="Проверка дополнений завершена.";
   },false);
   if(addonsCheck.Text=="Проверка файлов дополнений…")addonsCheck.Text=AddonsSummary();
  }
  void BuildHome() {
   var f=Flow(NewPage("Играть"));f.Controls.Add(Label("Вернуться в Зону",28,Ink));f.Controls.Add(Label("Сборка ReFix · S.T.A.L.K.E.R. Тень Чернобыля",11,Muted));
   location=Label("Выберите папку игры",10,Muted);location.MaximumSize=new Size(690,0);f.Controls.Add(location);
   choose=Button("Выбрать папку игры");choose.Click+=async(s,e)=> {using(var d=new FolderBrowserDialog{Description="Корневая папка ReFix с bin и gamedata"})if(d.ShowDialog()==DialogResult.OK){await SetRoot(d.SelectedPath);if(game!=null)await Check();}};f.Controls.Add(choose);
   version=Label("Версия установленных исправлений: не определена",12,Gold);f.Controls.Add(version);
   var row=new FlowLayoutPanel{AutoSize=true,Width=700};
   play=Button("ИГРАТЬ",220);play.Height=52;play.BackColor=Gold;play.ForeColor=Bg;play.Font=new Font(Font,FontStyle.Bold);play.Click+=(s,e)=> {try{game.Launch();status.Text="Игра запущена. Для изменения настроек закройте её.";}catch(Exception ex){Error(ex);}};
   refresh=Button("Проверить обновления");refresh.Click+=async(s,e)=>await Check();
   update=Button("Скачать обновление");update.Enabled=false;update.Click+=async(s,e)=>await Run(async()=> {
    prepared=null;string archive=await game.Download(catalog.patch);status.Text="Проверка архива и подготовка списка файлов…";
    prepared=await Task.Run(()=>game.PreparePatch(archive,catalog.version,catalog.patch.sha256));
    notes.Text="Обновление скачано. Игровые файлы ещё не изменены.\r\nДля установки нажмите «Просмотреть и установить».\r\n\r\n"+String.Join("\r\n",prepared.Changes);
    status.Text="Архив проверен. Установка выполняется отдельной кнопкой.";
   });
   row.Controls.Add(play);row.Controls.Add(refresh);row.Controls.Add(update);f.Controls.Add(row);
   install=Button("Просмотреть и установить",280);install.Enabled=false;install.Click+=async(s,e)=>await Run(async()=> {
    prepared=await Task.Run(()=>game.PreparePatch(prepared.Archive,prepared.Version,prepared.Hash));
    using(var dialog=new Form{Text="Установка обновления",Size=new Size(820,580),StartPosition=FormStartPosition.CenterParent,MinimizeBox=false,MaximizeBox=false}) {
     var text=new TextBox{Multiline=true,ReadOnly=true,ScrollBars=ScrollBars.Both,WordWrap=false,Dock=DockStyle.Fill,Text="Папка игры: "+game.Root+"\r\nВерсия: "+prepared.Version+"\r\n\r\n"+String.Join("\r\n",prepared.Changes)+"\r\n\r\nБудут созданы резервные копии. Личные настройки и сохранения сохраняются."};
     var buttons=new FlowLayoutPanel{Dock=DockStyle.Bottom,Height=55,FlowDirection=FlowDirection.RightToLeft};
     var cancel=new Button{Text="Отмена",DialogResult=DialogResult.Cancel,Width=120,Height=36};var confirm=new Button{Text="Установить",DialogResult=DialogResult.OK,Width=160,Height=36};buttons.Controls.Add(cancel);buttons.Controls.Add(confirm);dialog.Controls.Add(text);dialog.Controls.Add(buttons);dialog.CancelButton=cancel;
     if(dialog.ShowDialog(this)!=DialogResult.OK){game.Log("INSTALL_CANCELLED");status.Text="Установка отменена. Архив оставлен для дальнейшей установки.";return;}
    }
    status.Text="Установка обновления…";await Task.Run(()=>game.InstallPreparedPatch(prepared));prepared=null;await Inspect();RenderState();status.Text="Обновление установлено. Проверка файлов завершена.";
   });f.Controls.Add(install);
   verify=Button("Проверить установку",280);verify.Click+=async(s,e)=>await Run(async()=>{await Inspect(true);RenderState();notes.Text=installation.Title+"\r\nПроверено файлов патча: "+installation.Checked+"\r\n\r\n"+(installation.Differences.Count==0?"Файлы соответствуют эталону.":String.Join("\r\n",installation.Differences));status.Text="Проверка установки завершена.";});f.Controls.Add(verify);
   selfUpdate=Button("Обновить лаунчер",280);selfUpdate.Visible=false;selfUpdate.Click+=async(s,e)=>await UpdateLauncher();f.Controls.Add(selfUpdate);
   f.Controls.Add(Label("Новости сборки",17,Ink));
   notes=new TextBox{Multiline=true,ReadOnly=true,BorderStyle=BorderStyle.None,BackColor=Card,ForeColor=Ink,Width=700,Height=245,ScrollBars=ScrollBars.Vertical,Text="Проверка GitHub выполняется при каждом запуске.\r\nПри отсутствии сети можно продолжить игру."};f.Controls.Add(notes);
   f.Controls.Add(Label("Перед установкой создаётся резервная копия. Ваши сохранения и настройки остаются на месте.",10,Muted));
  }
  void BuildAddons() {
   var f=Flow(NewPage("Дополнения"));f.Controls.Add(Label("Зона на ваш вкус",28,Ink));f.Controls.Add(Label("Загружайте только нужные дополнения. Выбор можно отменить.",11,Muted));
   f.Controls.Add(Label("Сезон",16,Gold));season=Combo(new Choice("Как было до лаунчера",""),new Choice("Весна","optional_season_spring"),new Choice("Лето","optional_season_summer"),new Choice("Осень — основа сборки","optional_season_autumn"));f.Controls.Add(season);
   f.Controls.Add(new Panel{Height=18});f.Controls.Add(Label("Интерфейс",16,Gold));
   minimal=new CheckBox{Text="Минималистичный HUD",AutoSize=true,Margin=new Padding(0,4,0,18)};
   newHud=new CheckBox{Text="Адаптация Adm-RAL NEW HUD",AutoSize=true,Margin=new Padding(0,4,0,18)};f.Controls.Add(minimal);f.Controls.Add(newHud);
   f.Controls.Add(Label("Производительность",16,Gold));
   textures=new CheckBox{Text="Оптимизация текстур — меньше фризов при появлении NPC у баз (около 1,4 ГБ)",AutoSize=true,Margin=new Padding(0,4,0,18)};f.Controls.Add(textures);
   f.Controls.Add(Label("Текстуры NPC, оружия и техники приведены к размерам степени двойки с полными мип-уровнями и картами рельефа: движок больше не пересчитывает их при загрузке (фризы 1–3 с). Предлагается автоматически после проверки обновлений.",11,Muted));
   f.Controls.Add(Label("Снятие отметки восстанавливает файлы, которые были до установки дополнения через лаунчер. Сезоны (весна, лето, осень) устанавливаются по очереди: выбор одного снимает другой. Меняются только дополнения, выбор которых изменился: уже установленные не переустанавливаются.",11,Muted));
   addonsCheck=Label("",11,Muted);f.Controls.Add(addonsCheck);
   addonsButton=Button("Применить дополнения",260);addonsButton.Click+=async(s,e)=>await Run(async()=> {
    if(catalog==null)throw new IOException("Сначала проверьте обновления — нужен каталог дополнений.");
    var ids=new List<string>();string seasonId=Selected(season);if(seasonId!="")ids.Add(seasonId);if(minimal.Checked)ids.Add("optional_minimal_hud");if(newHud.Checked)ids.Add("optional_new_hud");if(textures.Checked)ids.Add(TexturesAddon);
    var selected=new Dictionary<string,string>();foreach(string id in ids){var asset=catalog.addons.FirstOrDefault(a=>a.id==id);if(asset==null)throw new IOException("Дополнение пока не опубликовано: "+id);selected[id]=await game.Download(asset);}
    status.Text="Установка дополнений…";await Task.Run(()=>game.ApplyAddons(selected));RenderState();addonsCheck.Text=AddonsSummary();status.Text="Выбранные дополнения применены.";
   });f.Controls.Add(addonsButton);
   var checkButton=Button("Проверить дополнения",260);checkButton.Click+=async(s,e)=>await CheckAddons();f.Controls.Add(checkButton);
  }
  TableLayoutPanel SettingsTable(FlowLayoutPanel f) {var t=new TableLayoutPanel{AutoSize=true,ColumnCount=2,Width=690,Margin=new Padding(0,0,0,10)};t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,310));t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,320));f.Controls.Add(t);return t;}
  void Row(TableLayoutPanel table,string text,Control input) {int i=table.RowCount++;table.RowStyles.Add(new RowStyle(SizeType.Absolute,38));var label=Label(text,11,Ink);label.Margin=new Padding(0,3,0,0);table.Controls.Add(label,0,i);table.Controls.Add(input,1,i);}
  void BuildSettings() {
   var f=Flow(NewPage("Настройки"));f.Controls.Add(Label("Настройки игры",28,Ink));f.Controls.Add(Label("Изменения сохраняются сразу и действуют при следующем запуске игры. Перед изменением закройте игру.",11,Muted));
   var table=SettingsTable(f);
   render=Combo(new Choice("Статическое освещение","renderer_r1"),new Choice("Динамическое объектов","renderer_r2a"),new Choice("Полное динамическое","renderer_r2"));Row(table,"Освещение",render);
   resolution=Combo(new Choice("1280 × 720","1280x720"),new Choice("1600 × 900","1600x900"),new Choice("1920 × 1080","1920x1080"),new Choice("2560 × 1440","2560x1440"),new Choice("3840 × 2160","3840x2160"));Row(table,"Разрешение",resolution);
   fullscreen=new CheckBox{Text="Полноэкранный режим",AutoSize=true};Row(table,"Режим окна",fullscreen);
   vsync=new CheckBox{Text="Вертикальная синхронизация",AutoSize=true};Row(table,"Синхронизация",vsync);
   texture=Combo(new Choice("Высокое","0"),new Choice("Среднее","1"),new Choice("Низкое","2"),new Choice("Минимальное","3"));Row(table,"Качество текстур",texture);
   aniso=Combo(new Choice("1×","1"),new Choice("2×","2"),new Choice("4×","4"),new Choice("8×","8"),new Choice("16×","16"));Row(table,"Фильтрация текстур",aniso);
   distance=Number(40,150,5,100);Row(table,"Дальность видимости, %",distance);
   grass=Number(20,60,5,30);Row(table,"Шаг травы (меньше — гуще)",grass);
   gamma=Number(0.5m,1.5m,0.05m,1,2);Row(table,"Гамма",gamma);
   fontSize=Combo(new Choice("Исходный","original"),new Choice("Крупный","large"),new Choice("Очень крупный","largest"),new Choice("Изменён вне лаунчера","custom"));Row(table,"Текст интерфейса игры",fontSize);
   f.Controls.Add(Label("Текст в диалогах, КПК и других окнах. Крупный текст набран шрифтом Inter и в отдельных окнах может не помещаться.",10,Muted));
   // 1.9: grass draw distance - pairs of xrRender_R1/R2.dll by the author of the build
   var grassTable=SettingsTable(f);
   grassRadius=Combo(new Choice("Как в сборке (R1 — 49, R2 — 241)",""),new Choice("49 — стандарт ТЧ","049"),new Choice("81","081"),new Choice("121","121"),new Choice("161","161"),new Choice("201","201"),new Choice("241 — как R2 в сборке","241"),new Choice("281","281"),new Choice("321","321"),new Choice("361","361"),new Choice("401 — максимум","401"));
   Row(grassTable,"Дальность травы",grassRadius);
   f.Controls.Add(Label("Насколько далеко видна трава. Чем дальше, тем красивее, но ниже FPS — особенно с динамическим освещением. Варианты подготовлены автором сборки; «Как в сборке» возвращает исходные.",10,Muted));
   var ui=SettingsTable(f);
   uiScale=Combo(new Choice("Исходный","original"),new Choice("Крупный","large"),new Choice("Изменён вне лаунчера","custom"));Row(ui,"Масштаб интерфейса",uiScale);
   f.Controls.Add(Label("Крупный: больше иконки предметов в инвентаре, торговле, ящиках и трупах, крупнее пункты главного меню. Ячеек на экране меньше, списки прокручиваются.",10,Muted));
   f.Controls.Add(Label("Персонаж",17,Gold));var actor=SettingsTable(f);
   weight=Number(1,10000,10,30);maximum=Number(1,10000,10,60);speed=Number(0.25m,5,0.05m,1,2);
   Row(actor,"Комфортный вес, кг",weight);Row(actor,"Предельный вес, кг",maximum);Row(actor,"Скорость, × от исходной",speed);
   f.Controls.Add(Label("Исходные значения: 30 и 60 кг, скорость 1×. Предельный вес не бывает меньше комфортного.",10,Muted));
   var loot=SettingsTable(f);
   lootDrop=Combo(new Choice("Как в сборке (много)","mod"),new Choice("Уменьшенный","reduced"),new Choice("Как в оригинальном ТЧ","vanilla"),new Choice("Мусорщик (очень мало)","scavenger"));Row(loot,"Дроп лута с трупов NPC",lootDrop);
   f.Controls.Add(Label("Как в сборке: костюм почти с каждого, патроны и аптечки 77–88%, артефакты. Уменьшенный: примерно вдвое меньше, костюм в 30%, половина денег. Как в оригинальном ТЧ: без артефактов и костюмов, патроны 10–30%, аптечка 10%. Мусорщик: патроны ~25%, костюм 8%, четверть денег. Сохранения не ломает.",10,Muted));
   // 1.9: every change is saved by itself: game settings after a short pause (typing a number), the rest at once
   saveTimer=new System.Windows.Forms.Timer{Interval=700};saveTimer.Tick+=async(s,e)=>{saveTimer.Stop();if(busy){saveTimer.Start();return;}await SaveSettingsNow();};
   foreach(var c in new Control[]{render,resolution,texture,aniso,lootDrop}) ((ComboBox)c).SelectedIndexChanged+=(s,e)=>QueueSave();
   foreach(var c in new[]{fullscreen,vsync}) c.CheckedChanged+=(s,e)=>QueueSave();
   foreach(var n in new[]{distance,grass,gamma,weight,maximum,speed}) n.ValueChanged+=(s,e)=>QueueSave();
   fontSize.SelectedIndexChanged+=async(s,e)=>{if(loading||game==null)return;string preset=Selected(fontSize);if(preset=="custom")return;
    await Apply(async()=>{await Task.Run(()=>GameFonts.Save(game,preset));status.Text="Размер текста сохранён. Перезапустите игру.";},()=>Select(fontSize,GameFonts.Current(game)));};
   uiScale.SelectedIndexChanged+=async(s,e)=>{if(loading||game==null)return;string preset=Selected(uiScale);if(preset=="custom")return;
    await Apply(async()=>{await Task.Run(()=>GameUiScale.Save(game,preset));status.Text="Масштаб интерфейса сохранён. Перезапустите игру.";},()=>Select(uiScale,GameUiScale.Current(game)));};
   grassRadius.SelectedIndexChanged+=async(s,e)=>{if(loading||game==null)return;
    await Apply(async()=> {
     if(catalog==null)throw new IOException("Сначала проверьте обновления — нужен каталог дополнений.");
     var asset=catalog.addons.FirstOrDefault(a=>a.id==Grass.AddonId);if(asset==null)throw new IOException("Пакет дальности травы пока не опубликован.");
     string radius=Selected(grassRadius);if(radius=="")radius=null;
     if(radius==game.State.grass)return;
     string archive=await game.Download(asset);var profiles=Installation.Profiles(game.Root,catalog);
     status.Text="Установка рендера…";await Task.Run(()=>Grass.Apply(game,archive,radius,profiles));
     status.Text=radius==null?"Рендер сборки возвращён. Перезапустите игру.":"Дальность травы "+radius.TrimStart('0')+" установлена. Перезапустите игру.";
    },()=>Select(grassRadius,game.State.grass??""));};
  }
  static string Selected(ComboBox c){return ((Choice)c.SelectedItem).Value;}
  void QueueSave(){if(loading||game==null)return;saveTimer.Stop();saveTimer.Start();status.Text="Сохранение настроек…";}
  // runs a change; if it fails (game running, no network...) the control shows the value that is really in effect
  async Task Apply(Func<Task> action,Action revert) {
   if(busy){status.Text="Дождитесь окончания текущей операции.";Revert(revert);return;}
   bool ok=false;await Run(async()=>{await action();ok=true;});
   if(!ok)Revert(revert);
  }
  void Revert(Action revert){loading=true;try{revert();}catch{}finally{loading=false;}}
  Dictionary<string,string> GraphicsValues() {
   return new Dictionary<string,string>{{"renderer",Selected(render)},{"vid_mode",Selected(resolution)},{"rs_fullscreen",fullscreen.Checked?"on":"off"},{"rs_v_sync",vsync.Checked?"on":"off"},{"texture_lod",Selected(texture)},{"r__tf_aniso",Selected(aniso)},{"rs_vis_distance",(distance.Value/100).ToString(CultureInfo.InvariantCulture)},{"r__detail_density",(grass.Value/100).ToString(CultureInfo.InvariantCulture)},{"rs_c_gamma",gamma.Value.ToString(CultureInfo.InvariantCulture)}};
  }
  void SaveSettingsSync() {
   decimal w=weight.Value,m=Math.Max(maximum.Value,weight.Value);
   game.SaveSettings(GraphicsValues(),w,m,speed.Value,Selected(lootDrop));
  }
  async Task SaveSettingsNow() {
   if(game==null)return;
   if(maximum.Value<weight.Value){loading=true;maximum.Value=weight.Value;loading=false;}   // the limit follows the comfortable weight
   var values=GraphicsValues();
   decimal w=weight.Value,m=maximum.Value,sp=speed.Value;string lootPreset=Selected(lootDrop);
   bool ok=false;
   await Run(async()=>{await Task.Run(()=>game.SaveSettings(values,w,m,sp,lootPreset));ok=true;});
   if(ok)status.Text="Сохранено: вес "+w+"/"+m+" кг, скорость ×"+sp+", дальность "+distance.Value+"%. Действует при следующем запуске игры.";
   else LoadSettings();
  }
  static void Select(ComboBox c,string value){var match=c.Items.Cast<Choice>().FirstOrDefault(x=>x.Value==value);if(match==null){match=new Choice(value,value);c.Items.Add(match);}c.SelectedItem=match;}
  static decimal ReadNumber(string text,string key,decimal fallback,string section=null){decimal d;return Decimal.TryParse(Config.Value(text,key,section),NumberStyles.Float,CultureInfo.InvariantCulture,out d)?d:fallback;}
  static void SetNumber(NumericUpDown input,decimal value){input.Value=Math.Max(input.Minimum,Math.Min(input.Maximum,value));}
  async Task SetRoot(string root) {
   try {
    prepared=null;installation=null;selfAvailable=false;game=new GameService(root);catalog=null;location.Text=game.Root;
    game.Progress=s=> {if(IsHandleCreated)BeginInvoke(new Action(()=>status.Text=s));};
    // 1.9: an operation interrupted last time is rolled back here, in the background, with a message; finished
    // transactions and unused bases are deleted
    bool pending=Transaction.Pending(game.Root);
    busy=true;EnableActions();
    status.Text=pending?"Возврат файлов после прерванной установки…":"Очистка временных файлов лаунчера…";
    try{await Task.Run(()=>game.Recover());}finally{busy=false;}
    if(pending)game.Log("RECOVERED_ON_START");
    LoadSettings();
    RenderState();status.Text=pending?"Прерванная установка отменена, файлы возвращены.":"Папка игры выбрана.";
   }catch(Exception ex){game=null;location.Text="Папка игры не выбрана";Error(ex);}EnableActions();
  }
  // values from user.ltx / bz_lord.ltx / font and UI state without triggering the auto-save
  void LoadSettings() {
   if(game==null)return;
   loading=true;
   try {
    string u=game.ReadConfig("userdata/user.ltx"),l=game.ReadConfig("gamedata/config/bz_lord.ltx");
    Select(render,Config.Value(u,"renderer")??"renderer_r1");Select(resolution,Config.Value(u,"vid_mode")??"1920x1080");Select(texture,Config.Value(u,"texture_lod")??"0");Select(aniso,Config.Value(u,"r__tf_aniso")??"4");
    fullscreen.Checked=(Config.Value(u,"rs_fullscreen")??"on")=="on";vsync.Checked=Config.Value(u,"rs_v_sync")=="on";
    SetNumber(distance,ReadNumber(u,"rs_vis_distance",1)*100);SetNumber(grass,ReadNumber(u,"r__detail_density",0.3m)*100);SetNumber(gamma,ReadNumber(u,"rs_c_gamma",1));
    Select(lootDrop,Config.Value(l,"preset","bz_loot")??"mod");SetNumber(weight,ReadNumber(l,"max_weight",30,"bz_lord_inventory"));SetNumber(maximum,ReadNumber(l,"max_walk_weight",60,"bz_lord_actor_condition"));SetNumber(speed,ReadNumber(l,"walk_accel",17,"bz_lord_actor")/17);
    Select(fontSize,GameFonts.Current(game));Select(uiScale,GameUiScale.Current(game));
   } finally { loading=false; }
  }
  void RenderState() {
   version.Text=installation==null?"Версия: проверка файлов…":installation.Title;
   if(catalog!=null)version.Text+="  ·  доступны: "+catalog.version;
   var ids=game.State.addons.Select(a=>a.id).ToList();Select(season,ids.FirstOrDefault(id=>id.StartsWith("optional_season_"))??"");loading=true;try{Select(grassRadius,game.State.grass??"");}finally{loading=false;}minimal.Checked=ids.Contains("optional_minimal_hud");newHud.Checked=ids.Contains("optional_new_hud");textures.Checked=ids.Contains(TexturesAddon);
  }
  void EnableActions() {
   choose.Enabled=!busy;refresh.Enabled=!busy&&game!=null;play.Enabled=!busy&&game!=null;addonsButton.Enabled=!busy&&game!=null&&catalog!=null;
   update.Enabled=!busy&&game!=null&&catalog!=null&&(installation==null||installation.PatchHash!=catalog.patch.sha256);
   install.Enabled=!busy&&game!=null&&prepared!=null;
   verify.Enabled=!busy&&game!=null;fontSize.Enabled=game!=null;grassRadius.Enabled=game!=null&&catalog!=null;uiScale.Enabled=game!=null&&GameUiScale.Available(game);
   cancelOp.Visible=busy&&cts!=null;cancelOp.Enabled=cts!=null&&!cts.IsCancellationRequested;
    selfUpdate.Visible=selfAvailable&&catalog!=null;selfUpdate.Enabled=!busy&&selfAvailable&&catalog!=null;
  }
  // 1.9: one operation at a time; long ones can be cancelled ("Отмена" / closing the window), the transaction is rolled back
  async Task Run(Func<Task> action,bool changesFiles=true) {
   if(busy)return;busy=true;writing=changesFiles;cts=new CancellationTokenSource();
   if(game!=null)game.Token=cts.Token;EnableActions();
   try {if(game==null)throw new IOException("Выберите папку игры");if(changesFiles)game.EnsureStopped();await action();}
   catch(OperationCanceledException){status.Text="Операция отменена, изменённые файлы возвращены.";if(game!=null)game.Log("CANCELLED");}
   catch(Exception ex){if(cts!=null && cts.IsCancellationRequested){status.Text="Операция отменена, изменённые файлы возвращены.";}else Error(ex);}
   finally{busy=false;writing=false;if(game!=null)game.Token=CancellationToken.None;cts=null;EnableActions();if(closeRequested)BeginInvoke(new Action(Close));}
  }
  async Task Check() {
   if(busy || game==null)return;busy=true;EnableActions();
   // 1.9: the catalog first, then one check of the installed files (1.8 checked them twice)
   try {status.Text="Проверка обновлений на GitHub…";Exception checkError=null;try{catalog=await game.Check();}catch(Exception ce){catalog=null;checkError=ce;}await Inspect();if(checkError!=null){RenderState();throw checkError;}selfAvailable=SelfUpdate.IsNewer(catalog.launcher);selfUpdate.Text=selfAvailable?"Обновить лаунчер до "+catalog.launcher.version:"Обновить лаунчер";notes.Text=(catalog.notes??"").Replace("\n","\r\n");RenderState();status.Text=selfAvailable?"Доступна новая версия лаунчера. Нажмите «Обновить лаунчер».":installation.PatchHash==catalog.patch.sha256?"Установлена актуальная версия, файлы проверены.":"Доступен пакет исправлений. Нажмите «Скачать обновление».";}
   catch(Exception ex) {catalog=null;status.Text=installation==null?"Не удалось проверить установленные файлы.":"Не удалось проверить обновления. Можно играть с текущими файлами.";notes.Text=installation==null?ex.Message:"Сервер обновлений временно недоступен или релиз ещё не опубликован.\r\nЛокальная проверка: "+installation.Title+"\r\n\r\nИгра и сохранённые настройки доступны без сети.";}
   finally {busy=false;EnableActions();}
   if(catalog!=null && !selfAvailable)await OfferTextures();
  }
  // 1.7: the textures add-on is offered once; a refusal is remembered in state.json (declined), the checkbox stays available.
  async Task OfferTextures() {
   if(busy || game==null || catalog==null)return;
   var asset=catalog.addons.FirstOrDefault(a=>a.id==TexturesAddon);if(asset==null)return;
   if(game.State.addons.Any(a=>a.id==TexturesAddon) || (game.State.declined!=null && game.State.declined.Contains(TexturesAddon)))return;
   var answer=MessageBox.Show(this,"Доступно дополнение «Оптимизация текстур» ("+(asset.bytes/1048576)+" МБ для скачивания, около 1,4 ГБ на диске).\n\nТекстуры NPC, оружия и техники приведены к размерам степени двойки с полными мип-уровнями и картами рельефа — движок перестаёт пересчитывать их при загрузке, исчезают фризы 1–3 с при появлении NPC у баз.\n\nСкачать и установить сейчас? Отказ запоминается; включить можно позже на вкладке «Дополнения».","ReFix — дополнение",MessageBoxButtons.YesNo,MessageBoxIcon.Question);
   if(answer!=DialogResult.Yes){try{game.Decline(TexturesAddon);}catch(Exception ex){Error(ex);}return;}
   await Run(async()=> {
    var ids=game.State.addons.Select(a=>a.id).ToList();if(!ids.Contains(TexturesAddon))ids.Add(TexturesAddon);
    var selected=new Dictionary<string,string>();foreach(string id in ids){var a2=catalog.addons.FirstOrDefault(a=>a.id==id);if(a2==null)throw new IOException("Дополнение пока не опубликовано: "+id);selected[id]=await game.Download(a2);}
    status.Text="Установка дополнений…";await Task.Run(()=>game.ApplyAddons(selected));await Inspect();RenderState();status.Text="Дополнение «Оптимизация текстур» установлено.";
   });
  }
  async Task Inspect(bool withAddons=false) {status.Text="Проверка установленных файлов…";var profiles=Installation.Profiles(game.Root,catalog);installation=await Task.Run(()=>Installation.Verify(game.Root,game.State,profiles,withAddons));game.Log("INSTALLATION_CHECK",installation.Title+"; differences="+installation.Differences.Count);}
  async Task UpdateLauncher() {
   if(busy || !selfAvailable || catalog==null)return;
   if(MessageBox.Show(this,"Скачать новую версию и перезапустить лаунчер? Настройки игры сохранятся.","Обновление лаунчера",MessageBoxButtons.YesNo,MessageBoxIcon.Question)!=DialogResult.Yes)return;
   bool restart=false;
   await Run(async()=> {status.Text="Скачивание и проверка нового лаунчера…";string job=await SelfUpdate.Prepare(catalog.launcher,AppDomain.CurrentDomain.BaseDirectory,game.Root);SelfUpdate.Begin(job);restart=true;});
   if(restart)Close();
  }
  void Error(Exception ex){if(game!=null){try{game.Log("ERROR",ex.ToString());}catch{}}status.Text=ex.Message;MessageBox.Show(this,ex.Message,"ReFix",MessageBoxButtons.OK,MessageBoxIcon.Information);}
 }
}
