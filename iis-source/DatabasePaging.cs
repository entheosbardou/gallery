using System;using System.IO;using System.Linq;using System.Text;using System.Collections.Generic;using System.Collections.Specialized;using System.Web;using System.Web.Security;using MySqlConnector;
public static partial class Gallery {
 public static bool PagedInventory {get{return DatabaseEnabled&&System.Configuration.ConfigurationManager.AppSettings["GalleryPagedInventory"]=="true";}}
 class PageScope {public List<object> args=new List<object>();public string where;public List<long> owners;public string[] categories;public bool admin;public string fingerprint;}
 static string Param(List<object> args,object value){string key="@p"+(args.Count/2);args.Add(key);args.Add(value);return key;}
 static string InParams(List<object> args,IEnumerable<object> values){return String.Join(",",values.Select(v=>Param(args,v)));}
 static string[] FilterValues(NameValueCollection query,string name){string raw=query[name];if(String.IsNullOrEmpty(raw))return null;if(raw.Length>8192)throw new GalleryError(400,"Filter is too long.");try{var values=Json.Deserialize<string[]>(raw);if(values.Length>100)throw new Exception();return values.Distinct().ToArray();}catch{throw new GalleryError(400,"Invalid filter.");}}
 static PageScope BrowseScope(HttpContext context,MySqlConnection db,bool applyFilters){
  var s=new PageScope{admin=GalleryAuth.IsAdmin(context),categories=GalleryAuth.Categories(context)};
  var policy=RequestAccessPolicy(context);var groups=GalleryAuth.Groups(context);
  var collections=DBRows(db,null,"SELECT c.id,c.collection_key FROM collections c JOIN fm_collection_discovery m ON m.collection_id=c.id WHERE m.is_marked=TRUE ORDER BY c.id");
  s.owners=collections.Where(c=>s.admin||CollectionAccessAllowed(groups,Text(c,"collection_key"),policy)).Select(c=>Convert.ToInt64(c["id"])).ToList();
  var predicates=new List<string>{"e.deleted_at IS NULL","c.id IN ("+(s.owners.Count==0?"NULL":InParams(s.args,s.owners.Cast<object>()))+")"};
  if(!s.admin||qShowDuplicates(context)==false)predicates.Add("e.is_hidden=FALSE AND e.status<>'duplicate'");
  if(!s.admin&&s.categories.Length<AccessCategories.Length)predicates.Add("e.record_type='image'");
  var q=context.Request.QueryString;var cats=applyFilters?FilterValues(q,"categories"):null;
  if(!s.admin){if(cats!=null)s.categories=s.categories.Intersect(cats).ToArray();predicates.Add(BrowseFilesExist(s));}
  else if(cats!=null)predicates.Add("COALESCE(NULLIF(e.category,''),'not_provided') IN ("+(cats.Length==0?"NULL":InParams(s.args,cats.Cast<object>()))+")");
  if(applyFilters){
   string selected=q["collection"]??"";
   if(selected!=""&&selected!="."){var selectedIds=collections.Where(c=>Text(c,"collection_key").Equals(selected,StringComparison.OrdinalIgnoreCase)||Text(c,"collection_key").StartsWith(selected+"/",StringComparison.OrdinalIgnoreCase)).Select(c=>c["id"]);string ids=InParams(s.args,selectedIds);predicates.Add("c.id IN ("+(ids==""?"NULL":ids)+")");}
   var sites=FilterValues(q,"sites");if(sites!=null)predicates.Add("e.site IN ("+(sites.Length==0?"NULL":InParams(s.args,sites.Cast<object>()))+")");
   var statuses=FilterValues(q,"statuses");if(statuses!=null){var options=new List<string>();if(statuses.Contains("saved"))options.Add(BrowseFilesExist(s));var others=statuses.Where(x=>x!="saved").ToArray();if(others.Length>0)options.Add("e.status IN ("+InParams(s.args,others.Cast<object>())+")");predicates.Add(options.Count==0?"FALSE":"("+String.Join(" OR ",options)+")");}
   if(!String.IsNullOrEmpty(q["kind"]))predicates.Add("e.record_type="+Param(s.args,q["kind"]));
   string search=(q["search"]??"").Trim();if(search.Length>200)throw new GalleryError(400,"Search is too long.");
   if(search!=""){
    if(search.StartsWith("id:",StringComparison.OrdinalIgnoreCase))predicates.Add("e.source_id="+Param(s.args,search.Substring(3).Trim()));
    else if(search.StartsWith("file:",StringComparison.OrdinalIgnoreCase)){string prefix=search.Substring(5).Trim().Replace("\\","\\\\").Replace("%","\\%").Replace("_","\\_")+"%";predicates.Add("e.id IN (SELECT image.id FROM files f JOIN inventory_entries image ON image.file_id=f.id WHERE f.file_name LIKE "+Param(s.args,prefix)+" UNION SELECT relation.entry_id FROM files f JOIN inventory_entries image ON image.file_id=f.id AND image.deleted_at IS NULL JOIN inventory_entry_relations relation ON relation.related_entry_id=image.id AND relation.relation_kind='parent_of' WHERE f.file_name LIKE "+Param(s.args,prefix)+")");}
    else{string p=Param(s.args,search);predicates.Add("e.id IN (SELECT id FROM inventory_entries WHERE MATCH(description) AGAINST ("+p+" IN NATURAL LANGUAGE MODE) UNION SELECT id FROM inventory_entries WHERE source_id="+p+" UNION SELECT image.id FROM files f JOIN inventory_entries image ON image.file_id=f.id WHERE f.file_name="+p+" UNION SELECT relation.entry_id FROM files f JOIN inventory_entries image ON image.file_id=f.id AND image.deleted_at IS NULL JOIN inventory_entry_relations relation ON relation.related_entry_id=image.id AND relation.relation_kind='parent_of' WHERE f.file_name="+p+")");}
   }
   string tagQuery=(q["tags"]??"").Trim();if(tagQuery.Length>2048)throw new GalleryError(400,"Tag filter is too long.");var terms=tagQuery.Split(',').Select(x=>x.Trim()).Where(x=>x!="").Distinct().ToArray();if(terms.Length>10)throw new GalleryError(400,"Choose at most ten tags.");
   foreach(string term in terms){var tag=DBRows(db,null,"SELECT id FROM tags WHERE term=@term","@term",term);if(tag.Count==0){predicates.Add("FALSE");break;}predicates.Add("EXISTS(SELECT 1 FROM inventory_entry_tags et WHERE et.entry_id=e.id AND et.tag_id="+Param(s.args,tag[0]["id"])+")");}
  }
  s.where=String.Join(" AND ",predicates);s.fingerprint=BitConverter.ToString(DatabaseHash(s.where+"|"+Json.Serialize(s.args)+"|"+policy.revision+"|"+String.Join(",",groups.OrderBy(x=>x))+"|"+(q["limit"]??"")+"|"+(q["sort"]??""))).Replace("-","");return s;
 }
 static bool qShowDuplicates(HttpContext context){return context.Request.QueryString["duplicates"]=="1";}
 static string BrowseFilesExist(PageScope s){
  string owners=s.owners.Count==0?"NULL":InParams(s.args,s.owners.Cast<object>());
  string suffix=" AND actual.is_present=TRUE AND actual.collection_id IN ("+owners+")";
  if(!s.admin)suffix+=" AND NOT EXISTS(SELECT 1 FROM fm_file_dirty pending WHERE pending.file_id=actual.id) AND actual.access_category IN ("+(s.categories.Length==0?"NULL":InParams(s.args,s.categories.Cast<object>()))+")";
  string files=" JOIN files fi ON fi.id=image.file_id JOIN fm_file_catalog actual ON actual.id=COALESCE(fi.canonical_file_id,fi.id) JOIN fm_collection_discovery md ON md.collection_id=actual.collection_id AND md.is_marked=TRUE ";
  return "(EXISTS(SELECT 1 FROM inventory_entries image"+files+"WHERE image.id=e.id"+suffix+") OR EXISTS(SELECT 1 FROM inventory_entry_relations relation JOIN inventory_entries image ON image.id=relation.related_entry_id AND image.deleted_at IS NULL"+files+"WHERE relation.entry_id=e.id AND relation.relation_kind='parent_of'"+suffix+"))";

 }
 static long ReadPageCursor(string cursor,string signature){
  if(String.IsNullOrEmpty(cursor))return 0;
  if(cursor.Length>2048)throw new GalleryError(400,"Invalid page cursor.");
  try{string value=Encoding.UTF8.GetString(MachineKey.Unprotect(HttpServerUtility.UrlTokenDecode(cursor),"GalleryPage-v1"));var parts=value.Split('|');if(parts.Length!=2||parts[1]!=signature)throw new Exception();return Int64.Parse(parts[0]);}catch{throw new GalleryError(400,"Page filters changed. Return to the first page.");}
 }
 static string PageCursor(long id,string signature){return HttpServerUtility.UrlTokenEncode(MachineKey.Protect(Encoding.UTF8.GetBytes(id+"|"+signature),"GalleryPage-v1"));}
 public static object DatabaseInventoryPage(HttpContext context){
  using(var db=OpenDatabase()){
   var scope=BrowseScope(context,db,true);int limit;if(!Int32.TryParse(context.Request.QueryString["limit"],out limit))limit=60;limit=Math.Max(1,Math.Min(5000,limit));bool descending=context.Request.QueryString["sort"]!="oldest";string requestedPage=context.Request.QueryString["page"];long page=1;if(requestedPage!=null&&(!Int64.TryParse(requestedPage,out page)||page<1))throw new GalleryError(400,"Choose a valid page number.");if(requestedPage!=null&&!String.IsNullOrEmpty(context.Request.QueryString["cursor"]))throw new GalleryError(400,"Choose a page or a cursor, not both.");long after=requestedPage==null?(String.IsNullOrEmpty(context.Request.QueryString["cursor"])?(descending?Int64.MaxValue:0):ReadPageCursor(context.Request.QueryString["cursor"],scope.fingerprint)):0;
   long total=Convert.ToInt64(DBRows(db,null,"SELECT COUNT(*) n FROM inventory_entries e JOIN collections c ON c.id=e.collection_id WHERE "+scope.where,scope.args.ToArray())[0]["n"]);
   if(requestedPage!=null&&page>Math.Max(1,(total+limit-1)/limit))throw new GalleryError(400,"That page is beyond the matching results.");
   string boundary=requestedPage==null?" AND e.id"+(descending?"<":">")+Param(scope.args,after):"";
   string take=Param(scope.args,limit+1),offset=requestedPage==null?"":Param(scope.args,(page-1)*limit)+",";
   string sql="SELECT e.id AS entry_id,e.revision,e.site,e.source_id,e.record_type,e.status,COALESCE(NULLIF(e.category,''),'not_provided') category,LEFT(e.description,4096) description,e.source_url,e.image_url,e.resolution,c.collection_key,m.reason,m.error FROM inventory_entries e JOIN collections c ON c.id=e.collection_id LEFT JOIN fm_entry_metadata m ON m.legacy_entry_id=e.id WHERE "+scope.where+boundary+" ORDER BY e.id "+(descending?"DESC":"ASC")+" LIMIT "+offset+take;
   var source=DBRows(db,null,sql,scope.args.ToArray());bool more=source.Count>limit;if(more)source.RemoveAt(source.Count-1);
   var records=new List<Dictionary<string,object>>();var lookup=new Dictionary<long,Dictionary<string,object>>();
   foreach(var row in source){var r=new Dictionary<string,object>();foreach(string field in new[]{"site","record_type","status","category","description","resolution","reason","error"})if(row[field]!=null)r[field]=row[field];r["entry_id"]=row["entry_id"];r["revision"]=row["revision"];r["id"]=row["source_id"];r["collection"]=row["collection_key"];if(row["source_url"]!=null)r["source"]=row["source_url"];if(row["image_url"]!=null)r["url"]=row["image_url"];r["entry_token"]=BitConverter.ToString(DatabaseHash(Text(row,"entry_id")+"|"+Text(row,"revision"))).Replace("-","");lookup[Convert.ToInt64(row["entry_id"])]=r;records.Add(r);}
   if(records.Count>0){
    var args=new List<object>();string ids=InParams(args,lookup.Keys.Cast<object>());string owners=scope.owners.Count==0?"NULL":InParams(args,scope.owners.Cast<object>());
    string fileSql="SELECT * FROM (SELECT l.entry_id,l.reference_kind,l.position,f.relative_path,c.collection_key,f.access_category,asset.id asset_id,COUNT(*) OVER(PARTITION BY l.entry_id) link_count,ROW_NUMBER() OVER(PARTITION BY l.entry_id ORDER BY FIELD(l.reference_kind,'primary','duplicate','related','saved'),l.position) rn FROM fm_entry_file_projection l JOIN fm_file_catalog original ON original.id=l.file_id JOIN fm_file_catalog f ON f.id=COALESCE(original.canonical_file_id,original.id) LEFT JOIN fm_assets asset ON asset.legacy_canonical_file_id=f.id JOIN collections c ON c.id=f.collection_id JOIN fm_collection_discovery md ON md.collection_id=c.id AND md.is_marked=TRUE WHERE l.entry_id IN ("+ids+") AND f.is_present=TRUE AND c.id IN ("+owners+")";
    if(!scope.admin)fileSql+=" AND NOT EXISTS(SELECT 1 FROM fm_file_dirty pending WHERE pending.file_id=f.id) AND f.access_category IN ("+(scope.categories.Length==0?"NULL":InParams(args,scope.categories.Cast<object>()))+")";
    fileSql+=") page_links WHERE rn<=50 ORDER BY entry_id,rn";
    foreach(var f in DBRows(db,null,fileSql,args.ToArray())){
     var r=lookup[Convert.ToInt64(f["entry_id"])];r["related_has_more"]=Convert.ToInt64(f["link_count"])>50;string owner=Text(f,"collection_key"),path=Text(f,"relative_path"),name=owner=="."?path:path.Substring(owner.Length+1);var c=new Collection{Id=owner};
     if(!r.ContainsKey("local")){r["asset_id"]=f["asset_id"];r["local"]=MediaUrl(c,name);r["thumb"]=MediaUrl(c,name,true);r["file"]=name;if(owner!=Text(r,"collection"))r["file_collection"]=owner;if(!scope.admin)r["category"]=f["access_category"];}
     if(new[]{"post","album","post_or_album"}.Contains(Text(r,"record_type"))){object value;if(!r.TryGetValue("related_image_links",out value))r["related_image_links"]=value=new List<object>();((List<object>)value).Add(new{collection=owner,file=name,url=MediaUrl(c,name)});}
    }
    foreach(var r in records)r["tags"]=new List<object>();
    // Only the current page's relations are read; never the entire tag catalog.
    foreach(var tag in DBRows(db,null,"SELECT et.entry_id,t.id,t.term FROM inventory_entry_tags et JOIN tags t ON t.id=et.tag_id WHERE et.entry_id IN ("+ids+") ORDER BY et.entry_id,t.id",args.Take(lookup.Count*2).ToArray()))((List<object>)lookup[Convert.ToInt64(tag["entry_id"])]["tags"]).Add(new{id=tag["id"],term=tag["term"]});
   }
   return new{records=records,total=total,next_cursor=more&&records.Count>0?PageCursor(Convert.ToInt64(source.Last()["entry_id"]),scope.fingerprint):null,has_more=more,page_size=limit,paged=true,categories=scope.categories,restricted=!scope.admin&&scope.categories.Length<AccessCategories.Length,issues=new string[0],root=scope.admin?Root:""};
  }
 }
 public static List<GalleryQueueItem> DatabaseFilteredDownloadItems(HttpContext context){
  using(var db=OpenDatabase()){
   var scope=BrowseScope(context,db,true);
   string sql="SELECT e.id,e.site,e.source_id,e.record_type,e.image_url,c.collection_key FROM inventory_entries e JOIN collections c ON c.id=e.collection_id WHERE "+scope.where+" ORDER BY e.id LIMIT 10001";
   var selected=DBRows(db,null,sql,scope.args.ToArray());if(selected.Count>10000)throw new GalleryError(400,"Choose at most 10,000 matching entries at a time. Narrow the filters.");
   return selected.Select(r=>new GalleryQueueItem{entry_id=Text(r,"id"),collection=Text(r,"collection_key"),site=Text(r,"site"),id=Text(r,"source_id"),kind=Text(r,"record_type"),url=Text(r,"image_url")}).ToList();
  }
 }
 public static object EnqueueFilteredDownloads(HttpContext context){
  lock(QueueSync){LoadQueue();if(DownloadJob!=null&&new[]{"queued","running"}.Contains(DownloadJob.phase))throw new GalleryError(409,"Downloads are already running. Their progress is shown on this page.");
   var items=DatabaseFilteredDownloadItems(context);if(items.Count==0)throw new GalleryError(400,"No entries match the filters.");
   DownloadJob=new GalleryDownloadJob{id=Guid.NewGuid().ToString("N"),phase="queued",entries=items};SaveQueue();EnsureDownloadWorker();return QueueSummary();
  }
 }
 class MetadataCacheItem {public DateTime expires;public object value;}
 public static object DatabaseEntryFiles(HttpContext context){
  long id,after;if(!Int64.TryParse(context.Request.QueryString["entry_id"],out id))throw new GalleryError(400,"Choose an entry.");if(!Int64.TryParse(context.Request.QueryString["after"],out after))after=0;
  using(var db=OpenDatabase()){
   var s=BrowseScope(context,db,false);string entry=Param(s.args,id);
   if(DBRows(db,null,"SELECT e.id FROM inventory_entries e JOIN collections c ON c.id=e.collection_id WHERE "+s.where+" AND e.id="+entry,s.args.ToArray()).Count==0)throw new GalleryError(404,"Entry not found.");
   var args=new List<object>();string owners=s.owners.Count==0?"NULL":InParams(args,s.owners.Cast<object>());
   string sql="SELECT l.id,f.relative_path,c.collection_key FROM fm_entry_file_projection l JOIN fm_file_catalog original ON original.id=l.file_id JOIN fm_file_catalog f ON f.id=COALESCE(original.canonical_file_id,original.id) JOIN collections c ON c.id=f.collection_id JOIN fm_collection_discovery m ON m.collection_id=c.id AND m.is_marked=TRUE WHERE l.entry_id="+Param(args,id)+" AND l.id>"+Param(args,after)+" AND f.is_present=TRUE AND c.id IN ("+owners+")";
   if(!s.admin)sql+=" AND NOT EXISTS(SELECT 1 FROM fm_file_dirty pending WHERE pending.file_id=f.id) AND f.access_category IN ("+(s.categories.Length==0?"NULL":InParams(args,s.categories.Cast<object>()))+")";
   var page=DBRows(db,null,sql+" ORDER BY l.id LIMIT 101",args.ToArray());bool more=page.Count>100;if(more)page.RemoveAt(100);
   var links=page.Select(f=>{string owner=Text(f,"collection_key"),path=Text(f,"relative_path"),name=owner=="."?path:path.Substring(owner.Length+1);return new{collection=owner,file=name,url=MediaUrl(new Collection{Id=owner},name)};}).ToArray();
   return new{links=links,next_after=more&&page.Count>0?(object)page.Last()["id"]:null};
  }
 }
 static readonly object MetadataCacheLock=new object();static Dictionary<string,MetadataCacheItem> MetadataCache=new Dictionary<string,MetadataCacheItem>();
 public static object DatabaseInventoryMetadata(HttpContext context){
  using(var db=OpenDatabase()){
   var s=BrowseScope(context,db,false);string key=s.fingerprint;
   lock(MetadataCacheLock){MetadataCacheItem old;if(MetadataCache.TryGetValue(key,out old)&&old.expires>DateTime.UtcNow)return old.value;}
   // Metadata is cached independently; browsing never waits for a full JSON snapshot.
   var stats=DBRows(db,null,"SELECT c.collection_key,e.site,e.status,COUNT(*) n FROM inventory_entries e JOIN collections c ON c.id=e.collection_id WHERE "+s.where+" GROUP BY c.collection_key,e.site,e.status",s.args.ToArray());
   var active=DBRows(db,null,"SELECT c.collection_key FROM collections c JOIN fm_collection_discovery m ON m.collection_id=c.id WHERE m.is_marked=TRUE AND c.id IN ("+(s.owners.Count==0?"NULL":String.Join(",",s.owners))+") ORDER BY c.collection_key");
   var collections=active.Select(c=>new{id=Text(c,"collection_key"),records=stats.Where(x=>Text(x,"collection_key")==Text(c,"collection_key")).Sum(x=>Convert.ToInt64(x["n"]))}).Where(c=>s.admin||c.records>0).ToArray();
   string owners=s.owners.Count==0?"NULL":String.Join(",",s.owners);var savedArgs=new List<object>();string savedSql="SELECT COUNT(*) n FROM fm_file_catalog f WHERE f.is_present=TRUE AND f.canonical_file_id IS NULL AND f.collection_id IN ("+owners+") AND EXISTS(SELECT 1 FROM inventory_entries e WHERE e.file_id=f.id AND e.deleted_at IS NULL)";if(!s.admin)savedSql+=" AND NOT EXISTS(SELECT 1 FROM fm_file_dirty pending WHERE pending.file_id=f.id) AND f.access_category IN ("+(s.categories.Length==0?"NULL":InParams(savedArgs,s.categories.Cast<object>()))+")";long saved=Convert.ToInt64(DBRows(db,null,savedSql,savedArgs.ToArray())[0]["n"]);
   var result=new{saved=saved,collections=collections,sites=stats.Select(x=>Text(x,"site")).Distinct().OrderBy(x=>x).ToArray(),statuses=stats.Select(x=>Text(x,"status")).Concat(new[]{"saved"}).Distinct().OrderBy(x=>x).ToArray(),categories=s.categories,total=stats.Sum(x=>Convert.ToInt64(x["n"])),paged=true};
   lock(MetadataCacheLock){if(MetadataCache.Count>100)MetadataCache.Clear();MetadataCache[key]=new MetadataCacheItem{value=result,expires=DateTime.UtcNow.AddSeconds(60)};}return result;
  }
 }
 public static object DatabaseDeleteEntry(Dictionary<string,object> payload){
  long id,revision;if(!Int64.TryParse(Text(payload,"entry_id"),out id)||!Int64.TryParse(Text(payload,"revision"),out revision))throw new GalleryError(400,"Choose an entry to delete.");
  using(var db=OpenDatabase())using(var tx=db.BeginTransaction()){
   var rows=DBRows(db,tx,"SELECT raw_record,revision FROM inventory_entries WHERE id=@id AND deleted_at IS NULL FOR UPDATE","@id",id);
   if(rows.Count!=1||Convert.ToInt64(rows[0]["revision"])!=revision)throw new GalleryError(409,"This entry changed. Refresh before deleting.");
   DBExec(db,tx,"UPDATE inventory_entries SET deleted_at=UTC_TIMESTAMP(6),is_hidden=TRUE WHERE id=@id AND revision=@revision","@id",id,"@revision",revision);
   DBExec(db,tx,"INSERT INTO inventory_entry_history(entry_id,revision,action,previous_record,actor) VALUES(@id,@revision,'delete',@raw,'IIS gallery')","@id",id,"@revision",revision+1,"@raw",rows[0]["raw_record"]);tx.Commit();return new{status="deleted",files_deleted=0};
  }
 }
 public static object DatabaseSetTags(Dictionary<string,object> payload){
  long id,revision;if(!Int64.TryParse(Text(payload,"entry_id"),out id)||!Int64.TryParse(Text(payload,"revision"),out revision))throw new GalleryError(400,"Choose an entry.");
  object raw;if(!payload.TryGetValue("tags",out raw))throw new GalleryError(400,"Provide tag terms.");string[] terms;
  try{terms=Json.Deserialize<string[]>(Json.Serialize(raw)).Select(t=>t.Trim()).Where(t=>t!="").Distinct().ToArray();}catch{throw new GalleryError(400,"Provide a list of tag terms.");}
  if(terms.Length>500||terms.Any(t=>t.Length>512))throw new GalleryError(400,"Use at most 500 tags of at most 512 characters.");
  using(var db=OpenDatabase())using(var tx=db.BeginTransaction()){
   var entry=DBRows(db,tx,"SELECT revision FROM inventory_entries WHERE id=@id AND deleted_at IS NULL FOR UPDATE","@id",id);if(entry.Count!=1||Convert.ToInt64(entry[0]["revision"])!=revision)throw new GalleryError(409,"This entry changed. Refresh before editing tags.");
   // Record the raw-input fingerprint before accepting a relational correction.
   // The worker preserves these relations until the source tag value changes.
   if(DBRows(db,tx,"SELECT entry_id FROM fm_record_index WHERE entry_id=@id","@id",id).Count==0)throw new GalleryError(409,"This entry is still being indexed. Retry shortly.");
   DBExec(db,tx,"DELETE FROM inventory_entry_tags WHERE entry_id=@id","@id",id);
   DBExec(db,tx,"DELETE FROM fm_tag_import_issues WHERE entry_id=@id","@id",id);
   foreach(string term in terms){DBExec(db,tx,"INSERT INTO tags(term) VALUES(@term) ON DUPLICATE KEY UPDATE id=LAST_INSERT_ID(id)","@term",term);long tag=Convert.ToInt64(DBRows(db,tx,"SELECT LAST_INSERT_ID() id")[0]["id"]);DBExec(db,tx,"INSERT INTO inventory_entry_tags(entry_id,tag_id) VALUES(@entry,@tag)","@entry",id,"@tag",tag);}
   DBExec(db,tx,"UPDATE inventory_entries SET revision=revision+1 WHERE id=@id","@id",id);
   DBExec(db,tx,"INSERT INTO inventory_entry_history(entry_id,revision,action,details,actor) SELECT id,revision,'tags_updated',JSON_OBJECT('terms',CAST(@terms AS JSON)),'IIS gallery' FROM inventory_entries WHERE id=@id","@id",id,"@terms",Json.Serialize(terms));tx.Commit();return new{status="updated",entry_id=id,revision=revision+1,tags=terms};
  }
 }
 static int PayloadIndex(Collection collection,Dictionary<string,object> payload){
  long id;if(DatabaseEnabled&&Int64.TryParse(Text(payload,"entry_id"),out id)){
   int found=collection.Records.FindIndex(r=>Text(r,"_db_id")==id.ToString());if(found<0)throw new GalleryError(409,"This entry moved or changed. Refresh and retry.");
   long expected;if(Int64.TryParse(Text(payload,"revision"),out expected)&&expected!=Convert.ToInt64(collection.Records[found]["_db_revision"]))throw new GalleryError(409,"This entry changed. Refresh and retry.");return found;
  }
  int index;if(!Int32.TryParse(Text(payload,"index"),out index)||index<0||index>=collection.Records.Count)throw new GalleryError(409,"This entry changed. Refresh and retry.");return index;
 }
 static Dictionary<string,object> DatabaseActionRecord(Dictionary<string,object> payload){
  long id,revision;if(!Int64.TryParse(Text(payload,"entry_id"),out id)||!Int64.TryParse(Text(payload,"revision"),out revision))throw new GalleryError(400,"Choose an entry.");
  using(var db=OpenDatabase()){
   var rows=DBRows(db,null,"SELECT raw_record,revision,site,source_id,record_type,status,category,description,source_url,image_url,resolution FROM inventory_entries WHERE id=@id AND deleted_at IS NULL","@id",id);
   if(rows.Count!=1||Convert.ToInt64(rows[0]["revision"])!=revision)throw new GalleryError(409,"This entry changed. Refresh and retry.");
   var row=rows[0];var record=Json.Deserialize<Dictionary<string,object>>(Text(row,"raw_record"));foreach(string field in new[]{"site","record_type","status","category","description","resolution"})record[field]=row[field];record["id"]=row["source_id"];record["source"]=row["source_url"];record["url"]=row["image_url"];return record;
  }
 }
 public static bool DatabaseImageAccessAllowed(HttpContext context,string collection,string file){
  string relative=AccessRelativePath(collection,file);if(relative==null||!CollectionVisible(context,collection))return false;
  using(var db=OpenDatabase()){
   var found=DBRows(db,null,"SELECT f.access_category,c.collection_key FROM fm_file_catalog f JOIN collections c ON c.id=f.collection_id JOIN fm_collection_discovery m ON m.collection_id=c.id AND m.is_marked=TRUE WHERE f.path_key=@key AND f.is_present=TRUE AND f.canonical_file_id IS NULL AND NOT EXISTS(SELECT 1 FROM fm_file_dirty pending WHERE pending.file_id=f.id)","@key",DatabaseHash(relative.ToLowerInvariant()));
   return found.Count==1&&CollectionVisible(context,Text(found[0],"collection_key"))&&GalleryAuth.Categories(context).Contains(Text(found[0],"access_category"));
  }
 }
 public static object DatabaseLinkEntries(Dictionary<string,object> payload){
  var local=payload.ContainsKey("local")?payload["local"] as Dictionary<string,object>:null;var target=payload.ContainsKey("target")?payload["target"] as Dictionary<string,object>:null;
  long lid,tid,lr,tr;if(local==null||target==null||!Int64.TryParse(Text(local,"entry_id"),out lid)||!Int64.TryParse(Text(target,"entry_id"),out tid)||!Int64.TryParse(Text(local,"revision"),out lr)||!Int64.TryParse(Text(target,"revision"),out tr)||lid==tid)throw new GalleryError(400,"Choose two different entries.");
  using(var db=OpenDatabase())using(var tx=db.BeginTransaction()){
   var entries=DBRows(db,tx,"SELECT id,revision,site,record_type,raw_record FROM inventory_entries WHERE id IN (@l,@t) AND deleted_at IS NULL ORDER BY id FOR UPDATE","@l",lid,"@t",tid);
   if(entries.Count!=2||Convert.ToInt64(entries.First(e=>Convert.ToInt64(e["id"])==lid)["revision"])!=lr||Convert.ToInt64(entries.First(e=>Convert.ToInt64(e["id"])==tid)["revision"])!=tr)throw new GalleryError(409,"An entry changed. Refresh before linking.");
   var source=entries.First(e=>Convert.ToInt64(e["id"])==lid);var destination=entries.First(e=>Convert.ToInt64(e["id"])==tid);bool post=Text(destination,"record_type")!="image";
   if(!new[]{"image","post","album","post_or_album"}.Contains(Text(destination,"record_type")))throw new GalleryError(400,"Choose an image or post.");
   var files=DBRows(db,tx,"SELECT f.id,f.relative_path,c.collection_key FROM fm_entry_file_projection l JOIN fm_file_catalog original ON original.id=l.file_id JOIN fm_file_catalog f ON f.id=COALESCE(original.canonical_file_id,original.id) JOIN collections c ON c.id=f.collection_id WHERE l.entry_id=@id AND f.is_present=TRUE ORDER BY FIELD(l.reference_kind,'primary','duplicate','related','saved'),l.position LIMIT 1","@id",lid);
   if(files.Count==0)throw new GalleryError(404,"The local file is missing.");var f=files[0];
   var existing=DBRows(db,tx,"SELECT COALESCE(f.canonical_file_id,f.id) id FROM fm_entry_file_projection l JOIN fm_file_catalog f ON f.id=l.file_id WHERE l.entry_id=@id AND l.reference_kind IN ('primary','duplicate') AND f.is_present=TRUE","@id",tid);
   if(!post&&existing.Any(x=>Convert.ToInt64(x["id"])!=Convert.ToInt64(f["id"])))throw new GalleryError(409,"The target image already has another file. Choose its post or album instead.");
   string owner=Text(f,"collection_key"),path=Text(f,"relative_path"),name=owner=="."?path:path.Substring(owner.Length+1),kind=post?"related":"primary";
   if(post){
    if(Text(source,"record_type")!="image")throw new GalleryError(400,"Choose an individual image for the child entry.");
    DBExec(db,tx,"INSERT IGNORE INTO inventory_entry_relations(entry_id,related_entry_id,relation_kind) VALUES(@parent,@child,'parent_of')","@parent",tid,"@child",lid);
   }else{
    DBExec(db,tx,"CALL fm_bind_entry_file(@entry,@file)","@entry",tid,"@file",f["id"]);
    DBExec(db,tx,"INSERT IGNORE INTO inventory_entry_relations(entry_id,related_entry_id,relation_kind) VALUES(@duplicate,@original,'duplicate_of')","@duplicate",lid,"@original",tid);
   }
   DBExec(db,tx,"UPDATE inventory_entries SET status='saved',is_hidden=FALSE,canonical_entry_id=NULL WHERE id=@id","@id",tid);
   if(!post&&Text(source,"site")=="Local")DBExec(db,tx,"UPDATE inventory_entries SET deleted_at=UTC_TIMESTAMP(6),is_hidden=TRUE WHERE id=@id","@id",lid);
   DBExec(db,tx,"INSERT INTO inventory_entry_history(entry_id,revision,action,previous_record,details,actor) SELECT id,revision,'manual_file_link',@raw,JSON_OBJECT('file_id',@file,'local_entry_id',@local),'IIS gallery' FROM inventory_entries WHERE id=@id","@id",tid,"@file",f["id"],"@local",lid,"@raw",destination["raw_record"]);
   if(!post&&Text(source,"site")=="Local")DBExec(db,tx,"INSERT INTO inventory_entry_history(entry_id,revision,action,previous_record,details,actor) SELECT id,revision,'manual_entry_linked',@raw,JSON_OBJECT('target_entry_id',@target),'IIS gallery' FROM inventory_entries WHERE id=@id","@id",lid,"@target",tid,"@raw",source["raw_record"]);
   tx.Commit();return new{status="linked",files_deleted=0};
  }
 }
 public static object DatabaseMoveEntry(Dictionary<string,object> payload){
  long id,revision;if(!Int64.TryParse(Text(payload,"entry_id"),out id)||!Int64.TryParse(Text(payload,"revision"),out revision))throw new GalleryError(400,"Choose an entry.");
  string target=Text(payload,"target_collection");
  using(var db=OpenDatabase())using(var tx=db.BeginTransaction()){
   var records=DBRows(db,tx,"SELECT id,revision,inventory_document_id,collection_id FROM inventory_entries WHERE id=@id AND deleted_at IS NULL FOR UPDATE","@id",id);
   if(records.Count!=1||Convert.ToInt64(records[0]["revision"])!=revision)throw new GalleryError(409,"This entry changed. Refresh before moving it.");
   var destinations=DBRows(db,tx,"SELECT c.id,d.id doc FROM collections c JOIN fm_collection_discovery m ON m.collection_id=c.id AND m.is_marked=TRUE JOIN inventory_documents d ON d.collection_id=c.id WHERE c.collection_key=@target","@target",target);
   if(destinations.Count!=1)throw new GalleryError(400,"Choose an existing marked collection.");
   if(Convert.ToInt64(destinations[0]["id"])==Convert.ToInt64(records[0]["collection_id"]))throw new GalleryError(400,"This entry is already in that collection.");
   var active=DBRows(db,tx,"SELECT id FROM fm_collection_moves WHERE entry_id=@id AND state IN ('pending','running') LIMIT 1","@id",id);
   if(active.Count>0)throw new GalleryError(409,"A move is already queued for this entry.");
   var retry=DBRows(db,tx,"SELECT id FROM fm_collection_moves WHERE entry_id=@id AND source_collection_id=@doc AND target_collection_id=@target AND state='failed' ORDER BY id DESC LIMIT 1","@id",id,"@doc",records[0]["collection_id"],"@target",destinations[0]["id"]);
   if(retry.Count>0){long previous=Convert.ToInt64(retry[0]["id"]);DBExec(db,tx,"UPDATE fm_collection_moves SET state='pending',error=NULL WHERE id=@id","@id",previous);tx.Commit();return new{move_id=previous,state="pending"};}
   DBExec(db,tx,"INSERT INTO fm_collection_moves(entry_id,source_document_id,source_collection_id,target_collection_id) VALUES(@entry,@doc,@source,@target)","@entry",id,"@doc",records[0]["inventory_document_id"],"@source",records[0]["collection_id"],"@target",destinations[0]["id"]);
   long job=Convert.ToInt64(DBRows(db,tx,"SELECT LAST_INSERT_ID() id")[0]["id"]);
   DBExec(db,tx,"INSERT INTO fm_collection_move_files(move_id,file_id) SELECT DISTINCT @move,COALESCE(f.canonical_file_id,f.id) FROM fm_entry_file_projection l JOIN fm_file_catalog f ON f.id=l.file_id WHERE l.entry_id=@entry","@move",job,"@entry",id);
   tx.Commit();return new{move_id=job,state="pending"};
  }
 }
 public static object DatabaseMoveStatus(HttpContext context){
  long id;if(!Int64.TryParse(context.Request.QueryString["id"],out id))throw new GalleryError(400,"Choose a move.");
  using(var db=OpenDatabase()){
   var jobs=DBRows(db,null,"SELECT m.id,m.entry_id,m.state,m.error,(SELECT revision FROM inventory_entries e WHERE e.id=m.entry_id) current_revision,c.collection_key target_collection,(SELECT COUNT(*) FROM fm_collection_move_files f WHERE f.move_id=m.id) total,(SELECT COUNT(*) FROM fm_collection_move_files f WHERE f.move_id=m.id AND f.state='completed') processed FROM fm_collection_moves m JOIN inventory_entries current_entry ON current_entry.id=m.entry_id JOIN collections c ON c.id=COALESCE(m.target_collection_id,current_entry.collection_id) WHERE m.id=@id","@id",id);
   if(jobs.Count!=1)throw new GalleryError(404,"Move not found.");if(Text(jobs[0],"state")=="completed")lock(MetadataCacheLock){MetadataCache.Clear();}return jobs[0];
  }
 }
 static string CsvValue(object value){string text=Convert.ToString(value)??"";if(text.Length>0&&"=+-@".IndexOf(text[0])>=0)text="'"+text;return "\""+text.Replace("\"","\"\"")+"\"";}
 public static void DatabaseExportCsv(HttpContext context){
  context.Server.ScriptTimeout=1800;context.Response.ContentType="text/csv; charset=utf-8";context.Response.AppendHeader("Content-Disposition","attachment; filename=wallpaper-inventory.csv");context.Response.BufferOutput=false;context.Response.Write("\ufeffentry_id,collection,site,id,status,category,record_type,resolution,source,url,description\r\n");
  using(var db=OpenDatabase()){
   var scope=BrowseScope(context,db,true);long last=0;
   while(context.Response.IsClientConnected){
    var args=new List<object>(scope.args);string p=Param(args,last);
    var page=DBRows(db,null,"SELECT e.id,c.collection_key,e.site,e.source_id,e.status,e.category,e.record_type,e.resolution,e.source_url,e.image_url,e.description FROM inventory_entries e JOIN collections c ON c.id=e.collection_id WHERE "+scope.where+" AND e.id>"+p+" ORDER BY e.id LIMIT 500",args.ToArray());
    if(page.Count==0)break;foreach(var row in page)context.Response.Write(String.Join(",",new[]{"id","collection_key","site","source_id","status","category","record_type","resolution","source_url","image_url","description"}.Select(k=>CsvValue(row[k])))+"\r\n");last=Convert.ToInt64(page.Last()["id"]);context.Response.Flush();
   }
  }
 }
 public static void DatabaseExportJson(HttpContext context){
  context.Server.ScriptTimeout=1800;context.Response.ContentType="application/json; charset=utf-8";context.Response.AppendHeader("Content-Disposition","attachment; filename=wallpaper-inventory.json");context.Response.BufferOutput=false;context.Response.Write("[");bool first=true;
  using(var db=OpenDatabase()){
   var scope=BrowseScope(context,db,true);long last=0;
   while(context.Response.IsClientConnected){
    var args=new List<object>(scope.args);string p=Param(args,last);
    var page=DBRows(db,null,"SELECT e.*,c.collection_key FROM inventory_entries e JOIN collections c ON c.id=e.collection_id WHERE "+scope.where+" AND e.id>"+p+" ORDER BY e.id LIMIT 25",args.ToArray());
    if(page.Count==0)break;
    foreach(var row in page){
     var r=Json.Deserialize<Dictionary<string,object>>(Text(row,"raw_record"));
     foreach(string field in new[]{"site","record_type","status","category","description","resolution"}){if(row[field]!=null)r[field]=row[field];else r.Remove(field);}r["id"]=row["source_id"];r["source"]=row["source_url"];r["url"]=row["image_url"];
     foreach(string field in new[]{"file","file_collection","duplicate_of","duplicate_collection","related_image_files","related_image_references","saved_image_references"})r.Remove(field);
     var links=DBRows(db,null,"SELECT l.reference_kind,SUBSTRING_INDEX(COALESCE(fc.relative_path,f.relative_path),'/',-1) recorded_path,COALESCE(cc.collection_key,c.collection_key) recorded_collection,COALESCE(fc.relative_path,f.relative_path) actual_path,COALESCE(cc.collection_key,c.collection_key) owner FROM fm_entry_file_projection l LEFT JOIN fm_file_catalog f ON f.id=l.file_id LEFT JOIN collections c ON c.id=f.collection_id LEFT JOIN fm_file_catalog fc ON fc.id=f.canonical_file_id LEFT JOIN collections cc ON cc.id=fc.collection_id WHERE l.entry_id=@id ORDER BY l.reference_kind,l.position","@id",row["id"]);
     foreach(var group in links.GroupBy(l=>Text(l,"reference_kind"))){var refs=new List<object>();foreach(var link in group){string owner=Text(link,"owner"),name=Text(link,"recorded_path"),actual=Text(link,"actual_path");if(owner=="")owner=Text(link,"recorded_collection");if(actual!=""&&owner!="")name=owner=="."?actual:actual.Substring(owner.Length+1);if(group.Key=="primary"||group.Key=="duplicate"){r[group.Key=="primary"?"file":"duplicate_of"]=name;r[group.Key=="primary"?"file_collection":"duplicate_collection"]=owner;}else refs.Add(new{collection=owner,file=name});}if(refs.Count>0)r[group.Key=="saved"?"saved_image_references":"related_image_references"]=refs;}
     var tags=DBRows(db,null,"SELECT t.id,t.term FROM inventory_entry_tags et JOIN tags t ON t.id=et.tag_id WHERE et.entry_id=@id ORDER BY t.id","@id",row["id"]);if(tags.Count>0){r["tags"]=tags.Select(t=>t["term"]).ToArray();r["tag_ids"]=tags.Select(t=>t["id"]).ToArray();}
     if(!first)context.Response.Write(",");context.Response.Write(Json.Serialize(r));first=false;
    }
    last=Convert.ToInt64(page.Last()["id"]);context.Response.Flush();
   }
  }context.Response.Write("]");
 }
 public static object DatabaseIntegrationStatus(){using(var db=OpenDatabase())return new{integrations=DBRows(db,null,"SELECT integration_key,database_name,enabled,status FROM fm_adapter_config ORDER BY integration_key"),pending_entries=DBRows(db,null,"SELECT COUNT(*) n FROM fm_entry_dirty")[0]["n"],tag_issues=DBRows(db,null,"SELECT COUNT(*) n FROM fm_tag_import_issues")[0]["n"]};}
 public static object DatabaseChanges(HttpContext context){long after;if(!Int64.TryParse(context.Request.QueryString["after"],out after))after=0;using(var db=OpenDatabase()){var page=DBRows(db,null,"SELECT id,event_kind,asset_id,asset_revision,replacement_asset_id,location_id,created_at FROM fm_change_events WHERE id>@after ORDER BY id LIMIT 101","@after",after);bool more=page.Count>100;if(more)page.RemoveAt(100);return new{events=page,next_after=page.Count>0?page.Last()["id"]:(object)after,has_more=more};}}
 public static object DatabaseAsset(HttpContext context){
  long requested;if(!Int64.TryParse(context.Request.QueryString["id"],out requested))throw new GalleryError(400,"Choose an asset.");
  using(var db=OpenDatabase()){
   long id=requested;var seen=new HashSet<long>();Dictionary<string,object> asset=null;
   for(int depth=0;depth<32;depth++){if(!seen.Add(id))throw new GalleryError(503,"Asset references need repair.");var found=DBRows(db,null,"SELECT id,canonical_asset_id,revision,width,height FROM fm_assets WHERE id=@id","@id",id);if(found.Count==0)throw new GalleryError(404,"Asset not found.");asset=found[0];if(asset["canonical_asset_id"]==null)break;id=Convert.ToInt64(asset["canonical_asset_id"]);}
   if(asset==null||asset["canonical_asset_id"]!=null)throw new GalleryError(503,"Asset references need repair.");
   var links=new List<object>();foreach(var f in DBRows(db,null,"SELECT f.id,f.relative_path,f.access_category,c.collection_key FROM fm_asset_locations l JOIN fm_file_catalog f ON f.id=l.legacy_file_id JOIN collections c ON c.id=f.collection_id JOIN fm_collection_discovery m ON m.collection_id=c.id AND m.is_marked=TRUE WHERE l.asset_id=@id AND f.is_present=TRUE AND f.canonical_file_id IS NULL AND NOT EXISTS(SELECT 1 FROM fm_file_dirty pending WHERE pending.file_id=f.id) ORDER BY l.is_preferred DESC,l.id LIMIT 100","@id",id)){
    string owner=Text(f,"collection_key");if(!GalleryAuth.IsAdmin(context)&&(!CollectionVisible(context,owner)||!GalleryAuth.Categories(context).Contains(Text(f,"access_category"))))continue;string path=Text(f,"relative_path"),name=owner=="."?path:path.Substring(owner.Length+1);links.Add(new{collection=owner,file=name,url=MediaUrl(new Collection{Id=owner},name)});
   }
   if(links.Count==0&&!GalleryAuth.IsAdmin(context))throw new GalleryError(404,"Asset not found.");return new{requested_id=requested,asset_id=id,revision=asset["revision"],width=asset["width"],height=asset["height"],files=links};
  }
 }
}
