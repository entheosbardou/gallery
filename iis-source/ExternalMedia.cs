using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Web;
using System.Web.Security;

public static partial class Gallery {
 static string ExternalBearer(HttpContext c){string value=c.Request.Headers["Authorization"]??"";return value.StartsWith("Bearer ",StringComparison.OrdinalIgnoreCase)?value.Substring(7).Trim():"";}
 static ClaimsPrincipal ExternalPrincipal(HttpContext c){string token=ExternalBearer(c);if(token=="")throw new GalleryError(401,"A Gallery identity token is required.");return GalleryAuth.ValidateExternalIdToken(token);}
 static string[] ExternalGroups(ClaimsPrincipal principal){return principal.FindAll("gallery_groups").Select(x=>x.Value).Where(x=>!String.IsNullOrWhiteSpace(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();}
 static string ExternalSubject(ClaimsPrincipal p){return p.FindFirst("sub").Value;}
 static string ExternalIssuer(ClaimsPrincipal p){return p.FindFirst("iss").Value;}
 static string ExternalText(Dictionary<string,object> d,string key){object value;return d!=null&&d.TryGetValue(key,out value)&&value!=null?Convert.ToString(value):"";}
 static string NewExternalSecret(){byte[] b=new byte[32];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(b);return Convert.ToBase64String(b).TrimEnd('=').Replace('+','-').Replace('/','_');}
 static byte[] ExternalDigest(string token){using(var sha=SHA256.Create())return sha.ComputeHash(Encoding.UTF8.GetBytes(token));}
 static byte[] ProtectExternalRefresh(string token,byte[] grantHash){return MachineKey.Protect(Encoding.UTF8.GetBytes(token),"GalleryExternalRefresh-v1",BitConverter.ToString(grantHash).Replace("-",""));}
 static string UnprotectExternalRefresh(byte[] token,byte[] grantHash){try{return Encoding.UTF8.GetString(MachineKey.Unprotect(token,"GalleryExternalRefresh-v1",BitConverter.ToString(grantHash).Replace("-","")));}catch{throw new GalleryError(503,"Gallery device authorization needs to be renewed.");}}
 static List<long> ExternalIds(Dictionary<string,object> body){try{object raw;if(!body.TryGetValue("file_ids",out raw)||raw==null)throw new Exception();var ids=Json.Deserialize<long[]>(Json.Serialize(raw)).Distinct().ToList();if(ids.Count==0||ids.Count>500||ids.Any(x=>x<1))throw new Exception();return ids;}catch{throw new GalleryError(400,"Provide between 1 and 500 valid Gallery file IDs.");}}
 static Dictionary<long,Dictionary<string,object>> ExternalFiles(IEnumerable<long> ids,string[] groups,GalleryAccessPolicy policy){
  var wanted=ids.Distinct().ToList();var result=new Dictionary<long,Dictionary<string,object>>();if(wanted.Count==0)return result;
  var rights=Gallery.GrantForGroups(groups,policy);if(!rights.allowed)return result;
  using(var db=OpenDatabase()){
   var args=new List<object>();string placeholders=String.Join(",",wanted.Select((x,i)=>{string key="@id"+i;args.Add(key);args.Add(x);return key;}));
   var rows=DBRows(db,null,"SELECT requested.id requested_id,f.id,f.relative_path,f.byte_size,f.width,f.height,f.access_category,c.collection_key FROM fm_file_catalog requested JOIN fm_file_catalog f ON f.id=COALESCE(requested.canonical_file_id,requested.id) JOIN collections c ON c.id=f.collection_id JOIN fm_collection_discovery d ON d.collection_id=c.id AND d.is_marked=TRUE WHERE requested.id IN ("+placeholders+") AND f.is_present=TRUE AND f.canonical_file_id IS NULL AND NOT EXISTS(SELECT 1 FROM fm_file_dirty pending WHERE pending.file_id=f.id)",args.ToArray());
   foreach(var f in rows){string collection=Text(f,"collection_key"),category=Text(f,"access_category");if(!Gallery.CollectionAccessAllowed(groups,collection,policy)||!rights.categories.Contains(category))continue;string path=Text(f,"relative_path");result[Convert.ToInt64(f["requested_id"])]=new Dictionary<string,object>{{"file_id",f["id"]},{"relative_path",path},{"byte_size",f["byte_size"]},{"width",f["width"]},{"height",f["height"]},{"category",category},{"collection",collection},{"name",Path.GetFileName(path)},{"mime",ExternalMime(path)}};}
   return result;
  }
 }
 static string ExternalMime(string path){switch(Path.GetExtension(path).ToLowerInvariant()){case ".jpg":case ".jpeg":return "image/jpeg";case ".png":return "image/png";case ".gif":return "image/gif";case ".webp":return "image/webp";case ".avif":return "image/avif";case ".bmp":return "image/bmp";case ".tif":case ".tiff":return "image/tiff";default:return "application/octet-stream";}}
 static object ExternalCatalog(HttpContext c){
  var principal=ExternalPrincipal(c);var groups=ExternalGroups(principal);var rights=Gallery.GrantForGroups(groups);if(!rights.allowed)throw new GalleryError(403,"This identity has no Gallery media access.");
  long after=0;int limit=100;string selectedCollection=c.Request.QueryString["collection"],selectedCategory=c.Request.QueryString["category"];if(!String.IsNullOrEmpty(c.Request.QueryString["after"])&&!Int64.TryParse(c.Request.QueryString["after"],out after))throw new GalleryError(400,"Invalid catalog cursor.");if(!String.IsNullOrEmpty(c.Request.QueryString["limit"])&&!Int32.TryParse(c.Request.QueryString["limit"],out limit))throw new GalleryError(400,"Invalid page size.");limit=Math.Max(1,Math.Min(250,limit));
  using(var db=OpenDatabase()){
   var policy=Gallery.GetAccessPolicy();var collections=DBRows(db,null,"SELECT c.id,c.collection_key FROM collections c JOIN fm_collection_discovery d ON d.collection_id=c.id AND d.is_marked=TRUE ORDER BY c.id").Where(x=>Gallery.CollectionAccessAllowed(groups,Text(x,"collection_key"),policy)).Where(x=>selectedCollection==null||Text(x,"collection_key")==selectedCollection).ToList();if(collections.Count==0)return new{items=new object[0],next_after=(long?)null};
   var args=new List<object>();var ownerParams=collections.Select((x,i)=>"@c"+i).ToArray();for(int i=0;i<collections.Count;i++){args.Add(ownerParams[i]);args.Add(collections[i]["id"]);}
   var cats=rights.categories;string catSql=cats.Length==0?"NULL":String.Join(",",cats.Select((x,i)=>"@k"+i));for(int i=0;i<cats.Length;i++){args.Add("@k"+i);args.Add(cats[i]);}
   args.Add("@after");args.Add(after);args.Add("@take");args.Add(limit+1);args.Add("@selectedCategory");args.Add(selectedCategory);
   string sql="SELECT f.id file_id,f.relative_path,f.byte_size,f.width,f.height,f.access_category category,c.collection_key collection FROM fm_file_catalog f JOIN collections c ON c.id=f.collection_id JOIN fm_collection_discovery d ON d.collection_id=c.id AND d.is_marked=TRUE WHERE f.id>@after AND f.is_present=TRUE AND f.canonical_file_id IS NULL AND f.access_category IN ("+catSql+") AND (@selectedCategory IS NULL OR f.access_category=@selectedCategory) AND c.id IN ("+String.Join(",",ownerParams)+") AND NOT EXISTS(SELECT 1 FROM fm_file_dirty pending WHERE pending.file_id=f.id) ORDER BY f.id LIMIT @take";
   var rows=DBRows(db,null,sql,args.ToArray());bool more=rows.Count>limit;if(more)rows.RemoveAt(rows.Count-1);
   var items=rows.Select(f=>new{file_id=f["file_id"],relative_path=f["relative_path"],name=Path.GetFileName(Text(f,"relative_path")),mime=ExternalMime(Text(f,"relative_path")),byte_size=f["byte_size"],width=f["width"],height=f["height"],category=f["category"],collection=f["collection"]}).ToArray();
   return new{items=items,next_after=more&&rows.Count>0?(long?)Convert.ToInt64(rows.Last()["file_id"]):null};
  }
 }
 static object ExternalCollections(HttpContext c){
  var principal=ExternalPrincipal(c);var groups=ExternalGroups(principal);var rights=Gallery.GrantForGroups(groups);if(!rights.allowed)throw new GalleryError(403,"This identity has no Gallery media access.");
  using(var db=OpenDatabase()){
   var policy=Gallery.GetAccessPolicy();var items=DBRows(db,null,"SELECT c.collection_key FROM collections c JOIN fm_collection_discovery d ON d.collection_id=c.id AND d.is_marked=TRUE ORDER BY c.collection_key").Where(x=>Gallery.CollectionAccessAllowed(groups,Text(x,"collection_key"),policy)).Select(x=>Text(x,"collection_key")).ToArray();
   var allowed=new HashSet<string>(items,StringComparer.OrdinalIgnoreCase);var visibleCategories=new HashSet<string>(rights.categories,StringComparer.OrdinalIgnoreCase);
   var categories=items.ToDictionary(x=>x,x=>new List<string>(),StringComparer.OrdinalIgnoreCase);
   if(allowed.Count>0&&visibleCategories.Count>0){
    var rows=DBRows(db,null,"SELECT DISTINCT c.collection_key,f.access_category FROM fm_file_catalog f JOIN collections c ON c.id=f.collection_id JOIN fm_collection_discovery d ON d.collection_id=c.id AND d.is_marked=TRUE WHERE f.is_present=TRUE AND f.canonical_file_id IS NULL AND NOT EXISTS(SELECT 1 FROM fm_file_dirty pending WHERE pending.file_id=f.id) ORDER BY c.collection_key,f.access_category");
    foreach(var row in rows){string key=Text(row,"collection_key"),category=Text(row,"access_category");if(allowed.Contains(key)&&visibleCategories.Contains(category))categories[key].Add(category);}
   }
   return new{collections=items,categories=categories};
  }
 }
 static object ExternalResolveMedia(HttpContext c,Dictionary<string,object> body){
  var principal=ExternalPrincipal(c);var groups=ExternalGroups(principal);var policy=Gallery.GetAccessPolicy();var rights=Gallery.GrantForGroups(groups,policy);if(!rights.allowed)throw new GalleryError(403,"This identity has no Gallery media access.");
  var ids=ExternalIds(body);var access=ExternalFiles(ids,groups,policy);
  return new{items=ids.Select(id=>{Dictionary<string,object> media;bool allowed=access.TryGetValue(id,out media);return new{file_id=id,status=allowed?"allowed":"denied",media=allowed?media:null};}).ToArray()};
 }
 static object ExternalCreateGrant(HttpContext c,Dictionary<string,object> body){
  var principal=ExternalPrincipal(c);var groups=ExternalGroups(principal);var files=ExternalIds(body);string refresh=ExternalText(body,"refresh_token"),device=ExternalText(body,"device_id"),instance=ExternalText(body,"client_instance");if(String.IsNullOrEmpty(refresh)||refresh.Length>8192||String.IsNullOrWhiteSpace(device)||device.Length>128||String.IsNullOrWhiteSpace(instance)||instance.Length>128)throw new GalleryError(400,"A refresh token, client instance, and device ID are required.");
  var refreshed=GalleryAuth.RefreshExternalTokens(refresh);string idToken=ExternalText(refreshed,"id_token");if(String.IsNullOrEmpty(idToken))throw new GalleryError(401,"The identity provider did not refresh the Gallery identity.");var fresh=GalleryAuth.ValidateExternalIdToken(idToken);if(ExternalIssuer(fresh)!=ExternalIssuer(principal)||ExternalSubject(fresh)!=ExternalSubject(principal))throw new GalleryError(401,"The refreshed Gallery identity does not match the signed-in user.");groups=ExternalGroups(fresh);var policy=Gallery.GetAccessPolicy();var access=ExternalFiles(files,groups,policy);var allowed=files.Where(access.ContainsKey).ToList();var denied=files.Where(id=>!access.ContainsKey(id)).ToList();
  string latestRefresh=ExternalText(refreshed,"refresh_token");if(String.IsNullOrEmpty(latestRefresh))latestRefresh=refresh;
  string grant=NewExternalSecret();byte[] hash=ExternalDigest(grant);byte[] encrypted=ProtectExternalRefresh(latestRefresh,hash);long expires=DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds();
  using(var db=OpenDatabase())using(var tx=db.BeginTransaction()){
   DBExec(db,tx,"UPDATE external_device_grants SET state='revoked',refresh_token_encrypted='',updated_at=CURRENT_TIMESTAMP(6) WHERE client_instance_id=@client AND device_id=@device AND oidc_issuer=@issuer AND oidc_sub=@sub AND state='active'","@client",instance,"@device",device,"@issuer",ExternalIssuer(fresh),"@sub",ExternalSubject(fresh));
   DBExec(db,tx,"INSERT INTO external_device_grants(grant_id_hash,client_instance_id,device_id,oidc_issuer,oidc_sub,refresh_token_encrypted,state,expires_at) VALUES(@hash,@client,@device,@issuer,@sub,@refresh,'active',FROM_UNIXTIME(@expires))","@hash",hash,"@client",instance,"@device",device,"@issuer",ExternalIssuer(fresh),"@sub",ExternalSubject(fresh),"@refresh",encrypted,"@expires",expires);
   foreach(long id in allowed)DBExec(db,tx,"INSERT INTO external_device_grant_items(grant_id_hash,file_id) VALUES(@hash,@file)","@hash",hash,"@file",id);tx.Commit();
  }
  // This endpoint is called by the trusted Open Wallpaper server, which also retains the user's OIDC refresh token for later Gallery catalog browsing. Return the rotated value so the caller can replace its prior copy.
  return new{grant_token=grant,device_id=device,client_instance=instance,authorized_file_ids=allowed,denied_file_ids=denied,expires_at=expires,refresh_token=latestRefresh};
 }
 static object ExternalAuthorizeGrant(HttpContext c,Dictionary<string,object> body){
  string grant=c.Request.Headers["X-Gallery-Device-Grant"]??"",device=ExternalText(body,"device_id"),instance=ExternalText(body,"client_instance");if(grant.Length<32||grant.Length>256||device.Length==0||device.Length>128||instance.Length==0||instance.Length>128)throw new GalleryError(401,"A valid Gallery device grant is required.");var ids=ExternalIds(body);byte[] hash=ExternalDigest(grant);Dictionary<string,object> record;
  using(var db=OpenDatabase()){
   using(var tx=db.BeginTransaction()){
    var rows=DBRows(db,tx,"SELECT client_instance_id,device_id,oidc_issuer,oidc_sub,refresh_token_encrypted,state,expires_at FROM external_device_grants WHERE grant_id_hash=@hash FOR UPDATE","@hash",hash);if(rows.Count!=1)throw new GalleryError(401,"Gallery device grant is unavailable.");record=rows[0];
    if(Text(record,"state")!="active"||Text(record,"client_instance_id")!=instance||Text(record,"device_id")!=device||Convert.ToDateTime(record["expires_at"]).ToUniversalTime()<=DateTime.UtcNow)throw new GalleryError(401,"Gallery device grant is unavailable.");
    var items=new HashSet<long>(DBRows(db,tx,"SELECT file_id FROM external_device_grant_items WHERE grant_id_hash=@hash","@hash",hash).Select(x=>Convert.ToInt64(x["file_id"])));
    string storedRefresh=UnprotectExternalRefresh((byte[])record["refresh_token_encrypted"],hash);var refreshed=GalleryAuth.RefreshExternalTokens(storedRefresh);string idToken=ExternalText(refreshed,"id_token");if(String.IsNullOrEmpty(idToken))throw new GalleryError(503,"Gallery identity could not be refreshed.");var principal=GalleryAuth.ValidateExternalIdToken(idToken);
    if(ExternalIssuer(principal)!=Text(record,"oidc_issuer")||ExternalSubject(principal)!=Text(record,"oidc_sub"))throw new GalleryError(401,"Gallery device grant identity changed.");
    string next=ExternalText(refreshed,"refresh_token");if(String.IsNullOrEmpty(next))next=storedRefresh;DBExec(db,tx,"UPDATE external_device_grants SET refresh_token_encrypted=@refresh,expires_at=FROM_UNIXTIME(@expires),updated_at=CURRENT_TIMESTAMP(6) WHERE grant_id_hash=@hash","@refresh",ProtectExternalRefresh(next,hash),"@expires",DateTimeOffset.UtcNow.AddYears(1).ToUnixTimeSeconds(),"@hash",hash);tx.Commit();
    var groups=ExternalGroups(principal);var policy=Gallery.GetAccessPolicy();var access=ExternalFiles(ids.Where(items.Contains),groups,policy);var results=ids.Select(id=>{Dictionary<string,object> file;bool assigned=items.Contains(id),allowed=access.TryGetValue(id,out file);return new{file_id=id,status=!assigned?"not_assigned":allowed?"allowed":"denied",media=allowed?file:null};}).ToArray();return new{device_id=device,items=results};
   }
  }
 }
 static object ExternalRevokeGrant(HttpContext c,Dictionary<string,object> body){var principal=ExternalPrincipal(c);string grant=ExternalText(body,"grant_token");if(grant.Length<32||grant.Length>256)throw new GalleryError(400,"A Gallery device grant is required.");byte[] hash=ExternalDigest(grant);using(var db=OpenDatabase()){int count=DBExec(db,null,"UPDATE external_device_grants SET state='revoked',refresh_token_encrypted='',updated_at=CURRENT_TIMESTAMP(6) WHERE grant_id_hash=@hash AND ((oidc_issuer=@issuer AND oidc_sub=@sub) OR @admin=1)","@hash",hash,"@issuer",ExternalIssuer(principal),"@sub",ExternalSubject(principal),"@admin",Gallery.GrantForGroups(ExternalGroups(principal)).admin?1:0);if(count==0)throw new GalleryError(404,"Gallery device grant not found.");}return new{revoked=true};}
 public static void ProcessExternal(HttpContext c){
  c.Response.Cache.SetNoStore();c.Response.Cache.SetCacheability(HttpCacheability.NoCache);c.Response.Headers["X-Content-Type-Options"]="nosniff";c.Response.ContentType="application/json; charset=utf-8";
  try{
   string path=c.Request.Path.TrimEnd('/').ToLowerInvariant();object result;
   if(path=="/api/external/media"&&c.Request.HttpMethod=="GET")result=ExternalCatalog(c);
   else if(path=="/api/external/media/resolve"&&c.Request.HttpMethod=="POST")result=ExternalResolveMedia(c,ReadExternalBody(c));
   else if(path=="/api/external/collections"&&c.Request.HttpMethod=="GET")result=ExternalCollections(c);
   else if(path=="/api/external/grants"&&c.Request.HttpMethod=="POST")result=ExternalCreateGrant(c,ReadExternalBody(c));
   else if(path=="/api/external/grants/authorize"&&c.Request.HttpMethod=="POST")result=ExternalAuthorizeGrant(c,ReadExternalBody(c));
   else if(path=="/api/external/grants/revoke"&&c.Request.HttpMethod=="POST")result=ExternalRevokeGrant(c,ReadExternalBody(c));
   else throw new GalleryError(404,"Unknown external media endpoint.");
   c.Response.StatusCode=200;c.Response.Write(Json.Serialize(result));
  }catch(GalleryError e){c.Response.StatusCode=e.Code;c.Response.TrySkipIisCustomErrors=true;c.Response.Write(Json.Serialize(new{error=e.Message}));}catch{c.Response.StatusCode=503;c.Response.TrySkipIisCustomErrors=true;c.Response.Write(Json.Serialize(new{error="Gallery external media service is temporarily unavailable."}));}
 }
 static Dictionary<string,object> ReadExternalBody(HttpContext c){if(c.Request.ContentLength>131072)throw new GalleryError(400,"Request is too large.");try{return Json.Deserialize<Dictionary<string,object>>(new StreamReader(c.Request.InputStream).ReadToEnd());}catch{throw new GalleryError(400,"Invalid request body.");}}
}
