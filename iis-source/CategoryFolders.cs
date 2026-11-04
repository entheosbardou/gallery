using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;

public static partial class Gallery {
    static readonly object OrganizationSync=new object();
    static bool OrganizationRunning;
    static object OrganizationState=new {phase="idle"};
    public static object OrganizationStatus(){if(DatabaseEnabled)return DatabaseWorkerStatus();lock(OrganizationSync)return OrganizationState;}
    static void OrganizationProgress(string message,int processed,int total){lock(OrganizationSync)OrganizationState=new {phase="running",message=message,processed=processed,total=total};}
    public static object StartOrganization(){
        if(DatabaseEnabled)return RequestDatabaseWorker();
        lock(OrganizationSync){
            if(OrganizationRunning)return OrganizationState;
            OrganizationRunning=true;OrganizationState=new {phase="running",message="Waiting for the current file operation...",processed=0,total=0};
            try{System.Web.Hosting.HostingEnvironment.QueueBackgroundWorkItem(token=>{
                try{var result=Json.Deserialize<Dictionary<string,object>>(Json.Serialize(OrganizeCategoryFolders()));result["phase"]="complete";lock(OrganizationSync)OrganizationState=result;ViewExpires=DateTime.MinValue;}
                catch(Exception e){lock(OrganizationSync)OrganizationState=new {phase="error",error="Organization stopped: "+e.Message};}
                finally{lock(OrganizationSync)OrganizationRunning=false;}
            });}catch{OrganizationRunning=false;throw;}
            return OrganizationState;
        }
    }
    public static string CategoryFolder(Dictionary<string,object> record) {
        string category=Text(record,"category").Trim().ToLowerInvariant();
        return new[]{"sfw","sketchy","nsfw"}.Contains(category) ? category : "not_provided";
    }
    // Serialized with downloads and inventory edits. Originals remain until inventories are saved.
    public static object OrganizeCategoryFolders() {
        lock(Sync) {
            var issues=new List<string>();var collections=Collections(issues);
            if(OrganizationRunning)OrganizationProgress("Planning category folders...",0,0);
            if(issues.Count>0)throw new IOException(String.Join("; ",issues));
            var categories=new Dictionary<string,List<string>>(StringComparer.OrdinalIgnoreCase);
            foreach(var c in collections)foreach(var record in c.Records.Where(r=>Text(r,"record_type")=="image")) {
                var reference=ResolveReference(c,record);if(reference==null)continue;
                List<string> values;if(!categories.TryGetValue(reference.Path,out values))categories[reference.Path]=values=new List<string>();
                values.Add(CategoryFolder(record));
            }
            var replacements=new Dictionary<string,GalleryFileReference>(StringComparer.OrdinalIgnoreCase);
            foreach(var c in collections) {
                foreach(string category in new[]{"sfw","sketchy","nsfw","not_provided"}) {
                    string folder=Path.Combine(c.Folder,category);
                    if(Directory.Exists(folder)&&Linked(folder))throw new IOException("Linked category folder: "+folder);
                    Directory.CreateDirectory(folder);
                }
                foreach(string source in DuplicateImageFiles(c.Folder,issues)) {
                    if(OwnerFor(source,collections)!=c)continue;
                    List<string> values;categories.TryGetValue(source,out values);
                    string category=new[]{"nsfw","sketchy","sfw","not_provided"}.First(x=>values==null ? x=="not_provided" : values.Contains(x));
                    string name=category+"/"+Path.GetFileName(source),target=SafeFile(c,name);
                    if(target==null)throw new IOException("Unsafe category destination: "+name);
                    if(source.Equals(target,StringComparison.OrdinalIgnoreCase))continue;
                    if(File.Exists(target)||replacements.Values.Any(r=>r.Path.Equals(target,StringComparison.OrdinalIgnoreCase)))throw new IOException("Category destination already exists: "+target);
                    replacements[source]=new GalleryFileReference {Owner=c,Name=name,Path=target};
                }
            }
            if(issues.Count>0)throw new IOException(String.Join("; ",issues));
            string run=DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff");
            if(!DatabaseEnabled)foreach(var c in collections)File.Copy(c.Inventory,c.Inventory+".pre-categories-"+run+".bak");
            var manifest=replacements.Select(pair=>new {source=pair.Key,destination=pair.Value.Path}).ToArray();
            DuplicateJson("category-moves-"+run+".json",manifest);
            var digests=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
            int copied=0,removed=0;
            foreach(var pair in replacements) {
                using(var source=new FileStream(pair.Key,FileMode.Open,FileAccess.Read,FileShare.Read)) {
                    string digest=HashStream(source);source.Position=0;
                    using(var target=new FileStream(pair.Value.Path,FileMode.CreateNew,FileAccess.Write,FileShare.None))source.CopyTo(target);
                    using(var target=File.OpenRead(pair.Value.Path))if(HashStream(target)!=digest)throw new IOException("Category copy verification failed.");
                    digests[pair.Key]=digest;
                }
                if(OrganizationRunning)OrganizationProgress("Copying and verifying images...",++copied,replacements.Count);
            }
            foreach(var c in collections) {
                foreach(var record in c.Records)RemapRecord(c,record,replacements,false);
                Reconcile(c);Save(c);
            }
            foreach(var pair in replacements) {
                using(var source=File.OpenRead(pair.Key))if(HashStream(source)!=digests[pair.Key])throw new IOException("Original changed during organization; retained: "+pair.Key);
                using(var target=File.OpenRead(pair.Value.Path))if(HashStream(target)!=digests[pair.Key])throw new IOException("Destination changed during organization; original retained.");
                File.Delete(pair.Key);
                if(OrganizationRunning)OrganizationProgress("Removing verified old file locations...",++removed,replacements.Count);
            }
            return new {collections=collections.Count,moved=replacements.Count,manifest="category-moves-"+run+".json"};
        }
    }
}
