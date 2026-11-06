using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Configuration;
using System.Net;
using System.Net.Http;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Threading;
using System.Web;
using System.Web.Security;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;

public static class GalleryAuth {
 const string SessionCookie="__Host-WallpaperGallery",FlowCookie="__Host-WallpaperGalleryFlow";
 static readonly object ConfigLock=new object();
 static Dictionary<string,object> config;
 static Microsoft.IdentityModel.Protocols.ConfigurationManager<OpenIdConnectConfiguration> discovery;
 public static bool Enabled {get{return ConfigurationManager.AppSettings["GalleryAuthEnabled"]=="true";}}
 static Dictionary<string,object> Config {get{lock(ConfigLock){if(config==null)config=Gallery.Json.Deserialize<Dictionary<string,object>>(File.ReadAllText(ConfigurationManager.AppSettings["GalleryAuthConfig"]));return config;}}}
 static string Setting(string key){return Gallery.Text(Config,key);}
 public static string ExternalClientId {get{return Setting("externalClientId");}}
 public static string PublicUrl {get{return Enabled?Setting("publicUrl").TrimEnd('/'):"";}}
 static long UnixNow {get{return DateTimeOffset.UtcNow.ToUnixTimeSeconds();}}
 static string Random(){byte[] b=new byte[32];using(var rng=RandomNumberGenerator.Create())rng.GetBytes(b);return Base64UrlEncoder.Encode(b);}
 static bool Equal(string a,string b){byte[] x=System.Text.Encoding.UTF8.GetBytes(a??""),y=System.Text.Encoding.UTF8.GetBytes(b??"");if(x.Length!=y.Length)return false;int diff=0;for(int i=0;i<x.Length;i++)diff|=x[i]^y[i];return diff==0;}
 static HttpCookie Cookie(string name,string value,DateTime expires){return new HttpCookie(name,value){Secure=true,HttpOnly=true,SameSite=SameSiteMode.Lax,Path="/",Expires=expires};}
 static void SetCookie(HttpContext c,string name,object value,int seconds){byte[] bytes=System.Text.Encoding.UTF8.GetBytes(Gallery.Json.Serialize(value));string protectedValue=Base64UrlEncoder.Encode(MachineKey.Protect(bytes,"WallpaperGallery",name));c.Response.Cookies.Set(Cookie(name,protectedValue,DateTime.UtcNow.AddSeconds(seconds)));}
 static Dictionary<string,object> ReadCookie(HttpContext c,string name){try{var cookie=c.Request.Cookies[name];if(cookie==null||cookie.Value.Length>16000)return null;byte[] data=MachineKey.Unprotect(Base64UrlEncoder.DecodeBytes(cookie.Value),"WallpaperGallery",name);var value=Gallery.Json.Deserialize<Dictionary<string,object>>(System.Text.Encoding.UTF8.GetString(data));if(Convert.ToInt64(value["expires"])<=UnixNow)return null;return value;}catch{return null;}}
 static void Expire(HttpContext c,string name){c.Response.Cookies.Set(Cookie(name,"",DateTime.UtcNow.AddDays(-1)));}
 static OpenIdConnectConfiguration Metadata(bool refresh=false){lock(ConfigLock){if(discovery==null)discovery=new Microsoft.IdentityModel.Protocols.ConfigurationManager<OpenIdConnectConfiguration>(Setting("authority").TrimEnd('/')+"/.well-known/openid-configuration",new OpenIdConnectConfigurationRetriever(),new HttpDocumentRetriever(){RequireHttps=true});if(refresh)discovery.RequestRefresh();}return discovery.GetConfigurationAsync(CancellationToken.None).GetAwaiter().GetResult();}
 public static ClaimsPrincipal ValidateExternalIdToken(string token){
  string audience=ExternalClientId;if(String.IsNullOrWhiteSpace(audience))throw new GalleryError(503,"External media integration is not configured.");
  var md=Metadata();var handler=new JwtSecurityTokenHandler(){MapInboundClaims=false};SecurityToken validated;ClaimsPrincipal principal=null;
  for(int attempt=0;attempt<2;attempt++)try{principal=handler.ValidateToken(token,new TokenValidationParameters{ValidateIssuerSigningKey=true,IssuerSigningKeys=md.SigningKeys,ValidateIssuer=true,ValidIssuer=Setting("authority").TrimEnd('/')+"/",ValidateAudience=true,ValidAudience=audience,RequireSignedTokens=true,RequireExpirationTime=true,ValidateLifetime=true,ClockSkew=TimeSpan.FromSeconds(60),ValidAlgorithms=new[]{SecurityAlgorithms.RsaSha256}},out validated);break;}catch(SecurityTokenSignatureKeyNotFoundException){if(attempt!=0)throw;md=Metadata(true);}
  var subject=principal==null?null:principal.FindFirst("sub");if(subject==null||String.IsNullOrWhiteSpace(subject.Value))throw new GalleryError(401,"The identity token is invalid.");
  var jwt=handler.ReadJwtToken(token);var azp=principal.FindFirst("azp");if((jwt.Audiences.Count()>1&&azp==null)||(azp!=null&&azp.Value!=audience))throw new GalleryError(401,"The identity token was issued for another application.");
  return principal;
 }
 public static Dictionary<string,object> RefreshExternalTokens(string refreshToken){
  string clientId=ExternalClientId,clientSecret=Setting("externalClientSecret");if(String.IsNullOrWhiteSpace(clientId)||String.IsNullOrWhiteSpace(clientSecret))throw new GalleryError(503,"External media integration is not configured.");
  var md=Metadata();using(var client=new HttpClient()){client.Timeout=TimeSpan.FromSeconds(15);var data=new Dictionary<string,string>{{"grant_type","refresh_token"},{"refresh_token",refreshToken},{"client_id",clientId},{"client_secret",clientSecret}};var response=client.PostAsync(md.TokenEndpoint,new FormUrlEncodedContent(data)).GetAwaiter().GetResult();if(!response.IsSuccessStatusCode)throw new GalleryError(401,"Gallery identity needs to be refreshed by the user.");return Gallery.Json.Deserialize<Dictionary<string,object>>(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());}
 }
 public static string Role(HttpContext c){return Convert.ToString(c.Items["GalleryRole"]??(Enabled?"":"admins"));}
 public static bool IsAdmin(HttpContext c){return Role(c)=="admins";}
 public static string[] Groups(HttpContext c){return c.Items["GalleryGroups"] as string[]??new string[0];}
 public static string[] Categories(HttpContext c){return c.Items["GalleryCategories"] as string[]??(Enabled?new string[0]:Gallery.AccessCategories);}
 public static bool NeedsAdmin(string path,string method,string rescan){
  string route=HttpUtility.UrlDecode(path).TrimEnd('/').ToLowerInvariant();if(route.Split('/').Any(part=>new[]{"access.html","access.js","status.html","status.js","recovery.js"}.Contains(part.Split(';')[0].TrimEnd(' ','.'))))return true;if(!route.StartsWith("/api/"))return false;route=route.Substring(5);
  return (method!="GET"&&method!="HEAD") || !(route=="image"||((route=="inventory"||route=="inventory-meta"||route=="entry-files"||route=="asset")&&rescan!="1"));
 }
 static void Deny(HttpContext c,int code,string message){c.Response.StatusCode=code;c.Response.TrySkipIisCustomErrors=true;c.Response.SuppressFormsAuthenticationRedirect=true;c.Response.ContentType="application/json";c.Response.Write(Gallery.Json.Serialize(new{error=message,login=PublicUrl+"/auth/login"}));c.ApplicationInstance.CompleteRequest();}
 public static void Authorize(HttpContext c){
  if(!Enabled)return;
  string path=c.Request.Path.ToLowerInvariant();
  // The external API uses validated bearer identity or grant credentials and never browser cookies.
  if(path.StartsWith("/api/external/"))return;
  // Only the local queue guardian may poll its dedicated endpoint without a browser session.
  IPAddress address;bool loopback=IPAddress.TryParse(c.Request.UserHostAddress,out address)&&IPAddress.IsLoopback(address);
  if(loopback&&c.Request.HttpMethod=="GET"&&path=="/api/downloads"&&Setting("workerKey").Length>=32&&Equal(c.Request.Headers["X-Gallery-Worker"],Setting("workerKey"))){c.Items["GalleryRole"]="worker";return;}
  if(path=="/auth/login"||path=="/auth/callback"||path=="/auth/signed-out"||path=="/auth/logout")return;
  var session=ReadCookie(c,SessionCookie);
  if(session==null||!session.ContainsKey("groups")){if(path.StartsWith("/api/")||path=="/auth/session"||path=="/auth/session.js"){Deny(c,401,"Sign in to the gallery.");return;}string next=new[]{"/status.html","/status.js","/recovery.js"}.Contains(path)?"?next=%2Fstatus.html":"";c.Response.Redirect(PublicUrl+"/auth/login"+next,false);c.ApplicationInstance.CompleteRequest();return;}
  var groups=Gallery.Json.Deserialize<string[]>(Gallery.Json.Serialize(session["groups"]));var grant=Gallery.GrantForGroups(groups);if(!grant.allowed){Deny(c,403,"Your account does not have gallery access.");return;}string role=grant.admin?"admins":"users";c.Items["GalleryGroups"]=groups;c.Items["GalleryCategories"]=grant.categories;
  // Renew the short browser session while requests continue, with a fixed sign-in limit.
  long absolute=Convert.ToInt64(session.ContainsKey("absolute")?session["absolute"]:session["expires"]);
  if(absolute<=UnixNow){Expire(c,SessionCookie);Deny(c,401,"Sign in to the gallery.");return;}
  if(Convert.ToInt64(session["expires"])<UnixNow+300){long expiry=Math.Min(absolute,UnixNow+900);SetCookie(c,SessionCookie,new{subject=Gallery.Text(session,"subject"),name=Gallery.Text(session,"name"),groups=groups,expires=expiry,absolute=absolute},Math.Max(1,(int)(expiry-UnixNow)));}
  c.Items["GalleryRole"]=role;c.Items["GalleryName"]=Gallery.Text(session,"name");c.User=new GenericPrincipal(new GenericIdentity(Gallery.Text(session,"name"),"Authentik"),role=="admins"?new[]{"admins","users"}:new[]{"users"});Thread.CurrentPrincipal=c.User;
  if(NeedsAdmin(path,c.Request.HttpMethod,c.Request.QueryString["rescan"])&&role!="admins")Deny(c,403,"Only gallery administrators can perform this action.");
 }
 public static object Session(HttpContext c){return new{authenticated=true,admin=IsAdmin(c),name=Convert.ToString(c.Items["GalleryName"]??"Local gallery"),role=Role(c),categories=Categories(c),legacy_downloads=ConfigurationManager.AppSettings["GalleryLegacyDownloads"]=="true"};}
 public static void Login(HttpContext c){
  if(!Enabled){c.Response.Redirect("/",false);return;}
  // Never trust a caller-supplied Host or forwarded URL when constructing callbacks.
  var origin=new Uri(PublicUrl);if(c.Request.Url.Host!=origin.Host){c.Response.Redirect(PublicUrl+"/auth/login",false);return;}
  string state=Random(),nonce=Random(),verifier=Random(),challenge;using(var sha=SHA256.Create())challenge=Base64UrlEncoder.Encode(sha.ComputeHash(System.Text.Encoding.ASCII.GetBytes(verifier)));
  string next=c.Request.QueryString["next"]=="/status.html"?"/status.html":"/";
  SetCookie(c,FlowCookie,new{state=state,nonce=nonce,verifier=verifier,next=next,expires=UnixNow+300},300);
  var md=Metadata();c.Response.Redirect(md.AuthorizationEndpoint+"?client_id="+HttpUtility.UrlEncode(Setting("clientId"))+"&redirect_uri="+HttpUtility.UrlEncode(PublicUrl+"/auth/callback")+"&response_type=code&scope=openid%20profile%20email%20gallery_roles&state="+state+"&nonce="+nonce+"&code_challenge_method=S256&code_challenge="+challenge,false);
 }
 public static void Callback(HttpContext c){
  var flow=ReadCookie(c,FlowCookie);Expire(c,FlowCookie);
  if(flow==null||!Equal(Gallery.Text(flow,"state"),c.Request.QueryString["state"])||String.IsNullOrEmpty(c.Request.QueryString["code"]))throw new GalleryError(400,"The sign-in attempt expired or was not valid. Open the gallery and sign in again.");
  var md=Metadata();Dictionary<string,object> tokens;
  using(var client=new HttpClient()){client.Timeout=TimeSpan.FromSeconds(30);var response=client.PostAsync(md.TokenEndpoint,new FormUrlEncodedContent(new Dictionary<string,string>{{"grant_type","authorization_code"},{"client_id",Setting("clientId")},{"client_secret",Setting("clientSecret")},{"code",c.Request.QueryString["code"]},{"redirect_uri",PublicUrl+"/auth/callback"},{"code_verifier",Gallery.Text(flow,"verifier")}})).GetAwaiter().GetResult();if(!response.IsSuccessStatusCode)throw new GalleryError(401,"Authentik did not accept the sign-in attempt. Start sign-in again.");tokens=Gallery.Json.Deserialize<Dictionary<string,object>>(response.Content.ReadAsStringAsync().GetAwaiter().GetResult());}
  string idToken=Gallery.Text(tokens,"id_token");var handler=new JwtSecurityTokenHandler(){MapInboundClaims=false};SecurityToken validated;ClaimsPrincipal principal=null;
  for(int attempt=0;attempt<2;attempt++)try{principal=handler.ValidateToken(idToken,new TokenValidationParameters{ValidateIssuerSigningKey=true,IssuerSigningKeys=md.SigningKeys,ValidateIssuer=true,ValidIssuer=Setting("authority").TrimEnd('/')+"/",ValidateAudience=true,ValidAudience=Setting("clientId"),RequireSignedTokens=true,RequireExpirationTime=true,ValidateLifetime=true,ClockSkew=TimeSpan.FromSeconds(60),ValidAlgorithms=new[]{SecurityAlgorithms.RsaSha256}},out validated);break;}catch(SecurityTokenSignatureKeyNotFoundException){if(attempt!=0)throw;md=Metadata(true);}
  if(principal==null||!Equal(principal.FindFirst("nonce")==null?"":principal.FindFirst("nonce").Value,Gallery.Text(flow,"nonce")))throw new GalleryError(401,"The sign-in response was not valid.");
  var jwt=handler.ReadJwtToken(idToken);if(String.IsNullOrEmpty(jwt.Subject))throw new GalleryError(401,"The sign-in response did not identify an account.");
  var azp=principal.FindFirst("azp");if((jwt.Audiences.Count()>1&&azp==null)||(azp!=null&&azp.Value!=Setting("clientId")))throw new GalleryError(401,"The sign-in response was for another application.");
  var groups=principal.FindAll("gallery_groups").Select(x=>x.Value).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();var grant=Gallery.GrantForGroups(groups);
  if(!grant.allowed)throw new GalleryError(403,"Your account does not have gallery access. Contact a gallery administrator.");
  long expiry=UnixNow+900,absolute=UnixNow+86400;var name=principal.FindFirst("preferred_username")??principal.FindFirst("name")??principal.FindFirst("sub");
  SetCookie(c,SessionCookie,new{subject=jwt.Subject,name=name.Value,groups=groups,expires=expiry,absolute=absolute},Math.Max(1,(int)(expiry-UnixNow)));c.Response.Redirect(PublicUrl+(Gallery.Text(flow,"next")=="/status.html"?"/status.html":"/"),false);
 }
 public static void Logout(HttpContext c){if(c.Request.HttpMethod!="POST"||c.Request.Headers["Origin"]!=PublicUrl||c.Request.Headers["X-Gallery-Request"]!="1")throw new GalleryError(403,"Use the gallery sign-out control.");Expire(c,SessionCookie);c.Response.ContentType="application/json";c.Response.Write("{\"signedOut\":true}");}
}
public class GalleryAuthModule:IHttpModule {
 public void Init(HttpApplication application){application.AuthenticateRequest+=(sender,args)=>{var app=(HttpApplication)sender;try{GalleryAuth.Authorize(app.Context);}catch{app.Context.Response.StatusCode=503;app.Context.Response.TrySkipIisCustomErrors=true;app.Context.Response.ContentType="text/plain";app.Context.Response.Write("Gallery sign-in is temporarily unavailable.");app.CompleteRequest();}};}
 public void Dispose(){}
}
public class GalleryAuthHandler:IHttpHandler {
 public bool IsReusable {get{return true;}}
 public void ProcessRequest(HttpContext c){c.Response.Cache.SetCacheability(HttpCacheability.NoCache);c.Response.Cache.SetNoStore();c.Response.Headers["X-Content-Type-Options"]="nosniff";try{string route=c.Request.Path.TrimEnd('/').ToLowerInvariant();if(route=="/auth/login")GalleryAuth.Login(c);else if(route=="/auth/callback")GalleryAuth.Callback(c);else if(route=="/auth/logout")GalleryAuth.Logout(c);else if(route=="/auth/signed-out"){c.Response.ContentType="text/html; charset=utf-8";c.Response.Write("<!doctype html><title>Signed out</title><h1>Signed out of the gallery</h1><p><a href=\"/auth/login\">Sign in</a></p>");}else if(route=="/auth/session.js"){c.Response.ContentType="application/javascript";c.Response.Write("window.gallerySession="+Gallery.Json.Serialize(GalleryAuth.Session(c))+";");}else{c.Response.ContentType="application/json";c.Response.Write(Gallery.Json.Serialize(GalleryAuth.Session(c)));}}catch(GalleryError e){c.Response.StatusCode=e.Code;c.Response.TrySkipIisCustomErrors=true;c.Response.ContentType="text/plain";c.Response.Write(e.Message);}catch{c.Response.StatusCode=401;c.Response.TrySkipIisCustomErrors=true;c.Response.ContentType="text/plain";c.Response.Write("Sign-in could not be completed. Open the gallery and try signing in again.");}}
}
