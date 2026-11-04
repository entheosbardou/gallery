using System;
using System.IO;
using System.Linq;
using System.Collections;
using System.Collections.Generic;

public class GalleryFileReference {
    public Collection Owner;
    public string Name, Path;
}
public static partial class Gallery {
    static void UpdateRoutedDownloadStatuses() {
        lock(Sync) {
            var issues=new List<string>();var collections=Collections(issues);
            foreach(var collection in collections)if(SyncRoutedRecords(collection,collections))Save(collection);
        }
    }
    static bool SyncRoutedRecords(Collection source,List<Collection> collections) {
        bool changed=false;var reconciled=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach(var row in source.Records.Where(r=>IsOtherCollection(Text(r,"download_collection")))) {
            var target=collections.FirstOrDefault(c=>c.Id==Text(row,"download_collection"));if(target==null||target==source)continue;
            if(reconciled.Add(target.Id)&&Reconcile(target))Save(target);
            var saved=target.Records.FirstOrDefault(r=>Text(r,"site")==Text(row,"site")&&Text(r,"id")==Text(row,"id")&&Text(r,"record_type")==Text(row,"record_type"));
            if(saved==null)continue;
            var files=RecordReferences(target,saved).GroupBy(f=>f.Path,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).ToList();
            if(files.Count==0)continue;
            string before=Json.Serialize(row);
            SetReference(row,"file","file_collection",source,files[0]);
            row["related_image_references"]=files.Select(f=>Pointer(f)).ToArray();
            row["status"]=Text(saved,"status")=="duplicate" ? "duplicate" : "saved";
            if(Text(saved,"image_count")!="")row["image_count"]=saved["image_count"];
            row.Remove("reason");row.Remove("error");row.Remove("previous_status");
            changed|=before!=Json.Serialize(row);
        }
        return changed;
    }
    static Collection ReferenceCollection(Collection current,string id) {
        if(String.IsNullOrEmpty(id)||id==current.Id)return current;
        if(current.References==null)current.References=new Dictionary<string,Collection>(StringComparer.OrdinalIgnoreCase);
        Collection owner;if(current.References.TryGetValue(id,out owner))return owner;
        string folder;
        try {folder=System.IO.Path.GetFullPath(System.IO.Path.Combine(Root,id.Replace('/','\\')));}catch{return null;}
        if((!Under(folder,Root)&&!folder.Equals(Root,StringComparison.OrdinalIgnoreCase))||!Directory.Exists(System.IO.Path.Combine(folder,"_download-info")))return null;
        string cursor=folder;
        while(cursor.Length>=Root.Length) {if(Linked(cursor))return null;if(cursor.Equals(Root,StringComparison.OrdinalIgnoreCase))break;cursor=System.IO.Path.GetDirectoryName(cursor);}
        owner=new Collection {Id=id,Folder=folder,CacheFiles=true};current.References[id]=owner;return owner;
    }
    static GalleryFileReference Reference(Collection current,string name,string id="") {
        if(String.IsNullOrEmpty(name))return null;
        var owner=ReferenceCollection(current,id);if(owner==null)return null;
        string path=SafeFile(owner,name);return Present(owner,path) ? new GalleryFileReference {Owner=owner,Name=name,Path=path} : null;
    }
    static IEnumerable<GalleryFileReference> RecordReferences(Collection current,Dictionary<string,object> record) {
        var file=Reference(current,Text(record,"file"),Text(record,"file_collection"));if(file!=null)yield return file;
        file=Reference(current,Text(record,"duplicate_of"),Text(record,"duplicate_collection"));if(file!=null)yield return file;
        foreach(string field in new[]{"saved_image_references","related_image_references"}) {
            object raw;if(!record.TryGetValue(field,out raw)||!(raw is IEnumerable))continue;
            foreach(object item in (IEnumerable)raw) {
                var pointer=item as Dictionary<string,object>;if(pointer==null)continue;
                file=Reference(current,Text(pointer,"file"),Text(pointer,"collection"));if(file!=null)yield return file;
            }
        }
        object saved;
        if(record.TryGetValue("related_image_files",out saved)&&saved is IEnumerable&&!(saved is string))foreach(object name in (IEnumerable)saved) {file=Reference(current,Convert.ToString(name));if(file!=null)yield return file;}
    }
    static GalleryFileReference ResolveReference(Collection current,Dictionary<string,object> record) {return RecordReferences(current,record).FirstOrDefault();}
    static Dictionary<string,object> Pointer(GalleryFileReference file) {return new Dictionary<string,object>{{"collection",file.Owner.Id},{"file",file.Name}};}
    static void SetReference(Dictionary<string,object> record,string field,string collectionField,Collection current,GalleryFileReference file) {
        record[field]=file.Name;if(file.Owner.Id!=current.Id)record[collectionField]=file.Owner.Id;else record.Remove(collectionField);
    }
    static void ReconcilePosts(Collection collection) {
        var images=collection.Records.Where(r=>Text(r,"record_type")=="image"&&PostKey(r)!="").ToLookup(r=>Text(r,"site")+"|"+PostKey(r));
        foreach(var post in collection.Records.Where(r=>Text(r,"record_type")=="post"||Text(r,"record_type")=="album"||Text(r,"record_type")=="post_or_album")) {
            var related=images[Text(post,"site")+"|"+PostKey(post)].ToList();
            var candidates=RecordReferences(collection,post).ToList();object raw;
            if(post.TryGetValue("images_saved",out raw)&&raw is IEnumerable&&!(raw is string))foreach(object name in (IEnumerable)raw) {var file=Reference(collection,Convert.ToString(name));if(file!=null)candidates.Add(file);}
            var savedRelated=related.Where(r=>ResolveReference(collection,r)!=null).ToList();
            candidates.AddRange(savedRelated.Select(r=>ResolveReference(collection,r)));
            int outcomesSaved=0;
            if(post.TryGetValue("image_outcomes",out raw)&&raw is IEnumerable)foreach(object item in (IEnumerable)raw) {
                var outcome=item as Dictionary<string,object>;if(outcome==null)continue;
                var file=ResolveReference(collection,outcome);if(file!=null) {candidates.Add(file);outcomesSaved++;}
            }
            var files=candidates.GroupBy(f=>f.Path,StringComparer.OrdinalIgnoreCase).Select(g=>g.First()).ToList();
            if(files.Count>0) {
                post["related_image_files"]=files.Select(f=>f.Name).ToArray();
                post["related_image_references"]=files.Select(f=>Pointer(f)).ToArray();
                post["saved_image_count"]=Math.Max(files.Count,Math.Max(savedRelated.Count,outcomesSaved));
                int expected;Int32.TryParse(Text(post,"image_count"),out expected);
                post["status"]=savedRelated.Count>0&&savedRelated.All(r=>Text(r,"status")=="duplicate") ? "duplicate" : "saved";
                if(savedRelated.Count>0) {
                    SetReference(post,"duplicate_of","duplicate_collection",collection,files[0]);
                    post["duplicate_record_of"]=Text(savedRelated[0],"id");post["related_image_records"]=savedRelated.Select(r=>Text(r,"id")).ToArray();post["duplicate_detected_by"]="post_image_link";
                }
                post.Remove("error");post.Remove("reason");post.Remove("previous_status");
            } else {
                foreach(string key in new[]{"related_image_files","related_image_references","saved_image_count","duplicate_record_of","related_image_records"})post.Remove(key);
                if(new[]{"downloaded","duplicate","partial"}.Contains(Text(post,"status"))) {post["reason"]="The saved images referenced by this post are missing on disk.";}
            }
        }
    }
}
