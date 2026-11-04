using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Web;

public class GalleryAccessRule {public string group;public string permission;public List<string> categories;public string collection_default;public List<GalleryCollectionRule> collections;}
public class GalleryCollectionRule {public string collection;public string visibility;}
public class GalleryAccessPolicy {public int revision;public List<GalleryAccessRule> rules;public string[] collection_options;}
public class GalleryGrant {public bool admin;public string[] categories;public bool allowed {get{return admin||categories.Length>0;}}}
public static partial class Gallery {
 public static readonly string[] AccessCategories={"sfw","sketchy","nsfw","not_provided"};
 const string AccessPolicyKey="gallery_access_policy_v1";
 public static GalleryAccessPolicy GetAccessPolicy(){
  using(var db=OpenDatabase()){
   var rows=DBRows(db,null,"SELECT value FROM gallery_state WHERE state_key=@key","@key",AccessPolicyKey);
   if(rows.Count>0)return Json.Deserialize<GalleryAccessPolicy>(Text(rows[0],"value"));
   var policy=new GalleryAccessPolicy{revision=1,rules=new List<GalleryAccessRule>{new GalleryAccessRule{group="wallpaper-admins",permission="admin",categories=AccessCategories.ToList()},new GalleryAccessRule{group="wallpaper-users",permission="view",categories=AccessCategories.ToList()},new GalleryAccessRule{group="wallpaper-sfw-users",permission="view",categories=new List<string>{"sfw"}}}};
   DBExec(db,null,"INSERT IGNORE INTO gallery_state(state_key,value) VALUES(@key,@value)","@key",AccessPolicyKey,"@value",Json.Serialize(policy));
   return Json.Deserialize<GalleryAccessPolicy>(Text(DBRows(db,null,"SELECT value FROM gallery_state WHERE state_key=@key","@key",AccessPolicyKey)[0],"value"));
  }
 }
 public static GalleryGrant GrantForGroups(IEnumerable<string> groups,GalleryAccessPolicy policy=null){
  policy=policy??GetAccessPolicy();var names=new HashSet<string>(groups,StringComparer.OrdinalIgnoreCase);var rules=policy.rules.Where(r=>names.Contains(r.group)).ToList();bool admin=rules.Any(r=>r.permission=="admin");
  return new GalleryGrant{admin=admin,categories=admin?AccessCategories.ToArray():rules.Where(r=>r.permission=="view").SelectMany(r=>r.categories??new List<string>()).Where(AccessCategories.Contains).Distinct().OrderBy(x=>x).ToArray()};
 }
 public static object AccessSettingsView(){
  var policy=GetAccessPolicy();var active=DiscoveredCollectionKeys();using(var db=OpenDatabase())policy.collection_options=DBRows(db,null,"SELECT collection_key FROM collections ORDER BY collection_key").Where(r=>active==null||active.Contains(Text(r,"collection_key"))).Select(r=>Text(r,"collection_key")).ToArray();return policy;
 }
 public static bool CollectionAccessAllowed(IEnumerable<string> groups,string collection,GalleryAccessPolicy policy){
  var names=new HashSet<string>(groups,StringComparer.OrdinalIgnoreCase);var matching=policy.rules.Where(r=>names.Contains(r.group)).ToList();
  if(matching.Any(r=>r.permission=="admin"))return true;
  var viewers=matching.Where(r=>r.permission=="view").ToList();bool allowed=viewers.Any(r=>r.collection_default!="hidden");int depth=-1;
  foreach(var rule in viewers)foreach(var item in rule.collections??new List<GalleryCollectionRule>()){
   if(!(item.collection=="."||String.Equals(collection,item.collection,StringComparison.OrdinalIgnoreCase)||collection.StartsWith(item.collection+"/",StringComparison.OrdinalIgnoreCase)))continue;
   int level=item.collection=="."?0:item.collection.Split('/').Length;
   if(level>depth){depth=level;allowed=item.visibility=="show";}else if(level==depth&&item.visibility=="show")allowed=true;
  }return allowed;
 }
 static GalleryAccessPolicy RequestAccessPolicy(HttpContext context){
  const string key="GalleryCollectionPolicy";var policy=context.Items[key] as GalleryAccessPolicy;if(policy==null){policy=GetAccessPolicy();context.Items[key]=policy;}return policy;
 }
 static bool CollectionVisible(HttpContext context,string collection){return GalleryAuth.IsAdmin(context)||CollectionAccessAllowed(GalleryAuth.Groups(context),collection,RequestAccessPolicy(context));}
 static Dictionary<string,string> MediaCollections(string relative=null){
  var active=DiscoveredCollectionKeys();
  using(var db=OpenDatabase())return DBRows(db,null,"SELECT f.relative_path,c.collection_key FROM fm_file_catalog f JOIN collections c ON c.id=f.collection_id WHERE f.is_present=TRUE AND f.canonical_file_id IS NULL"+(relative==null?"":" AND BINARY f.relative_path=BINARY @path"),"@path",relative).Where(r=>active==null||active.Contains(Text(r,"collection_key"))).ToDictionary(r=>Text(r,"relative_path"),r=>Text(r,"collection_key"),StringComparer.OrdinalIgnoreCase);
 }
 static bool VisibleMediaUrl(HttpContext context,string url,Dictionary<string,string> files,Dictionary<string,string> owners,string[] categories){
  try{var query=HttpUtility.ParseQueryString(new Uri(new Uri("https://gallery.invalid"),url).Query);string collection=query["collection"]??"",relative=AccessRelativePath(collection,query["file"]??""),category,owner;return relative!=null&&files.TryGetValue(relative,out category)&&categories.Contains(category)&&owners.TryGetValue(relative,out owner)&&CollectionVisible(context,collection)&&CollectionVisible(context,owner);}catch{return false;}
 }
 public static object SaveAccessPolicy(HttpContext context,Dictionary<string,object> input){
  if(!GalleryAuth.IsAdmin(context))throw new GalleryError(403,"Only gallery administrators can change access settings.");
  var posted=Json.Deserialize<GalleryAccessPolicy>(Json.Serialize(input));
  if(posted.rules==null||posted.rules.Count==0||posted.rules.Count>100)throw new GalleryError(400,"Add between 1 and 100 group rules.");
  var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
  foreach(var rule in posted.rules){
   rule.group=(rule.group??"").Trim();rule.permission=(rule.permission??"").ToLowerInvariant();
   if(rule.group.Length==0||rule.group.Length>120||rule.group.Any(Char.IsControl)||!names.Add(rule.group))throw new GalleryError(400,"Each rule needs a distinct AD group name.");
   if(!new[]{"view","admin","none"}.Contains(rule.permission))throw new GalleryError(400,"Choose viewing, administration or no access.");
   rule.categories=(rule.categories??new List<string>()).Select(x=>(x??"").ToLowerInvariant()).Distinct().ToList();
   if(rule.categories.Any(x=>!AccessCategories.Contains(x)))throw new GalleryError(400,"Unknown category in an access rule.");
   rule.collection_default=(rule.collection_default??"visible").ToLowerInvariant();
   if(!new[]{"visible","hidden"}.Contains(rule.collection_default))throw new GalleryError(400,"Choose a default collection visibility.");
   rule.collections=rule.collections??new List<GalleryCollectionRule>();if(rule.collections.Count>1000)throw new GalleryError(400,"Use at most 1,000 collection overrides per group.");
   var paths=new HashSet<string>(StringComparer.OrdinalIgnoreCase);
   foreach(var item in rule.collections){
    if(item==null)throw new GalleryError(400,"Invalid collection override.");
    item.collection=(item.collection??"").Trim().Replace('\\','/');item.visibility=(item.visibility??"").ToLowerInvariant();
    if(item.collection.Length==0||item.collection.Length>1000||item.collection.Any(Char.IsControl)||item.collection.Contains(":")||item.collection.StartsWith("/")||item.collection.EndsWith("/")||(item.collection!="."&&item.collection.Split('/').Any(part=>part==""||part=="."||part==".."))||!paths.Add(item.collection))throw new GalleryError(400,"Each collection override needs a distinct relative collection path.");
    if(!new[]{"show","hide"}.Contains(item.visibility))throw new GalleryError(400,"Choose Show or Hide for each collection override.");
   }
   if(rule.permission=="admin")rule.categories=AccessCategories.ToList();
   if(rule.permission=="view"&&rule.categories.Count==0)throw new GalleryError(400,"Choose at least one category for each viewing group, or select no access.");
  }
  if(!GrantForGroups(GalleryAuth.Groups(context),posted).admin)throw new GalleryError(400,"Keep administrator access for a group you belong to, so you do not lock yourself out.");
  using(var db=OpenDatabase())using(var tx=db.BeginTransaction()){
   var rows=DBRows(db,tx,"SELECT value FROM gallery_state WHERE state_key=@key FOR UPDATE","@key",AccessPolicyKey);if(rows.Count!=1)throw new GalleryError(409,"Reload access settings before saving.");
   var current=Json.Deserialize<GalleryAccessPolicy>(Text(rows[0],"value"));if(current.revision!=posted.revision)throw new GalleryError(409,"Access settings changed. Reload them before saving.");
   posted.revision=current.revision+1;posted.collection_options=null;
   DBExec(db,tx,"UPDATE gallery_state SET value=@value WHERE state_key=@key","@value",Json.Serialize(posted),"@key",AccessPolicyKey);
   DBExec(db,tx,"INSERT INTO gallery_state(state_key,value) VALUES(@key,@value)","@key","gallery_access_history:"+DateTime.UtcNow.Ticks,"@value",Json.Serialize(new{actor=Convert.ToString(context.Items["GalleryName"]),changed_utc=DateTime.UtcNow.ToString("o"),before=current,after=posted}));
   tx.Commit();return posted;
  }
 }
 static string AccessRelativePath(string collection,string file){
  try{if(String.IsNullOrEmpty(file)||Path.IsPathRooted(file))return null;string folder=Path.GetFullPath(Path.Combine(Root,collection.Replace('/','\\'))),path=Path.GetFullPath(Path.Combine(folder,file.Replace('/','\\')));return Under(path,Root)?path.Substring(Root.Length+1).Replace('\\','/'):null;}catch{return null;}
 }
 static int FolderCategoryRank(string name){
  int rank=0;var parts=name.Split('/');foreach(var part in parts.Take(parts.Length-1)){string folder=part.ToLowerInvariant();int candidate=folder=="sfw"?1:folder=="sketchy"?3:folder=="nsfw"?4:folder=="not_provided"?2:0;rank=Math.Max(rank,candidate);}return rank==0?2:rank;
 }
 static Dictionary<string,string> MediaCategories(string relative=null){
  var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
  using(var db=OpenDatabase()){
   string sql="SELECT f.relative_path,c.collection_key,MAX(CASE WHEN e.record_type='image' THEN CASE LOWER(COALESCE(e.category,'')) WHEN 'sfw' THEN 1 WHEN 'sketchy' THEN 3 WHEN 'nsfw' THEN 4 ELSE 2 END ELSE CASE LOWER(COALESCE(e.category,'')) WHEN 'sketchy' THEN 3 WHEN 'nsfw' THEN 4 ELSE 0 END END) AS category_rank FROM fm_file_catalog f JOIN collections c ON c.id=f.collection_id JOIN fm_entry_file_projection l ON l.file_id=f.id JOIN inventory_entries e ON e.id=l.entry_id AND e.deleted_at IS NULL WHERE f.is_present=TRUE AND f.canonical_file_id IS NULL";
   if(relative!=null)sql+=" AND BINARY f.relative_path=BINARY @path";
   sql+=" GROUP BY f.id,f.relative_path,c.collection_key";
   foreach(var row in DBRows(db,null,sql,"@path",relative)){
    string path=Text(row,"relative_path"),owner=Text(row,"collection_key"),name=owner=="."?path:path.Substring(owner.Length+1);int folderRank=FolderCategoryRank(name);
    int rank=Math.Max(folderRank,Convert.ToInt32(row["category_rank"]));result[path]=rank==1?"sfw":rank==3?"sketchy":rank==4?"nsfw":"not_provided";
   }
  }return result;
 }
 public static bool ImageAccessAllowed(HttpContext context,string collection,string file){
  if(GalleryAuth.IsAdmin(context))return true;
  if(PagedInventory)return DatabaseImageAccessAllowed(context,collection,file);
  string relative=AccessRelativePath(collection,file);if(relative==null||!CollectionVisible(context,collection))return false;
  string owner;if(!MediaCollections(relative).TryGetValue(relative,out owner)||!CollectionVisible(context,owner))return false;
  string category;return MediaCategories(relative).TryGetValue(relative,out category)&&GalleryAuth.Categories(context).Contains(category);
 }
 public static object AccessibleInventory(object inventory,HttpContext context){
  var data=Json.Deserialize<Dictionary<string,object>>(Json.Serialize(inventory));var source=Json.Deserialize<List<Dictionary<string,object>>>(Json.Serialize(data["records"]));var categories=GalleryAuth.Categories(context);bool restricted=categories.Length<AccessCategories.Length;var files=MediaCategories();var owners=MediaCollections();var records=new List<Dictionary<string,object>>();
  foreach(var r in source){
   if(!CollectionVisible(context,Text(r,"collection"))||Text(r,"local")==""||(restricted&&Text(r,"record_type")!="image"))continue;
   var query=HttpUtility.ParseQueryString(new Uri(new Uri("https://gallery.invalid"),Text(r,"local")).Query);string relative=AccessRelativePath(query["collection"]??"",query["file"]??""),category;
   if(relative==null||!files.TryGetValue(relative,out category)||!categories.Contains(category)||!VisibleMediaUrl(context,Text(r,"local"),files,owners,categories))continue;
   var copy=new Dictionary<string,object>();foreach(var field in new[]{"site","id","record_type","status","description","resolution","collection","source","url","file","file_collection","local","thumb"})if(r.ContainsKey(field))copy[field]=r[field];copy["category"]=category;
   if(!restricted&&r.ContainsKey("related_image_links")){
    var links=Json.Deserialize<List<Dictionary<string,object>>>(Json.Serialize(r["related_image_links"]));copy["related_image_links"]=links.Where(l=>CollectionVisible(context,Text(l,"collection"))&&VisibleMediaUrl(context,Text(l,"url"),files,owners,categories)).ToArray();
   }
   copy["search"]=String.Join(" ",new[]{"id","description","file","site","status","category","collection"}.Select(k=>Text(copy,k))).ToLowerInvariant();records.Add(copy);
  }
  return new{records=records,collections=records.GroupBy(r=>Text(r,"collection")).Select(g=>new{id=g.Key,records=g.Count(),saved=g.Count()}).ToArray(),issues=new string[0],root="",categories=categories,restricted=restricted};
 }
}
