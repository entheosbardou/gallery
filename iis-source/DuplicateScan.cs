using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Configuration;
using System.Security.Cryptography;
using System.Threading;
using System.Web.Hosting;

public class GalleryHashEntry {
    public string Path, Sha256;
    public long Bytes, Modified;
}
public class GalleryImageRecord {
    public Collection Collection;
    public Dictionary<string,object> Record;
    public GalleryHashEntry Entry;
    public int Index;
}
public static partial class Gallery {
    static GalleryFileReference ExistingContent(string digest,long length) {
        if(DatabaseEnabled)return DatabaseExistingContent(digest,length);
        var issues=new List<string>();
        foreach(var collection in Collections(issues))foreach(var row in collection.Records.Where(r=>Text(r,"sha256")==digest)) {
            var reference=ResolveReference(collection,row);if(reference==null)continue;
            try{using(var stream=new FileStream(reference.Path,FileMode.Open,FileAccess.Read,FileShare.Read))if(stream.Length==length&&HashStream(stream)==digest)return reference;}catch{}
        }
        return null;
    }
    static readonly object DuplicateSync=new object();
    static bool DuplicateRunning=false, DuplicateAgain=false;
    static object DuplicateState=null;
    static string DuplicateData {get {return ConfigurationManager.AppSettings["GalleryData"] ?? System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"App_Data");}}
    public static object DuplicateStatus() {
        if(DatabaseEnabled)return DatabaseWorkerStatus();
        lock(DuplicateSync) {
            if(DuplicateState!=null)return DuplicateState;
            string report=System.IO.Path.Combine(DuplicateData,"duplicate-report.json");
            try {if(File.Exists(report))return Json.DeserializeObject(File.ReadAllText(report));} catch {}
            return new {phase="idle"};
        }
    }
    public static object StartDuplicateScan() {
        if(DatabaseEnabled)return RequestDatabaseWorker();
        lock(DuplicateSync) {
            if(DuplicateRunning) {DuplicateAgain=true;return DuplicateState;}
            DuplicateRunning=true;DuplicateState=new {phase="scanning",processed=0,total=0,message="Finding image files…"};
            try {HostingEnvironment.QueueBackgroundWorkItem(token=>DuplicateWorker(token));}
            catch {DuplicateRunning=false;DuplicateState=new {phase="error",error="Duplicate checking could not start. Retry."};throw;}
            return DuplicateState;
        }
    }
    static void DuplicateWorker(CancellationToken token) {
        try {
            do {
                lock(DuplicateSync)DuplicateAgain=false;
                var report=CheckDuplicateFiles(token);
                lock(DuplicateSync) {
                    DuplicateState=report;
                    if(!DuplicateAgain) {DuplicateRunning=false;return;}
                    DuplicateState=new {phase="scanning",processed=0,total=0,message="Checking files added during the previous pass…"};
                }
            } while(!token.IsCancellationRequested);
        } catch(OperationCanceledException) {
            lock(DuplicateSync)DuplicateState=new {phase="error",error="Duplicate checking was interrupted by an application restart. Run it again."};
        } catch {
            lock(DuplicateSync)DuplicateState=new {phase="error",error="Duplicate checking could not finish. Check file access and retry."};
        }
        lock(DuplicateSync)DuplicateRunning=false;
    }
    static IEnumerable<string> DuplicateImageFiles(string folder,List<string> issues) {
        string[] files,folders;
        try {files=Directory.GetFiles(folder);folders=Directory.GetDirectories(folder);} catch {issues.Add("Cannot read "+folder);yield break;}
        foreach(string file in files) {
            bool valid=false;
            try {valid=ImageFile(file)&&!Linked(file);} catch {issues.Add("Cannot inspect "+file);}
            if(valid)yield return file;
        }
        foreach(string child in folders) {
            bool skip=true;
            try {skip=Linked(child)||System.IO.Path.GetFileName(child).Equals("_download-info",StringComparison.OrdinalIgnoreCase);} catch {issues.Add("Cannot inspect "+child);}
            if(!skip)foreach(string file in DuplicateImageFiles(child,issues))yield return file;
        }
    }
    static void DuplicateJson(string filename,object value) {
        Directory.CreateDirectory(DuplicateData);
        string path=System.IO.Path.Combine(DuplicateData,filename),stage=path+"."+Guid.NewGuid().ToString("N")+".tmp";
        try {
            File.WriteAllText(stage,Json.Serialize(value)+Environment.NewLine,new UTF8Encoding(false));
            if(File.Exists(path))File.Replace(stage,path,null);else File.Move(stage,path);
        } finally {if(File.Exists(stage))File.Delete(stage);}
    }
    // Also callable by the test harness; IIS invokes this on a background worker.
    public static object CheckDuplicateFiles(CancellationToken token) {
        var issues=new List<string>();
        var paths=DuplicateImageFiles(Root,issues).OrderBy(p=>p,StringComparer.OrdinalIgnoreCase).ToList();
        var old=new Dictionary<string,GalleryHashEntry>(StringComparer.OrdinalIgnoreCase);
        string cache=System.IO.Path.Combine(DuplicateData,"duplicate-hashes.json");
        try {
            if(File.Exists(cache))foreach(var entry in Json.Deserialize<List<GalleryHashEntry>>(File.ReadAllText(cache)))old[entry.Path]=entry;
        } catch {}
        var hashes=new List<GalleryHashEntry>();int processed=0,hashed=0,cached=0;
        foreach(string path in paths) {
            token.ThrowIfCancellationRequested();
            try {
                string relative=path.Substring(Root.Length+1).Replace('\\','/');
                var before=new FileInfo(path);long length=before.Length,modified=before.LastWriteTimeUtc.Ticks;
                if(length==0)continue;
                GalleryHashEntry entry;
                if(old.TryGetValue(relative,out entry)&&entry.Bytes==length&&entry.Modified==modified&&entry.Sha256!=null&&entry.Sha256.Length==64)cached++;
                else {
                    string digest;
                    using(var sha=SHA256.Create())using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read,1024*1024,FileOptions.SequentialScan))
                        digest=BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();
                    var after=new FileInfo(path);
                    if(after.Length!=length||after.LastWriteTimeUtc.Ticks!=modified)throw new IOException("Image changed during scan.");
                    entry=new GalleryHashEntry {Path=relative,Bytes=length,Modified=modified,Sha256=digest};hashed++;
                }
                hashes.Add(entry);
            } catch {issues.Add("Could not hash "+path+"; it may be locked or changing.");}
            finally {
                processed++;
                lock(DuplicateSync)DuplicateState=new {phase="scanning",processed=processed,total=paths.Count,hashed=hashed,cached=cached,message="Comparing exact file contents…"};
            }
        }
        token.ThrowIfCancellationRequested();
        hashes=hashes.Where(entry=>{
            try {var file=new FileInfo(System.IO.Path.Combine(Root,entry.Path.Replace('/','\\')));if(file.Exists&&file.Length==entry.Bytes&&file.LastWriteTimeUtc.Ticks==entry.Modified)return true;}catch {}
            issues.Add(entry.Path+": changed or disappeared during the scan; left out of duplicate results.");return false;
        }).ToList();
        var groups=hashes.GroupBy(h=>h.Bytes+":"+h.Sha256).Where(g=>g.Count()>1).Select(g=>g.OrderBy(h=>h.Path,StringComparer.OrdinalIgnoreCase).ToList()).OrderBy(g=>g[0].Path,StringComparer.OrdinalIgnoreCase).ToList();
        var byPath=hashes.ToDictionary(h=>h.Path,StringComparer.OrdinalIgnoreCase);
        var duplicatePaths=new Dictionary<string,List<GalleryHashEntry>>(StringComparer.OrdinalIgnoreCase);
        foreach(var group in groups)foreach(var entry in group)duplicatePaths[entry.Path]=group;
        int changed=0;
        lock(Sync) {
            var collections=Collections(issues);
            var deleted=issues.Count==0 ? CleanupDuplicateCopies(groups,collections,issues,token) : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            hashes.RemoveAll(entry=>deleted.Contains(entry.Path));
            groups=hashes.GroupBy(h=>h.Bytes+":"+h.Sha256).Where(g=>g.Count()>1).Select(g=>g.OrderBy(h=>h.Path,StringComparer.OrdinalIgnoreCase).ToList()).OrderBy(g=>g[0].Path,StringComparer.OrdinalIgnoreCase).ToList();
            byPath=hashes.ToDictionary(h=>h.Path,StringComparer.OrdinalIgnoreCase);duplicatePaths.Clear();
            foreach(var group in groups)foreach(var entry in group)duplicatePaths[entry.Path]=group;
            var imageRecords=new List<GalleryImageRecord>();
            foreach(var collection in collections) {
                Reconcile(collection);
                for(int index=0;index<collection.Records.Count;index++) {
                    var record=collection.Records[index];if(Text(record,"record_type")!="image")continue;
                    var reference=ResolveReference(collection,record);string path=reference==null ? null : reference.Path;
                    GalleryHashEntry entry;
                    if(path!=null&&byPath.TryGetValue(path.Substring(Root.Length+1).Replace('\\','/'),out entry))
                        imageRecords.Add(new GalleryImageRecord {Collection=collection,Record=record,Entry=entry,Index=index});
                }
            }
            var recordPrimaries=new Dictionary<Dictionary<string,object>,GalleryImageRecord>();
            foreach(var content in imageRecords.GroupBy(r=>r.Entry.Sha256).Where(g=>g.Count()>1)) {
                var primary=content.OrderBy(r=>r.Entry.Path,StringComparer.OrdinalIgnoreCase).ThenByDescending(r=>!String.IsNullOrEmpty(Text(r.Record,"file"))).ThenBy(r=>r.Collection.Id,StringComparer.OrdinalIgnoreCase).ThenBy(r=>r.Index).First();
                foreach(var image in content)if(!Object.ReferenceEquals(image.Record,primary.Record))recordPrimaries[image.Record]=primary;
            }
            foreach(var collection in collections) {
                token.ThrowIfCancellationRequested();
                foreach(var record in collection.Records) {
                    if(Text(record,"record_type")!="image")continue;
                    var reference=ResolveReference(collection,record);string path=reference==null ? null : reference.Path;
                    string relative=path==null ? "" : path.Substring(Root.Length+1).Replace('\\','/');
                    GalleryHashEntry entry;List<GalleryHashEntry> group;
                    bool known=byPath.TryGetValue(relative,out entry);
                    // Ignore old hash results if a download or external edit changed this file.
                    if(known) {var file=ImageInfo(reference.Owner,path);known=file.Exists&&file.Length==entry.Bytes&&file.LastWriteTimeUtc.Ticks==entry.Modified;}
                    if(!known&&reference!=null)continue;
                    bool extra=known&&((duplicatePaths.TryGetValue(relative,out group)&&!group[0].Path.Equals(relative,StringComparison.OrdinalIgnoreCase))||recordPrimaries.ContainsKey(record));
                    if(Text(record,"duplicate_detected_by")=="sha256") {
                        if(!extra&&known) {record.Remove("duplicate_detected_by");if(Text(record,"status")=="duplicate")record["status"]="saved";}
                    }
                    foreach(string key in new[]{"content_duplicate","duplicate_group_sha256","duplicate_primary_path","duplicate_paths","duplicate_record_of","duplicate_record_site","duplicate_record_collection"})record.Remove(key);
                    if(known)record["sha256"]=entry.Sha256;
                    GalleryImageRecord primary;
                    if(known&&recordPrimaries.TryGetValue(record,out primary)) {
                        record["duplicate_record_of"]=Text(primary.Record,"id");record["duplicate_record_site"]=Text(primary.Record,"site");record["duplicate_record_collection"]=primary.Collection.Id;
                    }
                    if(known&&duplicatePaths.TryGetValue(relative,out group)) {
                        record["content_duplicate"]=extra;
                        record["duplicate_group_sha256"]=entry.Sha256;
                        record["duplicate_primary_path"]=group[0].Path;
                        record["duplicate_paths"]=group.Select(h=>h.Path).ToArray();
                    }
                    if(extra&&Text(record,"status")!="duplicate") {record["status"]="duplicate";record["duplicate_detected_by"]="sha256";}
                }
                if(Json.Serialize(collection.Records)!=Json.Serialize(Json.DeserializeObject(collection.Original))) {
                    try {Save(collection);changed++;}catch {issues.Add(collection.Id+": duplicate results could not be saved to the inventory. Refresh and retry.");}
                }
            }
            var reportGroups=new List<object>();
            foreach(var group in groups) {
                var files=new List<object>();
                foreach(var entry in group) {
                    string full=System.IO.Path.Combine(Root,entry.Path.Replace('/','\\'));
                    var owner=collections.Where(c=>Under(full,c.Folder)).OrderByDescending(c=>c.Folder.Length).FirstOrDefault();
                    string url=owner==null ? "" : MediaUrl(owner,full.Substring(owner.Folder.Length+1));
                    files.Add(new {path=entry.Path,bytes=entry.Bytes,url=url});
                }
                reportGroups.Add(new {sha256=group[0].Sha256,primary=group[0].Path,files=files});
            }
            var history=DeletionHistory().Where(action=>Text(action,"status")=="deleted").ToList();
            var report=new {phase="complete",checked_utc=DateTime.UtcNow.ToString("o"),total=paths.Count,files_remaining=paths.Count-deleted.Count,hashed=hashed,cached=cached,duplicate_groups=groups.Count,duplicate_files=groups.Sum(g=>g.Count-1),duplicate_records=collections.Sum(c=>c.Records.Count(r=>Text(r,"status")=="duplicate")),deleted_this_run=deleted.Count,deleted_files=history.Count,deletions=history,inventories_updated=changed,groups=reportGroups,issues=issues};
            DuplicateJson("duplicate-hashes.json",hashes);
            DuplicateJson("duplicate-report.json",report);
            return report;
        }
    }
}
