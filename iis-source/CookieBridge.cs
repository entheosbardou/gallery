using System;
using System.IO;
using System.Text;
using System.Linq;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.IO.Pipes;
using System.Web.Script.Serialization;
using System.Security.Cryptography;

// Runs in the signed-in user's session. Only the gallery's IIS pool can request
// Reddit cookies from its dedicated profile. Personal browser profiles are never read.
class CookieBridge {
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_open_v2(byte[] filename,out IntPtr db,int flags,IntPtr vfs);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_close(IntPtr db);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_prepare_v2(IntPtr db,byte[] sql,int length,out IntPtr statement,IntPtr tail);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_step(IntPtr statement);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] static extern IntPtr sqlite3_column_text(IntPtr statement,int column);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] static extern IntPtr sqlite3_column_blob(IntPtr statement,int column);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_column_bytes(IntPtr statement,int column);
    [DllImport("winsqlite3.dll",CallingConvention=CallingConvention.Cdecl)] static extern int sqlite3_finalize(IntPtr statement);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool GetNamedPipeClientComputerName(IntPtr pipe,StringBuilder computer,int length);
    static readonly JavaScriptSerializer Json=new JavaScriptSerializer();
    static string Column(IntPtr statement,int column) {
        int count=sqlite3_column_bytes(statement,column);if(count==0)return "";
        byte[] bytes=new byte[count];Marshal.Copy(sqlite3_column_text(statement,column),bytes,0,count);return Encoding.UTF8.GetString(bytes);
    }
    static bool Domain(string domain) {
        domain=domain.TrimStart('.').ToLowerInvariant();
        return domain=="reddit.com"||domain.EndsWith(".reddit.com")||domain=="redd.it"||domain.EndsWith(".redd.it");
    }
    static List<object> Read(string filename,bool firefox) {
        var cookies=new List<object>();IntPtr db=IntPtr.Zero,statement=IntPtr.Zero;
        if(!File.Exists(filename))return cookies;
        try {
            // SQLITE_OPEN_READONLY. A locked or unreadable profile is skipped.
            if(sqlite3_open_v2(Encoding.UTF8.GetBytes(filename+"\0"),out db,1,IntPtr.Zero)!=0)return cookies;
            string filter="(host = 'reddit.com' OR host LIKE '%.reddit.com' OR host = 'redd.it' OR host LIKE '%.redd.it')";
            string sql=firefox ? "SELECT host,path,name,value,expiry FROM moz_cookies WHERE "+filter :
                "SELECT host_key,path,name,value,expires_utc,encrypted_value FROM cookies WHERE "+filter.Replace("host","host_key");
            if(sqlite3_prepare_v2(db,Encoding.UTF8.GetBytes(sql+"\0"),-1,out statement,IntPtr.Zero)!=0)return cookies;
            while(sqlite3_step(statement)==100) {
                string domain=Column(statement,0);if(!Domain(domain))continue;
                string name=Column(statement,2),value=Column(statement,3);long expires;
                if(Int64.TryParse(Column(statement,4),out expires)&&expires>0) {
                    long now=firefox ? (long)(DateTime.UtcNow-new DateTime(1970,1,1)).TotalSeconds : (long)(DateTime.UtcNow-new DateTime(1601,1,1)).TotalMilliseconds*1000;
                    if(expires<now)continue;
                }
                if(!firefox&&value=="") {
                    int length=sqlite3_column_bytes(statement,5);if(length==0)continue;
                    byte[] encrypted=new byte[length];Marshal.Copy(sqlite3_column_blob(statement,5),encrypted,0,length);
                    // Modern Chromium app-bound encryption is deliberately not bypassed.
                    if(length>3&&encrypted[0]=='v')continue;
                    try {value=Encoding.UTF8.GetString(ProtectedData.Unprotect(encrypted,null,DataProtectionScope.CurrentUser));} catch {continue;}
                }
                if(value!="")cookies.Add(new Dictionary<string,object>{{"domain",domain},{"path",Column(statement,1)},{"name",name},{"value",value}});
            }
        } catch {} finally {if(statement!=IntPtr.Zero)sqlite3_finalize(statement);if(db!=IntPtr.Zero)sqlite3_close(db);}
        return cookies;
    }
    static bool HasSession(List<object> cookies) {return cookies.Cast<Dictionary<string,object>>().Any(c=>(string)c["name"]=="reddit_session");}
    static object CachedCookies;static DateTime CachedUntil;
    static object Cookies() {
        if(CachedCookies!=null&&DateTime.UtcNow<CachedUntil)return CachedCookies;
        try {
            var start=new System.Diagnostics.ProcessStartInfo(@"C:\Program Files\nodejs\node.exe","\""+Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"ReadRedditEdge.js")+"\"") {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
            using(var process=System.Diagnostics.Process.Start(start)) {
                var output=process.StandardOutput.ReadToEndAsync();var errors=process.StandardError.ReadToEndAsync();
                if(!process.WaitForExit(20000)) {try{process.Kill();}catch{}return new {browser="Dedicated Edge",available=false,cookies=new object[0]};}
                var result=Json.Deserialize<Dictionary<string,object>>(output.Result);
                object available;if(result.TryGetValue("available",out available)&&Convert.ToBoolean(available)){CachedCookies=result;CachedUntil=DateTime.UtcNow.AddSeconds(60);}
                return result;
            }
        }catch{return new {browser="Dedicated Edge",available=false,cookies=new object[0]};}
    }
    static void Main(string[] args) {
        if(args.Length==1&&args[0]=="--check") {
            var data=Json.Deserialize<Dictionary<string,object>>(Json.Serialize(Cookies()));
            Console.WriteLine(Json.Serialize(new {browser=data["browser"],available=data["available"]}));return;
        }
        if(args.Length!=1)return;
        var pool=new SecurityIdentifier(args[0]);
        var security=new PipeSecurity();security.SetAccessRuleProtection(true,false);
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User,PipeAccessRights.FullControl,AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(pool,PipeAccessRights.ReadWrite,AccessControlType.Allow));
        while(true) {
            try {
                using(var pipe=new NamedPipeServerStream("WallpaperGalleryReddit",PipeDirection.InOut,1,PipeTransmissionMode.Byte,PipeOptions.Asynchronous,65536,65536,security)) {
                    pipe.WaitForConnection();
                    var computer=new StringBuilder(256);
                    bool knownComputer=GetNamedPipeClientComputerName(pipe.SafePipeHandle.DangerousGetHandle(),computer,computer.Capacity);
                    // ERROR_PIPE_LOCAL (229) is returned for a local pipe client.
                    bool localClient=(!knownComputer&&Marshal.GetLastWin32Error()==229)||(knownComputer&&computer.ToString().TrimStart('\\').Equals(Environment.MachineName,StringComparison.OrdinalIgnoreCase));
                    if(!localClient)continue;
                    var read=System.Threading.Tasks.Task.Factory.StartNew(()=>new StreamReader(pipe).ReadLine());
                    if(!read.Wait(3000)||read.Result!="reddit")continue;
                    var writer=new StreamWriter(pipe,new UTF8Encoding(false));writer.WriteLine(Json.Serialize(Cookies()));writer.Flush();
                }
            } catch {System.Threading.Thread.Sleep(1000);}
        }
    }
}
