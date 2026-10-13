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
   bool created;
   using(var mutex=new Mutex(true,"Local\\SunChessReFixLauncher",out created)) {
    if(!created){MessageBox.Show("Лаунчер уже запущен.");return;}
    Application.EnableVisualStyles();Application.SetCompatibleTextRenderingDefault(false);
    Application.ThreadException+=(s,e)=>MessageBox.Show(e.Exception.Message,"ReFix",MessageBoxButtons.OK,MessageBoxIcon.Warning);
    string root=args.Length==2 && args[0]=="--game"?args[1]:AppDomain.CurrentDomain.BaseDirectory;
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
  Panel page; Label status,location,version; TextBox notes;
  Button update,play,addonsButton,save,choose,refresh;
  GameService game; Catalog catalog; bool busy; string initialRoot;
  ComboBox season,render,resolution,texture,aniso;CheckBox minimal,newHud,fullscreen,vsync;
  NumericUpDown weight,maximum,speed,distance,gamma,grass;
  Dictionary<string,Panel> pages=new Dictionary<string,Panel>();
  public MainForm(string root) {
   initialRoot=root;Text="Большая Зона · ReFix";Size=new Size(1080,780);MinimumSize=new Size(960,730);StartPosition=FormStartPosition.CenterScreen;
   BackColor=Bg;ForeColor=Ink;Font=new Font("Segoe UI",10);AutoScaleMode=AutoScaleMode.Dpi;
   var footer=new Panel {Dock=DockStyle.Bottom,Height=48,BackColor=Card,Padding=new Padding(20,8,20,8)};
   status=Label("Готов к запуску",10,Muted);status.Dock=DockStyle.Fill;footer.Controls.Add(status);Controls.Add(footer);
   var nav=new FlowLayoutPanel {Dock=DockStyle.Left,Width=200,Padding=new Padding(20,28,14,0),FlowDirection=FlowDirection.TopDown,WrapContents=false,BackColor=Card};
   nav.Controls.Add(Label("☢  ReFix",25,Gold));nav.Controls.Add(Label("БОЛЬШАЯ ЗОНА",10,Muted));
   var spacer=new Panel{Height=34,Width=160};nav.Controls.Add(spacer);
   foreach(string name in new[]{"Играть","Дополнения","Настройки"}) {string n=name;var b=Button(name,160);b.Height=46;b.Click+=(s,e)=>ShowPage(n);nav.Controls.Add(b);}
   nav.Controls.Add(new Panel{Height=30,Width=160});nav.Controls.Add(Label("by SunChess\nЛаунчер 1.0.0",10,Muted));Controls.Add(nav);
   page=new Panel {Dock=DockStyle.Fill,Padding=new Padding(28,25,28,15)};Controls.Add(page);page.BringToFront();
   BuildHome();BuildAddons();BuildSettings();ShowPage("Играть");
   Shown+=async(s,e)=> {SetRoot(initialRoot);if(game!=null)await Check();};
   FormClosing+=(s,e)=> {if(busy){e.Cancel=true;status.Text="Дождитесь завершения операции — файлы ещё обрабатываются.";}};
  }
  static Label Label(string text,int size,Color color){return new Label{Text=text,Font=new Font("Segoe UI",size),ForeColor=color,AutoSize=true,MaximumSize=new Size(760,0),Margin=new Padding(0,0,0,12)};}
  static Button Button(string text,int width=210) {return new Button{Text=text,Width=width,Height=40,FlatStyle=FlatStyle.Flat,BackColor=Color.FromArgb(43,51,47),ForeColor=Ink,Cursor=Cursors.Hand,Margin=new Padding(0,0,12,12)};}
  static ComboBox Combo(params Choice[] choices) {var c=new ComboBox{DropDownStyle=ComboBoxStyle.DropDownList,Width=260,BackColor=Card,ForeColor=Ink,FlatStyle=FlatStyle.Flat};c.Items.AddRange(choices);c.SelectedIndex=0;return c;}
  static NumericUpDown Number(decimal min,decimal max,decimal step,decimal value,int decimals=0){return new NumericUpDown{Minimum=min,Maximum=max,Increment=step,Value=value,DecimalPlaces=decimals,Width=140,BackColor=Card,ForeColor=Ink};}
  Panel NewPage(string name) {var p=new Panel {Dock=DockStyle.Fill,AutoScroll=true};pages[name]=p;page.Controls.Add(p);return p;}
  FlowLayoutPanel Flow(Panel parent){var f=new FlowLayoutPanel{Dock=DockStyle.Fill,FlowDirection=FlowDirection.TopDown,WrapContents=false,AutoScroll=true};parent.Controls.Add(f);return f;}
  void ShowPage(string name){foreach(var item in pages)item.Value.Visible=item.Key==name;pages[name].BringToFront();}
  void BuildHome() {
   var f=Flow(NewPage("Играть"));f.Controls.Add(Label("Вернуться в Зону",28,Ink));f.Controls.Add(Label("Сборка ReFix · S.T.A.L.K.E.R. Тень Чернобыля",11,Muted));
   location=Label("Выберите папку игры",10,Muted);location.MaximumSize=new Size(690,0);f.Controls.Add(location);
   choose=Button("Выбрать папку игры");choose.Click+=async(s,e)=> {using(var d=new FolderBrowserDialog{Description="Корневая папка ReFix с bin и gamedata"})if(d.ShowDialog()==DialogResult.OK){SetRoot(d.SelectedPath);if(game!=null)await Check();}};f.Controls.Add(choose);
   version=Label("Версия установленных исправлений: не определена",12,Gold);f.Controls.Add(version);
   var row=new FlowLayoutPanel{AutoSize=true,Width=700};
   play=Button("ИГРАТЬ",220);play.Height=52;play.BackColor=Gold;play.ForeColor=Bg;play.Font=new Font(Font,FontStyle.Bold);play.Click+=(s,e)=> {try{game.Launch();status.Text="Игра запущена. Для изменения настроек закройте её.";}catch(Exception ex){Error(ex);}};
   refresh=Button("Проверить обновления");refresh.Click+=async(s,e)=>await Check();
   update=Button("Установить обновление");update.Enabled=false;update.Click+=async(s,e)=>await Run(async()=> {string archive=await game.Download(catalog.patch);status.Text="Проверка и установка обновления…";await Task.Run(()=>game.ApplyPatch(archive,catalog.version,catalog.patch.sha256));RenderState();status.Text="Обновление установлено. Можно играть.";});
   row.Controls.Add(play);row.Controls.Add(refresh);row.Controls.Add(update);f.Controls.Add(row);
   f.Controls.Add(Label("Новости сборки",17,Ink));
   notes=new TextBox{Multiline=true,ReadOnly=true,BorderStyle=BorderStyle.None,BackColor=Card,ForeColor=Ink,Width=700,Height=245,ScrollBars=ScrollBars.Vertical,Text="Проверка GitHub выполняется при каждом запуске.\r\nПри отсутствии сети можно продолжить игру."};f.Controls.Add(notes);
   f.Controls.Add(Label("Перед установкой создаётся резервная копия. Ваши сохранения и настройки остаются на месте.",10,Muted));
  }
  void BuildAddons() {
   var f=Flow(NewPage("Дополнения"));f.Controls.Add(Label("Зона на ваш вкус",28,Ink));f.Controls.Add(Label("Загружайте только нужные дополнения. Выбор можно отменить.",11,Muted));
   f.Controls.Add(Label("Сезон",16,Gold));season=Combo(new Choice("Как было до лаунчера",""),new Choice("Весна","optional_season_spring"),new Choice("Осень — основа сборки","optional_season_autumn"));f.Controls.Add(season);
   f.Controls.Add(new Panel{Height=18});f.Controls.Add(Label("Интерфейс",16,Gold));
   minimal=new CheckBox{Text="Минималистичный HUD",AutoSize=true,Margin=new Padding(0,4,0,18)};
   newHud=new CheckBox{Text="Адаптация Adm-RAL NEW HUD",AutoSize=true,Margin=new Padding(0,4,0,18)};f.Controls.Add(minimal);f.Controls.Add(newHud);
   f.Controls.Add(Label("Снятие отметки восстанавливает файлы, которые были до установки дополнения через лаунчер. Весна и осень устанавливаются по очереди.",11,Muted));
   addonsButton=Button("Применить дополнения",260);addonsButton.Click+=async(s,e)=>await Run(async()=> {
    if(catalog==null)throw new IOException("Сначала проверьте обновления — нужен каталог дополнений.");
    var ids=new List<string>();string seasonId=Selected(season);if(seasonId!="")ids.Add(seasonId);if(minimal.Checked)ids.Add("optional_minimal_hud");if(newHud.Checked)ids.Add("optional_new_hud");
    var selected=new Dictionary<string,string>();foreach(string id in ids){var asset=catalog.addons.FirstOrDefault(a=>a.id==id);if(asset==null)throw new IOException("Дополнение пока не опубликовано: "+id);selected[id]=await game.Download(asset);}
    status.Text="Установка дополнений…";await Task.Run(()=>game.ApplyAddons(selected));RenderState();status.Text="Выбранные дополнения применены.";
   });f.Controls.Add(addonsButton);
  }
  TableLayoutPanel SettingsTable(FlowLayoutPanel f) {var t=new TableLayoutPanel{AutoSize=true,ColumnCount=2,Width=690,Margin=new Padding(0,0,0,10)};t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,310));t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute,320));f.Controls.Add(t);return t;}
  void Row(TableLayoutPanel table,string text,Control input) {int i=table.RowCount++;table.RowStyles.Add(new RowStyle(SizeType.Absolute,38));var label=Label(text,11,Ink);label.Margin=new Padding(0,3,0,0);table.Controls.Add(label,0,i);table.Controls.Add(input,1,i);}
  void BuildSettings() {
   var f=Flow(NewPage("Настройки"));f.Controls.Add(Label("Настройки игры",28,Ink));f.Controls.Add(Label("Применяются при следующем запуске. Закройте игру перед сохранением.",11,Muted));
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
   f.Controls.Add(Label("Персонаж",17,Gold));var actor=SettingsTable(f);
   weight=Number(1,10000,10,30);maximum=Number(1,10000,10,60);speed=Number(0.25m,5,0.05m,1,2);
   Row(actor,"Комфортный вес, кг",weight);Row(actor,"Предельный вес, кг",maximum);Row(actor,"Скорость, × от исходной",speed);
   f.Controls.Add(Label("Исходные значения: 30 / 60 кг, скорость 1×. Предельный вес должен быть не меньше комфортного.",10,Muted));
   save=Button("Сохранить настройки",240);save.Click+=async(s,e)=>await Run(async()=> {
    var values=new Dictionary<string,string>{{"renderer",Selected(render)},{"vid_mode",Selected(resolution)},{"rs_fullscreen",fullscreen.Checked?"on":"off"},{"rs_v_sync",vsync.Checked?"on":"off"},{"texture_lod",Selected(texture)},{"r__tf_aniso",Selected(aniso)},{"rs_vis_distance",(distance.Value/100).ToString(CultureInfo.InvariantCulture)},{"r__detail_density",(grass.Value/100).ToString(CultureInfo.InvariantCulture)},{"rs_c_gamma",gamma.Value.ToString(CultureInfo.InvariantCulture)}};
    decimal w=weight.Value,m=maximum.Value,sp=speed.Value;await Task.Run(()=>game.SaveSettings(values,w,m,sp));status.Text="Настройки сохранены. Изменения вступят в силу при запуске игры.";
   });f.Controls.Add(save);
  }
  static string Selected(ComboBox c){return ((Choice)c.SelectedItem).Value;}
  static void Select(ComboBox c,string value){var match=c.Items.Cast<Choice>().FirstOrDefault(x=>x.Value==value);if(match==null){match=new Choice(value,value);c.Items.Add(match);}c.SelectedItem=match;}
  static decimal ReadNumber(string text,string key,decimal fallback,string section=null){decimal d;return Decimal.TryParse(Config.Value(text,key,section),NumberStyles.Float,CultureInfo.InvariantCulture,out d)?d:fallback;}
  static void SetNumber(NumericUpDown input,decimal value){input.Value=Math.Max(input.Minimum,Math.Min(input.Maximum,value));}
  void SetRoot(string root) {
   try {
    game=new GameService(root);catalog=null;location.Text=game.Root;
    game.Progress=s=> {if(IsHandleCreated)BeginInvoke(new Action(()=>status.Text=s));};
    string u=game.ReadConfig("userdata/user.ltx"),l=game.ReadConfig("gamedata/config/bz_lord.ltx");
    Select(render,Config.Value(u,"renderer")??"renderer_r1");Select(resolution,Config.Value(u,"vid_mode")??"1920x1080");Select(texture,Config.Value(u,"texture_lod")??"0");Select(aniso,Config.Value(u,"r__tf_aniso")??"4");
    fullscreen.Checked=(Config.Value(u,"rs_fullscreen")??"on")=="on";vsync.Checked=Config.Value(u,"rs_v_sync")=="on";
    SetNumber(distance,ReadNumber(u,"rs_vis_distance",1)*100);SetNumber(grass,ReadNumber(u,"r__detail_density",0.3m)*100);SetNumber(gamma,ReadNumber(u,"rs_c_gamma",1));
    SetNumber(weight,ReadNumber(l,"max_weight",30,"bz_lord_inventory"));SetNumber(maximum,ReadNumber(l,"max_walk_weight",60,"bz_lord_actor_condition"));SetNumber(speed,ReadNumber(l,"walk_accel",17,"bz_lord_actor")/17);
    RenderState();status.Text="Папка игры выбрана.";
   }catch(Exception ex){game=null;location.Text="Папка игры не выбрана";Error(ex);}EnableActions();
  }
  void RenderState() {
   version.Text="Установленные исправления: "+game.State.version;
   if(catalog!=null)version.Text+="  ·  доступны: "+catalog.version;
   var ids=game.State.addons.Select(a=>a.id).ToList();Select(season,ids.Contains("optional_season_spring")?"optional_season_spring":ids.Contains("optional_season_autumn")?"optional_season_autumn":"");minimal.Checked=ids.Contains("optional_minimal_hud");newHud.Checked=ids.Contains("optional_new_hud");
  }
  void EnableActions() {
   choose.Enabled=!busy;refresh.Enabled=!busy&&game!=null;play.Enabled=!busy&&game!=null;save.Enabled=!busy&&game!=null;addonsButton.Enabled=!busy&&game!=null&&catalog!=null;
   update.Enabled=!busy&&game!=null&&catalog!=null&&game.State.patchHash!=catalog.patch.sha256;
  }
  async Task Run(Func<Task> action) {
   if(busy)return;busy=true;EnableActions();
   try {if(game==null)throw new IOException("Выберите папку игры");game.EnsureStopped();await action();}
   catch(Exception ex){Error(ex);}finally{busy=false;EnableActions();}
  }
  async Task Check() {
   if(busy || game==null)return;busy=true;EnableActions();
   try {status.Text="Проверка обновлений на GitHub…";catalog=await game.Check();notes.Text=(catalog.notes??"").Replace("\n","\r\n");RenderState();status.Text=game.State.patchHash==catalog.patch.sha256?"Установлена актуальная версия.":"Доступен пакет исправлений. Нажмите «Установить обновление».";}
   catch(Exception) {catalog=null;status.Text="Не удалось проверить обновления. Можно играть с текущими файлами.";notes.Text="Сервер обновлений временно недоступен или релиз ещё не опубликован.\r\n\r\nПроверьте подключение к интернету и повторите проверку позже.\r\nИгра и сохранённые настройки доступны без сети.";}
   finally {busy=false;EnableActions();}
  }
  void Error(Exception ex){status.Text=ex.Message;MessageBox.Show(this,ex.Message,"ReFix",MessageBoxButtons.OK,MessageBoxIcon.Information);}
 }
}
