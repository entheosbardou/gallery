using System;using System.Net;using System.Threading;using System.IO;using System.Web.Script.Serialization;using System.Collections.Generic;
class QueueGuardian {
 [STAThread]static void Main(){
  bool created;using(var mutex=new Mutex(true,"Local\\WallpaperGalleryQueueGuardian",out created)) {
   if(!created)return;
   for(;;){
    try{var request=(HttpWebRequest)WebRequest.Create("http://localhost:8791/api/downloads");request.Headers["X-Gallery-Request"]="1";var config=new JavaScriptSerializer().Deserialize<Dictionary<string,object>>(File.ReadAllText(@"C:\downloads\gallery\iis-data\oidc.json"));request.Headers["X-Gallery-Worker"]=Convert.ToString(config["workerKey"]);request.Timeout=15000;request.ReadWriteTimeout=15000;using(var response=request.GetResponse()){} }catch{}
    Thread.Sleep(15000);
   }
  }
 }
}
