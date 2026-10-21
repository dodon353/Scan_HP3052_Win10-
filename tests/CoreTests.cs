using HP3052Scanner;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

internal static class CoreTests {
 static int passed;
 static string root="";
 static void Check(bool value,string message){if(!value)throw new Exception("FAIL: "+message);Console.WriteLine("PASS: "+message);passed++;}
 static void Throws(Action work,string message){bool caught=false;try{work();}catch{caught=true;}Check(caught,message);}
 static async Task ThrowsAsync(Func<Task> work,string message){bool caught=false;try{await work();}catch{caught=true;}Check(caught,message);}
 static string Image(DocumentSession s,string contents="image fixture"){string p=s.NewImagePath();File.WriteAllText(p,contents);return p;}
 static async Task Main(string[] args){
  root=Path.GetFullPath(args[0]);Directory.CreateDirectory(root);
  try{ProcessingTests.Run(root,Check);ModelTests();ReliabilityTests();SaveDialogTests();RasterTests();PdfTests();if(!args.Contains("--skip-process"))await BackendTests(Path.GetFullPath(args[1]));Console.WriteLine("TOTAL PASSED: "+passed);}catch(Exception e){Console.Error.WriteLine(e);Environment.ExitCode=1;}
 }
 static void ModelTests(){
  var legacy=JsonSerializer.Deserialize<Configuration>("{\"ip\":\"192.168.1.20\",\"distro\":\"Custom Ubuntu\"}")!;
  Check(legacy.ip=="192.168.1.20"&&legacy.distro=="Custom Ubuntu","Existing printer IP and custom Ubuntu name survive settings upgrade");
  Check(legacy.Profiles.Count==3&&legacy.Current.Dpi==300,"Legacy settings acquire default profiles and scan settings");
  var p=ScanProfile.Defaults()[2];Check(p.Dpi==600&&p.Format=="JPG"&&p.Width==100,"Photo profile has explicit paper, quality and resolution");
  var cloned=p.Copy();cloned.Name="changed";Check(p.Name!="changed","Profile copy is independent");
  foreach(string ip in new[]{"192.168.110.100","10.0.0.1","172.16.1.50"}){AppFiles.ValidateIp(ip);Check(true,"Valid printer IPv4: "+ip);}
  foreach(string ip in new[]{"127.0.0.1","0.0.0.0","255.255.255.255","224.1.1.1","192.168.001.1","1.2.3","::1","192.168.1.300","1.2.3.4; touch /tmp/no"})Throws(()=>AppFiles.ValidateIp(ip),"Invalid printer address rejected: "+ip);
  var r=new ScanRequest("192.168.1.20","Ubuntu-22.04","Gray",300,"Flatbed",0,0,210,297);
  string cmd=r.Arguments();Check(cmd.Contains("--source 'Flatbed'")&&cmd.Contains("--resolution 300")&&cmd.Contains("-x 210 -y 297"),"Glass source and physical paper size are always explicit");
  var snapshot=r with{Dpi=600,Source="ADF"};Check(r.Dpi==300&&r.Source=="Flatbed","Captured scan request is immutable while UI values change");
  Check(snapshot.Arguments().Contains("--source 'ADF'"),"Feeder source is explicitly passed to SANE");
  foreach(var bad in new[]{r with{Dpi=0},r with{Dpi=2400},r with{Width=0},r with{Left=-1},r with{Height=double.NaN},r with{Mode="injected"}})Throws(()=>bad.Arguments(),"Invalid scan request is rejected");
  CultureInfo.CurrentCulture=new CultureInfo("ru-RU");Check((r with{Width=100.5}).Arguments().Contains("-x 100.5"),"Scan dimensions use decimal dot under Russian locale");CultureInfo.CurrentCulture=CultureInfo.InvariantCulture;
  Check(ScanRequest.Quote("a'b")=="'a'\\''b'","Shell values correctly escape apostrophes");
  string text="  --source Flatbed|ADF [Flatbed]\n  --mode Lineart|Gray|Color [Color]\n  --resolution 75|100|150|200|300|600|1200dpi [300]\n  -x 0..215.9mm [215.9]\n  -y 0..297mm [297]\n";
  var caps=ScannerCapabilities.Parse(text);
  Check(caps.Sources.SequenceEqual(new[]{"Flatbed","ADF"}),"Source list excludes current-value brackets");
  Check(caps.Resolutions.SequenceEqual(new[]{75,100,150,200,300,600,1200}),"Enumerated driver DPI values are parsed");
  Check(caps.MaxWidth==215.9&&caps.MaxHeight==297,"Driver scan-area limits are parsed");
  caps.Validate(r);Check(true,"Supported paper size, mode and resolution pass validation");
  Throws(()=>caps.Validate(r with{Dpi=250}),"Unavailable enumerated DPI is rejected");
  Throws(()=>caps.Validate(r with{Width=220}),"Oversized original is rejected before scanning");
  Throws(()=>caps.Validate(r with{Mode="RGB"}),"Unavailable color mode is rejected");
  Check(caps.ResolveSource("ADF")=="ADF","Driver feeder source resolves exactly");
  var oldHp=ScannerCapabilities.Parse(text.Replace("Flatbed|ADF [Flatbed]","Auto|ADF [Auto]"));
  Check(oldHp.ResolveSource("Flatbed")=="Auto","Regression: legacy HPLIP Auto|ADF permits glass scanning through Auto");
  Check(oldHp.ResolveSource("ADF")=="ADF","Legacy HPLIP feeder still uses explicit ADF");
  var allHp=ScannerCapabilities.Parse(text.Replace("Flatbed|ADF [Flatbed]","Auto|Flatbed|ADF [Auto]"));
  Check(allHp.ResolveSource("Flatbed")=="Flatbed","Explicit Flatbed takes priority over Auto when both are available");
  Throws(()=>ScannerCapabilities.Parse(text.Replace("Flatbed|ADF [Flatbed]","ADF [ADF]")).ResolveSource("Flatbed"),"ADF-only device cannot silently substitute a feeder for glass");
  Throws(()=>ScannerCapabilities.Parse(text.Replace("Flatbed|ADF [Flatbed]","Auto [Auto]")).ResolveSource("ADF"),"Auto cannot silently substitute for an explicit feeder batch");
  var autoRequest=r with{DriverSource="Auto"};
  Check(autoRequest.Source=="Flatbed"&&autoRequest.RequiresEmptyFeeder,"Auto mapping retains logical glass source and requires an empty feeder");
  Check(autoRequest.Arguments().Contains("--source 'Auto'")&&!autoRequest.Arguments().Contains("--source 'Flatbed'"),"Actual scan command passes the supported Auto value");
  Check(!r.RequiresEmptyFeeder&&!(snapshot with{DriverSource="ADF"}).RequiresEmptyFeeder,"Native Flatbed and explicit ADF do not get the Auto warning");
  var autoRestored=JsonSerializer.Deserialize<ScanRequest>(JsonSerializer.Serialize(autoRequest))!;
  Check(autoRestored.DriverSource=="Auto"&&autoRestored.Source=="Flatbed","Recovery request retains both physical and driver source");
  var limited=ScannerCapabilities.Parse(" --source Flatbed [Flatbed]\n --resolution 100..600dpi [300]\n");
  Check(limited.MinDpi==100&&limited.MaxDpi==600&&limited.Resolutions.Count==0,"Driver DPI ranges are parsed separately from enumerations");
  Throws(()=>limited.ResolveSource("ADF"),"Unsupported feeder is rejected instead of falling back to glass");
  Throws(()=>limited.Validate(r with{Dpi=75}),"DPI below driver's minimum is rejected");
  Check(FeederCompletion.From(7,0).Information!=null&&!FeederCompletion.From(7,0).Error,"Empty feeder before first page is guidance, not a fatal scan error");
  Check(FeederCompletion.From(7,3).Information==null&&!FeederCompletion.From(7,3).Error,"Paper ending after received pages is successful batch completion");
  Check(FeederCompletion.From(6,3).Error&&FeederCompletion.From(9,3).Error,"Jam and connection errors remain errors even after received pages");
  Check(ScanMessages.IsFeederEmpty(7),"Screenshot regression: SANE status 7 means empty feeder");
  foreach(int code in new[]{1,3,5,6,8,9,10,11})Check(!ScanMessages.IsFeederEmpty(code),"SANE error is not mistaken for normal feeder end: "+code);
  Check(ScanMessages.Describe(7).Contains("лицевой стороной вверх"),"Empty feeder gets Russian paper-loading guidance");
  Check(ScanMessages.Describe(6).Contains("замялась")&&ScanMessages.Describe(9).Contains("обмена"),"Paper jams and device I/O retain distinct messages");
  Check(ExportNames.Safe("CON.txt")=="_CON.txt"&&ExportNames.Safe("Lpt1")=="_Lpt1","Windows reserved file names are protected on every platform");
  Check(ExportNames.Safe("a/b:c*?")=="a_b_c__"&&ExportNames.Safe(" . ")=="Документ","Unsafe file-name characters and empty names are normalized");
  var pages=new[]{new PageRecord("1","Страница_003","a.png",1,1,300,300),new PageRecord("2","Счёт","b.png",1,1,300,300)};
  Check(ExportSelection.Choose(pages,new[]{1},true,false).Count==1,"Explicit selected-page export preserves selected-only mode");
  Check(ExportSelection.Choose(pages,new[]{1},true,true).Count==2,"Save-before-close/new-document always exports every page");
  var plan=ExportNames.Plan(root,"Договор","png",pages,false,false,DateTime.Now);
  Check(Path.GetFileName(plan[0])=="Договор_001.png"&&Path.GetFileName(plan[1])=="Договор_Счёт.png","Document prefix applies to separate pages while explicit page names are retained");
  Check(Path.GetFileName(ExportNames.Plan(root,"Документ.pdf","pdf",pages,true,false,DateTime.Now).Single())=="Документ.pdf","Typed file extension is not duplicated during export");
  var dated=ExportNames.Plan(root,"Договор","pdf",pages,true,true,new DateTime(2026,10,21,15,30,0));
  Check(Path.GetFileName(dated.Single())=="Договор_2026-10-21_15-30-00.pdf","Date naming uses a stable file-system-safe format");
  Throws(()=>ExportNames.Plan(root,"doc","pdf",new[]{pages[0] with{Name="same"},pages[1] with{Name="SAME"}},false,false,DateTime.Now),"Case-insensitive export name collisions are rejected");
  string folder=Path.Combine(root,"project");var s=new DocumentSession(folder);Check(s.Pages.Count==0&&!s.Dirty,"New disk-backed project is empty and clean");
  s.Add(Image(s),100,200,300,300,"batch1/1");Check(s.Pages.Count==1&&s.Dirty,"Scanned page is committed immediately and marked unsaved");
  var restored=new DocumentSession(folder);Check(restored.Pages.Count==1&&restored.Pages[0].Origin=="batch1/1"&&restored.Dirty,"Restart restores page metadata and feeder origin marker");
  s.Add(Image(s),100,200,300,300,"batch1/1");Check(s.Pages.Count==1,"Repeating batch recovery does not duplicate an imported page");
  s.Remove(new[]{0});Check(s.WasImported("batch1/1"),"Deleting a recovered page retains the import tombstone");s.Undo();
  s.MarkSaved();Check(!s.Dirty,"Successful full export marks document saved");
  s.Rename(0,"Договор");Check(s.Dirty&&s.Pages[0].Name=="Договор","Renaming a page creates an undoable unsaved change");
  s.Undo();Check(!s.Dirty&&s.Pages[0].Name=="Страница_001","Undoing rename restores the exported document state");
  s.Add(Image(s),150,250,200,200);s.Move(1,0);Check(s.Pages[0].Name=="Страница_002","Page reordering persists to the disk project");
  s.Undo();Check(s.Pages[0].Name=="Страница_001","Undo restores original page order");
  string original=s.Pages[0].File;string edited=Image(s,"rotated bitmap");s.Replace(0,edited,200,100,300,300);Check(s.Pages[0].Width==200&&s.Pages[0].File!=original,"Rotation/crop commits a separate image revision");
  s.Undo();Check(s.Pages[0].File==original&&s.Pages[0].Width==100,"Undo restores original image file and physical metadata");
  s.Remove(new[]{0,1});Check(s.Pages.Count==0,"Multi-page removal is applied as one edit");s.Undo();Check(s.Pages.Count==2,"One undo restores all removed pages");
  s.Rename(0,"saved name");s.MarkSaved();s.Undo();Check(s.Dirty,"Undo after export correctly marks a different document unsaved");
  string manifest=Path.Combine(folder,"session.json"),good=File.ReadAllText(manifest),beforeName=s.Pages[0].Name;File.Delete(manifest);Directory.CreateDirectory(manifest);
  Throws(()=>s.Rename(0,"must rollback"),"Manifest write failure is surfaced");Check(s.Pages[0].Name==beforeName,"Failed autosave rolls back in-memory edit");Directory.Delete(manifest);File.WriteAllText(manifest,good);
  Throws(()=>s.Add(Path.Combine(root,"outside.png"),10,10,300,300),"Pages outside the project cannot be registered");
  File.WriteAllText(manifest,"{broken");Throws(()=>new DocumentSession(folder),"Corrupt recovery manifest is rejected");Check(File.ReadAllText(manifest)=="{broken","Corrupt manifest is preserved for recovery rather than overwritten");File.WriteAllText(manifest,good);
  var malicious=new SessionManifest{Pages=new(){pages[0] with{File="../outside.png"}}};AppFiles.AtomicJson(manifest,malicious);Throws(()=>new DocumentSession(folder),"Recovery rejects image path traversal");File.WriteAllText(manifest,good);
  var missing=new DocumentSession(folder);File.Delete(missing.ImagePath(missing.Pages[0]));Throws(()=>new DocumentSession(folder),"Missing recovery image is detected without resetting the project");
  string abandoned=Path.Combine(folder,"batch_0123456789abcdef0123456789abcdef");Directory.CreateDirectory(abandoned);File.WriteAllText(Path.Combine(abandoned,"page_00001.complete"),"");
  s.Clear();Check(s.IsBatchIgnored(Path.GetFileName(abandoned))&&new DocumentSession(folder).IsBatchIgnored(Path.GetFileName(abandoned)),"New document persistently ignores old batches even if their files cannot be removed");Check(s.Pages.Count==0&&!s.CanUndo&&!s.Dirty,"New document clears history and creates a clean manifest");Check(new DocumentSession(folder).Pages.Count==0,"Clearing document is persisted before old images are removed");
  string config=Path.Combine(root,"settings.json");AppFiles.AtomicJson(config,legacy);Check(JsonSerializer.Deserialize<Configuration>(File.ReadAllText(config))!.distro=="Custom Ubuntu","Atomic settings save retains installation compatibility");
 }
 static void ReliabilityTests(){
  string dir=Path.Combine(root,"reliability");Directory.CreateDirectory(dir);
  var s=new DocumentSession(Path.Combine(dir,"project"));
  s.Add(Image(s),10,20,300,300);Check(!s.CanUndo&&!s.CanRedo,"Acquiring an original does not create a destructive undo step");
  s.Rename(0,"Edited");s.Add(Image(s),10,20,300,300);s.Add(Image(s),10,20,300,300);
  s.Undo();Check(s.Pages.Count==3&&s.Pages[0].Name=="Страница_001","Undo after a feeder batch keeps every acquired page and only reverts the edit");
  Check(!s.CanUndo&&s.CanRedo,"No per-page feeder undo steps remain");
  s.Redo();Check(s.Pages.Count==3&&s.Pages[0].Name=="Edited","Redo restores the edit with all scanned pages intact");
  s.Remove(new[]{1,2});Check(s.Pages.Count==1,"Multi-page removal is one action");s.Undo();Check(s.Pages.Count==3,"Undo restores an entire removed selection");s.Redo();Check(s.Pages.Count==1,"Redo reapplies the removal");s.Undo();
  s.Undo();s.Rename(0,"Different");Check(!s.CanRedo,"A new edit invalidates obsolete redo history");
  var ids=s.Pages.Select(p=>p.Id).ToArray();s.MoveMany(new[]{0,2},3);Check(s.Pages.Select(p=>p.Id).SequenceEqual(new[]{ids[1],ids[0],ids[2]}),"Drag of several non-adjacent pages preserves their relative order");s.Undo();Check(s.Pages.Select(p=>p.Id).SequenceEqual(ids),"One Undo restores a multi-page drag");
  string old=s.ImagePath(s.Pages[0]),revision=Image(s);s.Replace(0,revision,20,10,300,300);s.PruneImages();Check(File.Exists(old),"Cleanup keeps revisions reachable by Undo");s.Undo();s.PruneImages();Check(File.Exists(revision),"Cleanup keeps revisions reachable by Redo");s.Redo();s.ClearHistory();s.PruneImages();Check(!File.Exists(old)&&File.Exists(revision),"Closing history allows cleanup of unreachable old PNGs only");
  string path=Path.Combine(dir,"settings.json");var c=SettingsStore.Load(path,out var warning);Check(warning==null&&c.ip=="","First run has no invented printer address");
  var original=new Configuration{ip="192.168.1.22",ShowAutoSourceHint=false};SettingsStore.Save(path,original);SettingsStore.Save(path,new Configuration{ip="192.168.1.23"});string goodBackup=File.ReadAllText(path+".bak");Check(goodBackup.Contains("192.168.1.22"),"Saving settings retains the previous valid version");
  File.WriteAllText(path,"{damaged");c=SettingsStore.Load(path,out warning);Check(warning!=null&&File.ReadAllText(path)=="{damaged","Damaged settings warn and are not overwritten during loading");Check(Directory.GetFiles(dir,"settings.json.corrupt_*.bak").Any(f=>File.ReadAllText(f)=="{damaged"),"Corrupt settings are backed up byte-for-byte before defaults are offered");
  SettingsStore.Save(path,c);Check(File.ReadAllText(path+".bak")==goodBackup,"First save after corruption preserves the last valid backup");
  original.ip="scanner.local";SettingsStore.Save(path,original);var loaded=SettingsStore.Load(path,out warning);Check(loaded.ip=="scanner.local"&&!loaded.ShowAutoSourceHint,"Hostname and disabled Auto hint survive reload");
  foreach(var name in new[]{"scanner.local","HP3052","hp-3052.office.lan"}){ScannerAddress.Validate(name);Check(true,"Valid scanner hostname: "+name);}
  foreach(var name in new[]{"","bad;command","https://scanner","bad..local","-bad.local","192.168.1.999"})Throws(()=>ScannerAddress.Validate(name),"Invalid scanner hostname rejected: "+name);
  Check(ScannerDiscovery.Parse("device `hpaio:/net/HP_LaserJet_3052?ip=192.168.1.3'\ndevice hpaio:/net/Other?ip=192.168.1.4\nhpaio:/net/HP_LaserJet_3052?ip=192.168.1.3").SequenceEqual(new[]{"192.168.1.3"}),"Discovery selects HP 3052 devices and removes duplicate addresses");
  var r=new ScanRequest("192.168.1.20","Ubuntu","Color",1200,"Flatbed",0,0,210,297);Check(ScanPolicy.Timeout(r)>180000&&ScanPolicy.Timeout(r)<=3600000,"High-resolution color scan gets a bounded longer deadline");Check(ScanPolicy.Timeout(r with{Dpi=300})<ScanPolicy.Timeout(r),"Scan deadline scales with acquisition workload");
  var caps=ScannerCapabilities.Parse(" --source Flatbed|ADF [Flatbed]\n --resolution 100|200|300|600dpi [300]\n -l 0..215.9mm [0]\n -t 0..297mm [0]\n -x 0..215.9mm [215.9]\n -y 0..297mm [297]\n");
  var preview=ScanPolicy.Preview(r with{Width=100,Height=150},caps);caps.Validate(preview);Check(preview.Dpi==100,"Preview uses driver minimum when 75 DPI is unsupported");Check(preview.Width==215.9&&preview.Height==297,"Photo preset does not limit full-glass preview dimensions");
  Check(preview.DriverDefaultArea&&!preview.Arguments().Contains(" -x ")&&!preview.Arguments().Contains(" -y ")&&!preview.Arguments().Contains(" -l ")&&!preview.Arguments().Contains(" -t "),"Regression br-x: full preview does not reapply rounded CLI geometry");
  Check(r.Arguments().Contains(" -x 210 -y 297"),"Ordinary paper-size scanning retains explicit dimensions");
  var result=ScanPolicy.PreviewResult(preview,849,1169);Check(Math.Abs(result.Width-215.646)<.000001&&Math.Abs(result.Height-296.926)<.000001,"Preview geometry uses actual acquired pixels rather than the advertised maximum");
  var offset=ScannerCapabilities.Parse(" --resolution 100..600dpi [100]\n -l 0..215.9mm [1.25]\n -t 0..297mm [2.5]\n -x 0..215.9mm [214.65]\n -y 0..297mm [294.5]\n");
  var offsetPreview=ScanPolicy.Preview(r,offset);Check(offsetPreview.Left==1.25&&offsetPreview.Top==2.5,"Native preview preserves reported nonzero default origin for crop coordinates");
  Check(JsonSerializer.Deserialize<ScanRequest>(JsonSerializer.Serialize(result))!.DriverDefaultArea,"Native-area mode survives request serialization");
  var crop=r with{Left=result.Left+100*25.4/result.Dpi,Top=result.Top+100*25.4/result.Dpi,Width=50,Height=60,DriverDefaultArea=false};Check(crop.Arguments().Contains(" -l 25.4 -t 25.4 -x 50 -y 60"),"Selected area keeps explicit millimeter coordinates after native preview");
  Throws(()=>ScanPolicy.PreviewResult(preview,0,100),"Invalid acquired preview dimensions are rejected");
  string error="scanimage: setting of option --br-x failed (Invalid argument)";string message=ScanMessages.DescribeCommandFailure(1,error);Check(message.Contains("границы")&&message.Contains("--br-x")&&!message.Contains("не поддерживает"),"CLI exit 1 reports the actual rejected geometry instead of SANE unsupported-function status");
  Check(ScanMessages.DescribeCommandFailure(1,"unknown argument").Contains("unknown argument"),"Other CLI errors preserve driver details");
  Throws(()=>new LinuxResult(1,"",error).RequireSuccess(),"Driver option failure still stops acquisition");
 }
 static void SaveDialogTests(){
  string folder=Path.Combine(root,"save-dialog");Directory.CreateDirectory(folder);
  var now=new DateTime(2026,10,21,16,30,45);
  foreach(var f in new[]{"PDF","JPG","PNG","TIFF","BMP"}){
   string suggested=ExportSavePlan.Suggest(folder,f,1,true,now);
   Check(Path.GetFileName(suggested)=="Скан_2026-10-21_16-30-45."+f.ToLowerInvariant(),"Save dialog automatically suggests a name with the correct extension: "+f);
   string chosen=Path.Combine(folder,"Мой документ."+f.ToLowerInvariant());
   Check(ExportSavePlan.FromChoice(chosen,f,1,false).Single()==chosen,"One page uses the exact filename chosen in the save dialog: "+f);
   Check(ExportSavePlan.FromChoice(chosen,f,3,false).Select(Path.GetFileName).SequenceEqual(new[]{"Мой документ_001."+f.ToLowerInvariant(),"Мой документ_002."+f.ToLowerInvariant(),"Мой документ_003."+f.ToLowerInvariant()}),"Several separate files use the chosen stem and sequential numbering: "+f);
   Check(ExportSavePlan.Filter(f).Contains("*."+f.ToLowerInvariant()),"Save dialog filter matches output bytes: "+f);
  }
  Check(ExportSavePlan.FromChoice(Path.Combine(folder,"Все страницы.pdf"),"PDF",8,true).Length==1,"Merged PDF keeps one chosen filename for every page");
  Check(Path.GetFileName(ExportSavePlan.FromChoice(Path.Combine(folder,"photo.jpeg"),"JPG",1,false).Single())=="photo.jpeg","JPEG alias from the save dialog is preserved");
  Check(Path.GetFileName(ExportSavePlan.FromChoice(Path.Combine(folder,"scan.tif"),"TIFF",1,false).Single())=="scan.tif","TIFF alias from the save dialog is preserved");
  Check(Path.GetFileName(ExportSavePlan.FromChoice(Path.Combine(folder,"Документ.pdf"),"PNG",1,false).Single())=="Документ.png","A typed extension cannot mislabel PNG bytes as PDF");
  Check(Path.GetFileName(ExportSavePlan.FromChoice(Path.Combine(folder,"Счёт.v2"),"PNG",1,false).Single())=="Счёт.v2.png","Dots in a custom name are retained while the output extension is added");
  string name=ExportSavePlan.Suggest(folder,"PNG",3,false,now);var planned=ExportSavePlan.FromChoice(name,"PNG",3,false);File.WriteAllText(planned[1],"existing page");
  string next=ExportSavePlan.Suggest(folder,"PNG",3,false,now);Check(next!=name&&ExportSavePlan.FromChoice(next,"PNG",3,false).All(p=>!File.Exists(p)),"Automatic naming avoids a collision with any file in a batch");
  File.WriteAllText(ExportSavePlan.Suggest(folder,"PDF",1,true,now),"existing PDF");Check(Path.GetFileName(ExportSavePlan.Suggest(folder,"PDF",1,true,now)).EndsWith("_2.pdf"),"Same-second saves suggest a fresh filename instead of an overwrite");
 }
 static void RasterTests(){
  byte[] bgr={0,0,0,255,255,255,128,128,128};Check(RasterPacking.Row(bgr,3,"Gray").SequenceEqual(new byte[]{0,255,128}),"Grayscale storage has one byte per pixel");Check(RasterPacking.Row(bgr,3,"Lineart").SequenceEqual(new byte[]{96}),"Monochrome rows are bit-packed MSB-first with correct black/white polarity");Check(RasterPacking.Row(new byte[]{1,2,3},1,"Color").SequenceEqual(new byte[]{3,2,1}),"Color rows convert BGR to RGB");
  foreach(string mode in new[]{"Gray","Lineart","Color"}){
   string packed=Path.Combine(root,mode+".z");using(var f=File.Create(packed))RasterPacking.Write(f,3,2,mode,_=>bgr,CancellationToken.None);
   using(var f=File.OpenRead(packed))using(var z=new ZLibStream(f,CompressionMode.Decompress))using(var raw=new MemoryStream()){z.CopyTo(raw);byte[] bytes=raw.ToArray();int length=RasterPacking.Row(bgr,3,mode).Length;Check(bytes[0]==2&&bytes[length+1]==2&&bytes.Skip(length+2).All(v=>v==0),"PNG Up predictor resets identical following row to zero: "+mode);}
   string pdf=Path.Combine(root,mode+".pdf");PdfStreamWriter.Write(pdf,1,_=>new EncodedPdfPage(packed,3,2,72,72,"FlateDecode",false,mode=="Color"?"DeviceRGB":"DeviceGray",RasterPacking.Bits(mode),true),false,CancellationToken.None);string text=Encoding.ASCII.GetString(File.ReadAllBytes(pdf));Check(text.Contains("/Predictor 15")&&text.Contains("/BitsPerComponent "+RasterPacking.Bits(mode))&&text.Contains(mode=="Color"?"/DeviceRGB":"/DeviceGray"),"PDF declares the actual channels, bit depth and predictor: "+mode);
  }
 }
 static void PdfTests(){
  string red=Path.Combine(root,"red.z"),blue=Path.Combine(root,"blue.z");
  void Pixels(string f,byte r,byte g,byte b){using var fs=File.Create(f);using var z=new ZLibStream(fs,CompressionLevel.Optimal);for(int i=0;i<600;i++)z.Write(new[]{r,g,b});}
  Pixels(red,255,0,0);Pixels(blue,0,0,255);
  var pages=new[]{new EncodedPdfPage(red,20,30,72,72,"FlateDecode"),new EncodedPdfPage(blue,30,20,72,72,"FlateDecode")};
  string pdf=Path.Combine(root,"two_pages.pdf");PdfStreamWriter.Write(pdf,2,i=>pages[i],false,CancellationToken.None);byte[] bytes=File.ReadAllBytes(pdf);string text=Encoding.ASCII.GetString(bytes);
  Check(text.StartsWith("%PDF-1.4")&&text.Contains("/Count 2"),"Lossless multi-page PDF includes a valid header and page count");
  Check(text.Contains("/MediaBox [0 0 20 30]")&&text.Contains("/MediaBox [0 0 30 20]"),"PDF retains physical size and rotated orientation");
  Check(text.Contains("/Filter /FlateDecode"),"Text PDF uses lossless zlib streams");
  int xrefStart=text.LastIndexOf("startxref\n",StringComparison.Ordinal)+10;long xref=long.Parse(text[xrefStart..].Split('\n')[0]);Check(text[(int)xref..].StartsWith("xref\n"),"PDF startxref points to the actual cross-reference table");
  var lines=text[(int)xref..].Split('\n');bool valid=true;for(int i=1;i<=8;i++){int offset=int.Parse(lines[i+2][..10]);valid&=text[offset..].StartsWith(i+" 0 obj\n");}Check(valid,"Every PDF object offset points to the correct object");
  Throws(()=>PdfStreamWriter.Write(pdf,1,_=>pages[0],false,CancellationToken.None),"PDF overwrite requires explicit consent");Check(File.ReadAllBytes(pdf).SequenceEqual(bytes),"Refused overwrite leaves existing PDF byte-for-byte unchanged");
  using var cancelled=new CancellationTokenSource();cancelled.Cancel();Throws(()=>PdfStreamWriter.Write(Path.Combine(root,"cancelled.pdf"),1,_=>pages[0],false,cancelled.Token),"Cancelled export is stopped before replacing destination");Check(!File.Exists(Path.Combine(root,"cancelled.pdf")),"Cancelled export leaves no partial destination file");
  Throws(()=>PdfStreamWriter.Write(Path.Combine(root,"bad.pdf"),2,i=>i==0?pages[0]:throw new IOException("missing second page"),false,CancellationToken.None),"Page encoding failure is propagated");Check(!File.Exists(Path.Combine(root,"bad.pdf")),"Failed multi-page export leaves no broken final PDF");
  Throws(()=>PdfStreamWriter.Write(Path.Combine(root,"invalid.pdf"),1,_=>pages[0] with{DpiX=0},false,CancellationToken.None),"Invalid physical resolution cannot produce malformed PDF");
  string disposable=Path.Combine(root,"temporary-image.z");File.Copy(red,disposable,true);PdfStreamWriter.Write(Path.Combine(root,"temporary.pdf"),1,_=>pages[0] with{File=disposable,DeleteAfterUse=true},false,CancellationToken.None);Check(!File.Exists(disposable),"Per-page encoded image is removed after PDF streaming");
  Check(Directory.GetFiles(root,"*.tmp").Length==0,"Export failure and cancellation clean temporary PDF files");
 }
 static async Task BackendTests(string qa){
  string calls=Path.Combine(root,"scanimage-calls.jsonl");File.WriteAllText(calls,"");Environment.SetEnvironmentVariable("HP3052_TEST_CALLS",calls);Environment.SetEnvironmentVariable("PATH",qa+Path.PathSeparator+Environment.GetEnvironmentVariable("PATH"));
  var backend=new ScannerBackend(Path.Combine(qa,"fake-wsl.sh"));var token=CancellationToken.None;
  var good=await backend.RunAsync("test distro","printf '%s' 'проверка UTF-8'",token);Check(good.Code==0&&good.Output=="проверка UTF-8","Real child process preserves Linux UTF-8 output and distro argument with spaces");
  string fixture=Path.Combine(root,"large.py");File.WriteAllText(fixture,"import sys\nsys.stdout.write('x'*100000)\nsys.stderr.write('y'*100000)\nsys.exit(9)\n");
  var large=await backend.RunAsync("fixture","python3 "+ScanRequest.Quote(fixture),token);Check(large.Code==9&&large.Output.Length==100000&&large.Error.TrimEnd().Length==100000,"Both native pipes are drained concurrently and error code retained");
  var r=new ScanRequest("192.168.1.20","test distro","Gray",300,"Flatbed",0,0,210,297);
  var prepared=await backend.Prepare(r,token);Check(prepared.Request.Source=="Flatbed"&&prepared.Capabilities.MaxDpi==1200,"Capabilities are fetched for the selected physical source");
  Check(File.ReadAllLines(calls).Length==2,"Initial capability check queries general and source-specific options");
  await backend.Prepare(r,token);Check(File.ReadAllLines(calls).Length==2,"Repeated scan reuses capabilities for the same scanner/source");
  await backend.Prepare(r,token,true);Check(File.ReadAllLines(calls).Length==4,"Explicit connection check refreshes driver capabilities");
  await ThrowsAsync(async()=>await backend.Prepare(r with{Dpi=250},token),"Backend rejects unsupported resolution before acquiring data");
  await backend.Prepare(r with{Source="ADF"},token);Check(File.ReadAllLines(calls).Any(x=>x.Contains("ADF")),"Feeder capabilities use an explicit ADF source");
  var completed=new List<int>();var batch=await backend.RunAsync("fixture",(r with{Source="ADF"}).Arguments(),token,180000,n=>completed.Add(n));Check(batch.Code==7&&completed.SequenceEqual(new[]{1,2}),"Completed feeder pages are retained when SANE ends with no-documents status");
  Check(ScanMessages.IsFeederEmpty(batch.Code),"Feeder end is classified separately from hardware errors");
  Environment.SetEnvironmentVariable("HP3052_TEST_SOURCES","Auto|ADF");
  try{
   var oldHp=new ScannerBackend(Path.Combine(qa,"fake-wsl.sh"));int before=File.ReadAllLines(calls).Length;
   var glass=await oldHp.Prepare(r,token,true);
   Check(glass.Request.Source=="Flatbed"&&glass.Request.DriverSource=="Auto"&&glass.Request.RequiresEmptyFeeder,"Real process fixture: legacy HP connection check accepts Auto without changing glass into a batch");
   var newCalls=File.ReadAllLines(calls).Skip(before).Select(x=>JsonSerializer.Deserialize<string[]>(x)!).ToArray();
   Check(newCalls.Length==2&&newCalls.All(x=>x.Contains("-A"))&&newCalls[1].Contains("Auto"),"Connection check only probes options and uses Auto in the source-specific query");
   Check(glass.Request.Arguments().Contains("--source 'Auto'"),"Prepared legacy glass scan uses Auto on acquisition too");
   var adf=await oldHp.Prepare(r with{Source="ADF"},token);
   Check(adf.Request.Source=="ADF"&&adf.Request.DriverSource=="ADF"&&!adf.Request.RequiresEmptyFeeder,"Legacy batch remains explicit ADF after a glass connection check");
   var cached=await oldHp.Prepare(r,token);Check(cached.Request.DriverSource=="Auto"&&cached.Request.Source=="Flatbed","Cache keeps glass/Auto mapping independent of the feeder");
  }finally{Environment.SetEnvironmentVariable("HP3052_TEST_SOURCES",null);}
  using var cancel=new CancellationTokenSource(150);await ThrowsAsync(async()=>await backend.RunAsync("fixture","sleep 20",cancel.Token),"Cancellation stops the recorded Linux job");
  var survivor=backend.RunAsync("fixture","/bin/bash -c 'sleep .6; printf survivor'",token,5000);
  using var cancelOther=new CancellationTokenSource(150);try{await backend.RunAsync("fixture","sleep 20",cancelOther.Token);}catch(OperationCanceledException){}
  Check((await survivor).Output=="survivor","Cancelling one scan job does not kill another Linux job");
  await ThrowsAsync(async()=>await backend.RunAsync("fixture","sleep 20",token,150),"Timeout stops the recorded job and reports a timeout");
  using var preCancelled=new CancellationTokenSource();preCancelled.Cancel();await ThrowsAsync(async()=>await backend.RunAsync("fixture","printf should-not-run",preCancelled.Token),"Pre-cancelled operation does not launch WSL");
  string survivorFile=Path.Combine(root,"early-cancel-survivor");Environment.SetEnvironmentVariable("HP3052_TEST_DETACH","1");
  try {using var early=new CancellationTokenSource(150);await ThrowsAsync(async()=>await backend.RunAsync("fixture","/bin/bash -c "+ScanRequest.Quote("sleep .9; printf escaped > "+ScanRequest.Quote(survivorFile)),early.Token),"Cancellation before PID publication returns cancellation");await Task.Delay(1200);Check(!File.Exists(survivorFile),"Early cancellation stops a detached Linux job even when its PID was not written yet");}
  finally {Environment.SetEnvironmentVariable("HP3052_TEST_DETACH",null);}
  Environment.SetEnvironmentVariable("HP3052_TEST_GEOMETRY","1");
  try {
   var failure=await backend.RunAsync("fixture",r.Arguments(),token);Check(failure.Code==1&&failure.Error.Contains("--br-x"),"Process fixture reproduces the supplied br-x Invalid argument log");
   var c=ScannerCapabilities.Parse(" --resolution 75|100|300dpi [75]\n -l 0..215.9mm [0]\n -t 0..297mm [0]\n -x 0..215.9mm [215.9]\n -y 0..297mm [297]\n");
   var native=ScanPolicy.Preview(r,c);var success=await backend.RunAsync("fixture",native.Arguments(),token);Check(success.Code==0&&success.Output=="native-preview-fixture","Native-area preview succeeds against the same fixture without retrying or overriding limits");
  } finally {Environment.SetEnvironmentVariable("HP3052_TEST_GEOMETRY",null);}
 }
}
