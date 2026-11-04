using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections;
using System.Collections.Generic;
using System.Configuration;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Threading;
using Microsoft.Win32.SafeHandles;

public static partial class Gallery {
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)]
    static extern SafeFileHandle CreateFile(string path,uint access,uint share,IntPtr security,uint creation,uint flags,IntPtr template);
    [StructLayout(LayoutKind.Sequential)]
    struct DeleteDisposition {[MarshalAs(UnmanagedType.Bool)]public bool Delete;}
    [DllImport("kernel32.dll",SetLastError=true)]
    static extern bool SetFileInformationByHandle(SafeFileHandle handle,int infoClass,ref DeleteDisposition info,uint size);
    static Collection OwnerFor(string path,List<Collection> collections) {return collections.Where(c=>Under(path,c.Folder)).OrderByDescending(c=>c.Folder.Length).FirstOrDefault();}
    static string HashStream(Stream stream) {stream.Position=0;using(var sha=SHA256.Create())return BitConverter.ToString(sha.ComputeHash(stream)).Replace("-","").ToLowerInvariant();}
    static void ValidateCleanupPath(string path) {
        string full=System.IO.Path.GetFullPath(path);
        if(!Under(full,Root)||!ImageFile(full))throw new IOException("Image path is outside the wallpaper root.");
        string cursor=full;
        while(cursor.Length>=Root.Length) {if(Linked(cursor))throw new IOException("Linked paths cannot be deduplicated.");if(cursor.Equals(Root,StringComparison.OrdinalIgnoreCase))break;cursor=System.IO.Path.GetDirectoryName(cursor);}
    }
    static List<Dictionary<string,object>> DeletionHistory() {
        string path=System.IO.Path.Combine(DuplicateData,"duplicate-deletions.json");
        if(!File.Exists(path))return new List<Dictionary<string,object>>();
        // A damaged deletion log must not be silently overwritten.
        return Json.Deserialize<List<Dictionary<string,object>>>(File.ReadAllText(path));
    }
    static bool RemapRecord(Collection collection,Dictionary<string,object> record,Dictionary<string,GalleryFileReference> replacements,bool markDuplicate=true) {
        bool changed=false;
        foreach(string field in new[]{"file","duplicate_of"}) {
            string collectionField=field=="file" ? "file_collection" : "duplicate_collection";
            var current=Reference(collection,Text(record,field),Text(record,collectionField));GalleryFileReference target;
            if(current!=null&&replacements.TryGetValue(current.Path,out target)) {
                SetReference(record,field,collectionField,collection,target);changed=true;
                if(markDuplicate&&Text(record,"record_type")=="image") {record["status"]="duplicate";record["duplicate_detected_by"]="sha256";}
            }
        }
        foreach(string field in new[]{"saved_image_references","related_image_references"}) {
            object raw;if(!record.TryGetValue(field,out raw)||!(raw is IEnumerable))continue;
            var pointers=new List<object>();bool remapped=false;
            foreach(object item in (IEnumerable)raw) {
                var pointer=item as Dictionary<string,object>;if(pointer==null)continue;
                var current=Reference(collection,Text(pointer,"file"),Text(pointer,"collection"));GalleryFileReference target;
                if(current!=null&&replacements.TryGetValue(current.Path,out target)) {pointers.Add(Pointer(target));remapped=true;}else pointers.Add(pointer);
            }
            if(remapped) {record[field]=pointers.ToArray();changed=true;}
        }
        foreach(string field in new[]{"images_saved","related_image_files"}) {
            object raw;if(!record.TryGetValue(field,out raw)||!(raw is IEnumerable)||raw is string)continue;
            var names=new List<string>();var pointers=new List<object>();bool remapped=false;
            foreach(object value in (IEnumerable)raw) {
                string name=Convert.ToString(value);var current=Reference(collection,name);GalleryFileReference target;
                if(current!=null&&replacements.TryGetValue(current.Path,out target)) {names.Add(target.Name);pointers.Add(Pointer(target));remapped=true;}
                else {names.Add(name);if(current!=null)pointers.Add(Pointer(current));}
            }
            if(remapped) {
                record[field]=names.ToArray();record[field=="images_saved" ? "saved_image_references" : "related_image_references"]=pointers.ToArray();changed=true;
            }
        }
        object outcomes;
        if(record.TryGetValue("image_outcomes",out outcomes)&&outcomes is IEnumerable)foreach(object value in (IEnumerable)outcomes) {
            var outcome=value as Dictionary<string,object>;if(outcome!=null&&RemapRecord(collection,outcome,replacements,markDuplicate)) {if(markDuplicate)outcome["status"]="duplicate";changed=true;}
        }
        return changed;
    }
    static HashSet<string> CleanupDuplicateCopies(List<List<GalleryHashEntry>> groups,List<Collection> collections,List<string> issues,CancellationToken token) {
        var deleted=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if(!String.Equals(ConfigurationManager.AppSettings["DeleteDuplicateFiles"],"true",StringComparison.OrdinalIgnoreCase))return deleted;
        var history=DeletionHistory();
        foreach(var group in groups) {
            token.ThrowIfCancellationRequested();
            var ordered=group.OrderBy(e=>OwnerFor(System.IO.Path.Combine(Root,e.Path.Replace('/','\\')),collections)==null ? 1 : 0).ThenBy(e=>e.Path,StringComparer.OrdinalIgnoreCase).ToList();
            var keep=ordered[0];string keepPath=System.IO.Path.Combine(Root,keep.Path.Replace('/','\\'));
            var owner=OwnerFor(keepPath,collections);
            if(owner==null) {issues.Add(keep.Path+": duplicate group is outside managed collections; no files deleted.");continue;}
            var canonical=new GalleryFileReference {Owner=owner,Name=keepPath.Substring(owner.Folder.Length+1),Path=keepPath};
            var streams=new List<FileStream>();
            try {
                ValidateCleanupPath(keepPath);
                // Keep the canonical file open without write/delete sharing throughout the commit.
                var keeper=new FileStream(keepPath,FileMode.Open,FileAccess.Read,FileShare.Read);streams.Add(keeper);
                if(keeper.Length!=keep.Bytes||HashStream(keeper)!=keep.Sha256)throw new IOException("The retained file changed since comparison.");
                var duplicates=new List<Tuple<GalleryHashEntry,FileStream>>();
                var replacements=new Dictionary<string,GalleryFileReference>(StringComparer.OrdinalIgnoreCase);
                foreach(var entry in ordered.Skip(1)) {
                    string path=System.IO.Path.GetFullPath(System.IO.Path.Combine(Root,entry.Path.Replace('/','\\')));
                    ValidateCleanupPath(path);
                    if(path.Equals(keepPath,StringComparison.OrdinalIgnoreCase))throw new IOException("Refusing to remove the retained file.");
                    // Read + DELETE access, read sharing only. No other process can replace,
                    // rename or modify this file between verification and handle-based deletion.
                    var handle=CreateFile(path,0x80010000,1,IntPtr.Zero,3,0x08000000,IntPtr.Zero);
                    if(handle.IsInvalid) {handle.Dispose();throw new Win32Exception(Marshal.GetLastWin32Error());}
                    var stream=new FileStream(handle,FileAccess.Read);streams.Add(stream);
                    if(stream.Length!=keeper.Length||HashStream(stream)!=keep.Sha256)throw new IOException("A duplicate changed since comparison.");
                    replacements[path]=canonical;duplicates.Add(Tuple.Create(entry,stream));
                }
                // Persist every affected inventory before removing any copy in this group.
                foreach(var collection in collections) {
                    bool changed=false;foreach(var record in collection.Records)if(RemapRecord(collection,record,replacements))changed=true;
                    if(!changed)continue;
                    string backup=collection.Inventory+".pre-dedup-"+DateTime.UtcNow.ToString("yyyyMMddTHHmmss")+"-"+Guid.NewGuid().ToString("N").Substring(0,8)+".bak";
                    if(!DatabaseEnabled)File.Copy(collection.Inventory,backup,false);ReconcilePosts(collection);Save(collection);
                }
                foreach(var duplicate in duplicates) {
                    var entry=duplicate.Item1;
                    var action=new Dictionary<string,object>{{"file",entry.Path},{"kept_file",keep.Path},{"sha256",keep.Sha256},{"bytes",entry.Bytes},{"utc",DateTime.UtcNow.ToString("o")},{"status","planned"}};
                    history.Add(action);DuplicateJson("duplicate-deletions.json",history);
                    var disposition=new DeleteDisposition {Delete=true};
                    if(!SetFileInformationByHandle(duplicate.Item2.SafeFileHandle,4,ref disposition,4))throw new Win32Exception(Marshal.GetLastWin32Error());
                    duplicate.Item2.Dispose();deleted.Add(entry.Path);action["status"]="deleted";DuplicateJson("duplicate-deletions.json",history);
                }
            } catch {
                foreach(var entry in ordered)entry.Modified=0; // Force a fresh hash on retry.
                issues.Add(keep.Path+": cleanup was not completed; verified files and source records were retained where deletion did not succeed. Retry after closing applications using these images.");
            }
            finally {foreach(var stream in streams)stream.Dispose();foreach(var collection in collections) {collection.ImageFiles=null;collection.References=null;}}
        }
        return deleted;
    }
}
