using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Net;
using System.Collections;
using System.Collections.Generic;
using System.Security.Cryptography;

public static partial class Gallery {
    public static object SourcePreview(Dictionary<string,object> payload) {
        Dictionary<string,object> row;
        lock(Sync) {
            var c=Find(Text(payload,"collection"));int index;
            index=PayloadIndex(c,payload);
            row=new Dictionary<string,object>(c.Records[index]);
            if(Text(row,"id")!=Text(payload,"id")||Text(row,"site")!=Text(payload,"site"))throw new GalleryError(409,"The entry changed. Refresh before loading its preview.");
        }
        string cacheKey;using(var sha=SHA256.Create())cacheKey=BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(Json.Serialize(row)))).Replace("-","");
        string folder=Path.Combine(DuplicateData,"source-previews"),cache=Path.Combine(folder,cacheKey+".json");
        if(File.Exists(cache)&&File.GetLastWriteTimeUtc(cache)>DateTime.UtcNow.AddHours(-12))try{return Json.DeserializeObject(File.ReadAllText(cache));}catch{}
        object result;
        if(Text(row,"site")=="Wallhaven")result=Wallhaven(Text(row,"id"),"");
        else if(Text(row,"site")=="Reddit") {
            string id=PostKey(row),body;
            if(id=="")throw new GalleryError(404,"No source preview is available for this entry.");
            using(var response=Open(new Uri("https://www.reddit.com/comments/"+Uri.EscapeDataString(id)+".json?raw_json=1&limit=1"),RedditCookies()))using(var reader=new StreamReader(response.GetResponseStream()))body=reader.ReadToEnd();
            var images=ExtractRedditImages(body,id);
            string original=images.FirstOrDefault(),thumb=null;
            var listings=RedditItems(Json.DeserializeObject(body));
            foreach(var listing in listings) {
                var data=RedditDict(RedditValue(RedditDict(listing),"data"));
                foreach(var child in RedditItems(RedditValue(data,"children"))) {
                    var post=RedditDict(RedditValue(RedditDict(child),"data"));if(post==null||Text(post,"id")!=id)continue;
                    var preview=RedditDict(RedditValue(post,"preview"));
                    foreach(var image in RedditItems(RedditValue(preview,"images"))) {
                        var item=RedditDict(image);var source=RedditDict(RedditValue(item,"source"));
                        thumb=WebUtility.HtmlDecode(Text(source,"url"));
                        foreach(var resolution in RedditItems(RedditValue(item,"resolutions"))) {var r=RedditDict(resolution);int width;if(Int32.TryParse(Text(r,"width"),out width)&&width>=320){thumb=WebUtility.HtmlDecode(Text(r,"url"));break;}}
                        if(!String.IsNullOrEmpty(thumb))break;
                    }
                }
            }
            if(String.IsNullOrEmpty(original))original=thumb;
            if(String.IsNullOrEmpty(original))throw new GalleryError(404,"Reddit did not provide an image preview for this entry.");
            ValidateUrl(new Uri(original));if(!String.IsNullOrEmpty(thumb))ValidateUrl(new Uri(thumb));
            result=new {path=original,thumb=String.IsNullOrEmpty(thumb)?original:thumb};
        } else {
            string url=Text(row,"url");Uri uri;
            if(!Uri.TryCreate(url,UriKind.Absolute,out uri))throw new GalleryError(404,"No image preview is available from this source.");
            ValidateUrl(uri);
            if(!System.Text.RegularExpressions.Regex.IsMatch(uri.AbsolutePath,@"\.(?:png|jpe?g|gif|webp)$",System.Text.RegularExpressions.RegexOptions.IgnoreCase))throw new GalleryError(404,"No direct image preview is available from this source.");
            result=new {path=url,thumb=url};
        }
        Directory.CreateDirectory(folder);string temp=cache+"."+Guid.NewGuid().ToString("N")+".tmp";
        try{File.WriteAllText(temp,Json.Serialize(result));if(File.Exists(cache))File.Replace(temp,cache,null);else File.Move(temp,cache);}finally{if(File.Exists(temp))File.Delete(temp);}
        return result;
    }
    static Dictionary<string,object> RedditDict(object value) {return value as Dictionary<string,object>;}
    static IEnumerable<object> RedditItems(object value) {var sequence=value as IEnumerable;if(sequence!=null&&!(value is string))foreach(object item in sequence)yield return item;}
    static object RedditValue(Dictionary<string,object> value,string key) {object result;return value!=null&&value.TryGetValue(key,out result) ? result : null;}
    static void AddRedditImage(List<string> urls,string value) {
        value=WebUtility.HtmlDecode(value);Uri uri;
        if(!Uri.TryCreate(value,UriKind.Absolute,out uri)||!ImageFile(uri.AbsolutePath))return;
        try {ValidateUrl(uri);}catch{return;}
        if(!urls.Contains(value,StringComparer.Ordinal))urls.Add(value);
    }
    static void RedditMedia(Dictionary<string,object> post,List<string> urls,int depth=0) {
        if(post==null||depth>2)return;
        var gallery=RedditDict(RedditValue(post,"gallery_data"));var metadata=RedditDict(RedditValue(post,"media_metadata"));
        if(gallery!=null&&metadata!=null)foreach(object raw in RedditItems(RedditValue(gallery,"items"))) {
            var item=RedditDict(raw);var media=RedditDict(RedditValue(metadata,Text(item ?? new Dictionary<string,object>(),"media_id")));var source=RedditDict(RedditValue(media,"s"));
            if(source!=null)AddRedditImage(urls,Text(source,"u")!="" ? Text(source,"u") : Text(source,"gif"));
        }
        AddRedditImage(urls,Text(post,"url_overridden_by_dest"));if(urls.Count==0)AddRedditImage(urls,Text(post,"url"));
        if(urls.Count==0)foreach(object parent in RedditItems(RedditValue(post,"crosspost_parent_list")))RedditMedia(RedditDict(parent),urls,depth+1);
        if(urls.Count==0&&Text(post,"is_video")!="True") {
            var preview=RedditDict(RedditValue(post,"preview"));
            foreach(object raw in RedditItems(RedditValue(preview,"images"))) {var image=RedditDict(raw);var source=RedditDict(RedditValue(image,"source"));if(source!=null)AddRedditImage(urls,Text(source,"url"));}
        }
    }
    public static List<string> ExtractRedditImages(string json,string expectedId) {
        var urls=new List<string>();var root=Json.DeserializeObject(json);
        foreach(object raw in RedditItems(root)) {
            var listing=RedditDict(raw);var data=RedditDict(RedditValue(listing,"data"));
            foreach(object child in RedditItems(RedditValue(data,"children"))) {
                var wrapper=RedditDict(child);var post=RedditDict(RedditValue(wrapper,"data"));
                if(post!=null&&Text(post,"id")==expectedId) {RedditMedia(post,urls);return urls.Take(50).ToList();}
            }
        }
        throw new GalleryError(502,"Reddit did not return the requested post. No inventory entries were changed.");
    }
    public static object ResolveRedditPost(Dictionary<string,object> payload) {
        string collectionId=Text(payload,"collection"),id=Text(payload,"id");int index;
        index=-1;
        lock(Sync) {
            var c=Find(collectionId);index=PayloadIndex(c,payload);
            if(index<0||index>=c.Records.Count||Text(c.Records[index],"id")!=id)throw new GalleryError(409,"The inventory changed. Refresh and retry.");
            var record=c.Records[index];
            if(Text(record,"site")!="Reddit"||!new[]{"post","album","post_or_album"}.Contains(Text(record,"record_type"))||PostKey(record)!=id)throw new GalleryError(400,"Select a Reddit post or album.");
        }
        List<string> urls;
        try {
            using(var response=Open(new Uri("https://www.reddit.com/comments/"+Uri.EscapeDataString(id)+".json?raw_json=1&limit=1"),RedditCookies()))
            using(var reader=new StreamReader(response.GetResponseStream()))urls=ExtractRedditImages(reader.ReadToEnd(),id);
        } catch(WebException error) {
            var response=error.Response as HttpWebResponse;int code=response==null ? 0 : (int)response.StatusCode;if(response!=null)response.Close();
            string message=code==401||code==403 ? "Reddit denied access to this post (HTTP "+code+"). The saved login may need attention on Reddit, or the post may be restricted. Previously saved images are kept." : code==429 ? "Reddit is rate-limiting requests. Wait a little and retry." : "Reddit could not return this post"+(code>0 ? " (HTTP "+code+")" : "")+". Retry later or open the source.";
            lock(Sync) {var c=Find(collectionId);index=PayloadIndex(c,payload);if(index<c.Records.Count&&Text(c.Records[index],"id")==id&&ResolveReference(c,c.Records[index])==null) {var r=c.Records[index];if(code!=401&&code!=403){r["error"]=message;}r["last_retry_utc"]=DateTime.UtcNow.ToString("o");Save(c);}}
            throw new GalleryError(502,message);
        }
        if(urls.Count==0) {
            string message="The post is accessible, but Reddit returned no downloadable images. Open the source to check its media.";
            lock(Sync) {var current=Find(collectionId);if(index<current.Records.Count&&Text(current.Records[index],"id")==id&&ResolveReference(current,current.Records[index])==null) {var record=current.Records[index];record["error"]=message;record["last_retry_utc"]=DateTime.UtcNow.ToString("o");Save(current);}}
            throw new GalleryError(400,message);
        }
        lock(Sync) {
            var c=Find(collectionId);index=PayloadIndex(c,payload);
            if(index>=c.Records.Count||Text(c.Records[index],"id")!=id)throw new GalleryError(409,"The inventory changed. Refresh and retry.");
            var post=c.Records[index];var images=new List<object>();int position=0;
            foreach(string url in urls) {
                position++;
                var child=c.Records.FirstOrDefault(r=>Text(r,"record_type")=="image"&&Text(r,"site")=="Reddit"&&PostKey(r)==id&&Text(r,"url")==url);
                if(child==null) {
                    string childId=id+"-"+position;
                    if(c.Records.Any(r=>Text(r,"site")=="Reddit"&&Text(r,"id")==childId)) {
                        using(var sha=SHA256.Create())childId+="-"+BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(url))).Replace("-","").Substring(0,8).ToLowerInvariant();
                    }
                    child=new Dictionary<string,object>{{"site","Reddit"},{"id",childId},{"url",url},{"source",Text(post,"source")},{"description",Text(post,"description")},{"record_type","image"},{"status",Text(post,"status")},{"category",Text(post,"category")}};
                    c.Records.Add(child);
                }
                var output=new Dictionary<string,object>(child);output["collection"]=c.Id;output["inventory_index"]=c.Records.IndexOf(child);images.Add(output);
            }
            post["original_image_urls"]=urls.ToArray();post["image_count"]=urls.Count;post["last_retry_utc"]=DateTime.UtcNow.ToString("o");post.Remove("error");post.Remove("reason");
            
            Reconcile(c);Save(c);
            return new {status="resolved",images=images,count=images.Count};
        }
    }
}
