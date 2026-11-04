using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;

public static partial class Gallery {
    static Dictionary<string,object> LinkSelection(Collection collection,Dictionary<string,object> input){
        int index=PayloadIndex(collection,input);
        var row=collection.Records[index];
        if(Text(row,"site")!=Text(input,"site")||Text(row,"id")!=Text(input,"id")||EntryToken(row)!=Text(input,"token"))throw new GalleryError(409,"This entry changed. Refresh before linking.");
        return row;
    }
    public static object LinkLocalImage(Dictionary<string,object> payload){
        if(PagedInventory)return DatabaseLinkEntries(payload);
        lock(Sync){
            object raw;var localInput=payload.TryGetValue("local",out raw)?raw as Dictionary<string,object>:null;
            var targetInput=payload.TryGetValue("target",out raw)?raw as Dictionary<string,object>:null;
            if(localInput==null||targetInput==null)throw new GalleryError(400,"Choose a local image and its matching entry.");
            var localCollection=Find(Text(localInput,"collection"));
            var targetCollection=Text(targetInput,"collection")==localCollection.Id?localCollection:Find(Text(targetInput,"collection"));
            var local=LinkSelection(localCollection,localInput);var target=LinkSelection(targetCollection,targetInput);
            if(ReferenceEquals(local,target))throw new GalleryError(400,"Choose two different entries to link.");
            bool removeLocal=Text(local,"site")=="Local";
            if(!new[]{"image","post","album","post_or_album"}.Contains(Text(target,"record_type")))throw new GalleryError(400,"Choose an image, post or album entry.");
            var file=ResolveReference(localCollection,local);if(file==null)throw new GalleryError(404,"The local image is missing on disk.");
            string digest;long length;using(var stream=File.OpenRead(file.Path)){length=stream.Length;digest=HashStream(stream);}
            var existing=ResolveReference(targetCollection,target);
            bool post=new[]{"post","album","post_or_album"}.Contains(Text(target,"record_type"));
            if(!post&&existing!=null){using(var stream=File.OpenRead(existing.Path))if(HashStream(stream)!=digest)throw new GalleryError(409,"That image entry already has a different saved image. Choose the matching post or another entry.");file=existing;}
            string run=DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff")+"-"+Guid.NewGuid().ToString("N");
            string archive=Path.Combine(DuplicateData,removeLocal?"deleted-entries":"entry-links");Directory.CreateDirectory(archive);
            if(!DatabaseEnabled)File.WriteAllText(Path.Combine(archive,"linked-"+run+".json"),Json.Serialize(new {collection=localCollection.Id,record=local,linked_collection=targetCollection.Id,linked_id=Text(target,"id"),linked_utc=DateTime.UtcNow.ToString("o")}),new UTF8Encoding(false));
            if(!DatabaseEnabled)File.Copy(targetCollection.Inventory,targetCollection.Inventory+".pre-link-"+run+".bak");
            if(!DatabaseEnabled&&localCollection!=targetCollection)File.Copy(localCollection.Inventory,localCollection.Inventory+".pre-link-"+run+".bak");
            Dictionary<string,object> image=target;
            if(post){
                image=targetCollection.Records.FirstOrDefault(r=>Text(r,"record_type")=="image"&&Text(r,"site")==Text(target,"site")&&PostKey(r)==PostKey(target)&&Text(r,"sha256")==digest);
                if(image==null){image=new Dictionary<string,object>{{"site",Text(target,"site")},{"id",Text(target,"id")+"-local-"+digest.Substring(0,12)},{"record_type","image"},{"source",Text(target,"source")},{"description",Text(target,"description")},{"category",CategoryFolder(target)},{"manual_import",true}};
                    if(Text(target,"site")=="Reddit"&&Text(image,"source")=="")image["source"]="https://www.reddit.com/comments/"+PostKey(target)+"/";
                    targetCollection.Records.Add(image);
                }
            }
            SetReference(image,"file","file_collection",targetCollection,file);image["sha256"]=digest;image["file_size"]=length;image["status"]=!removeLocal||Text(local,"status")=="duplicate"&&existing==null?"duplicate":"saved";image["manual_import"]=removeLocal||Text(local,"manual_import")=="True";image.Remove("error");image.Remove("reason");image.Remove("duplicate_of");image.Remove("duplicate_collection");
            if(Text(image,"status")=="duplicate"){SetReference(image,"duplicate_of","duplicate_collection",targetCollection,file);image["duplicate_detected_by"]="sha256";}
            // Keep previous album images and add this verified local file.
            if(post){var pointers=RecordReferences(targetCollection,target).Concat(new[]{file}).GroupBy(f=>f.Path,StringComparer.OrdinalIgnoreCase).Select(g=>Pointer(g.First())).ToArray();target["related_image_references"]=pointers;target["status"]="saved";target.Remove("error");target.Remove("reason");ReconcilePosts(targetCollection);}
            if(localCollection!=targetCollection)Save(targetCollection);
            if(removeLocal)localCollection.Records.Remove(local);Save(localCollection);
            return new {status="linked",collection=targetCollection.Id,id=Text(target,"id"),files_deleted=0,local_removed=removeLocal};
        }
    }
    static readonly object ManualScanSync=new object();
    static bool ManualScanRunning;
    static object ManualScanState=new {phase="idle"};
    public static object ManualScanStatus(){if(DatabaseEnabled)return DatabaseWorkerStatus();lock(ManualScanSync)return ManualScanState;}
    public static object StartManualScan(){if(DatabaseEnabled)return RequestDatabaseWorker();lock(ManualScanSync){
        if(ManualScanRunning)return ManualScanState;
        ManualScanRunning=true;ManualScanState=new {phase="running",message="Checking manually saved images..."};
        try{System.Web.Hosting.HostingEnvironment.QueueBackgroundWorkItem(token=>{try{Scan();lock(ManualScanSync)ManualScanState=new {phase="complete",message="Manual images checked. Inventory and saved statuses updated."};}catch{lock(ManualScanSync)ManualScanState=new {phase="error",message="Manual scan could not finish. Check file access and retry."};}finally{lock(ManualScanSync)ManualScanRunning=false;}});}catch{ManualScanRunning=false;throw;}
        return ManualScanState;
    }}
    static string ImageName(string value){
        if(String.IsNullOrEmpty(value))return "";Uri uri;
        try{return Uri.TryCreate(value,UriKind.Absolute,out uri)&&uri.Scheme=="https" ? Uri.UnescapeDataString(Path.GetFileName(uri.AbsolutePath)) : Path.GetFileName(value.Replace('/',Path.DirectorySeparatorChar));}catch{return "";}
    }
    static bool ManualImageSignature(Stream stream){
        byte[] bytes=new byte[16];int length=stream.Read(bytes,0,bytes.Length);stream.Position=0;if(length<8)return false;
        return bytes[0]==255&&bytes[1]==216||bytes[0]==137&&bytes[1]==80&&bytes[2]==78&&bytes[3]==71||Encoding.ASCII.GetString(bytes,0,3)=="GIF"||Encoding.ASCII.GetString(bytes,0,4)=="RIFF"&&Encoding.ASCII.GetString(bytes,8,4)=="WEBP"||bytes[0]==66&&bytes[1]==77||bytes[0]==73&&bytes[1]==73&&bytes[2]==42||bytes[0]==77&&bytes[1]==77&&bytes[3]==42||Encoding.ASCII.GetString(bytes,4,4)=="ftyp"&&(Encoding.ASCII.GetString(bytes,8,4)=="avif"||Encoding.ASCII.GetString(bytes,8,4)=="avis");
    }
    static bool ManualNameMatch(Dictionary<string,object> row,string basename,string path){
        if(ImageName(Text(row,"url")).Equals(basename,StringComparison.OrdinalIgnoreCase)||ImageName(Text(row,"file")).Equals(basename,StringComparison.OrdinalIgnoreCase))return true;
        object raw;if(row.TryGetValue("original_image_urls",out raw)&&raw is IEnumerable&&!(raw is string))foreach(object url in (IEnumerable)raw)if(ImageName(Convert.ToString(url)).Equals(basename,StringComparison.OrdinalIgnoreCase))return true;
        return Text(row,"site")!=""&&Text(row,"id")!=""&&System.Text.RegularExpressions.Regex.IsMatch(Path.GetFileNameWithoutExtension(path),@"(?:^|\W)"+System.Text.RegularExpressions.Regex.Escape(Text(row,"site"))+@"[ _-]+"+System.Text.RegularExpressions.Regex.Escape(Text(row,"id"))+@"(?:$|\W)",System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    }
    static List<Collection> DetectManualImages(List<Collection> collections,List<string> issues){
        var files=DuplicateImageFiles(Root,issues).ToList();bool added=false;
        foreach(string path in files.Where(p=>OwnerFor(p,collections)==null)){
            string folder=Path.GetDirectoryName(path);
            if(new[]{"sfw","sketchy","nsfw","not_provided"}.Contains(Path.GetFileName(folder).ToLowerInvariant()))folder=Path.GetDirectoryName(folder);
            string info=Path.Combine(folder,"_download-info");
            if(Directory.Exists(info)&&Linked(info))continue;
            Directory.CreateDirectory(info);
            string inventory=Path.Combine(info,"inventory.json");
            if(Directory.GetFiles(info,"*inventory.json").Length!=0)continue;
            using(var stream=new FileStream(inventory,FileMode.CreateNew,FileAccess.Write))using(var writer=new StreamWriter(stream))writer.Write("[]");added=true;
        }
        if(added)collections=Collections(issues);
        var claimed=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string archives=Path.Combine(DuplicateData,"deleted-entries");
        if(Directory.Exists(archives))foreach(string archive in Directory.GetFiles(archives,"*.json"))try{
            var deleted=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(archive));
            var collection=collections.FirstOrDefault(c=>c.Id==Text(deleted,"collection"));object raw;
            if(collection!=null&&deleted.TryGetValue("record",out raw)){var row=raw as Dictionary<string,object>;if(row!=null)foreach(var file in RecordReferences(collection,row))claimed.Add(file.Path);}
        }catch{}
        var hashes=new Dictionary<string,List<GalleryFileReference>>(StringComparer.OrdinalIgnoreCase);
        foreach(var c in collections)foreach(var row in c.Records){
            foreach(var reference in RecordReferences(c,row))claimed.Add(reference.Path);
            var file=ResolveReference(c,row);string hash=Text(row,"sha256");
            if(file!=null&&hash.Length==64){List<GalleryFileReference> list;if(!hashes.TryGetValue(hash,out list))hashes[hash]=list=new List<GalleryFileReference>();list.Add(file);}
        }
        foreach(string path in files.Where(p=>!claimed.Contains(p))){
            var owner=OwnerFor(path,collections);if(owner==null)continue;
            try{
                var before=new FileInfo(path);if(before.Length==0||before.LastWriteTimeUtc>DateTime.UtcNow.AddSeconds(-3))continue;
                string digest;using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){
                    if(!ManualImageSignature(stream))continue;digest=HashStream(stream);
                }
                var after=new FileInfo(path);if(before.Length!=after.Length||before.LastWriteTimeUtc!=after.LastWriteTimeUtc)continue;
                if(digest=="9b5936f4006146e4e1e9025b474c02863c0b5614132ad40db4b925a10e8bfbb9")continue;
                bool duplicate=false;List<GalleryFileReference> known;
                if(hashes.TryGetValue(digest,out known))foreach(var reference in known)try{using(var stream=File.OpenRead(reference.Path))if(stream.Length==after.Length&&HashStream(stream)==digest){duplicate=true;break;}}catch{}
                string name=path.Substring(owner.Folder.Length+1).Replace('\\','/'),basename=Path.GetFileName(path);
                var matches=owner.Records.Where(r=>ResolveReference(owner,r)==null&&ManualNameMatch(r,basename,path)).ToList();
                // Only an unambiguous source match can identify a manually saved image.
                var match=matches.Count==1 ? matches[0] : null;
                Dictionary<string,object> row;
                if(match!=null&&Text(match,"record_type")=="image")row=match;
                else {
                    string id;using(var sha=SHA256.Create())id="local-"+BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(name))).Replace("-","").Substring(0,16).ToLowerInvariant();
                    row=new Dictionary<string,object>{{"site",match==null?"Local":Text(match,"site")},{"id",match==null?id:Text(match,"id")+"-"+id},{"record_type","image"},{"description",match==null?basename:Text(match,"description")},{"source",match==null?"":Text(match,"source")},{"url",match==null?"":Text(match,"url")}};
                    if(match!=null&&Text(match,"site")=="Reddit"&&Text(row,"source")=="")row["source"]="https://www.reddit.com/comments/"+PostKey(match)+"/";
                    string category=name.Split('/')[0].ToLowerInvariant();row["category"]=match!=null?CategoryFolder(match):new[]{"sfw","sketchy","nsfw"}.Contains(category)?category:"not_provided";
                    owner.Records.Add(row);
                }
                row["file"]=name;row.Remove("file_collection");row.Remove("duplicate_of");row.Remove("duplicate_collection");row["sha256"]=digest;row["file_size"]=after.Length;row["status"]=duplicate?"duplicate":"saved";row["manual_import"]=true;row["manual_detected_utc"]=DateTime.UtcNow.ToString("o");row.Remove("reason");row.Remove("error");
                claimed.Add(path);if(!hashes.TryGetValue(digest,out known))hashes[digest]=known=new List<GalleryFileReference>();known.Add(new GalleryFileReference{Owner=owner,Name=name,Path=path});
            }catch(IOException){/* Files still being downloaded are checked on the next pass. */}
            catch(UnauthorizedAccessException){issues.Add("Cannot inspect manual image: "+path);}
        }
        foreach(var c in collections)Save(c);
        return collections;
    }
}
