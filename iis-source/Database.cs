using System;using System.IO;using System.Linq;using System.Text;using System.Collections;using System.Collections.Generic;using System.Configuration;using System.Security.Cryptography;using System.Web;using MySqlConnector;
public static partial class Gallery {
 public static bool DatabaseEnabled {get{return ConfigurationManager.AppSettings["DatabaseEnabled"]=="true";}}
 static string DatabaseConfig {get{return ConfigurationManager.AppSettings["DatabaseConfig"]??@"C:\downloads\gallery\iis-data\mysql.json";}}
 public static MySqlConnection OpenDatabase(){
  var cfg=Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(DatabaseConfig));
  var b=new MySqlConnectionStringBuilder {Server=Text(cfg,"host"),Database=Text(cfg,"database"),UserID=Text(cfg,"user"),Password=Text(cfg,"password"),SslMode=MySqlSslMode.VerifyFull,TlsVersion="TLS 1.2",SslCa=Text(cfg,"ca"),ConnectionTimeout=10,DefaultCommandTimeout=30,MaximumPoolSize=20};
  var connection=new MySqlConnection(b.ConnectionString);connection.Open();return connection;
 }
 static MySqlCommand DBCommand(MySqlConnection db,MySqlTransaction tx,string sql,object[] args){var cmd=db.CreateCommand();cmd.Transaction=tx;cmd.CommandText=sql;for(int i=0;i<args.Length;i+=2)cmd.Parameters.AddWithValue((string)args[i],args[i+1]??DBNull.Value);return cmd;}
 static List<Dictionary<string,object>> DBRows(MySqlConnection db,MySqlTransaction tx,string sql,params object[] args){using(var cmd=DBCommand(db,tx,sql,args))using(var reader=cmd.ExecuteReader()){var rows=new List<Dictionary<string,object>>();while(reader.Read()){var row=new Dictionary<string,object>();for(int i=0;i<reader.FieldCount;i++)row[reader.GetName(i)]=reader.IsDBNull(i)?null:reader.GetValue(i);rows.Add(row);}return rows;}}
 static int DBExec(MySqlConnection db,MySqlTransaction tx,string sql,params object[] args){using(var cmd=DBCommand(db,tx,sql,args))return cmd.ExecuteNonQuery();}
 static string CleanRecord(Dictionary<string,object> row){return Json.Serialize(row.Where(p=>!p.Key.StartsWith("_db_")).ToDictionary(p=>p.Key,p=>p.Value));}
 static byte[] DatabaseHash(string value){using(var sha=SHA256.Create())return sha.ComputeHash(Encoding.UTF8.GetBytes(value));}
 static HashSet<string> DiscoveredCollectionKeys(){
  var value=DatabaseState("collection_markers") as Dictionary<string,object>;if(value==null||!value.ContainsKey("collections"))return null;
  return new HashSet<string>(Json.Deserialize<string[]>(Json.Serialize(value["collections"])),StringComparer.OrdinalIgnoreCase);
 }
 static List<Collection> DatabaseCollections(string selected=null){
  var active=DiscoveredCollectionKeys();
  using(var db=OpenDatabase()){
   var collections=new List<Collection>();var entries=new Dictionary<long,Dictionary<string,object>>();var owners=new Dictionary<long,Collection>();var collectionKeys=DBRows(db,null,"SELECT id,collection_key FROM collections").ToDictionary(x=>Convert.ToInt64(x["id"]),x=>Text(x,"collection_key"));
   foreach(var doc in DBRows(db,null,"SELECT d.id,c.collection_key,d.relative_path FROM inventory_documents d JOIN collections c ON c.id=d.collection_id "+(selected==null?"":" WHERE c.collection_key=@selected")+" ORDER BY c.collection_key,d.id","@selected",selected)){
    string key=Text(doc,"collection_key");if(active!=null&&!active.Contains(key))continue;var c=new Collection{Id=key,Folder=Path.GetFullPath(Path.Combine(Root,key.Replace('/','\\'))),Inventory=Path.Combine(Root,Text(doc,"relative_path").Replace('/','\\')),Records=new List<Dictionary<string,object>>(),CacheFiles=false,DatabaseFiles=new Dictionary<string,GalleryFileReference>(StringComparer.OrdinalIgnoreCase),DatabaseDocumentId=Convert.ToInt64(doc["id"])};
    if(!Under(c.Folder,Root)&&c.Folder!=Root)throw new InvalidDataException("Database collection path is outside the wallpaper root.");collections.Add(c);owners[c.DatabaseDocumentId]=c;
   }
   foreach(var file in DBRows(db,null,"SELECT f.relative_path,c.collection_key FROM fm_file_catalog f JOIN collections c ON c.id=f.collection_id WHERE f.is_present=TRUE AND f.canonical_file_id IS NULL"+(selected==null?"":" AND c.collection_key=@selected"),"@selected",selected)){var owner=collections.FirstOrDefault(c=>c.Id==Text(file,"collection_key"));if(owner==null)continue;string relative=Text(file,"relative_path"),name=owner.Id=="."?relative:relative.Substring(owner.Id.Length+1),path=Path.GetFullPath(Path.Combine(Root,relative.Replace('/','\\')));if(!Under(path,Root))continue;owner.DatabaseFiles[path]=new GalleryFileReference{Owner=owner,Name=name,Path=path};}
   foreach(var item in DBRows(db,null,"SELECT id,inventory_document_id,collection_id,revision,raw_record,site,source_id,record_type,status,category,description,source_url,image_url,resolution,is_hidden FROM inventory_entries WHERE deleted_at IS NULL"+(selected==null?"":" AND collection_id IN (SELECT id FROM collections WHERE collection_key=@selected)")+" ORDER BY inventory_document_id,source_ordinal,id","@selected",selected)){
    Collection c;c=collections.FirstOrDefault(x=>x.Id==collectionKeys[Convert.ToInt64(item["collection_id"])]);if(c==null)continue;
    var r=Json.Deserialize<Dictionary<string,object>>(Text(item,"raw_record"));
    foreach(string field in new[]{"site","record_type","status","category","description","resolution"}){if(item[field]!=null)r[field]=item[field];else r.Remove(field);}
    r["id"]=Text(item,"source_id");if(item["source_url"]!=null)r["source"]=item["source_url"];else r.Remove("source");if(item["image_url"]!=null)r["url"]=item["image_url"];else r.Remove("url");
    foreach(string field in new[]{"file","file_collection","duplicate_of","duplicate_collection","related_image_files","related_image_references","saved_image_references"})r.Remove(field);
    r["_db_id"]=Convert.ToInt64(item["id"]);r["_db_revision"]=Convert.ToInt64(item["revision"]);r["_db_hidden"]=Convert.ToBoolean(item["is_hidden"]);entries[(long)r["_db_id"]]=r;c.Records.Add(r);
   }
   var links=DBRows(db,null,"SELECT l.entry_id,l.reference_kind,l.position,COALESCE(fc.relative_path,f.relative_path) AS actual_path,SUBSTRING_INDEX(COALESCE(fc.relative_path,f.relative_path),'/',-1) recorded_path,COALESCE(cc.collection_key,c.collection_key) recorded_collection,COALESCE(cc.collection_key,c.collection_key) AS owner FROM fm_entry_file_projection l LEFT JOIN fm_file_catalog f ON f.id=l.file_id LEFT JOIN collections c ON c.id=f.collection_id LEFT JOIN fm_file_catalog fc ON fc.id=f.canonical_file_id LEFT JOIN collections cc ON cc.id=fc.collection_id "+(selected==null?"":" WHERE l.entry_id IN (SELECT e.id FROM inventory_entries e JOIN collections c ON c.id=e.collection_id WHERE c.collection_key=@selected)")+" ORDER BY l.entry_id,l.reference_kind,l.position","@selected",selected);
   foreach(var group in links.GroupBy(l=>Convert.ToInt64(l["entry_id"]))){Dictionary<string,object> r;if(!entries.TryGetValue(group.Key,out r))continue;
    foreach(var kind in group.GroupBy(l=>Text(l,"reference_kind"))){var pointers=new List<object>();foreach(var link in kind){string owner=Text(link,"owner"),name=Text(link,"recorded_path"),actual=Text(link,"actual_path");if(owner=="")owner=Text(link,"recorded_collection");if(actual!=""&&owner!=""&&actual.StartsWith(owner+"/",StringComparison.OrdinalIgnoreCase))name=actual.Substring(owner.Length+1);if(kind.Key=="primary"||kind.Key=="duplicate"){string field=kind.Key=="primary"?"file":"duplicate_of";r[field]=name;r[kind.Key=="primary"?"file_collection":"duplicate_collection"]=owner;}else pointers.Add(new Dictionary<string,object>{{"collection",owner},{"file",name}});}if(pointers.Count>0)r[kind.Key=="saved"?"saved_image_references":"related_image_references"]=pointers.ToArray();}
   }
   foreach(var c in collections){c.References=collections.ToDictionary(x=>x.Id,StringComparer.OrdinalIgnoreCase);c.Original=Json.Serialize(c.Records);}return collections;
  }
 }
 static void SaveDatabase(Collection c){
  var originals=Json.Deserialize<List<Dictionary<string,object>>>(c.Original).Where(r=>Text(r,"_db_id")!="").ToDictionary(r=>Convert.ToInt64(r["_db_id"]));
  using(var db=OpenDatabase())using(var tx=db.BeginTransaction()){
   int ordinal=Convert.ToInt32(DBRows(db,tx,"SELECT COALESCE(MAX(source_ordinal),-1)+1 AS next FROM inventory_entries WHERE inventory_document_id=@d","@d",c.DatabaseDocumentId)[0]["next"]);
   var seen=new HashSet<long>();
   foreach(var row in c.Records){string raw=CleanRecord(row);long id=Text(row,"_db_id")==""?0:Convert.ToInt64(row["_db_id"]);Dictionary<string,object> old;
    if(id!=0){seen.Add(id);if(originals.TryGetValue(id,out old)&&raw==CleanRecord(old))continue;}
    bool hidden=Text(row,"status")=="duplicate"||Text(row,"_db_hidden")=="True";
    var args=new object[]{"@d",c.DatabaseDocumentId,"@ordinal",ordinal,"@site",Text(row,"site"),"@sourceid",Text(row,"id"),"@kind",Text(row,"record_type"),"@status",Text(row,"status"),"@category",Text(row,"category"),"@description",Text(row,"description"),"@source",Text(row,"source"),"@url",Text(row,"url"),"@resolution",Text(row,"resolution"),"@raw",raw,"@hash",DatabaseHash(raw),"@hidden",hidden,"@id",id,"@revision",id==0?0:Convert.ToInt64(row["_db_revision"])};
    if(id==0){DBExec(db,tx,"INSERT INTO inventory_entries(inventory_document_id,source_ordinal,site,source_id,record_type,status,category,description,source_url,image_url,resolution,raw_record,record_sha256,is_hidden) VALUES(@d,@ordinal,@site,@sourceid,@kind,@status,@category,@description,@source,@url,@resolution,@raw,@hash,@hidden)",args);id=Convert.ToInt64(DBRows(db,tx,"SELECT LAST_INSERT_ID() AS id")[0]["id"]);row["_db_id"]=id;row["_db_revision"]=1;ordinal++;}
    else{if(DBExec(db,tx,"UPDATE inventory_entries SET site=@site,source_id=@sourceid,record_type=@kind,status=@status,category=@category,description=@description,source_url=@source,image_url=@url,resolution=@resolution,raw_record=@raw,record_sha256=@hash,is_hidden=@hidden WHERE id=@id AND revision=@revision AND deleted_at IS NULL",args)!=1)throw new GalleryError(409,"This entry changed in the database. Refresh and retry.");row["_db_revision"]=Convert.ToInt64(row["_db_revision"])+1;}
    row["_db_hidden"]=hidden;
    DBExec(db,tx,"INSERT INTO inventory_entry_history(entry_id,revision,action,new_record,actor) VALUES(@id,@rev,'gallery_update',@raw,'IIS gallery')","@id",id,"@rev",row["_db_revision"],"@raw",raw);
    DatabaseFileLinks(db,tx,c,row,id);row["_db_revision"]=Convert.ToInt64(DBRows(db,tx,"SELECT revision FROM inventory_entries WHERE id=@id","@id",id)[0]["revision"]);
   }
   foreach(var old in originals.Where(p=>!c.Records.Any(r=>Text(r,"_db_id")==p.Key.ToString()))){if(DBExec(db,tx,"UPDATE inventory_entries SET deleted_at=UTC_TIMESTAMP(6),is_hidden=TRUE WHERE id=@id AND revision=@rev AND deleted_at IS NULL","@id",old.Key,"@rev",old.Value["_db_revision"])!=1)throw new GalleryError(409,"This entry changed in the database. Refresh and retry.");DBExec(db,tx,"INSERT INTO inventory_entry_history(entry_id,revision,action,previous_record,actor) VALUES(@id,@rev,'delete',@raw,'IIS gallery')","@id",old.Key,"@rev",Convert.ToInt64(old.Value["_db_revision"])+1,"@raw",CleanRecord(old.Value));}
   DBExec(db,tx,"INSERT INTO gallery_state(state_key,value) VALUES('worker_requested',JSON_OBJECT('requested',TRUE)) ON DUPLICATE KEY UPDATE value=VALUES(value)");tx.Commit();c.Original=Json.Serialize(c.Records);ViewExpires=DateTime.MinValue;
  }
 }
 static void DatabaseFileLinks(MySqlConnection db,MySqlTransaction tx,Collection c,Dictionary<string,object> row,long id){

  var refs=new List<Tuple<string,string,string>>();foreach(string field in new[]{"file","duplicate_of"})if(Text(row,field)!="")refs.Add(Tuple.Create(field=="file"?"primary":"duplicate",Text(row,field=="file"?"file_collection":"duplicate_collection")==""?c.Id:Text(row,field=="file"?"file_collection":"duplicate_collection"),Text(row,field)));
  foreach(string field in new[]{"related_image_references","saved_image_references","related_image_files"}){object raw;if(!row.TryGetValue(field,out raw)||!(raw is IEnumerable)||raw is string)continue;foreach(object item in (IEnumerable)raw){var pointer=item as Dictionary<string,object>;if(pointer!=null)refs.Add(Tuple.Create(field.StartsWith("saved")?"saved":"related",Text(pointer,"collection")==""?c.Id:Text(pointer,"collection"),Text(pointer,"file")));else if(item is string)refs.Add(Tuple.Create("related",c.Id,(string)item));}}
  bool rebound=false;var positions=new Dictionary<string,int>();foreach(var reference in refs.Distinct()){
   if(reference.Item3=="")continue;int pos;positions.TryGetValue(reference.Item1,out pos);positions[reference.Item1]=pos+1;string relative=((reference.Item2=="."?"":reference.Item2+"/")+reference.Item3).Replace('\\','/');var files=DBRows(db,tx,"SELECT COALESCE(canonical_file_id,id) AS id FROM fm_file_catalog WHERE path_key=@key","@key",DatabaseHash(relative.ToLowerInvariant()));
   if(files.Count==0){string path=Path.GetFullPath(Path.Combine(Root,relative.Replace('/','\\')));if(Under(path,Root)&&ImageFile(path)&&File.Exists(path)&&!Linked(path)){var info=new FileInfo(path);var owner=DBRows(db,tx,"SELECT id FROM collections WHERE collection_key=@c","@c",reference.Item2);if(owner.Count>0){DBExec(db,tx,"INSERT INTO files(relative_path,path_key,byte_size,modified_utc_ticks,is_present,last_seen_at) VALUES(@path,@key,@size,@ticks,TRUE,UTC_TIMESTAMP(6)) ON DUPLICATE KEY UPDATE byte_size=VALUES(byte_size),modified_utc_ticks=VALUES(modified_utc_ticks),is_present=TRUE","@c",owner[0]["id"],"@path",relative,"@key",DatabaseHash(relative.ToLowerInvariant()),"@size",info.Length,"@ticks",info.LastWriteTimeUtc.Ticks);files=DBRows(db,tx,"SELECT id FROM fm_file_catalog WHERE path_key=@key","@key",DatabaseHash(relative.ToLowerInvariant()));}}}
   if(files.Count>0){if(!rebound&&!new[]{"post","album","post_or_album"}.Contains(Text(row,"record_type"))){DBExec(db,tx,"UPDATE inventory_entries SET file_id=NULL WHERE id=@id","@id",id);rebound=true;}DBExec(db,tx,"CALL fm_bind_entry_file(@e,@f)","@e",id,"@f",files[0]["id"]);}
  }
 }
 static object DatabaseState(string key){using(var db=OpenDatabase()){var rows=DBRows(db,null,"SELECT value FROM gallery_state WHERE state_key=@key","@key",key);return rows.Count==0?null:Json.DeserializeObject(Text(rows[0],"value"));}}
 static object DatabaseInventoryView(){
  var active=DiscoveredCollectionKeys();
  using(var db=OpenDatabase()){
   var records=new List<Dictionary<string,object>>();var lookup=new Dictionary<long,Dictionary<string,object>>();
   var rows=DBRows(db,null,"SELECT e.*,c.collection_key FROM (SELECT id,inventory_document_id,collection_id,revision,site,source_id,record_type,status,category,description,source_url,image_url,resolution,is_hidden,ROW_NUMBER() OVER(PARTITION BY collection_id ORDER BY inventory_document_id,source_ordinal,id)-1 AS inventory_index,JSON_UNQUOTE(JSON_EXTRACT(raw_record,'$.reason')) AS reason,JSON_UNQUOTE(JSON_EXTRACT(raw_record,'$.error')) AS error,JSON_UNQUOTE(JSON_EXTRACT(raw_record,'$.download_collection')) AS download_collection FROM inventory_entries WHERE deleted_at IS NULL) e JOIN collections c ON c.id=e.collection_id WHERE e.is_hidden=FALSE AND e.status<>'duplicate' ORDER BY c.collection_key,e.inventory_index");
   foreach(var row in rows){if(active!=null&&!active.Contains(Text(row,"collection_key")))continue;var r=new Dictionary<string,object>();foreach(string field in new[]{"site","record_type","status","category","description","resolution","reason","error","download_collection"})if(Text(row,field)!=""&&Text(row,field)!="null")r[field]=row[field];r["id"]=Text(row,"source_id");if(Text(row,"source_url")!="")r["source"]=row["source_url"];if(Text(row,"image_url")!="")r["url"]=row["image_url"];r["collection"]=row["collection_key"];r["inventory_index"]=row["inventory_index"];r["entry_token"]=BitConverter.ToString(DatabaseHash(Text(row,"id")+"|"+Text(row,"revision"))).Replace("-","");lookup[Convert.ToInt64(row["id"])]=r;records.Add(r);}
   var related=new Dictionary<long,List<object>>();var seen=new Dictionary<long,HashSet<string>>();
   foreach(var file in DBRows(db,null,"SELECT l.entry_id,l.reference_kind,l.position,COALESCE(fc.relative_path,f.relative_path) AS path,COALESCE(cc.collection_key,c.collection_key) AS owner FROM fm_entry_file_projection l JOIN fm_file_catalog f ON f.id=l.file_id LEFT JOIN fm_file_catalog fc ON fc.id=f.canonical_file_id LEFT JOIN collections cc ON cc.id=fc.collection_id JOIN collections c ON c.id=f.collection_id WHERE COALESCE(fc.is_present,f.is_present)=TRUE ORDER BY l.entry_id,FIELD(l.reference_kind,'primary','duplicate','related','saved'),l.position")){
    if(active!=null&&!active.Contains(Text(file,"owner")))continue;long id=Convert.ToInt64(file["entry_id"]);Dictionary<string,object> r;if(!lookup.TryGetValue(id,out r))continue;string owner=Text(file,"owner"),relative=Text(file,"path"),name=owner=="."?relative:relative.Substring(owner.Length+1);var c=new Collection{Id=owner};
    if(!r.ContainsKey("local")){r["local"]=MediaUrl(c,name);r["thumb"]=MediaUrl(c,name,true);r["file"]=name;if(owner!=Text(r,"collection"))r["file_collection"]=owner;}
    if(!related.ContainsKey(id)){related[id]=new List<object>();seen[id]=new HashSet<string>(StringComparer.OrdinalIgnoreCase);}if(seen[id].Add(relative))related[id].Add(new{collection=owner,file=name,url=MediaUrl(c,name)});
   }
   foreach(var pair in lookup){var r=pair.Value;if(new[]{"post","album","post_or_album"}.Contains(Text(r,"record_type"))&&related.ContainsKey(pair.Key))r["related_image_links"]=related[pair.Key].ToArray();r["search"]=String.Join(" ",new[]{"id","description","file","site","status","category","collection"}.Select(k=>Text(r,k))).ToLowerInvariant();}
   var documents=DBRows(db,null,"SELECT c.collection_key,d.relative_path,d.import_error FROM inventory_documents d JOIN collections c ON c.id=d.collection_id ORDER BY c.collection_key");var summaries=documents.Where(c=>active==null||active.Contains(Text(c,"collection_key"))).Select(c=>new{id=Text(c,"collection_key"),records=records.Count(r=>Text(r,"collection")==Text(c,"collection_key")),saved=records.Count(r=>Text(r,"collection")==Text(c,"collection_key")&&r.ContainsKey("local")),inventory=Path.GetFileName(Text(c,"relative_path"))}).ToArray();
   return new{records=records,collections=summaries,issues=documents.Where(d=>(active==null||active.Contains(Text(d,"collection_key")))&&Text(d,"import_error")!="").Select(d=>Text(d,"collection_key")+": "+Text(d,"import_error")).ToArray(),root=Root};
  }
 }
 static void SetDatabaseState(string key,object value){using(var db=OpenDatabase())DBExec(db,null,"INSERT INTO gallery_state(state_key,value) VALUES(@key,@value) ON DUPLICATE KEY UPDATE value=VALUES(value)","@key",key,"@value",Json.Serialize(value));}
 public static object ExportDatabaseInventory(string collection){var c=Find(collection);return new{collection=c.Id,records=c.Records.Where(r=>Text(r,"status")!="duplicate"&&Text(r,"_db_hidden")!="True").Select(r=>Json.DeserializeObject(CleanRecord(r))).ToArray(),generated_utc=DateTime.UtcNow.ToString("o")};}
 public static void RequestDatabaseImportUpdates(){SetDatabaseState("import_updates",new{requested=true});}
 public static object RequestDatabaseWorker(){SetDatabaseState("worker_requested",new{requested=true});var current=DatabaseState("worker_status") as Dictionary<string,object>;if(current!=null&&Text(current,"phase")=="scanning")return current;var result=new{phase="scanning",message="Background database processing requested."};SetDatabaseState("worker_status",result);return result;}
 public static object DatabaseWorkerStatus(){return DatabaseState("worker_status")??new{phase="idle"};}
 public static object DatabaseMoveQueue(string requestedState,string cursor){
  string state=String.IsNullOrWhiteSpace(requestedState)?"failed":requestedState;
  if(!new[]{"failed","pending","running","all"}.Contains(state))throw new GalleryError(400,"Choose a valid move state.");
  long before=0;if(!String.IsNullOrWhiteSpace(cursor)&&(!Int64.TryParse(cursor,out before)||before<1))throw new GalleryError(400,"Invalid move page.");
  using(var db=OpenDatabase()){
   long total=Convert.ToInt64(DBRows(db,null,"SELECT COUNT(*) n FROM fm_collection_moves WHERE state IN ('failed','pending','running') AND (@state='all' OR state=@state)","@state",state)[0]["n"]);
   var items=DBRows(db,null,"SELECT m.id,m.entry_id,m.state,m.error,m.created_at,m.completed_at,e.revision current_revision,e.source_id,e.site,c.collection_key target_collection,JSON_UNQUOTE(JSON_EXTRACT(s.value,'$.category')) target_category,(SELECT COUNT(*) FROM fm_collection_move_files f WHERE f.move_id=m.id) total_files,(SELECT COUNT(*) FROM fm_collection_move_files f WHERE f.move_id=m.id AND f.state='completed') completed_files,(SELECT COUNT(*) FROM fm_collection_move_files f WHERE f.move_id=m.id AND f.operation_id IS NOT NULL) started_operations,(SELECT MIN(f.operation_id) FROM fm_collection_move_files f WHERE f.move_id=m.id AND f.operation_id IS NOT NULL) first_operation_id FROM fm_collection_moves m JOIN inventory_entries e ON e.id=m.entry_id JOIN collections c ON c.id=COALESCE(m.target_collection_id,e.collection_id) LEFT JOIN gallery_state s ON s.state_key=CONCAT('category_move:',m.id) WHERE m.state IN ('failed','pending','running') AND (@state='all' OR m.state=@state) AND (@before=0 OR m.id<@before) ORDER BY m.id DESC LIMIT 101","@state",state,"@before",before);
   bool more=items.Count>100;if(more)items.RemoveAt(100);
   return new{total=total,items=items,next_before=more&&items.Count>0?items[items.Count-1]["id"]:null};
  }
 }
 public static object DatabaseRetryFailedMove(Dictionary<string,object> payload){
  long id;if(!Int64.TryParse(Text(payload,"id"),out id)||id<1)throw new GalleryError(400,"Choose a failed move.");
  using(var db=OpenDatabase())using(var tx=db.BeginTransaction()){
   var rows=DBRows(db,tx,"SELECT state FROM fm_collection_moves WHERE id=@id FOR UPDATE","@id",id);
   if(rows.Count!=1||Text(rows[0],"state")!="failed")throw new GalleryError(409,"This move is no longer failed. Refresh the list.");
   DBExec(db,tx,"DELETE FROM gallery_state WHERE state_key=@key","@key","manual_finish_move:"+id);
   DBExec(db,tx,"UPDATE fm_collection_moves SET state='pending',error=NULL WHERE id=@id","@id",id);
   tx.Commit();return new{message="Retry queued. The worker will check this existing move again."};
  }
 }
 public static object DatabaseRetryAllFailedMoves(){using(var db=OpenDatabase()){
  var ids=DBRows(db,null,"SELECT m.id FROM fm_collection_moves m LEFT JOIN gallery_state s ON s.state_key=CONCAT('manual_finish_move:',m.id) AND JSON_UNQUOTE(JSON_EXTRACT(s.value,'$.status')) IN ('pending','working') WHERE m.state='failed' AND s.state_key IS NULL ORDER BY m.id").Select(r=>Convert.ToInt64(r["id"])).ToArray();
  int queued=0;
  foreach(var id in ids){using(var tx=db.BeginTransaction()){
   queued+=DBExec(db,tx,"UPDATE fm_collection_moves m SET m.state='pending',m.error=NULL WHERE m.id=@id AND m.state='failed' AND NOT EXISTS (SELECT 1 FROM gallery_state s WHERE s.state_key=CONCAT('manual_finish_move:',m.id) AND JSON_UNQUOTE(JSON_EXTRACT(s.value,'$.status')) IN ('pending','working'))","@id",id);
   tx.Commit();
  }}
  var remaining=Convert.ToInt64(DBRows(db,null,"SELECT COUNT(*) n FROM fm_collection_moves WHERE state='failed'")[0]["n"]);
  return new{queued=queued,remaining=remaining,message=queued+" failed moves queued for retry."+(remaining>0?" "+remaining+" failed moves remain for review or were not eligible for retry.":"")};
 }}
 public static object DatabaseFailedFileJobs(string cursor){
  long after=0;if(cursor!=""&&(!Int64.TryParse(cursor,out after)||after<0))throw new GalleryError(400,"Invalid failed-job cursor.");
  using(var db=OpenDatabase()){
   var rows=DBRows(db,null,"SELECT w.id,w.task_kind,w.file_id,w.failure_count,w.error,w.available_at,f.relative_path,c.collection_key FROM fm_work_items w LEFT JOIN fm_file_catalog f ON f.id=w.file_id LEFT JOIN collections c ON c.id=f.collection_id WHERE w.state='failed' AND w.id>@after ORDER BY w.id LIMIT 51","@after",after);
   bool more=rows.Count>50;if(more)rows.RemoveAt(50);
   return new{items=rows,has_more=more,next_after=more&&rows.Count>0?(object)rows.Last()["id"]:null};
  }
 }
 public static object DatabaseRetryFailedFileJob(Dictionary<string,object> payload){
  long id;if(!Int64.TryParse(Text(payload,"id"),out id)||id<1)throw new GalleryError(400,"Choose a failed file job.");
  using(var db=OpenDatabase())using(var tx=db.BeginTransaction()){
   var rows=DBRows(db,tx,"SELECT id,state FROM fm_work_items WHERE id=@id FOR UPDATE","@id",id);
   if(rows.Count==0)throw new GalleryError(404,"File job not found.");
   if(Text(rows[0],"state")!="failed")throw new GalleryError(409,"This file job is no longer failed. Refresh the list.");
   DBExec(db,tx,"UPDATE fm_work_items SET state='pending',failure_count=0,available_at=UTC_TIMESTAMP(6),lease_owner=NULL,lease_token=NULL,lease_expires_at=NULL,error=NULL WHERE id=@id AND state='failed'","@id",id);
   tx.Commit();return new{job_id=id,state="pending"};
  }
 }
 public static object DatabaseSystemComponent(string component){using(var db=OpenDatabase()){
  object data;
  if(component=="worker"||component=="issues"){
   var rows=DBRows(db,null,"SELECT value FROM gallery_state WHERE state_key='worker_status' LIMIT 1");
   data=rows.Count==0?null:Json.DeserializeObject(Text(rows[0],"value"));
  }else if(component=="work")data=new{work_items=DBRows(db,null,"SELECT state,COUNT(*) n FROM fm_work_items GROUP BY state"),tasks=DBRows(db,null,"SELECT task_kind,state,COUNT(*) n FROM fm_work_items WHERE state IN ('pending','leased','failed') GROUP BY task_kind,state"),dirty_index=DBRows(db,null,"SELECT (SELECT COUNT(*) FROM fm_entry_dirty)+(SELECT COUNT(*) FROM fm_file_dirty) n")[0]["n"]};
  else if(component=="recovery")data=DBRows(db,null,"SELECT state,COUNT(*) n FROM file_operations WHERE state IN ('planned','copied','linked','error') GROUP BY state");
  else if(component=="moves")data=new{counts=DBRows(db,null,"SELECT state,COUNT(*) n FROM fm_collection_moves WHERE state IN ('pending','running','failed') GROUP BY state"),items=DBRows(db,null,"SELECT m.id,m.entry_id,m.state,m.error,e.revision current_revision,e.source_id,c.collection_key target_collection,(SELECT COUNT(*) FROM fm_collection_move_files f WHERE f.move_id=m.id) total,(SELECT COUNT(*) FROM fm_collection_move_files f WHERE f.move_id=m.id AND f.state='completed') processed FROM fm_collection_moves m JOIN inventory_entries e ON e.id=m.entry_id JOIN collections c ON c.id=COALESCE(m.target_collection_id,e.collection_id) WHERE m.state IN ('pending','running','failed') ORDER BY CASE m.state WHEN 'running' THEN 0 WHEN 'pending' THEN 1 ELSE 2 END,m.id DESC LIMIT 20"),recent=DBRows(db,null,"SELECT m.id,m.entry_id,m.completed_at,e.source_id,c.collection_key target_collection FROM fm_collection_moves m JOIN inventory_entries e ON e.id=m.entry_id JOIN collections c ON c.id=e.collection_id WHERE m.state='completed' ORDER BY m.completed_at DESC,m.id DESC LIMIT 5")};
  else if(component=="scan")data=DBRows(db,null,"SELECT id,state,started_at,finished_at,files_seen,files_processed,errors FROM scan_runs ORDER BY id DESC LIMIT 1").FirstOrDefault();
  else if(component=="downloads"){
   var rows=DBRows(db,null,"SELECT value FROM gallery_state WHERE state_key='download_queue' LIMIT 1");
   var item=rows.Count==0?new Dictionary<string,object>():Json.Deserialize<Dictionary<string,object>>(Text(rows[0],"value"));
   data=new{phase=Text(item,"phase"),processed=Text(item,"processed"),total=item.ContainsKey("entries")?((IList)item["entries"]).Count:0,saved=Text(item,"saved"),failed=Text(item,"failed"),updated_utc=Text(item,"updated_utc"),errors=item.ContainsKey("errors")?((IList)item["errors"]).Cast<object>().Take(20).ToArray():new object[0],failed_entries=item.ContainsKey("failed_entries")?((IList)item["failed_entries"]).Cast<object>().ToArray():new object[0]};
  }else if(component=="inventory")data=new{saved=DBRows(db,null,"SELECT COUNT(*) n FROM files WHERE is_present=TRUE AND canonical_file_id IS NULL")[0]["n"],records=DBRows(db,null,"SELECT COUNT(*) n FROM inventory_entries WHERE deleted_at IS NULL")[0]["n"],collections=DBRows(db,null,"SELECT COUNT(*) n FROM fm_collection_discovery WHERE is_marked=TRUE")[0]["n"]};
  else if(component=="services")data=new{wallhaven=WallhavenKeyAvailable(),reddit=RedditSessionAvailable()};
  else throw new GalleryError(400,"Choose a valid status component.");
  return new{checked_utc=DateTime.UtcNow.ToString("o"),data=data};
 }}
 public static object DatabaseSystemStatus(){using(var db=OpenDatabase()){
  var worker=DBRows(db,null,"SELECT value FROM gallery_state WHERE state_key='worker_status' LIMIT 1");
  var work=DBRows(db,null,"SELECT state,COUNT(*) n FROM fm_work_items GROUP BY state");
  var operations=DBRows(db,null,"SELECT state,COUNT(*) n FROM file_operations WHERE state IN ('planned','copied','linked','error') GROUP BY state");
  var dirty=DBRows(db,null,"SELECT (SELECT COUNT(*) FROM fm_entry_dirty)+(SELECT COUNT(*) FROM fm_file_dirty) n");
  var scan=DBRows(db,null,"SELECT id,state,started_at,finished_at,files_seen,files_processed,errors FROM scan_runs ORDER BY id DESC LIMIT 1");
  var downloadRows=DBRows(db,null,"SELECT value FROM gallery_state WHERE state_key='download_queue' LIMIT 1");
  var download=downloadRows.Count==0?new Dictionary<string,object>():Json.Deserialize<Dictionary<string,object>>(Text(downloadRows[0],"value"));
  return new{checked_utc=DateTime.UtcNow.ToString("o"),worker=worker.Count==0?null:Json.DeserializeObject(Text(worker[0],"value")),work_items=work,dirty_index=dirty[0]["n"],file_operations=operations,latest_scan=scan.Count==0?null:scan[0],downloads=new{phase=Text(download,"phase"),processed=Text(download,"processed"),total=download.ContainsKey("entries")?((IList)download["entries"]).Count:0,saved=Text(download,"saved"),failed=Text(download,"failed"),updated_utc=Text(download,"updated_utc")}};
 }}
 public static object DatabaseRecoveryOperations(string requestedId){long id=0;if(requestedId!=""&&(!Int64.TryParse(requestedId,out id)||id<1))throw new GalleryError(400,"Enter a valid operation ID.");using(var db=OpenDatabase()){
  var total=Convert.ToInt64(DBRows(db,null,"SELECT COUNT(*) n FROM file_operations WHERE state='error'")[0]["n"]);
  var items=DBRows(db,null,"SELECT o.id,o.operation_kind,o.state,COALESCE(o.error,m.error) error,o.source_path,o.target_path,HEX(o.source_sha256) expected_source_sha256,HEX(o.target_sha256) expected_target_sha256,o.source_file_id,o.target_file_id,s.relative_path current_source_path,(SELECT c.collection_key FROM fm_file_catalog fc JOIN collections c ON c.id=fc.collection_id WHERE fc.id=o.source_file_id) source_collection,s.is_present source_catalog_present,s.canonical_file_id source_canonical_file_id,t.relative_path current_target_path,(SELECT c.collection_key FROM fm_file_catalog fc JOIN collections c ON c.id=fc.collection_id WHERE fc.id=o.target_file_id) target_collection,t.is_present target_catalog_present,t.canonical_file_id target_canonical_file_id,k.relative_path survivor_path,(SELECT c.collection_key FROM fm_file_catalog fc JOIN collections c ON c.id=fc.collection_id WHERE fc.id=k.id) survivor_collection,k.is_present survivor_catalog_present,(SELECT COUNT(*) FROM inventory_entries e WHERE e.file_id=o.source_file_id) source_entry_links,(SELECT COUNT(*) FROM fm_external_bindings b WHERE b.file_id=o.source_file_id) source_downloader_links FROM file_operations o LEFT JOIN files s ON s.id=o.source_file_id LEFT JOIN files t ON t.id=o.target_file_id LEFT JOIN files k ON k.id=s.canonical_file_id AND k.id=t.canonical_file_id LEFT JOIN fm_collection_move_files mf ON mf.operation_id=o.id LEFT JOIN fm_collection_moves m ON m.id=mf.move_id WHERE (o.state='error' OR (@id<>0 AND o.id=@id AND m.state='failed')) AND (@id=0 OR o.id=@id) ORDER BY CASE WHEN o.error IN ('Missing source has different bytes; recovery needs review','Downloader MD5 differs from the actual file; original retained') THEN 0 ELSE 1 END,o.id LIMIT 50","@id",id);
  return new{total=total,items=items};
 }}
 public static object DatabaseWithdrawFailedMove(HttpContext context,Dictionary<string,object> payload){
  long id;if(!Int64.TryParse(Text(payload,"id"),out id)||id<1)throw new GalleryError(400,"Choose a failed move.");
  using(var db=OpenDatabase())using(var tx=db.BeginTransaction()){
   var rows=DBRows(db,tx,"SELECT id,state,entry_id,target_collection_id,error FROM fm_collection_moves WHERE id=@id FOR UPDATE","@id",id);
   if(rows.Count!=1||Text(rows[0],"state")!="failed")throw new GalleryError(409,"This move is no longer failed. Refresh its status.");
   var files=DBRows(db,tx,"SELECT file_id,state,operation_id FROM fm_collection_move_files WHERE move_id=@id FOR UPDATE","@id",id);
   if(files.Any(f=>Text(f,"state")!="pending"||f["operation_id"]!=null))throw new GalleryError(409,"This move already started changing files. Review its file operations or retry finishing it.");
   DBExec(db,tx,"INSERT INTO gallery_state(state_key,value) VALUES(@key,@value) ON DUPLICATE KEY UPDATE value=VALUES(value)","@key","manual_withdrawn_move:"+id,"@value",Json.Serialize(new{move_id=id,entry_id=rows[0]["entry_id"],target_collection_id=rows[0]["target_collection_id"],error=rows[0]["error"],actor=context.User.Identity.Name,withdrawn_utc=DateTime.UtcNow.ToString("o")}));
   DBExec(db,tx,"DELETE FROM fm_collection_move_files WHERE move_id=@id","@id",id);
   DBExec(db,tx,"DELETE FROM gallery_state WHERE state_key=@key","@key","category_move:"+id);
   DBExec(db,tx,"DELETE FROM fm_collection_moves WHERE id=@id AND state='failed'","@id",id);
   tx.Commit();return new{message="The unstarted failed move was withdrawn. No file or category was changed."};
  }
 }
 public static object DatabaseFinishFailedMove(HttpContext context,Dictionary<string,object> payload){
  long id,revision;if(!Int64.TryParse(Text(payload,"id"),out id)||id<1||!Int64.TryParse(Text(payload,"revision"),out revision)||revision<1)throw new GalleryError(400,"Review a failed move before finishing it.");
  using(var db=OpenDatabase())using(var tx=db.BeginTransaction()){
   var rows=DBRows(db,tx,"SELECT m.id,m.state,m.entry_id,m.target_collection_id,e.revision FROM fm_collection_moves m JOIN inventory_entries e ON e.id=m.entry_id WHERE m.id=@id FOR UPDATE","@id",id);
   if(rows.Count!=1||Text(rows[0],"state")!="failed")throw new GalleryError(409,"This move is no longer failed.");
   if(Convert.ToInt64(rows[0]["revision"])!=revision)throw new GalleryError(409,"The entry changed. Review this move again.");
   var files=DBRows(db,tx,"SELECT COUNT(*) total,SUM(f.state='completed') completed,SUM(f.operation_id IS NOT NULL AND COALESCE(o.state,'error')<>'complete') unfinished_operations FROM fm_collection_move_files f LEFT JOIN file_operations o ON o.id=f.operation_id WHERE f.move_id=@id","@id",id)[0];
   if(Convert.ToInt64(files["total"])<1||Convert.ToInt64(files["completed"]??0)!=Convert.ToInt64(files["total"])||Convert.ToInt64(files["unfinished_operations"]??0)!=0)throw new GalleryError(409,"Some files still need moving or file-operation review. Review those files before finishing this move.");
   string key="manual_finish_move:"+id;var existing=DBRows(db,tx,"SELECT value FROM gallery_state WHERE state_key=@key FOR UPDATE","@key",key);
   if(existing.Count>0){var decision=Json.Deserialize<Dictionary<string,object>>(Text(existing[0],"value"));if(new[]{"pending","working"}.Contains(Text(decision,"status")))return new{message="Your reviewed finish decision is already waiting for the worker."};}
   DBExec(db,tx,"INSERT INTO gallery_state(state_key,value) VALUES(@key,@value) ON DUPLICATE KEY UPDATE value=VALUES(value)","@key",key,"@value",Json.Serialize(new{move_id=id,entry_id=rows[0]["entry_id"],target_collection_id=rows[0]["target_collection_id"],revision=revision,status="pending",actor=context.User.Identity.Name,requested_utc=DateTime.UtcNow.ToString("o")}));
   tx.Commit();return new{message="Your decision to finish the completed file move was saved. Gallery will apply its remaining entry details."};
  }
 }
 public static object DatabaseRecoveryAction(Dictionary<string,object> payload){
  long id;if(!Int64.TryParse(Text(payload,"id"),out id)||id<1)throw new GalleryError(400,"Choose an issue.");
  string choice=Text(payload,"choice");if(!new[]{"first","second","both","cancel_move","finish_move_manual","retry","restore","locate"}.Contains(choice))throw new GalleryError(400,"Choose a resolution.");
  using(var db=OpenDatabase())using(var tx=db.BeginTransaction()){
   var rows=DBRows(db,tx,"SELECT id,state,source_file_id,target_file_id,source_path,target_path FROM file_operations WHERE id=@id FOR UPDATE","@id",id);
   if(rows.Count==0)throw new GalleryError(404,"Issue not found.");var op=rows[0];
   if(new[]{"complete","cancelled"}.Contains(Text(op,"state")))return new{message="Already resolved. The old error has been cleared."};
   if(Text(payload,"source_id")!=""&&(Text(payload,"source_id")!=Text(op,"source_file_id")||Text(payload,"target_id")!=Text(op,"target_file_id")))throw new GalleryError(409,"This cleanup changed since the page loaded. Refresh the issue list before choosing a copy.");
   string key="recovery_action:"+id;
   var old=DBRows(db,tx,"SELECT value FROM gallery_state WHERE state_key=@key FOR UPDATE","@key",key);
   if(old.Count>0){var request=Json.Deserialize<Dictionary<string,object>>(Text(old[0],"value"));if(new[]{"pending","working"}.Contains(Text(request,"status")))throw new GalleryError(409,"Your previous choice is still being checked. Its result will appear here automatically.");}
   var value=new{id=id,choice=choice,side=Text(payload,"side"),path=Text(payload,"path"),reviewed_source_sha256=Text(payload,"reviewed_source_sha256"),reviewed_target_sha256=Text(payload,"reviewed_target_sha256"),plan=op,status="pending",message=choice=="retry"?"Waiting for automated checks to run again.":"Waiting for the worker to apply your reviewed choice."};
   DBExec(db,tx,"INSERT INTO gallery_state(state_key,value) VALUES(@key,@value) ON DUPLICATE KEY UPDATE value=VALUES(value)","@key",key,"@value",Json.Serialize(value));tx.Commit();return new{message=value.message};
  }
 }
 public static object DatabaseRecoveryActionStatus(string requestedIds){
  var ids=(requestedIds??"").Split(',');if(ids.Length<1||ids.Length>50)throw new GalleryError(400,"Choose up to 50 issues.");
  var args=new List<object>();var names=new List<string>();foreach(string value in ids){long id;if(!Int64.TryParse(value,out id)||id<1)throw new GalleryError(400,"Choose valid issues.");string key="@id"+names.Count;names.Add(key);args.Add(key);args.Add(id);}
  using(var db=OpenDatabase())return new{items=DBRows(db,null,"SELECT o.id,o.state,o.error,s.value action FROM file_operations o LEFT JOIN gallery_state s ON s.state_key=CONCAT('recovery_action:',o.id) WHERE o.id IN ("+String.Join(",",names)+")",args.ToArray())};
 }
 static string RecoveryReviewPath(string root,string relative){
  if(String.IsNullOrWhiteSpace(relative)||Path.IsPathRooted(relative))throw new GalleryError(409,"A file has no usable location.");
  root=Path.GetFullPath(root).TrimEnd('\\');
  string path=Path.GetFullPath(Path.Combine(root,relative.Replace('/','\\')));
  if(!Under(path,root))throw new GalleryError(409,"The file location is outside wallpaper storage.");
  // Inspect existing ancestors as well as the leaf; missing old leaves are expected.
  for(string current=path;;current=Path.GetDirectoryName(current)){
   try{var attributes=File.GetAttributes(current);if((attributes&FileAttributes.ReparsePoint)!=0)throw new GalleryError(409,"A linked folder or file needs manual review.");}
   catch(FileNotFoundException){if(current==root)throw new GalleryError(409,"Wallpaper storage is unavailable.");}
   catch(DirectoryNotFoundException){if(current==root)throw new GalleryError(409,"Wallpaper storage is unavailable.");}
   if(String.Equals(current,root,StringComparison.OrdinalIgnoreCase))break;
  }
  return path;
 }
 public static object DatabaseRecoverySurvivorProof(string idText){
  long id;if(!Int64.TryParse(idText,out id)||id<1)throw new GalleryError(400,"Choose a file operation.");
  using(var db=OpenDatabase()){
   var rows=DBRows(db,null,"SELECT o.state,o.operation_kind,o.source_path,o.target_path,s.canonical_file_id source_canonical,t.canonical_file_id target_canonical,k.relative_path survivor_path,k.is_present survivor_present FROM file_operations o JOIN files s ON s.id=o.source_file_id JOIN files t ON t.id=o.target_file_id LEFT JOIN files k ON k.id=s.canonical_file_id AND k.id=t.canonical_file_id WHERE o.id=@id","@id",id);
   if(rows.Count!=1)throw new GalleryError(404,"Operation not found.");var op=rows[0];
   if(Text(op,"state")!="error"||Text(op,"operation_kind")!="deduplicate"||Text(op,"source_canonical")==""||Text(op,"source_canonical")!=Text(op,"target_canonical")||!Convert.ToBoolean(op["survivor_present"]))throw new GalleryError(409,"No single saved copy is ready for review.");
   string path=RecoveryReviewPath(Root,Text(op,"survivor_path"));
   if(!File.Exists(path))throw new GalleryError(409,"The saved copy is unavailable.");
   using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))return new{path=Text(op,"survivor_path"),size=stream.Length,sha256=HashStream(stream)};
  }
 }
 public static object DatabaseRecoveryCopiesProof(string idText){
  long id;if(!Int64.TryParse(idText,out id)||id<1)throw new GalleryError(400,"Choose a file operation.");
  using(var db=OpenDatabase()){
   var rows=DBRows(db,null,"SELECT o.state,o.source_path,o.target_path,m.state move_state FROM file_operations o LEFT JOIN fm_collection_move_files mf ON mf.operation_id=o.id LEFT JOIN fm_collection_moves m ON m.id=mf.move_id WHERE o.id=@id","@id",id);
   if(rows.Count!=1||(Text(rows[0],"state")!="error"&&Text(rows[0],"move_state")!="failed"))throw new GalleryError(409,"This issue is no longer waiting for review.");
   var op=rows[0];var result=new Dictionary<string,object>();
   foreach(var side in new[]{"source","target"}){
    string relative=Text(op,side+"_path"),path=RecoveryReviewPath(Root,relative);
    if(!File.Exists(path)){result[side]=null;continue;}
    using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))result[side]=new{path=relative,size=stream.Length,sha256=HashStream(stream)};
   }
   return result;
  }
 }
 public static object DatabaseResolveSupersededRecoveryOperation(Dictionary<string,object> payload){
  long id;if(!Int64.TryParse(Text(payload,"id"),out id)||id<1)throw new GalleryError(400,"Choose a file operation.");
  using(var db=OpenDatabase())using(var tx=db.BeginTransaction()){
   var rows=DBRows(db,tx,"SELECT o.id,o.state,o.operation_kind,o.source_path,o.target_path,HEX(o.source_sha256) source_sha,HEX(o.target_sha256) target_sha,o.source_file_id,o.target_file_id,s.canonical_file_id source_canonical,t.canonical_file_id target_canonical,k.relative_path survivor_path,k.is_present survivor_present,k.canonical_file_id survivor_canonical FROM file_operations o JOIN files s ON s.id=o.source_file_id JOIN files t ON t.id=o.target_file_id LEFT JOIN files k ON k.id=s.canonical_file_id AND k.id=t.canonical_file_id WHERE o.id=@id FOR UPDATE","@id",id);
   if(rows.Count!=1)throw new GalleryError(404,"Operation not found.");var op=rows[0];
   if(Text(op,"state")!="error"||Text(op,"operation_kind")!="deduplicate"||Text(op,"source_canonical")==""||Text(op,"source_canonical")!=Text(op,"target_canonical")||!Convert.ToBoolean(op["survivor_present"])||Text(op,"survivor_canonical")!="")throw new GalleryError(409,"The records do not identify one surviving copy. Review or restore the files.");
   var links=DBRows(db,tx,"SELECT (SELECT COUNT(*) FROM inventory_entries WHERE file_id IN (@source,@target)) entries,(SELECT COUNT(*) FROM fm_external_bindings WHERE file_id IN (@source,@target)) bindings","@source",op["source_file_id"],"@target",op["target_file_id"])[0];
   if(Convert.ToInt64(links["entries"])!=0||Convert.ToInt64(links["bindings"])!=0)throw new GalleryError(409,"Entries or downloader links still point to an old file ID. This needs manual repair.");
   foreach(var key in new[]{"source_path","target_path"}){string old=RecoveryReviewPath(Root,Text(op,key));if(File.Exists(old))throw new GalleryError(409,"An original copy still exists. Review that file before closing this cleanup.");}
   string path=RecoveryReviewPath(Root,Text(op,"survivor_path"));
   if(!File.Exists(path))throw new GalleryError(409,"The saved copy is unavailable. Reconnect storage or restore this image, then try again.");
   long reviewedSize;if(!Int64.TryParse(Text(payload,"reviewed_size"),out reviewedSize)||reviewedSize<1||!System.Text.RegularExpressions.Regex.IsMatch(Text(payload,"reviewed_sha256"),"^[a-fA-F0-9]{64}$"))throw new GalleryError(400,"Review the saved file before applying this decision.");
   using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))if(stream.Length!=reviewedSize||!String.Equals(HashStream(stream),Text(payload,"reviewed_sha256"),StringComparison.OrdinalIgnoreCase))throw new GalleryError(409,"The saved file changed after review. Review it again.");
   DBExec(db,tx,"UPDATE file_operations SET state='cancelled',error='Manually resolved using reviewed saved copy',completed_at=UTC_TIMESTAMP(6) WHERE id=@id AND state='error'","@id",id);
   tx.Commit();return new{message="Resolved. The saved copy was verified and the old cleanup task was closed."};
  }
 }
 public static object DatabaseHoldRecoveryOperation(Dictionary<string,object> payload){
  long id;if(!Int64.TryParse(Text(payload,"id"),out id)||id<1)throw new GalleryError(400,"Choose a file operation.");
  bool hold=Text(payload,"hold")=="True";
  using(var db=OpenDatabase())using(var tx=db.BeginTransaction()){
   var rows=DBRows(db,tx,"SELECT state,error FROM file_operations WHERE id=@id FOR UPDATE","@id",id);
   if(rows.Count!=1||Text(rows[0],"state")!="error")throw new GalleryError(409,"This operation is no longer waiting for review.");
   string error=Text(rows[0],"error");
   if(hold&&!error.StartsWith("On hold: "))error="On hold: "+error;
   if(!hold&&error.StartsWith("On hold: "))error=error.Substring(9);
   DBExec(db,tx,"UPDATE file_operations SET error=@error WHERE id=@id","@error",error,"@id",id);
   tx.Commit();return new{message=hold?"Operation held for review. Files and links were not changed.":"Operation resumed for the next recovery pass."};
  }
 }
 public static object ClearDatabaseIssues(){var result=new{phase="idle",message="Scan issues cleared. Use Retry scan issues to check the files again.",issues=new object[0]};SetDatabaseState("worker_status",result);return result;}
 public static object RetryDatabaseIssues(){var status=DatabaseState("worker_status") as Dictionary<string,object>;if(status!=null&&status.ContainsKey("issues")){var issues=Json.Deserialize<List<Dictionary<string,object>>>(Json.Serialize(status["issues"]));using(var db=OpenDatabase())foreach(var issue in issues){string path=Text(issue,"file");if(path!="")DBExec(db,null,"INSERT IGNORE INTO fm_file_dirty(file_id) SELECT id FROM files WHERE relative_path=@path AND is_present=TRUE","@path",path);}}return RequestDatabaseWorker();}
 static GalleryFileReference DatabaseExistingContent(string hash,long size){using(var db=OpenDatabase())foreach(var file in DBRows(db,null,"SELECT f.relative_path,c.collection_key FROM file_hashes h JOIN fm_file_catalog f ON f.id=h.file_id JOIN collections c ON c.id=f.collection_id WHERE h.hash_kind='file_sha256' AND h.hash_value=UNHEX(@hash) AND f.byte_size=@size AND h.source_modified_utc_ticks=f.modified_utc_ticks AND f.is_present=TRUE AND f.canonical_file_id IS NULL","@hash",hash,"@size",size)){string owner=Text(file,"collection_key"),name=owner=="."?Text(file,"relative_path"):Text(file,"relative_path").Substring(owner.Length+1);var c=Find(owner);string path=SafeFile(c,name);if(path!=null&&File.Exists(path)){using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read))if(stream.Length==size&&HashStream(stream)==hash)return new GalleryFileReference{Owner=c,Name=name,Path=path};}}return null;}
}
