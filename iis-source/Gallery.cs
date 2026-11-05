using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections;
using System.Collections.Generic;
using System.Web;
using System.Web.Script.Serialization;
using System.Configuration;
using System.Net;
using System.Security.Cryptography;
using System.IO.Pipes;
using System.Drawing;
using System.Drawing.Imaging;

public class GalleryHandler : IHttpHandler {
    public bool IsReusable { get { return true; } }
    public void ProcessRequest(HttpContext c) {
        c.Response.Cache.SetCacheability(HttpCacheability.NoCache);
        c.Response.Headers["X-Content-Type-Options"]="nosniff";
        try {
            string route=c.Request.Path.TrimEnd('/').Split('/').Last().ToLowerInvariant();
            if(c.Request.Path.StartsWith("/api/external/",StringComparison.OrdinalIgnoreCase)){Gallery.ProcessExternal(c);return;}
            if(GalleryAuth.Enabled){
                string role=GalleryAuth.Role(c);
                if(role==""){Reply(c,401,new{error="Sign in to the gallery."});return;}
                if((role=="worker"&&(route!="downloads"||c.Request.HttpMethod!="GET"))||(role!="admins"&&role!="worker"&&GalleryAuth.NeedsAdmin(c.Request.Path,c.Request.HttpMethod,c.Request.QueryString["rescan"]))){Reply(c,403,new{error="Only gallery administrators can perform this action."});return;}
            }
            if(route=="image") { Gallery.Image(c); return; }
            if(route=="export-csv"&&c.Request.HttpMethod=="GET"){Gallery.DatabaseExportCsv(c);return;}
            if(route=="export-json"&&c.Request.HttpMethod=="GET"){Gallery.DatabaseExportJson(c);return;}
            if(route=="health") { Reply(c,200,new {service="wallpaper-gallery-iis",root=Gallery.Root}); return; }
            if(c.Request.Headers["X-Gallery-Request"]!="1") throw new GalleryError(403,"Open the gallery to use this endpoint.");
            if(c.Request.HttpMethod=="POST") {
                Uri origin;
                string expectedOrigin=GalleryAuth.Enabled?GalleryAuth.PublicUrl:c.Request.Url.GetLeftPart(UriPartial.Authority);
                if(!Uri.TryCreate(c.Request.Headers["Origin"],UriKind.Absolute,out origin) || origin.GetLeftPart(UriPartial.Authority)!=expectedOrigin)
                    throw new GalleryError(403,"Request must come from this gallery.");
            }
            if(route=="inventory" && c.Request.HttpMethod=="GET") { if(c.Request.QueryString["rescan"]=="1")Gallery.RequestDatabaseWorker(); if(Gallery.PagedInventory){Reply(c,200,Gallery.DatabaseInventoryPage(c));return;} var inventory=Gallery.InventoryView(false);Reply(c,200,GalleryAuth.IsAdmin(c)?inventory:Gallery.AccessibleInventory(inventory,c)); return; }
            if(route=="entry-files" && c.Request.HttpMethod=="GET"){Reply(c,200,Gallery.DatabaseEntryFiles(c));return;}
            if(route=="inventory-meta" && c.Request.HttpMethod=="GET"){Reply(c,200,Gallery.DatabaseInventoryMetadata(c));return;}
            if(route=="changes" && c.Request.HttpMethod=="GET"){Reply(c,200,Gallery.DatabaseChanges(c));return;}
            if(route=="asset" && c.Request.HttpMethod=="GET"){Reply(c,200,Gallery.DatabaseAsset(c));return;}
            if(route=="collection-move" && c.Request.HttpMethod=="GET"){Reply(c,200,Gallery.DatabaseMoveStatus(c));return;}
            if(route=="integrations" && c.Request.HttpMethod=="GET"){Reply(c,200,Gallery.DatabaseIntegrationStatus());return;}
            if(route=="access-settings" && c.Request.HttpMethod=="GET") { Reply(c,200,Gallery.AccessSettingsView());return; }
            if(route=="reddit-status" && c.Request.HttpMethod=="GET") { Reply(c,200,new {available=Gallery.RedditSessionAvailable()}); return; }
            if(route=="wallhaven-status" && c.Request.HttpMethod=="GET") { Reply(c,200,new {available=Gallery.WallhavenKeyAvailable()}); return; }
            if(route=="downloads" && c.Request.HttpMethod=="GET") { Reply(c,200,Gallery.DownloadQueueStatus()); return; }
            if(route=="duplicates" && c.Request.HttpMethod=="GET") { Reply(c,200,Gallery.DuplicateStatus()); return; }
            if(route=="database-status" && c.Request.HttpMethod=="GET") { Reply(c,200,Gallery.DatabaseWorkerStatus()); return; }
            if(route=="recovery-action-status" && c.Request.HttpMethod=="GET") { if(!GalleryAuth.IsAdmin(c))throw new GalleryError(403,"Only administrators can review recovery decisions.");Reply(c,200,Gallery.DatabaseRecoveryActionStatus(c.Request.QueryString["ids"]??""));return; }
            if(route=="recovery-operations" && c.Request.HttpMethod=="GET") { if(!GalleryAuth.IsAdmin(c))throw new GalleryError(403,"Only gallery administrators can review file operations.");Reply(c,200,Gallery.DatabaseRecoveryOperations(c.Request.QueryString["id"]??"") );return; }
            if(route=="retry-recovery-operation" && c.Request.HttpMethod=="POST") { if(!GalleryAuth.IsAdmin(c))throw new GalleryError(403,"Only gallery administrators can retry file operations.");Reply(c,202,Gallery.RequestDatabaseWorker());return; }
            if(route=="clear-scan-issues" && c.Request.HttpMethod=="POST") { Reply(c,200,Gallery.ClearDatabaseIssues()); return; }
            if(route=="retry-scan-issues" && c.Request.HttpMethod=="POST") { Reply(c,202,Gallery.RetryDatabaseIssues()); return; }
            if(route=="manual-scan" && c.Request.HttpMethod=="GET") { Reply(c,200,Gallery.ManualScanStatus()); return; }
            if(route=="organize" && c.Request.HttpMethod=="GET") { Reply(c,200,Gallery.OrganizationStatus()); return; }
            if(c.Request.HttpMethod!="POST") throw new GalleryError(405,"Use POST for this action.");
            if(c.Request.ContentLength>(route=="downloads" ? 1048576 : route=="access-settings" ? 65536 : 8192)) throw new GalleryError(400,"Request is too large.");
            var payload=Gallery.Json.Deserialize<Dictionary<string,object>>(new StreamReader(c.Request.InputStream).ReadToEnd());
            if(payload==null) throw new GalleryError(400,"Invalid request.");
            if(route=="access-settings") { Reply(c,200,Gallery.SaveAccessPolicy(c,payload));return; }
            if(route=="recovery-action") { if(!GalleryAuth.IsAdmin(c))throw new GalleryError(403,"Only administrators can resolve recovery issues.");Reply(c,202,Gallery.DatabaseRecoveryAction(payload));return; }
            if(route=="resolve-superseded-recovery") { if(!GalleryAuth.IsAdmin(c))throw new GalleryError(403,"Only gallery administrators can resolve file operations.");Reply(c,200,Gallery.DatabaseResolveSupersededRecoveryOperation(payload));return; }
            if(route=="hold-recovery-operation") { if(!GalleryAuth.IsAdmin(c))throw new GalleryError(403,"Only gallery administrators can hold file operations.");Reply(c,200,Gallery.DatabaseHoldRecoveryOperation(payload));return; }
            if(route=="keep-both-recovery") { if(!GalleryAuth.IsAdmin(c))throw new GalleryError(403,"Only gallery administrators can resolve file operations.");Reply(c,200,Gallery.DatabaseKeepBothRecoveryOperation(payload));return; }
            if(route=="export-inventory") { Reply(c,200,Gallery.ExportDatabaseInventory(Gallery.Text(payload,"collection")));return; }
            if(route=="import-updates") { Gallery.RequestDatabaseImportUpdates(); Reply(c,202,Gallery.RequestDatabaseWorker());return; }
            if(route=="duplicates") { Reply(c,202,Gallery.StartDuplicateScan()); return; }
            if(route=="organize") { Reply(c,202,Gallery.StartOrganization()); return; }
            if(route=="manual-scan") { Reply(c,202,Gallery.StartManualScan()); return; }
            if(route=="link-local") { Reply(c,200,Gallery.LinkLocalImage(payload)); return; }
            if(route=="download") { Reply(c,200,Gallery.Download(payload)); return; }
            if(route=="delete-entry") { Reply(c,200,Gallery.DeleteEntry(payload)); return; }
            if(route=="move-entry") {Reply(c,202,Gallery.DatabaseMoveEntry(payload));return;}
            if(route=="set-tags") { Reply(c,200,Gallery.DatabaseSetTags(payload));return; }
            if(route=="downloads-filtered") { Reply(c,202,Gallery.EnqueueFilteredDownloads(c));return;}
            if(route=="downloads") { Reply(c,202,Gallery.EnqueueDownloads(payload)); return; }
            if(route=="download-stop") { Reply(c,200,Gallery.StopDownloadQueue()); return; }
            if(route=="download-resume") { Reply(c,200,Gallery.ResumeDownloadQueue()); return; }
            if(route=="reddit-post") { Reply(c,200,Gallery.ResolveRedditPost(payload)); return; }
            if(route=="wallhaven") { Reply(c,200,Gallery.Wallhaven(Gallery.Text(payload,"id"),Gallery.Text(payload,"key"))); return; }
            if(route=="preview") { Reply(c,200,Gallery.SourcePreview(payload)); return; }
            throw new GalleryError(404,"Unknown endpoint.");
        } catch(GalleryError e) { Reply(c,e.Code,new {error=e.Message}); }
          catch(MySqlConnector.MySqlException) { Reply(c,503,new {error="The database is temporarily unavailable. Existing files and pending work are preserved. Retry shortly."}); }
          catch(WebException e) {
              var response=e.Response as HttpWebResponse;
              int code=response==null ? 0 : (int)response.StatusCode;
              if(response!=null)response.Close();
              Reply(c,502,new {error=code==0 ? "The source could not be reached. Retry later." : "Source returned HTTP "+code+". Check your account access or retry later."});
          } catch(Exception) { Reply(c,500,new {error="The operation could not finish. Check gallery file permissions and retry."}); }
    }
    static void Reply(HttpContext c,int status,object data) {
        c.Response.StatusCode=status;c.Response.TrySkipIisCustomErrors=true;
        c.Response.ContentType="application/json; charset=utf-8";
        if(c.Request.Path.TrimEnd('/').EndsWith("/inventory",StringComparison.OrdinalIgnoreCase)&&(c.Request.Headers["Accept-Encoding"]??"").IndexOf("gzip",StringComparison.OrdinalIgnoreCase)>=0) {
            c.Response.AppendHeader("Content-Encoding","gzip");c.Response.AppendHeader("Vary","Accept-Encoding");
            c.Response.Filter=new System.IO.Compression.GZipStream(c.Response.Filter,System.IO.Compression.CompressionMode.Compress);
        }
        c.Response.Write(Gallery.Json.Serialize(data));
    }
}
public class GalleryError : Exception {
    public int Code;
    public GalleryError(int code,string message):base(message) {Code=code;}
}
public class Collection {
    public string Id, Folder, Inventory, Original;
    public List<Dictionary<string,object>> Records;
    public bool CacheFiles;
    public long DatabaseDocumentId;
    public Dictionary<string,GalleryFileReference> DatabaseFiles;
    public Dictionary<string,FileInfo> ImageFiles;
    public Dictionary<string,Collection> References;
}
public static partial class Gallery {
    static Gallery() {ServicePointManager.SecurityProtocol=SecurityProtocolType.Tls12;}
    public static readonly JavaScriptSerializer Json=new JavaScriptSerializer {MaxJsonLength=64000000,RecursionLimit=100};
    public static string Root {get {return Path.GetFullPath(ConfigurationManager.AppSettings["WallpaperRoot"] ?? @"C:\downloads\wallpapers").TrimEnd('\\');}}
    static readonly object Sync=new object();
    static readonly string[] Extensions={".jpg",".jpeg",".png",".webp",".gif",".bmp",".avif",".tif",".tiff",".swf"};
    public static string Text(Dictionary<string,object> r,string key) {object o;return r.TryGetValue(key,out o) && o!=null ? Convert.ToString(o) : "";}
    static bool ImageFile(string path) {return Extensions.Contains(Path.GetExtension(path).ToLowerInvariant());}
    static bool Linked(string path) {return (File.GetAttributes(path)&FileAttributes.ReparsePoint)!=0;}
    static bool Under(string path,string root) {return path.StartsWith(root.TrimEnd('\\')+"\\",StringComparison.OrdinalIgnoreCase);}
    static IEnumerable<FileInfo> CollectionImageInfos(string folder) {
        var directory=new DirectoryInfo(folder);
        foreach(var file in directory.GetFiles())if(ImageFile(file.Name)&&(file.Attributes&FileAttributes.ReparsePoint)==0)yield return file;
        foreach(var child in directory.GetDirectories())if((child.Attributes&FileAttributes.ReparsePoint)==0&&!child.Name.Equals("_download-info",StringComparison.OrdinalIgnoreCase))foreach(var file in CollectionImageInfos(child.FullName))yield return file;
    }
    static string SafeFile(Collection collection,string name) {
        if(String.IsNullOrEmpty(name)||Path.IsPathRooted(name))return null;
        string path;
        try {path=Path.GetFullPath(Path.Combine(collection.Folder,name));} catch {return null;}
        if(!Under(path,collection.Folder)||!ImageFile(path))return null;
        if(collection.CacheFiles) {
            if(collection.ImageFiles==null)collection.ImageFiles=CollectionImageInfos(collection.Folder).ToDictionary(f=>f.FullName.Substring(collection.Folder.Length+1).Replace('\\','/'),StringComparer.OrdinalIgnoreCase);
            FileInfo cached;if(collection.ImageFiles.TryGetValue(path.Substring(collection.Folder.Length+1).Replace('\\','/'),out cached))return cached.FullName;
        }
        var cursor=path;
        while(cursor.Length>=collection.Folder.Length) {
            if((File.Exists(cursor)||Directory.Exists(cursor))&&Linked(cursor))return null;
            if(String.Equals(cursor,collection.Folder,StringComparison.OrdinalIgnoreCase))break;
            cursor=Path.GetDirectoryName(cursor);
        }
        return path;
    }
    static FileInfo ImageInfo(Collection collection,string path) {
        if(path==null)return null;
        FileInfo file;
        if(collection.ImageFiles!=null&&Under(path,collection.Folder)&&collection.ImageFiles.TryGetValue(path.Substring(collection.Folder.Length+1).Replace('\\','/'),out file))return file;
        return new FileInfo(path);
    }
    static bool Present(Collection collection,string path) {if(DatabaseEnabled&&collection.DatabaseFiles!=null)return path!=null&&collection.DatabaseFiles.ContainsKey(path);var file=ImageInfo(collection,path);return file!=null&&file.Exists&&file.Length>0;}
    static IEnumerable<string> InfoFolders(string folder) {
        foreach(string child in Directory.GetDirectories(folder)) {
            if(Linked(child))continue;
            if(Path.GetFileName(child).Equals("_download-info",StringComparison.OrdinalIgnoreCase))yield return child;
            else foreach(string info in InfoFolders(child))yield return info;
        }
    }
    static List<Collection> Collections(List<string> issues) {
        if(DatabaseEnabled)return DatabaseCollections();
        var result=new List<Collection>();
        foreach(string info in InfoFolders(Root).OrderBy(p=>p)) {
            string file=Path.Combine(info,"inventory.json");
            if(!File.Exists(file)) {
                var options=Directory.GetFiles(info,"*inventory.json");
                if(options.Length!=1) {issues.Add(info+": "+(options.Length==0 ? "No inventory.json found." : "Multiple inventories; choose a canonical inventory.json."));continue;}
                file=options[0];
            }
            if(Linked(file)) {issues.Add(info+": Linked inventory skipped.");continue;}
            try {
                string original=File.ReadAllText(file,Encoding.UTF8);
                var records=Json.Deserialize<List<Dictionary<string,object>>>(original);
                if(records==null||records.Any(r=>r==null))throw new InvalidDataException();
                string folder=Path.GetDirectoryName(info);
                string id=folder.Equals(Root,StringComparison.OrdinalIgnoreCase) ? "." : folder.Substring(Root.Length+1).Replace('\\','/');
                result.Add(new Collection {Id=id,Folder=folder,Inventory=file,Original=original,Records=records,CacheFiles=true});
            } catch {issues.Add(info+": Inventory could not be read; left unchanged.");}
        }
        foreach(var collection in result)collection.References=result.ToDictionary(c=>c.Id,StringComparer.OrdinalIgnoreCase);
        return result;
    }
    static void Save(Collection c) {
        if(DatabaseEnabled){SaveDatabase(c);return;}
        string updated=Json.Serialize(c.Records);
        if(updated==Json.Serialize(Json.DeserializeObject(c.Original)))return;
        if(File.ReadAllText(c.Inventory,Encoding.UTF8)!=c.Original)throw new GalleryError(409,"The inventory changed during this operation. Refresh and retry.");
        string temp=c.Inventory+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            File.WriteAllText(temp,updated+Environment.NewLine,new UTF8Encoding(false));
            File.Replace(temp,c.Inventory,c.Inventory+".gallery.bak",true);
            c.Original=updated+Environment.NewLine;ViewExpires=DateTime.MinValue;
        } finally {if(File.Exists(temp))File.Delete(temp);}
    }
    static bool Reconcile(Collection c) {
        string original=Json.Serialize(c.Records);
        bool changed=false;
        foreach(var r in c.Records) {
            if(IsRedditAccessError(Text(r,"error"))){r.Remove("error");changed=true;}
            if(IsRedditAccessError(Text(r,"reason"))){r.Remove("reason");changed=true;}
            if(Text(r,"record_type")=="post"||Text(r,"record_type")=="album"||Text(r,"record_type")=="post_or_album")continue;
            string filename=Text(r,"file"),duplicate=Text(r,"duplicate_of"),status=Text(r,"status");
            var reference=ResolveReference(c,r);
            bool exists=reference!=null;
            if(exists) {
                string target=(status=="duplicate"||Text(r,"previous_status")=="duplicate"||(!String.IsNullOrEmpty(duplicate)&&String.IsNullOrEmpty(filename))) ? "duplicate" : "saved";
                if(status!=target) {r["status"]=target;changed=true;}
                long length=ImageInfo(reference.Owner,reference.Path).Length;
                if(Text(r,"file_size")!=length.ToString()) {r["file_size"]=length;changed=true;}
                if(r.Remove("error"))changed=true;
                if(r.Remove("reason"))changed=true;
                if(r.Remove("previous_status"))changed=true;
            } else if(status=="downloaded"||status=="existing"||status=="duplicate") {
                
                r["reason"]="The recorded image is missing or empty on disk.";changed=true;
            }
        }
        ReconcilePosts(c);
        return changed||Json.Serialize(c.Records)!=original;
    }
    static string PostKey(Dictionary<string,object> record) {
        if(Text(record,"site")!="Reddit")return "";
        var match=Regex.Match(Text(record,"source"),@"/(?:comments|gallery)/([a-z0-9]+)(?:/|$)",RegexOptions.IgnoreCase);
        if(match.Success)return match.Groups[1].Value.ToLowerInvariant();
        string id=Text(record,"id").Split('-')[0];return Regex.IsMatch(id,"^[a-z0-9]+$") ? id : "";
    }
    static string DisplayFile(Collection collection,Dictionary<string,object> record) {
        var candidates=new List<string>{Text(record,"file"),Text(record,"duplicate_of")};object saved;
        if(record.TryGetValue("related_image_files",out saved)&&saved is IEnumerable)foreach(object name in (IEnumerable)saved)candidates.Add(Convert.ToString(name));
        return candidates.FirstOrDefault(name=>Present(collection,SafeFile(collection,name))) ?? "";
    }
    static string MediaUrl(Collection c,string name,bool thumb=false) {
        return "/api/image?collection="+Uri.EscapeDataString(c.Id)+"&file="+Uri.EscapeDataString(name)+(thumb ? "&thumb=1" : "");
    }
    static readonly object ViewSync=new object();
    static object ViewSnapshot;
    static DateTime ViewExpires,LastMaintenance;
    static readonly HashSet<string> ViewFields=new HashSet<string>(new[]{"site","id","status","category","record_type","description","resolution","file","file_collection","duplicate_of","duplicate_collection","duplicate_record_of","duplicate_record_site","duplicate_record_collection","content_duplicate","duplicate_primary_path","source","url","reason","error","download_collection","origin_collection","saved_image_count","image_count"});
    static int MaintenanceRunning;
    public static object InventoryView(bool rescan=false) {
        if(DatabaseEnabled&&rescan){RequestDatabaseWorker();ViewExpires=DateTime.MinValue;}
        if(!DatabaseEnabled&&(rescan||DateTime.UtcNow-LastMaintenance>TimeSpan.FromMinutes(1))) {
            if(System.Threading.Interlocked.CompareExchange(ref MaintenanceRunning,1,0)==0) {
                LastMaintenance=DateTime.UtcNow;
                try{System.Web.Hosting.HostingEnvironment.QueueBackgroundWorkItem(token=>{try{Scan();lock(ViewSync)ViewExpires=DateTime.MinValue;}catch{}finally{System.Threading.Interlocked.Exchange(ref MaintenanceRunning,0);}});}catch{System.Threading.Interlocked.Exchange(ref MaintenanceRunning,0);}
            }
        }
        // Reading atomic inventory snapshots never waits for download/network operations.
        lock(ViewSync) {
            if(ViewSnapshot==null||DateTime.UtcNow>=ViewExpires){ViewSnapshot=DatabaseEnabled?DatabaseInventoryView():BuildInventory(false);ViewExpires=DateTime.UtcNow.AddSeconds(15);}
            return ViewSnapshot;
        }
    }
    public static object Scan() {lock(Sync)return BuildInventory(!DatabaseEnabled);}
    static object BuildInventory(bool reconcile) {
        {
            var issues=new List<string>();var collections=Collections(issues);var records=new List<object>();var summaries=new List<object>();
            if(reconcile&&!DatabaseEnabled)collections=DetectManualImages(collections,issues);
            foreach(var c in collections) {
                if(reconcile&&(SyncRoutedRecords(c,collections)|Reconcile(c))) {try {Save(c);} catch(Exception e) {issues.Add(c.Id+": Status update could not be saved. "+(e is GalleryError ? e.Message : "Check write permissions."));}}
                int saved=0,visible=0;
                for(int index=0;index<c.Records.Count;index++) {
                    if(DatabaseEnabled&&(Text(c.Records[index],"status")=="duplicate"||Text(c.Records[index],"_db_hidden")=="True"))continue;visible++;
                    var r=c.Records[index].Where(pair=>ViewFields.Contains(pair.Key)).ToDictionary(pair=>pair.Key,pair=>pair.Value);
                    r["entry_token"]=EntryToken(c.Records[index]);
                    r.Remove("local");r.Remove("thumb");
                    var reference=ResolveReference(c,c.Records[index]);
                    if(reference!=null) {r["local"]=MediaUrl(reference.Owner,reference.Name);r["thumb"]=MediaUrl(reference.Owner,reference.Name,true);saved++;}
                    if(Text(r,"record_type")=="post"||Text(r,"record_type")=="album"||Text(r,"record_type")=="post_or_album")r["related_image_links"]=RecordReferences(c,c.Records[index]).GroupBy(f=>f.Path,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).Select(f=>new {file=f.Name,collection=f.Owner.Id,url=MediaUrl(f.Owner,f.Name)}).ToArray();
                    r["collection"]=c.Id;r["inventory_index"]=index;
                    object validation;
                    var details=c.Records[index].TryGetValue("validation",out validation) ? validation as Dictionary<string,object> : null;
                    if(details!=null&&Text(details,"resolution")!="")r["resolution"]=Text(details,"resolution");
                    r["search"]=String.Join(" ",new[]{"id","description","file","duplicate_of","site","status","category"}.Select(k=>Text(r,k)).Concat(new[]{c.Id})).ToLowerInvariant();
                    records.Add(r);
                }
                summaries.Add(new {id=c.Id,records=visible,saved=saved,inventory=Path.GetFileName(c.Inventory)});
            }
            return new {records=records,collections=summaries,issues=issues,root=Root};
        }
    }
    static Collection Find(string id) {
        var issues=new List<string>();var c=(DatabaseEnabled?DatabaseCollections(id):Collections(issues)).FirstOrDefault(x=>x.Id==id);
        if(c==null)throw new GalleryError(404,"Collection not found. Refresh the gallery.");
        return c;
    }
    static string EntryToken(Dictionary<string,object> record) {
        if(DatabaseEnabled&&Text(record,"_db_id")!="")return BitConverter.ToString(DatabaseHash(Text(record,"_db_id")+"|"+Text(record,"_db_revision"))).Replace("-","");
        using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Json.Serialize(record)))).Replace("-","");
    }
    public static object DeleteEntry(Dictionary<string,object> payload) {
        if(PagedInventory&&Text(payload,"entry_id")!="")return DatabaseDeleteEntry(payload);
        lock(Sync) {
            var c=Find(Text(payload,"collection"));int index;
            if(!Int32.TryParse(Text(payload,"index"),out index)||index<0||index>=c.Records.Count)throw new GalleryError(409,"This inventory changed. Refresh before deleting.");
            var record=c.Records[index];
            if(Text(payload,"id")!=Text(record,"id")||Text(payload,"site")!=Text(record,"site")||Text(payload,"token")!=EntryToken(record))throw new GalleryError(409,"This entry changed. Refresh before deleting.");
            string archive=Path.Combine(DuplicateData,"deleted-entries");Directory.CreateDirectory(archive);
            string backup=Path.Combine(archive,DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")+"-"+Guid.NewGuid().ToString("N")+".json");
            if(!DatabaseEnabled)File.WriteAllText(backup,Json.Serialize(new {collection=c.Id,inventory=c.Inventory,index=index,archived_utc=DateTime.UtcNow.ToString("o"),record=record}),new UTF8Encoding(false));
            c.Records.RemoveAt(index);
            Save(c);
            return new {status="deleted",files_deleted=0};
        }
    }
    public static void Image(HttpContext context) {
        string id=context.Request.QueryString["collection"] ?? "";
        if(GalleryAuth.Enabled&&!Gallery.ImageAccessAllowed(context,id,context.Request.QueryString["file"]))throw new GalleryError(404,"Image not found.");
        string folder=Path.GetFullPath(Path.Combine(Root,id.Replace('/','\\')));
        if((!folder.Equals(Root,StringComparison.OrdinalIgnoreCase)&&!Under(folder,Root))||!Directory.Exists(Path.Combine(folder,"_download-info")))throw new GalleryError(404,"Collection not found.");
        string cursor=folder;
        while(cursor.Length>=Root.Length) {if(Linked(cursor))throw new GalleryError(404,"Collection not found.");if(cursor.Equals(Root,StringComparison.OrdinalIgnoreCase))break;cursor=Path.GetDirectoryName(cursor);}
        var c=new Collection {Id=id,Folder=folder};
        string path=SafeFile(c,context.Request.QueryString["file"]);
        if(path==null||!File.Exists(path))throw new GalleryError(404,"Image not found.");
        if(context.Request.QueryString["thumb"]=="1" && !new[]{".webp",".avif",".swf"}.Contains(Path.GetExtension(path).ToLowerInvariant())) {
            try {
                string cache=Path.Combine(ConfigurationManager.AppSettings["GalleryData"],"thumbnails");Directory.CreateDirectory(cache);
                string key;
                using(var sha=SHA256.Create())key=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(path+File.GetLastWriteTimeUtc(path).Ticks+new FileInfo(path).Length))).Replace("-","");
                string target=Path.Combine(cache,key+".jpg");
                if(!File.Exists(target)) {
                    using(var source=System.Drawing.Image.FromFile(path)) {
                        double scale=Math.Min(1,Math.Min(480.0/source.Width,270.0/source.Height));
                        using(var thumbnail=new Bitmap(Math.Max(1,(int)(source.Width*scale)),Math.Max(1,(int)(source.Height*scale)))) {
                            using(var graphics=Graphics.FromImage(thumbnail))graphics.DrawImage(source,0,0,thumbnail.Width,thumbnail.Height);
                            using(var stream=new MemoryStream()) {
                                thumbnail.Save(stream,ImageFormat.Jpeg);
                                // Parallel requests may render the same thumbnail; each response is valid.
                                try {using(var file=new FileStream(target,FileMode.CreateNew,FileAccess.Write,FileShare.Read))stream.WriteTo(file);} catch(IOException) {}
                            }
                        }
                    }
                }
                if(File.Exists(target)&&new FileInfo(target).Length>0) {context.Response.ContentType="image/jpeg";context.Response.TransmitFile(target);return;}
            } catch {}
        }
        var types=new Dictionary<string,string>{{".jpg","image/jpeg"},{".jpeg","image/jpeg"},{".png","image/png"},{".webp","image/webp"},{".gif","image/gif"},{".avif","image/avif"},{".bmp","image/bmp"},{".tif","image/tiff"},{".tiff","image/tiff"},{".swf","application/x-shockwave-flash"}};
        context.Response.ContentType=types[Path.GetExtension(path).ToLowerInvariant()];context.Response.TransmitFile(path);
    }
    static bool Reddit(string host) {return host=="reddit.com"||host.EndsWith(".reddit.com")||host=="redd.it"||host.EndsWith(".redd.it");}
    static void ValidateUrl(Uri uri) {
        if(uri.Scheme!="https"||!uri.IsDefaultPort||!String.IsNullOrEmpty(uri.UserInfo))throw new GalleryError(400,"Only direct HTTPS source URLs are supported.");
        string host=uri.DnsSafeHost.ToLowerInvariant();
        string[] allowed={"alphacoders.com","wallhaven.cc","redd.it","reddit.com","imgur.com","mediafire.com","ibb.co","awwni.me","minus.com","cloudfront.net","mixtape.moe","twimg.com"};
        if(!allowed.Any(d=>host==d||host.EndsWith("."+d)))throw new GalleryError(400,"This image host is not supported by the gallery.");
    }
    static CookieContainer RedditCookies() {
        var jar=new CookieContainer();
        try {
            using(var pipe=new NamedPipeClientStream(".","WallpaperGalleryReddit",PipeDirection.InOut)) {
                pipe.Connect(2000);
                var bytes=Encoding.UTF8.GetBytes("reddit\n");pipe.Write(bytes,0,bytes.Length);pipe.Flush();
                // The helper always closes the connection after its bounded reply.
                var read=System.Threading.Tasks.Task.Factory.StartNew(()=>new StreamReader(pipe).ReadLine());
                if(!read.Wait(25000))return jar;
                var data=Json.Deserialize<Dictionary<string,object>>(read.Result ?? "{}");
                object raw;
                if(data.TryGetValue("cookies",out raw))foreach(var item in (IEnumerable)raw) {
                    var cookie=item as Dictionary<string,object>;
                    if(cookie==null)continue;
                    string domain=Text(cookie,"domain").TrimStart('.').ToLowerInvariant();
                    if(!Reddit(domain))continue;
                    try {jar.Add(new Cookie(Text(cookie,"name"),Text(cookie,"value"),Text(cookie,"path"),Text(cookie,"domain")) {Secure=true});} catch {}
                }
            }
        } catch {}
        return jar;
    }
    public static bool RedditSessionAvailable() {return RedditCookies().GetCookies(new Uri("https://www.reddit.com/")).Cast<Cookie>().Any(c=>c.Name=="reddit_session");}
    static HttpWebResponse Open(Uri uri,CookieContainer cookies=null) {
        for(int redirects=0;redirects<6;redirects++) {
            ValidateUrl(uri);
            if(uri.Host.Equals("i.imgur.com",StringComparison.OrdinalIgnoreCase)&&Regex.IsMatch(uri.AbsolutePath,@"/(?:removed|deleted|404)\.(?:png|jpe?g|gif)$",RegexOptions.IgnoreCase))throw new GalleryError(502,"The image host returned a removed-image placeholder rather than the original image.");
            var request=(HttpWebRequest)WebRequest.Create(uri);
            request.UserAgent="Mozilla/5.0 (compatible; LocalWallpaperGallery/2.0)";
            request.Timeout=60000;request.ReadWriteTimeout=60000;request.AllowAutoRedirect=false;
            if(cookies!=null&&Reddit(uri.Host))request.CookieContainer=cookies;
            var response=(HttpWebResponse)request.GetResponse();
            if((int)response.StatusCode>=300&&(int)response.StatusCode<400) {string location=response.Headers["Location"];response.Close();uri=new Uri(uri,location);continue;}
            return response;
        }
        throw new GalleryError(502,"The image source redirected too many times.");
    }
    public static object Wallhaven(string id,string key) {
        key=WallhavenServerKey();
        if(!Regex.IsMatch(id,"^[a-z0-9]{6}$")||!Regex.IsMatch(key,"^[a-zA-Z0-9]{20,128}$"))throw new GalleryError(400,"Enter a valid Wallhaven key and wallpaper ID.");
        using(var response=Open(new Uri("https://wallhaven.cc/api/v1/w/"+id+"?apikey="+Uri.EscapeDataString(key))))
        using(var reader=new StreamReader(response.GetResponseStream())) {
            var record=(Dictionary<string,object>)Json.Deserialize<Dictionary<string,object>>(reader.ReadToEnd())["data"];
            string path=Text(record,"path"),thumb=Text((Dictionary<string,object>)record["thumbs"],"large");
            if(new Uri(path).Host!="w.wallhaven.cc"||new Uri(thumb).Host!="th.wallhaven.cc"||!path.StartsWith("https://")||!thumb.StartsWith("https://"))throw new GalleryError(502,"Unexpected image host.");
            return new {path=path,thumb=thumb};
        }
    }
    public static object Download(Dictionary<string,object> payload) {
        lock(Sync) {
            var c=Find(Text(payload,"collection"));int index;
            index=PayloadIndex(c,payload);
            var r=c.Records[index];
            // Identity check prevents an old page downloading a different row after inventory edits.
            if(Text(payload,"id")!=Text(r,"id")||Text(payload,"site")!=Text(r,"site"))throw new GalleryError(409,"This inventory changed. Refresh before downloading.");
            if(NeedsOtherRouting(c,r))return Download(CurrentQueuePayload(QueueItem(c,index)));
            var existing=ResolveReference(c,r);
            if(existing!=null) {Reconcile(c);Save(c);return new {file=existing.Name,status="existing",local=MediaUrl(existing.Owner,existing.Name),thumb=MediaUrl(existing.Owner,existing.Name,true)};}
            if(Text(r,"record_type")!="image")return new {status="skipped",reason="Post or album record; no individual image."};
            string url=Text(r,"url"),site=Text(r,"site");
            if(site=="Wallhaven") {
                var preview=Wallhaven(Text(r,"id"),Text(payload,"key"));
                url=Convert.ToString(preview.GetType().GetProperty("path").GetValue(preview,null));
            }
            Uri uri;if(!Uri.TryCreate(url,UriKind.Absolute,out uri))throw new GalleryError(400,"No direct image URL in inventory.");
            ValidateUrl(uri);
            CookieContainer cookies=site=="Reddit"&&Reddit(uri.Host) ? RedditCookies() : null;
            string temp=Path.Combine(c.Folder,".gallery-"+Guid.NewGuid().ToString("N")+".part");
            try {
                string ext;long length=0;string digest;
                using(var response=Open(uri,cookies)) {
                    string mime=response.ContentType.Split(';')[0].Trim().ToLowerInvariant();
                    var types=new Dictionary<string,string>{{"image/jpeg",".jpg"},{"image/png",".png"},{"image/webp",".webp"},{"image/gif",".gif"}};
                    if(!types.TryGetValue(mime,out ext))throw new GalleryError(502,"The source did not return an image. Browser login may have expired or this is a post/archive link.");
                    using(var sha=SHA256.Create())using(var source=response.GetResponseStream())using(var output=new FileStream(temp,FileMode.CreateNew)) {
                        byte[] buffer=new byte[65536];int count;
                        while((count=source.Read(buffer,0,buffer.Length))>0) {
                            length+=count;if(length>200L*1024*1024)throw new GalleryError(400,"Image exceeds 200 MB.");
                            output.Write(buffer,0,count);sha.TransformBlock(buffer,0,count,null,0);
                        }
                        sha.TransformFinalBlock(new byte[0],0,0);digest=BitConverter.ToString(sha.Hash).Replace("-","").ToLowerInvariant();
                    }
                }
                if(digest=="9b5936f4006146e4e1e9025b474c02863c0b5614132ad40db4b925a10e8bfbb9")throw new GalleryError(502,"Imgur returned a removal notice; the original image is no longer available from that URL.");
                if(length==0)throw new GalleryError(502,"The source returned an empty image.");
                // Validate actual image signatures, so HTML login pages cannot be saved as images.
                byte[] signature=new byte[12];using(var input=File.OpenRead(temp))input.Read(signature,0,signature.Length);
                bool image=(signature[0]==0xff&&signature[1]==0xd8)||(signature[0]==137&&signature[1]==80&&signature[2]==78&&signature[3]==71)||(Encoding.ASCII.GetString(signature,0,3)=="GIF")||(Encoding.ASCII.GetString(signature,0,4)=="RIFF"&&Encoding.ASCII.GetString(signature,8,4)=="WEBP");
                if(!image)throw new GalleryError(502,"The source response is not a recognized image.");
                var matching=ExistingContent(digest,length);
                if(matching!=null) {
                    SetReference(r,"file","file_collection",c,matching);SetReference(r,"duplicate_of","duplicate_collection",c,matching);
                    r["status"]="duplicate";r["duplicate_detected_by"]="sha256";r["sha256"]=digest;r["bytes"]=length;r["file_size"]=length;r.Remove("error");r.Remove("reason");r.Remove("previous_status");Save(c);
                    return new {file=matching.Name,status="duplicate",bytes=length,sha256=digest,local=MediaUrl(matching.Owner,matching.Name),thumb=MediaUrl(matching.Owner,matching.Name,true)};
                }
                string name=Regex.Replace(Path.GetFileName(c.Folder)+" - "+site+" "+Text(r,"id"),"[^\\w .-]","_")+" gallery-"+Guid.NewGuid().ToString("N").Substring(0,8)+ext;
                name=CategoryFolder(r)+"/"+name;
                string destination=SafeFile(c,name);
                if(destination==null)throw new GalleryError(400,"The category folder is not a safe destination.");
                Directory.CreateDirectory(Path.GetDirectoryName(destination));
                File.Move(temp,destination);
                r["file"]=name;foreach(string field in new[]{"file_collection","duplicate_collection","duplicate_of","duplicate_detected_by"})r.Remove(field);r["status"]="saved";r["bytes"]=length;r["file_size"]=length;r["sha256"]=digest;r.Remove("error");r.Remove("reason");r.Remove("previous_status");
                try {Save(c);} catch {File.Delete(Path.Combine(c.Folder,name));throw;}
                return new {file=name,status="saved",bytes=length,sha256=digest,local=MediaUrl(c,name),thumb=MediaUrl(c,name,true)};
            } catch(WebException e) {
                var response=e.Response as HttpWebResponse;
                string message=response==null ? "The source could not be reached." : "Source returned HTTP "+(int)response.StatusCode+". Check account access or retry later.";
                r["error"]=message;
                r.Remove("reason");r["last_retry_utc"]=DateTime.UtcNow.ToString("o");
                try {Save(c);} catch {}
                throw;
            } catch(GalleryError e) {
                if(e.Code!=409) {r["error"]=e.Message;r.Remove("reason");r["last_retry_utc"]=DateTime.UtcNow.ToString("o");try {Save(c);} catch {}}
                throw;
            } finally {if(File.Exists(temp))File.Delete(temp);}
        }
    }
}
