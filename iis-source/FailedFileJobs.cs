using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Web;
using MySqlConnector;

public static partial class Gallery {
 static long FailedJobId(string value){long id;if(!Int64.TryParse(value,out id)||id<1)throw new GalleryError(400,"Choose a failed file job.");return id;}
 static Dictionary<string,object> FailedJob(MySqlConnection db,MySqlTransaction tx,long id,bool locked){
  var rows=DBRows(db,tx,"SELECT w.id,w.state,w.task_kind,w.error,w.file_id,w.expected_size,w.expected_ticks,f.relative_path,f.byte_size,f.modified_utc_ticks,f.is_present,f.canonical_file_id,c.collection_key FROM fm_work_items w JOIN fm_file_catalog f ON f.id=w.file_id JOIN collections c ON c.id=f.collection_id WHERE w.id=@id"+(locked?" FOR UPDATE":""),"@id",id);
  if(rows.Count!=1)throw new GalleryError(404,"File job not found.");var job=rows[0];
  if(Text(job,"state")!="failed")throw new GalleryError(409,"This job is no longer failed. Refresh the list.");
  if(!Convert.ToBoolean(job["is_present"])||job["canonical_file_id"]!=null||Convert.ToInt64(job["expected_size"])!=Convert.ToInt64(job["byte_size"])||Convert.ToInt64(job["expected_ticks"])!=Convert.ToInt64(job["modified_utc_ticks"]))throw new GalleryError(409,"The saved file changed. Recheck the collection before resolving this job.");
  return job;
 }
 static Dictionary<string,object> FileProof(Dictionary<string,object> job,FileStream stream){
  long size=Convert.ToInt64(job["byte_size"]),ticks=Convert.ToInt64(job["modified_utc_ticks"]);
  if(stream.Length!=size||File.GetLastWriteTimeUtc(stream.Name).Ticks!=ticks)throw new GalleryError(409,"The file changed since Gallery indexed it. Recheck the collection.");
  stream.Position=0;string sha;using(var hash=SHA256.Create())sha=BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","").ToLowerInvariant();
  stream.Position=0;string md5;using(var hash=MD5.Create())md5=BitConverter.ToString(hash.ComputeHash(stream)).Replace("-","").ToLowerInvariant();
  if(stream.Length!=size||File.GetLastWriteTimeUtc(stream.Name).Ticks!=ticks)throw new GalleryError(409,"The file changed during review. Try again.");
  return new Dictionary<string,object>{{"sha256",sha},{"md5",md5},{"size",size},{"ticks",ticks}};
 }
 static Dictionary<string,object> DownloaderRecord(MySqlConnection db,MySqlTransaction tx,Dictionary<string,object> job,bool locked){
  var bindings=DBRows(db,tx,"SELECT b.external_id,b.last_known_path,c.database_name,c.adapter_kind,c.root_relative,c.storage_root_id FROM fm_external_bindings b JOIN fm_integrations i ON i.id=b.integration_id JOIN fm_adapter_config c ON c.integration_key=i.integration_key WHERE b.file_id=@file AND c.enabled=TRUE AND c.adapter_kind='booru-locations'","@file",job["file_id"]);
  if(bindings.Count!=1)throw new GalleryError(409,"This job needs one active Booru file binding for manual checksum correction.");
  var binding=bindings[0];string schema=Text(binding,"database_name");if(!Regex.IsMatch(schema,"^[A-Za-z0-9_-]+$"))throw new GalleryError(409,"The downloader database name is invalid.");
  long externalId;if(!Int64.TryParse(Text(binding,"external_id"),out externalId)||externalId<1)throw new GalleryError(409,"The downloader file binding is invalid.");
  string relative=Text(job,"relative_path").Replace('\\','/'),prefix=Text(binding,"root_relative").Trim('/');
  if(prefix!=""&&prefix!="."){if(!relative.StartsWith(prefix+"/",StringComparison.OrdinalIgnoreCase))throw new GalleryError(409,"The file is outside the downloader storage mapping.");relative=relative.Substring(prefix.Length+1);}
  var rows=DBRows(db,tx,"SELECT l.id location_id,l.file_id external_file_id,l.relative_path,l.storage_root_id,l.status,f.md5,f.sha256,f.size_bytes,(SELECT COUNT(*) FROM `"+schema+"`.file_locations other WHERE other.file_id=l.file_id AND other.status='present') present_locations,(SELECT COUNT(*) FROM `"+schema+"`.media_files mf WHERE mf.file_id=l.file_id) media_links FROM `"+schema+"`.file_locations l JOIN `"+schema+"`.files f ON f.id=l.file_id WHERE l.id=@id"+(locked?" FOR UPDATE":""),"@id",externalId);
  if(rows.Count!=1)throw new GalleryError(409,"The downloader file record is missing.");var record=rows[0];
  if(Text(record,"relative_path")!=relative||Text(binding,"last_known_path")!=relative||Text(record,"storage_root_id")!=Text(binding,"storage_root_id")||Text(record,"status")!="present")throw new GalleryError(409,"The downloader file pointer changed. No checksum was changed.");
  if(Convert.ToInt64(record["present_locations"])!=1||Convert.ToInt64(record["media_links"])<1)throw new GalleryError(409,"This downloader file has other locations or no media link. Review those links before correcting its checksum.");
  record["database_name"]=schema;return record;
 }
 static string FailedJobPreview(Dictionary<string,object> job){string owner=Text(job,"collection_key"),path=Text(job,"relative_path");string name=owner=="."?path:path.Substring(owner.Length+1);return MediaUrl(new Collection{Id=owner},name);}
 public static object DatabaseReviewFailedFileJob(string requestedId){
  long id=FailedJobId(requestedId);using(var db=OpenDatabase()){
   var job=FailedJob(db,null,id,false);string kind=Text(job,"task_kind"),error=Text(job,"error");
   string choice=kind=="hash_file"&&error.IndexOf("image file is truncated",StringComparison.OrdinalIgnoreCase)>=0?"accept_partial":kind=="match_file"&&error.IndexOf("Downloader MD5 differs",StringComparison.OrdinalIgnoreCase)>=0?"correct_checksum":"";
   if(choice=="")throw new GalleryError(409,"This failure has no manual resolution yet. You can retry it after correcting its cause.");
   string path=RecoveryReviewPath(Root,Text(job,"relative_path"));if(!File.Exists(path))throw new GalleryError(409,"The saved file is missing. Recheck the collection.");
   using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){
    var proof=FileProof(job,stream);Dictionary<string,object> record=null;if(choice=="correct_checksum")record=DownloaderRecord(db,null,job,false);
    return new{job_id=id,choice=choice,file=job["relative_path"],preview_url=FailedJobPreview(job),actual_sha256=proof["sha256"],actual_md5=proof["md5"],byte_size=proof["size"],recorded_md5=record==null?null:record["md5"],recorded_sha256=record==null?null:record["sha256"],recorded_size=record==null?null:record["size_bytes"],media_links=record==null?null:record["media_links"],external_file_id=record==null?null:record["external_file_id"]};
   }
  }
 }
 public static object DatabaseResolveFailedFileJob(HttpContext context,Dictionary<string,object> payload){
  long id=FailedJobId(Text(payload,"id"));string choice=Text(payload,"choice"),expectedSha=Text(payload,"sha256").ToLowerInvariant();
  if(!Regex.IsMatch(expectedSha,"^[0-9a-f]{64}$"))throw new GalleryError(400,"Review the current file before applying a manual decision.");
  using(var db=OpenDatabase()){
   var initial=FailedJob(db,null,id,false);string path=RecoveryReviewPath(Root,Text(initial,"relative_path"));
   using(var stream=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read)){
    var proof=FileProof(initial,stream);if(Text(proof,"sha256")!=expectedSha)throw new GalleryError(409,"The file bytes changed since review. Review it again.");
    using(var tx=db.BeginTransaction()){
     var job=FailedJob(db,tx,id,true);if(Text(job,"relative_path")!=Text(initial,"relative_path")||Text(job,"expected_size")!=Text(initial,"expected_size")||Text(job,"expected_ticks")!=Text(initial,"expected_ticks"))throw new GalleryError(409,"The job changed since review. Review it again.");
     if(choice=="accept_partial"&&Text(job,"task_kind")=="hash_file"&&Text(job,"error").IndexOf("image file is truncated",StringComparison.OrdinalIgnoreCase)>=0){
      DBExec(db,tx,"INSERT INTO gallery_state(state_key,value) VALUES(@key,@value) ON DUPLICATE KEY UPDATE value=VALUES(value)","@key","manual_truncated:"+Text(job,"file_id"),"@value",Json.Serialize(new{sha256=proof["sha256"],bytes=proof["size"],ticks=proof["ticks"],actor=context.User.Identity.Name,accepted_utc=DateTime.UtcNow.ToString("o")}));
     }else if(choice=="correct_checksum"&&Text(job,"task_kind")=="match_file"&&Text(job,"error").IndexOf("Downloader MD5 differs",StringComparison.OrdinalIgnoreCase)>=0){
      var record=DownloaderRecord(db,tx,job,true);if(Text(record,"md5")!=Text(payload,"recorded_md5")||Text(record,"sha256")!=Text(payload,"recorded_sha256")||Text(record,"size_bytes")!=Text(payload,"recorded_size"))throw new GalleryError(409,"The downloader record changed since review. Review it again.");
      string schema=Text(record,"database_name");if(DBRows(db,tx,"SELECT id FROM `"+schema+"`.files WHERE sha256=@sha AND id<>@id LIMIT 1","@sha",proof["sha256"],"@id",record["external_file_id"]).Count>0)throw new GalleryError(409,"Another downloader file already owns this checksum. Review its links before correcting this record.");
      DBExec(db,tx,"UPDATE `"+schema+"`.files SET md5=@md5,sha256=@sha,size_bytes=@size,verified_at=UTC_TIMESTAMP(6) WHERE id=@id","@md5",proof["md5"],"@sha",proof["sha256"],"@size",proof["size"],"@id",record["external_file_id"]);
      DBExec(db,tx,"INSERT INTO gallery_state(state_key,value) VALUES(@key,@value) ON DUPLICATE KEY UPDATE value=VALUES(value)","@key","manual_checksum:"+id,"@value",Json.Serialize(new{file_id=job["file_id"],external_file_id=record["external_file_id"],previous_md5=record["md5"],verified_md5=proof["md5"],verified_sha256=proof["sha256"],actor=context.User.Identity.Name,accepted_utc=DateTime.UtcNow.ToString("o")}));
     }else throw new GalleryError(409,"This job no longer supports that manual decision. Review it again.");
     DBExec(db,tx,"UPDATE fm_work_items SET state='pending',failure_count=0,available_at=UTC_TIMESTAMP(6),lease_owner=NULL,lease_token=NULL,lease_expires_at=NULL,error=NULL WHERE id=@id AND state='failed'","@id",id);
     tx.Commit();return new{job_id=id,state="pending",resolution=choice};
    }
   }
  }
 }
}
