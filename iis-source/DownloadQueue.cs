using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using System.Configuration;
using System.Threading;
using System.Web;
using System.Web.Hosting;

public class GalleryQueueItem {
    public string collection,site,id,kind,url,entry_id;
}
public class GalleryDownloadFailure {
    public string id,error,entry_id,collection,site,source_id,kind,resolved_utc,actor;
}
public class GalleryDownloadJob {
    public string id,phase,current,updated_utc;
    public List<GalleryQueueItem> entries=new List<GalleryQueueItem>();
    public List<string> errors=new List<string>();
    public List<GalleryDownloadFailure> failed_entries=new List<GalleryDownloadFailure>();
    public int processed,saved,existing,skipped,failed,resolved;
    public bool stop;
    public bool post_or_album_recovered;
}
public static partial class Gallery {
    static bool IsRedditAccessError(string message) {return message!=null&&message.IndexOf("Reddit",StringComparison.OrdinalIgnoreCase)>=0&&(message.Contains("HTTP 403")||message.Contains("HTTP 401"));}
    static readonly object QueueSync=new object();
    static bool QueueWorkerRunning;
    static GalleryDownloadJob DownloadJob;
    static string QueuePath {get {return Path.Combine(DuplicateData,"download-queue.json");}}
    static string WallhavenServerKey() {
        string path=ConfigurationManager.AppSettings["WallhavenKeyFile"] ?? @"C:\downloads\wallhaven-api-key.txt";
        string key;try {key=File.ReadAllText(path).Trim();}catch {throw new GalleryError(503,"The server could not read its Wallhaven API key file.");}
        if(!System.Text.RegularExpressions.Regex.IsMatch(key,"^[a-zA-Z0-9]{20,128}$"))throw new GalleryError(503,"The server's Wallhaven API key file is invalid.");
        return key;
    }
    public static bool WallhavenKeyAvailable() {try {WallhavenServerKey();return true;}catch {return false;}}
    static void LoadQueue() {
        if(DownloadJob!=null)return;
        if(DatabaseEnabled){var value=DatabaseState("download_queue");if(value!=null)DownloadJob=Json.Deserialize<GalleryDownloadJob>(Json.Serialize(value));}
        else if(File.Exists(QueuePath))DownloadJob=Json.Deserialize<GalleryDownloadJob>(File.ReadAllText(QueuePath));
        if(DownloadJob!=null&&DownloadJob.failed_entries==null)DownloadJob.failed_entries=new List<GalleryDownloadFailure>();
        // Recover only batches whose entire processed prefix was silently unsupported.
        if(DownloadJob!=null&&!DownloadJob.post_or_album_recovered) {
            DownloadJob.post_or_album_recovered=true;
            if(DownloadJob.processed>0&&DownloadJob.skipped==DownloadJob.processed&&DownloadJob.saved==0&&DownloadJob.existing==0&&DownloadJob.failed==0&&DownloadJob.resolved==0&&DownloadJob.entries.Take(DownloadJob.processed).All(e=>e.site=="Reddit"&&e.kind=="post_or_album")) {
                DownloadJob.processed=0;DownloadJob.skipped=0;DownloadJob.current="";
                if(!DownloadJob.stop)DownloadJob.phase="queued";
            }
            SaveQueue();
        }
    }
    static void SaveQueue() {
        if(DatabaseEnabled){DownloadJob.updated_utc=DateTime.UtcNow.ToString("o");SetDatabaseState("download_queue",DownloadJob);return;}
        Directory.CreateDirectory(DuplicateData);DownloadJob.updated_utc=DateTime.UtcNow.ToString("o");
        string temp=QueuePath+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {File.WriteAllText(temp,Json.Serialize(DownloadJob),new UTF8Encoding(false));if(File.Exists(QueuePath))File.Replace(temp,QueuePath,QueuePath+".bak");else File.Move(temp,QueuePath);}finally {if(File.Exists(temp))File.Delete(temp);}
    }
    static object QueueSummary() {
        if(DownloadJob==null)return new {phase="idle"};
        return new {id=DownloadJob.id,phase=DownloadJob.phase,total=DownloadJob.entries.Count,processed=DownloadJob.processed,saved=DownloadJob.saved,existing=DownloadJob.existing,skipped=DownloadJob.skipped,failed=DownloadJob.failed,resolved=DownloadJob.resolved,current=DownloadJob.current,stop=DownloadJob.stop,errors=DownloadJob.errors.ToArray(),failed_entries=(DownloadJob.failed_entries??new List<GalleryDownloadFailure>()).ToArray(),updated_utc=DownloadJob.updated_utc};
    }
    public static void EnsureDownloadWorker() {
        lock(QueueSync) {
            LoadQueue();if(QueueWorkerRunning||DownloadJob==null||!new[]{"queued","running"}.Contains(DownloadJob.phase))return;
            QueueWorkerRunning=true;
            try {HostingEnvironment.QueueBackgroundWorkItem(token=>DownloadQueueWorker(token));}catch {QueueWorkerRunning=false;throw;}
        }
    }
    public static object DownloadQueueStatus() {EnsureDownloadWorker();lock(QueueSync)return QueueSummary();}
    static GalleryQueueItem QueueItem(Collection c,int index) {
        var r=c.Records[index];return new GalleryQueueItem {collection=c.Id,site=Text(r,"site"),id=Text(r,"id"),kind=Text(r,"record_type"),url=Text(r,"url"),entry_id=Text(r,"_db_id")};
    }
    static bool SameItem(GalleryQueueItem a,GalleryQueueItem b) {if(!String.IsNullOrEmpty(a.entry_id)&&!String.IsNullOrEmpty(b.entry_id))return a.entry_id==b.entry_id;return a.collection==b.collection&&a.site==b.site&&a.id==b.id&&a.kind==b.kind&&a.url==b.url;}
    public static object EnqueueDownloads(Dictionary<string,object> payload) {
        lock(QueueSync) {
            LoadQueue();if(DownloadJob!=null&&new[]{"queued","running"}.Contains(DownloadJob.phase))throw new GalleryError(409,"Downloads are already running. Their progress is shown on this page.");
            object raw;if(!payload.TryGetValue("entries",out raw)||!(raw is IEnumerable)||raw is string)throw new GalleryError(400,"Choose entries to download.");
            var entries=new List<GalleryQueueItem>();
            lock(Sync) {
                var collections=new Dictionary<string,Collection>();
                foreach(object value in (IEnumerable)raw) {
                    var input=value as Dictionary<string,object>;int index;if(input==null)throw new GalleryError(400,"Invalid download entry.");
                    string collection=Text(input,"collection");Collection c;if(!collections.TryGetValue(collection,out c))collections[collection]=c=Find(collection);
                    index=PayloadIndex(c,input);
                    var item=QueueItem(c,index);
                    if(item.site!=Text(input,"site")||item.id!=Text(input,"id")||item.kind!=Text(input,"kind")||item.url!=Text(input,"url"))throw new GalleryError(409,"The inventory changed. Refresh and retry.");
                    if(!entries.Any(e=>SameItem(e,item)))entries.Add(item);
                    if(entries.Count>10000)throw new GalleryError(400,"Choose at most 10,000 entries at a time.");
                }
            }
            if(entries.Count==0)throw new GalleryError(400,"No entries selected.");
            var unresolved=DownloadJob==null?new List<GalleryDownloadFailure>():((DownloadJob.failed_entries??new List<GalleryDownloadFailure>()).Where(x=>x.resolved_utc==null).ToList());
            DownloadJob=new GalleryDownloadJob {id=Guid.NewGuid().ToString("N"),phase="queued",entries=entries,failed_entries=unresolved};SaveQueue();
            EnsureDownloadWorker();return QueueSummary();
        }
    }
    public static object StopDownloadQueue() {lock(QueueSync){LoadQueue();if(DownloadJob!=null&&new[]{"queued","running"}.Contains(DownloadJob.phase)){DownloadJob.stop=true;SaveQueue();}return QueueSummary();}}
    public static object ResumeDownloadQueue() {lock(QueueSync){LoadQueue();if(DownloadJob!=null&&DownloadJob.phase=="stopped"){DownloadJob.stop=false;DownloadJob.phase="queued";SaveQueue();EnsureDownloadWorker();}return QueueSummary();}}
    public static object ResolveDownloadFailure(HttpContext context,Dictionary<string,object> payload) {
        if(!DatabaseEnabled)throw new GalleryError(409,"Manual download review requires the database-backed gallery.");
        lock(QueueSync){
            LoadQueue();if(DownloadJob==null||new[]{"queued","running"}.Contains(DownloadJob.phase))throw new GalleryError(409,"Wait for the active download batch to finish before reviewing a failed item.");
            string id=Text(payload,"id");var failure=(DownloadJob.failed_entries??new List<GalleryDownloadFailure>()).FirstOrDefault(x=>x.id==id);
            if(failure==null||failure.resolved_utc!=null)throw new GalleryError(409,"This failed download is no longer open. Refresh the queue.");
            long entryId;if(!Int64.TryParse(failure.entry_id,out entryId))throw new GalleryError(409,"This old download has no linked entry to verify. Review it in Gallery.");
            using(var db=OpenDatabase()){
                var rows=DBRows(db,null,"SELECT e.id,e.site,e.source_id,e.record_type,e.deleted_at,COUNT(DISTINCT f.id) saved_files FROM inventory_entries e LEFT JOIN fm_entry_file_projection l ON l.entry_id=e.id LEFT JOIN fm_file_catalog f ON f.id=l.file_id AND f.is_present=TRUE WHERE e.id=@id GROUP BY e.id,e.site,e.source_id,e.record_type,e.deleted_at","@id",entryId);
                if(rows.Count!=1||rows[0]["deleted_at"]!=null||Text(rows[0],"site")!=failure.site||Text(rows[0],"source_id")!=failure.source_id||Text(rows[0],"record_type")!=failure.kind)throw new GalleryError(409,"The download entry changed. Review its current record.");
                if(Convert.ToInt64(rows[0]["saved_files"])<1)throw new GalleryError(409,"Link a saved file to this entry in Gallery before marking the download handled.");
            }
            failure.resolved_utc=DateTime.UtcNow.ToString("o");failure.actor=context.User.Identity.Name;SaveQueue();return new{message="The manually linked saved file was verified. This failed download is resolved."};
        }
    }
    static bool IsOtherCollection(string key){return !String.IsNullOrEmpty(key)&&key.Split('/').Last().Equals("other",StringComparison.OrdinalIgnoreCase);}
    static bool NeedsOtherRouting(Collection c,Dictionary<string,object> row){return Text(row,"collection_override")!=c.Id&&!IsOtherCollection(c.Id)&&(Text(row,"status")=="excluded_unrelated"||IsOtherCollection(Text(row,"download_collection")));}
    public static string ClosestOtherCollection(string source,IEnumerable<string> collections){
        if(IsOtherCollection(source))return source;
        var parts=source=="."?new string[0]:source.Split('/');var known=collections.Where(IsOtherCollection).ToDictionary(x=>x,x=>x,StringComparer.OrdinalIgnoreCase);
        for(int depth=Math.Max(0,parts.Length-1);depth>=0;depth--){string key=String.Join("/",parts.Take(depth).Concat(new[]{"other"}));string value;if(known.TryGetValue(key,out value))return value;}return null;
    }
    static Collection EnsureOtherCollection(string key){
        if(!IsOtherCollection(key))throw new GalleryError(400,"No marked parent collection named exactly 'other' was found. The entry was left in its current collection.");
        var c=Find(key);string parent=Path.GetDirectoryName(c.Folder);if(!Directory.Exists(parent)||!Directory.GetDirectories(parent).Any(path=>Path.GetFileName(path).Equals("other",StringComparison.OrdinalIgnoreCase)&&String.Equals(path,c.Folder,StringComparison.OrdinalIgnoreCase))||!Directory.Exists(Path.Combine(c.Folder,"_download-info")))throw new GalleryError(400,"The destination must be a marked collection named exactly 'other'.");return c;
    }
    static Dictionary<string,object> CurrentQueuePayload(GalleryQueueItem item) {
        lock(Sync) {
            if(DatabaseEnabled&&!String.IsNullOrEmpty(item.entry_id)){using(var db=OpenDatabase()){var moved=DBRows(db,null,"SELECT c.collection_key FROM inventory_entries e JOIN inventory_documents d ON d.id=e.inventory_document_id JOIN collections c ON c.id=d.collection_id WHERE e.id=@id AND e.deleted_at IS NULL","@id",item.entry_id);if(moved.Count==0)return null;item.collection=Text(moved[0],"collection_key");}}
            var c=Find(item.collection);int index=!String.IsNullOrEmpty(item.entry_id)?c.Records.FindIndex(r=>Text(r,"_db_id")==item.entry_id):c.Records.FindIndex(r=>Text(r,"id")==item.id&&Text(r,"site")==item.site&&Text(r,"record_type")==item.kind&&Text(r,"url")==item.url);
            if(index<0&&DatabaseEnabled){
                using(var db=OpenDatabase()){
                    var relocated=DBRows(db,null,"SELECT c.collection_key FROM inventory_entries e JOIN inventory_documents d ON d.id=e.inventory_document_id JOIN collections c ON c.id=d.collection_id WHERE e.deleted_at IS NULL AND JSON_UNQUOTE(JSON_EXTRACT(e.raw_record,'$.origin_collection'))=@origin AND e.site=@site AND e.source_id=@id AND e.record_type=@kind AND COALESCE(e.image_url,'')=@url ORDER BY e.is_hidden,e.id LIMIT 1","@origin",item.collection,"@site",item.site,"@id",item.id,"@kind",item.kind,"@url",item.url??"");
                    if(relocated.Count>0){c=Find(Text(relocated[0],"collection_key"));index=c.Records.FindIndex(r=>Text(r,"id")==item.id&&Text(r,"site")==item.site&&Text(r,"record_type")==item.kind&&Text(r,"url")==item.url);if(index>=0)item.collection=c.Id;}
                }
            }
            if(index<0)return null;
            var source=c.Records[index];
            if(NeedsOtherRouting(c,source)) {
                var targetKey=ClosestOtherCollection(c.Id,Collections(new List<string>()).Select(x=>x.Id));var target=EnsureOtherCollection(targetKey);
                int targetIndex=target.Records.FindIndex(r=>Text(r,"id")==item.id&&Text(r,"site")==item.site&&Text(r,"record_type")==item.kind&&Text(r,"url")==item.url);
                if(targetIndex<0) {
                    var copy=new Dictionary<string,object>(source);
                    foreach(var internalKey in copy.Keys.Where(k=>k.StartsWith("_db_")).ToList())copy.Remove(internalKey);
                    foreach(string field in new[]{"file","file_collection","duplicate_of","duplicate_collection","duplicate_record_of","duplicate_detected_by","related_image_files","related_image_references","saved_image_references","images_saved","image_outcomes","sha256","file_size","bytes","reason","error","previous_status"})copy.Remove(field);
                    copy["origin_collection"]=c.Id;copy["download_collection"]=targetKey;
                    target.Records.Add(copy);targetIndex=target.Records.Count-1;Save(target);
                }
                source["download_collection"]=targetKey;Save(c);
                item.collection=targetKey;c=target;index=targetIndex;
            }
            item.entry_id=Text(c.Records[index],"_db_id");
            return new Dictionary<string,object>{{"entry_id",item.entry_id},{"revision",Text(c.Records[index],"_db_revision")},{"collection",item.collection},{"index",index},{"id",item.id},{"site",item.site},{"already_saved",ResolveReference(c,c.Records[index])!=null}};
        }
    }
    static void DownloadQueueWorker(CancellationToken token) {
        try {
            for(;;) {
                token.ThrowIfCancellationRequested();GalleryQueueItem item;
                lock(QueueSync) {
                    if(DownloadJob.stop||DownloadJob.processed>=DownloadJob.entries.Count){DownloadJob.phase=DownloadJob.stop ? "stopped" : "complete";DownloadJob.current="";SaveQueue();return;}
                    int imageIndex=DownloadJob.entries.FindIndex(DownloadJob.processed,e=>e.kind=="image");
                    if(imageIndex>DownloadJob.processed){var image=DownloadJob.entries[imageIndex];DownloadJob.entries.RemoveAt(imageIndex);DownloadJob.entries.Insert(DownloadJob.processed,image);}
                    item=DownloadJob.entries[DownloadJob.processed];DownloadJob.phase="running";DownloadJob.current=item.collection+" · "+item.site+" "+item.id;SaveQueue();
                }
                string outcome="skipped";string error=null;var additions=new List<GalleryQueueItem>();
                // Reload the item's identity and current database version on each conflict retry.
                for(int attempt=0;;attempt++) {
                outcome="skipped";error=null;
                try {
                    var payload=CurrentQueuePayload(item);
                    lock(QueueSync){DownloadJob.current=item.collection+" · "+item.site+" "+item.id;SaveQueue();}
                    if(payload!=null) {
                        if(Text(payload,"already_saved")=="True")outcome="existing";
                        else if(item.kind=="image") {
                            var result=Json.Deserialize<Dictionary<string,object>>(Json.Serialize(Download(payload)));outcome=new[]{"existing","duplicate"}.Contains(Text(result,"status")) ? "existing" : "saved";
                        } else if(item.site=="Reddit"&&new[]{"post","album","post_or_album"}.Contains(item.kind)) {
                            ResolveRedditPost(payload);
                            lock(Sync) {var c=Find(item.collection);for(int i=0;i<c.Records.Count;i++)if(Text(c.Records[i],"site")=="Reddit"&&Text(c.Records[i],"record_type")=="image"&&PostKey(c.Records[i])==item.id)additions.Add(QueueItem(c,i));}
                            outcome="resolved";
                        }
                    }
                    if(IsOtherCollection(item.collection)&&(outcome=="saved"||outcome=="existing"))UpdateRoutedDownloadStatuses();
                } catch(GalleryError e) {
                    if(DatabaseEnabled&&e.Code==409&&e.Message=="This entry changed in the database. Refresh and retry."&&attempt<2) {
                        if(token.WaitHandle.WaitOne(500))token.ThrowIfCancellationRequested();
                        continue;
                    }
                    outcome="failed";error=e.Message;
                }
                  catch(System.Net.WebException e) {outcome="failed";var response=e.Response as System.Net.HttpWebResponse;error=response==null ? "The source could not be reached." : "Source returned HTTP "+(int)response.StatusCode+".";if(response!=null)response.Close();}
                  catch {outcome="failed";error="The download could not finish. Check source access and retry.";}
                break;
                }
                lock(QueueSync) {
                    foreach(var child in additions)if(!DownloadJob.entries.Any(e=>SameItem(e,child)))DownloadJob.entries.Add(child);
                    DownloadJob.processed++;if(outcome=="resolved")DownloadJob.resolved++;if(outcome=="saved")DownloadJob.saved++;if(outcome=="existing")DownloadJob.existing++;if(outcome=="skipped")DownloadJob.skipped++;if(outcome=="failed"){DownloadJob.failed++;DownloadJob.errors.Add(DownloadJob.current+": "+error);DownloadJob.failed_entries.Add(new GalleryDownloadFailure{id=Guid.NewGuid().ToString("N"),error=error,entry_id=item.entry_id,collection=item.collection,site=item.site,source_id=item.id,kind=item.kind});}SaveQueue();
                }
                // Respect source request limits without tying progress to any browser.
                if(token.WaitHandle.WaitOne(item.site=="Wallhaven" ? 1800 : 100))token.ThrowIfCancellationRequested();
            }
        } catch(OperationCanceledException) {lock(QueueSync){DownloadJob.phase="queued";SaveQueue();}}
          catch {lock(QueueSync){if(DownloadJob!=null){DownloadJob.phase="queued";try{SaveQueue();}catch {}}}}
        finally {lock(QueueSync)QueueWorkerRunning=false;}
    }
}
