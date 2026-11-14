// Model Lister Server - single-file HTTP server (raw TcpListener, no HTTP.sys / no admin needed)
// Registers OpenAI-compatible LLM servers, crawls their /v1/models and acts as a routing gateway.
// Build with the Windows built-in .NET Framework C# compiler (see build.ps1). C# 5 compatible.
// Settings (port + server list) persist in config.json next to the exe; logs are in-memory only (GET /api/logs).
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace ModelListerServer
{
    internal static class Program
    {
        private static void Main(string[] args)
        {
            // if the process ever dies on its own, leave a minimal trace (normal exits write nothing)
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                try
                {
                    File.AppendAllText(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "crash.txt"),
                        "[" + DateTime.Now.ToString("s") + "] unhandled: " + e.ExceptionObject + "\r\n");
                }
                catch { }
            };
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            var cfg = ServerConfig.Load(baseDir); // config.json (if any)

            // command line overrides config.json
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                string v = null;
                int eq = a.IndexOf('=');
                if (a.StartsWith("--") && eq > 0) { v = a.Substring(eq + 1); a = a.Substring(0, eq); }
                switch (a)
                {
                    case "--port":
                    {
                        int p;
                        if (!int.TryParse(Next(args, ref i, v), out p) || p < 1 || p > 65534)
                        {
                            Console.Error.WriteLine("Invalid --port value.");
                            return;
                        }
                        cfg.Port = p;
                        break;
                    }
                    case "--help":
                    case "-h":
                        Console.WriteLine(
                            "model-lister-server.exe [--port 8787]\r\n\r\n" +
                            "Settings (port + registered servers) are kept in config.json next to the exe.\r\n" +
                            "Dashboard: http://localhost:PORT/ - gateway: http://localhost:PORT/v1");
                        return;
                }
            }

            cfg.Save(baseDir); // persist effective settings

            // llama.cpp servers are http; https endpoints (denia etc.) need TLS1.2+
            try
            {
                ServicePointManager.SecurityProtocol =
                    ServicePointManager.SecurityProtocol | (SecurityProtocolType)3072 | (SecurityProtocolType)12288; // Tls12 | Tls13
            }
            catch { }

            var server = new Server(cfg, baseDir);
            try
            {
                server.Start();
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("Failed to listen on port " + cfg.Port + ": " + e.Message +
                    "\r\nClose other instances, change \"port\" in config.json, or use --port to change the port.");
                return;
            }
            Console.WriteLine("model-lister server listening on :" + cfg.Port + " (dashboard + /v1 gateway)");
            Thread.Sleep(Timeout.Infinite);
        }

        private static string Next(string[] args, ref int i, string inline)
        {
            if (inline != null) return inline;
            i++;
            return i < args.Length ? args[i] : null;
        }
    }

    // ---------- registered server entry ----------

    internal sealed class Srv
    {
        public string Id = "";
        public string Name = "";
        public string Url = "";
        public string Key = "";
        public string Alias = ""; // stable model name for this server (1 server = 1 model assumption)

        public Dictionary<string, object> ToDict()
        {
            var d = new Dictionary<string, object>();
            d["id"] = Id; d["name"] = Name; d["url"] = Url; d["key"] = Key; d["alias"] = Alias;
            return d;
        }
    }

    // ---------- settings persisted in config.json next to the exe ----------

    internal sealed class ServerConfig
    {
        public int Port = 8787;
        public int CrawlIntervalSec = 60; // 0 = periodic crawl off
        public readonly List<Srv> Servers = new List<Srv>();

        private static List<Srv> DefaultServers()
        {
            // first-run sample entry (config.json absent). Replace with your own server.
            var list = new List<Srv>();
            list.Add(new Srv { Id = "sample", Name = "サンプル(差し替えてください)", Url = "http://localhost:8080/v1", Key = "" });
            return list;
        }

        public static ServerConfig Load(string baseDir)
        {
            var c = new ServerConfig();
            try
            {
                string path = Path.Combine(baseDir, "config.json");
                if (!File.Exists(path)) { c.Servers.AddRange(DefaultServers()); return c; }
                object parsed = Util.Json.DeserializeObject(File.ReadAllText(path));
                var d = parsed as Dictionary<string, object>;
                if (d == null) { c.Servers.AddRange(DefaultServers()); return c; }
                object port;
                if (d.TryGetValue("port", out port))
                {
                    int p;
                    if (port != null && int.TryParse(port.ToString(), out p) && p >= 1 && p <= 65534) c.Port = p;
                }
                object iv;
                if (d.TryGetValue("crawlIntervalSec", out iv))
                {
                    int s2;
                    if (iv != null && int.TryParse(iv.ToString(), out s2) && s2 >= 10 && s2 <= 86400) c.CrawlIntervalSec = s2;
                    else if (iv != null && iv.ToString() == "0") c.CrawlIntervalSec = 0;
                }
                object srv;
                if (d.TryGetValue("servers", out srv))
                {
                    var arr = Util.AsList(srv);
                    if (arr != null)
                        foreach (var o in arr)
                        {
                            var sd = o as Dictionary<string, object>;
                            if (sd == null) continue;
                            var s = new Srv();
                            object t;
                            if (sd.TryGetValue("id", out t) && t != null) s.Id = t.ToString();
                            if (sd.TryGetValue("name", out t) && t != null) s.Name = t.ToString();
                            if (sd.TryGetValue("url", out t) && t != null) s.Url = t.ToString();
                            if (sd.TryGetValue("key", out t) && t != null) s.Key = t.ToString();
                            if (sd.TryGetValue("alias", out t) && t != null) s.Alias = t.ToString().Trim();
                            if (s.Url.Length > 0) c.Servers.Add(s);
                        }
                }
            }
            catch { } // corrupt config: fall back to defaults
            if (c.Servers.Count == 0 && !File.Exists(Path.Combine(baseDir, "config.json"))) c.Servers.AddRange(DefaultServers());
            return c;
        }

        // human-editable config.json
        public string BuildJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\r\n");
            sb.Append("  \"port\": ").Append(Port).Append(",\r\n");
            sb.Append("  \"crawlIntervalSec\": ").Append(CrawlIntervalSec).Append(",\r\n");
            sb.Append("  \"servers\": [\r\n");
            for (int i = 0; i < Servers.Count; i++)
            {
                var s = Servers[i];
                sb.Append("    { \"id\": \"").Append(Util.Esc(s.Id))
                  .Append("\", \"name\": \"").Append(Util.Esc(s.Name))
                  .Append("\", \"url\": \"").Append(Util.Esc(s.Url))
                  .Append("\", \"key\": \"").Append(Util.Esc(s.Key))
                  .Append("\", \"alias\": \"").Append(Util.Esc(s.Alias)).Append("\" }");
                sb.Append(i < Servers.Count - 1 ? ",\r\n" : "\r\n");
            }
            sb.Append("  ]\r\n");
            sb.Append("}\r\n");
            return sb.ToString();
        }

        public void Save(string baseDir)
        {
            try
            {
                string want = BuildJson();
                string path = Path.Combine(baseDir, "config.json");
                string have = File.Exists(path) ? File.ReadAllText(path) : null;
                if (want != have) File.WriteAllText(path, want);
            }
            catch { }
        }
    }

    // ---------- model -> servers routing ----------

    internal sealed class RouteTarget
    {
        public Srv Srv;
        public object Meta; // raw model object from upstream /v1/models
    }

    internal sealed class Server
    {
        private readonly ServerConfig _cfg;
        private readonly string _baseDir;
        private TcpListener _listener;

        private readonly object _routeLock = new object();
        private Dictionary<string, List<RouteTarget>> _routes; // modelId -> targets
        private Dictionary<string, RouteTarget> _aliasTargets; // user alias -> bound target (1 server = 1 model)
        private Dictionary<string, string> _aliasReal; // alias -> bound modelId (for change detection)
        private DateTime _routesUtc = DateTime.MinValue;
        private Thread _crawler;

        private readonly object _logLock = new object();
        private readonly List<string> _log = new List<string>(); // in-memory only; nothing is written to disk
        private DateTime _lastConnErrUtc = DateTime.MinValue;

        private static readonly string[] GatewayPaths =
            { "/v1/chat/completions", "/v1/completions", "/v1/embeddings", "/v1/rerank" };
        private const int GatewayTimeoutMs = 300000; // generation can take a while

        public Server(ServerConfig cfg, string baseDir)
        {
            _cfg = cfg;
            _baseDir = baseDir;
        }

        public int Port { get { return _cfg.Port; } }

        public void Start()
        {
            _listener = new TcpListener(IPAddress.Any, _cfg.Port);
            _listener.Start();
            var accept = new Thread(AcceptLoop);
            accept.IsBackground = true;
            accept.Start();

            if (_cfg.CrawlIntervalSec > 0)
            {
                _crawler = new Thread(CrawlLoop);
                _crawler.IsBackground = true;
                _crawler.Start();
                Log("periodic crawl every " + _cfg.CrawlIntervalSec + "s");
            }
        }

        // periodic crawl: keeps routes/aliases fresh and logs model changes on the provider side
        private void CrawlLoop()
        {
            while (true)
            {
                Thread.Sleep(_cfg.CrawlIntervalSec * 1000);
                try { EnsureRoutes(true); }
                catch (Exception e) { Log("crawl error: " + e.Message); }
            }
        }

        private void AcceptLoop()
        {
            while (true)
            {
                TcpClient client;
                try { client = _listener.AcceptTcpClient(); }
                catch (SocketException) { continue; }
                ThreadPool.QueueUserWorkItem(HandleConn, client);
            }
        }

        // ---------- connection handling ----------

        private void HandleConn(object obj)
        {
            var client = (TcpClient)obj;
            try
            {
                client.NoDelay = true;
                client.ReceiveTimeout = 15000;
                using (var stream = client.GetStream())
                {
                    var req = HttpReq.Parse(stream);
                    if (req == null) return;
                    Route(client, stream, req);
                }
            }
            catch (Exception e)
            {
                // browsers probe sockets that connect without sending data: throttle to keep the log useful
                if ((DateTime.UtcNow - _lastConnErrUtc).TotalSeconds > 10)
                {
                    _lastConnErrUtc = DateTime.UtcNow;
                    Log("conn error: " + e.Message);
                }
            }
            finally
            {
                try { client.Close(); } catch { }
            }
        }

        private void Route(TcpClient client, NetworkStream stream, HttpReq req)
        {
            string key = req.Method + " " + req.Path;

            if (key == "GET /" || key == "GET /index.html")
            {
                Send(stream, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(Embedded.IndexHtml), "no-cache");
                return;
            }
            if (key == "GET /api/ping")
            {
                SendJson(stream, 200, "{\"ok\":true}");
                return;
            }
            if (key == "GET /api/logs")
            {
                lock (_logLock)
                    SendJson(stream, 200, Util.Json.Serialize(new Dictionary<string, object> { { "lines", new List<string>(_log) } }));
                return;
            }

            try
            {
                switch (key)
                {
                    case "GET /api/servers":
                        SendServers(stream);
                        return;
                    case "POST /api/servers":
                        SaveServers(stream, req);
                        return;
                    case "POST /api/probe":
                    {
                        var d = ParseBodyDict(req);
                        string url = GetStr(d, "url"), k = GetStr(d, "key");
                        SendJson(stream, 200, Util.Json.Serialize(Probe(url, k)));
                        return;
                    }
                    case "POST /api/probe-all":
                    {
                        var d = ParseBodyDict(req);
                        var results = new List<object>();
                        object targets;
                        if (d != null && d.TryGetValue("targets", out targets))
                        {
                            var arr = Util.AsList(targets);
                            if (arr != null)
                                foreach (var o in arr)
                                {
                                    var t = o as Dictionary<string, object>;
                                    if (t == null) continue;
                                    results.Add(new Dictionary<string, object>
                                    {
                                        { "id", GetStr(t, "id") },
                                        { "result", Probe(GetStr(t, "url"), GetStr(t, "key")) },
                                    });
                                }
                        }
                        SendJson(stream, 200, Util.Json.Serialize(new Dictionary<string, object> { { "results", results } }));
                        return;
                    }
                    case "GET /api/routes":
                    {
                        EnsureRoutes(req.Query("refresh") == "1");
                        var list = new List<object>();
                        var aliasList = new List<object>();
                        lock (_routeLock)
                        {
                            foreach (var kv in _routes)
                            {
                                var servers = new List<object>();
                                foreach (var t in kv.Value)
                                    servers.Add(new Dictionary<string, object> { { "id", t.Srv.Id }, { "name", t.Srv.Name }, { "url", t.Srv.Url } });
                                list.Add(new Dictionary<string, object> { { "model", kv.Key }, { "servers", servers } });
                            }
                            if (_aliasTargets != null)
                                foreach (var kv in _aliasTargets)
                                    aliasList.Add(new Dictionary<string, object>
                                    {
                                        { "alias", kv.Key },
                                        { "model", ModelIdOfTarget(kv.Value) },
                                        { "server", kv.Value.Srv.Id },
                                        { "serverName", kv.Value.Srv.Name },
                                    });
                        }
                        list.Sort(delegate(object a, object b)
                        {
                            return string.Compare(((Dictionary<string, object>)a)["model"] as string, ((Dictionary<string, object>)b)["model"] as string, StringComparison.Ordinal);
                        });
                        DateTime epoch = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                        long genMs = (long)(_routesUtc - epoch).TotalMilliseconds;
                        SendJson(stream, 200, Util.Json.Serialize(new Dictionary<string, object>
                        {
                            { "generatedAt", genMs },
                            { "intervalSec", _cfg.CrawlIntervalSec },
                            { "routes", list },
                            { "aliases", aliasList },
                        }));
                        return;
                    }
                    case "GET /v1/models":
                    {
                        EnsureRoutes(true);
                        var data = new List<object>();
                        lock (_routeLock)
                        {
                            foreach (var kv in _routes)
                            {
                                data.Add(ModelEntry(kv.Key, kv.Value, kv.Value[0].Meta, null));
                                // models served by several servers also get "serverId/modelId" aliases
                                if (kv.Value.Count > 1)
                                    foreach (var t in kv.Value)
                                        data.Add(ModelEntry(t.Srv.Id + "/" + kv.Key, kv.Value, kv.Value[0].Meta, kv.Key));
                            }
                            // user aliases become usable model names as well
                            if (_aliasTargets != null)
                                foreach (var kv in _aliasTargets)
                                    data.Add(ModelEntry(kv.Key, new List<RouteTarget> { kv.Value }, kv.Value.Meta, ModelIdOfTarget(kv.Value)));
                        }
                        data.Sort(delegate(object a, object b)
                        {
                            return string.Compare(((Dictionary<string, object>)a)["id"] as string, ((Dictionary<string, object>)b)["id"] as string, StringComparison.Ordinal);
                        });
                        SendJson(stream, 200, Util.Json.Serialize(new Dictionary<string, object> { { "object", "list" }, { "data", data } }));
                        return;
                    }
                    default:
                        if (req.Method == "POST" && Array.IndexOf(GatewayPaths, req.Path) >= 0)
                        {
                            Gateway(stream, req, req.Path);
                            return;
                        }
                        SendJson(stream, 404, "{\"error\":\"not found\"}");
                        return;
                }
            }
            catch (Exception e)
            {
                Log("handler error: " + e.Message);
                try { SendJson(stream, 500, "{\"error\":\"" + Util.Esc(e.Message) + "\"}"); } catch { }
            }
        }

        // ---------- server registry ----------

        private void SendServers(NetworkStream stream)
        {
            var list = new List<object>();
            lock (_cfg) foreach (var s in _cfg.Servers) list.Add(s.ToDict());
            SendJson(stream, 200, Util.Json.Serialize(new Dictionary<string, object> { { "servers", list } }));
        }

        private void SaveServers(NetworkStream stream, HttpReq req)
        {
            var d = ParseBodyDict(req);
            object arr;
            if (d == null || !d.TryGetValue("servers", out arr))
            {
                SendJson(stream, 400, "{\"error\":\"servers は配列で指定してください\"}");
                return;
            }
            var next = new List<Srv>();
            var list = Util.AsList(arr);
            if (list != null)
                foreach (var o in list)
                {
                    var sd = o as Dictionary<string, object>;
                    if (sd == null) continue;
                    var s = new Srv
                    {
                        Id = GetStr(sd, "id"),
                        Name = GetStr(sd, "name"),
                        Url = GetStr(sd, "url"),
                        Key = GetStr(sd, "key"),
                        Alias = (GetStr(sd, "alias") ?? "").Trim(),
                    };
                    if (s.Url.Length == 0) continue;
                    if (s.Id.Length == 0) s.Id = "s" + DateTime.Now.Ticks;
                    next.Add(s);
                }
            lock (_cfg)
            {
                _cfg.Servers.Clear();
                _cfg.Servers.AddRange(next);
                _cfg.Save(_baseDir);
            }
            lock (_routeLock) _routesUtc = DateTime.MinValue; // routes are stale now
            Log("servers updated (" + next.Count + " entries)");
            SendServers(stream);
        }

        // ---------- OpenAI-compatible base URL helpers ----------

        private static string BaseUrl(string raw)
        {
            string u = (raw ?? "").Trim().TrimEnd('/');
            if (u.Length == 0) return null;
            if (!Regex.IsMatch(u, @"^https?://", RegexOptions.IgnoreCase)) u = "http://" + u;
            u = Regex.Replace(u, @"/models$", "", RegexOptions.IgnoreCase);
            if (!Regex.IsMatch(u, @"/v\d+$", RegexOptions.IgnoreCase)) u += "/v1";
            return u;
        }

        private static string ModelsUrl(string raw)
        {
            string b = BaseUrl(raw);
            return b == null ? null : b + "/models";
        }

        // ---------- crawl one server ----------

        private Dictionary<string, object> Probe(string urlRaw, string key)
        {
            string target = ModelsUrl(urlRaw);
            if (target == null) return new Dictionary<string, object> { { "ok", false }, { "error", "URL が空です" } };
            var sw = Stopwatch.StartNew();
            try
            {
                var wr = (HttpWebRequest)WebRequest.Create(target);
                wr.Method = "GET";
                wr.Timeout = 12000;
                wr.ReadWriteTimeout = 15000;
                wr.Accept = "application/json";
                if (!string.IsNullOrEmpty(key)) wr.Headers.Add("Authorization", "Bearer " + key);
                string text;
                using (var resp = (HttpWebResponse)wr.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                    text = sr.ReadToEnd();
                var parsed = Util.Json.DeserializeObject(text) as Dictionary<string, object>;
                var models = new List<object>();
                object data;
                if (parsed != null && parsed.TryGetValue("data", out data))
                {
                    var arr = Util.AsList(data);
                    if (arr != null)
                        foreach (var m in arr)
                        {
                            var md = m as Dictionary<string, object>;
                            object idv;
                            if (md != null && md.TryGetValue("id", out idv) && idv is string) models.Add(md);
                        }
                }
                return new Dictionary<string, object>
                {
                    { "ok", true },
                    { "endpoint", target },
                    { "latencyMs", (int)sw.ElapsedMilliseconds },
                    { "models", models },
                };
            }
            catch (WebException we)
            {
                string msg = we.Response != null
                    ? "HTTP " + (int)((HttpWebResponse)we.Response).StatusCode
                    : we.Message;
                return Fail(target, sw, msg);
            }
            catch (Exception e)
            {
                return Fail(target, sw, e.Message);
            }
        }

        private static Dictionary<string, object> Fail(string target, Stopwatch sw, string msg)
        {
            return new Dictionary<string, object>
            {
                { "ok", false },
                { "endpoint", target },
                { "latencyMs", (int)sw.ElapsedMilliseconds },
                { "error", msg },
            };
        }

        // ---------- routing table (30s cache) ----------

        private void EnsureRoutes(bool force)
        {
            lock (_routeLock)
            {
                if (!force && _routes != null && (DateTime.UtcNow - _routesUtc).TotalSeconds < 30) return;
                var routes = new Dictionary<string, List<RouteTarget>>();
                var perServer = new Dictionary<string, List<string>>(); // serverId -> modelIds seen (order kept)
                List<Srv> snapshot;
                lock (_cfg) snapshot = new List<Srv>(_cfg.Servers);
                foreach (var s in snapshot)
                {
                    var r = Probe(s.Url, s.Key);
                    object ok;
                    if (!r.TryGetValue("ok", out ok) || !(bool)ok) continue;
                    var models = r["models"] as List<object>;
                    if (models == null) continue;
                    var ids = new List<string>();
                    foreach (var m in models)
                    {
                        var md = (Dictionary<string, object>)m;
                        string id = (string)md["id"];
                        if (id == null) continue;
                        ids.Add(id);
                        List<RouteTarget> list;
                        if (!routes.TryGetValue(id, out list)) { list = new List<RouteTarget>(); routes[id] = list; }
                        list.Add(new RouteTarget { Srv = s, Meta = md });
                    }
                    perServer[s.Id] = ids;
                }

                // bind user aliases: one stable name per server -> its (first) model
                var aliasTargets = new Dictionary<string, RouteTarget>();
                var aliasReal = new Dictionary<string, string>(); // alias -> bound modelId
                foreach (var s in snapshot)
                {
                    if (s.Alias.Length == 0) continue;
                    List<string> ids;
                    if (!perServer.TryGetValue(s.Id, out ids) || ids.Count == 0) continue;
                    if (ids.Count > 1) Log("note: " + s.Name + " returned " + ids.Count + " models, alias \"" + s.Alias + "\" binds the first one");
                    if (routes.ContainsKey(s.Alias))
                    {
                        Log("alias \"" + s.Alias + "\" collides with a model id, skipped");
                        continue;
                    }
                    if (aliasTargets.ContainsKey(s.Alias))
                    {
                        Log("duplicate alias \"" + s.Alias + "\", skipped");
                        continue;
                    }
                    string bound = ids[0];
                    aliasTargets[s.Alias] = routes[bound][0];
                    aliasReal[s.Alias] = bound;
                }

                // log provider-side changes since last crawl (silent when nothing changed)
                if (_routes != null)
                {
                    foreach (string id in routes.Keys)
                        if (!_routes.ContainsKey(id)) Log("model appeared: " + id);
                    foreach (string id in _routes.Keys)
                        if (!routes.ContainsKey(id)) Log("model removed: " + id);
                    foreach (var kv in aliasReal)
                    {
                        string prev;
                        if (_aliasReal != null && _aliasReal.TryGetValue(kv.Key, out prev) && prev != kv.Value)
                            Log("alias \"" + kv.Key + "\" rebound: " + prev + " -> " + kv.Value);
                    }
                }

                _routes = routes;
                _aliasTargets = aliasTargets;
                _aliasReal = aliasReal;
                _routesUtc = DateTime.UtcNow;
            }
        }

        private static Dictionary<string, object> ModelEntry(string id, List<RouteTarget> targets, object meta, string aliasOf)
        {
            var e = meta as Dictionary<string, object>;
            var outDict = e != null ? new Dictionary<string, object>(e) : new Dictionary<string, object>();
            outDict["id"] = id;
            outDict["object"] = "model";
            var owned = new List<string>();
            foreach (var t in targets) owned.Add(t.Srv.Id);
            outDict["owned_by"] = string.Join(",", owned.ToArray());
            if (aliasOf != null) outDict["alias_of"] = aliasOf;
            return outDict;
        }

        // ---------- gateway (routing proxy) ----------

        private static string ModelIdOfTarget(RouteTarget t)
        {
            var md = t.Meta as Dictionary<string, object>;
            object id;
            if (md != null && md.TryGetValue("id", out id) && id is string) return (string)id;
            return "";
        }

        // model name -> target. user alias wins; then exact modelId; "serverId/modelId" alias disambiguates
        private RouteTarget Resolve(string model, out string modelId, out List<string> ambiguous)
        {
            ambiguous = null;
            modelId = model;
            lock (_routeLock)
            {
                if (_aliasTargets != null)
                {
                    RouteTarget at;
                    if (_aliasTargets.TryGetValue(model, out at))
                    {
                        string real = ModelIdOfTarget(at);
                        if (real.Length > 0) modelId = real;
                        return at;
                    }
                }
                List<RouteTarget> list;
                if (_routes.TryGetValue(model, out list))
                {
                    if (list.Count == 1) return list[0];
                    var ids = new List<string>();
                    foreach (var t in list) ids.Add(t.Srv.Id);
                    ambiguous = ids;
                    return null;
                }
                int slash = model.IndexOf('/');
                if (slash > 0)
                {
                    string alias = model.Substring(0, slash), rest = model.Substring(slash + 1);
                    if (_routes.TryGetValue(rest, out list))
                        foreach (var t in list)
                            if (t.Srv.Id == alias) { modelId = rest; return t; }
                }
                return null;
            }
        }

        private void Gateway(NetworkStream stream, HttpReq req, string path)
        {
            var d = ParseBodyDict(req);
            string model = GetStr(d, "model");
            if (model == null)
            {
                SendJson(stream, 400, "{\"error\":\"model を指定してください\"}");
                return;
            }

            EnsureRoutes(false);
            string modelId;
            List<string> ambiguous;
            RouteTarget t = Resolve(model, out modelId, out ambiguous);
            if (t == null && ambiguous == null)
            {
                SendJson(stream, 404, "{\"error\":\"モデルが見つかりません: " + Util.Esc(model) + "\",\"hint\":\"GET /v1/models で一覧が見られます\"}");
                return;
            }
            if (ambiguous != null)
            {
                var ids = new StringBuilder();
                for (int i = 0; i < ambiguous.Count; i++)
                {
                    if (i > 0) ids.Append(",");
                    ids.Append("\"" + Util.Esc(ambiguous[i]) + "\"");
                }
                SendJson(stream, 400, "{\"error\":\"モデル \\\"" + Util.Esc(model) + "\\\" は複数のサーバーに登録があります。\\\"サーバーID/モデルID\\\" 形式で指定してください\",\"servers\":[" + ids + "]}");
                return;
            }

            // baseUrl already contains /v1, so strip the /vN segment from the client path
            string suffix;
            Match mm = Regex.Match(path, @"^/v\d+/(.*)$");
            suffix = mm.Success ? mm.Groups[1].Value : path.TrimStart('/');
            string targetUrl = BaseUrl(t.Srv.Url) + "/" + suffix;

            d["model"] = modelId;
            string outBody = Util.Json.Serialize(d);
            byte[] ob = Encoding.UTF8.GetBytes(outBody);
            string clientAuth = req.Header("authorization");

            // connection reuse can hit a dead TLS keep-alive socket: retry once with a fresh one
            HttpWebResponse resp = null;
            string lastErr = null;
            for (int attempt = 0; attempt < 2 && resp == null; attempt++)
            {
                var wr = (HttpWebRequest)WebRequest.Create(targetUrl);
                wr.Method = "POST";
                wr.ContentType = "application/json";
                wr.Timeout = GatewayTimeoutMs;
                wr.ReadWriteTimeout = GatewayTimeoutMs;
                if (attempt > 0) wr.KeepAlive = false;
                if (!string.IsNullOrEmpty(t.Srv.Key))
                {
                    wr.Headers.Add("Authorization", "Bearer " + t.Srv.Key);
                }
                else if (!string.IsNullOrEmpty(clientAuth))
                {
                    // only pass the client's key through when the target has none configured
                    wr.Headers.Add("Authorization", clientAuth);
                }
                try
                {
                    wr.ContentLength = ob.Length;
                    using (var rq = wr.GetRequestStream()) rq.Write(ob, 0, ob.Length);
                    resp = (HttpWebResponse)wr.GetResponse();
                }
                catch (WebException we)
                {
                    resp = we.Response as HttpWebResponse; // upstream error status: pass through as-is
                    if (resp == null)
                        lastErr = we.Status == WebExceptionStatus.Timeout || we.Status == WebExceptionStatus.RequestCanceled
                            ? "アップストリームがタイムアウトしました" : we.Message;
                }
                catch (Exception io)
                {
                    lastErr = io.Message;
                }
                if (resp == null && attempt == 0)
                    Log("gateway retry: " + lastErr);
            }
            if (resp == null)
            {
                Log("gateway error: " + lastErr);
                SendJson(stream, 502, "{\"error\":\"転送に失敗しました: " + Util.Esc(lastErr) + "\",\"target\":\"" + Util.Esc(t.Srv.Name) + "\",\"url\":\"" + Util.Esc(targetUrl) + "\"}");
                return;
            }

            using (resp)
                StreamChunked(stream, resp);
        }

        // streams the upstream response to the client with chunked transfer-encoding (SSE safe)
        private static void StreamChunked(NetworkStream outStream, HttpWebResponse resp)
        {
            string ctype = string.IsNullOrEmpty(resp.ContentType) ? "application/json; charset=utf-8" : resp.ContentType;
            var head = "HTTP/1.1 " + (int)resp.StatusCode + " " + Reason(resp.StatusCode) + "\r\n" +
                       "Content-Type: " + ctype + "\r\n" +
                       "Cache-Control: no-store\r\n" +
                       "Transfer-Encoding: chunked\r\n" +
                       "Connection: close\r\n\r\n";
            byte[] hb = Encoding.UTF8.GetBytes(head);
            outStream.Write(hb, 0, hb.Length);

            byte[] buf = new byte[8192];
            using (var src = resp.GetResponseStream())
            {
                int n;
                while ((n = src.Read(buf, 0, buf.Length)) > 0)
                {
                    byte[] marker = Encoding.ASCII.GetBytes(n.ToString("x") + "\r\n");
                    outStream.Write(marker, 0, marker.Length);
                    outStream.Write(buf, 0, n);
                    outStream.Write(new byte[] { (byte)'\r', (byte)'\n' }, 0, 2);
                    outStream.Flush(); // SSE must not sit in a buffer
                }
            }
            byte[] end = Encoding.ASCII.GetBytes("0\r\n\r\n");
            outStream.Write(end, 0, end.Length);
            outStream.Flush();
        }

        // ---------- helpers ----------

        private static Dictionary<string, object> ParseBodyDict(HttpReq req)
        {
            if (string.IsNullOrEmpty(req.Body)) return null;
            try { return Util.Json.DeserializeObject(req.Body) as Dictionary<string, object>; }
            catch { return null; }
        }

        private static string GetStr(Dictionary<string, object> d, string name)
        {
            if (d == null) return null;
            object v;
            if (!d.TryGetValue(name, out v) || v == null) return null;
            return v as string ?? v.ToString();
        }

        private static string Reason(HttpStatusCode code)
        {
            switch ((int)code)
            {
                case 200: return "OK";
                case 400: return "Bad Request";
                case 401: return "Unauthorized";
                case 403: return "Forbidden";
                case 404: return "Not Found";
                case 405: return "Method Not Allowed";
                case 500: return "Internal Server Error";
                case 502: return "Bad Gateway";
                default: return "Status";
            }
        }

        private void Log(string msg)
        {
            lock (_logLock)
            {
                _log.Add("[" + DateTime.Now.ToString("s") + "] " + msg);
                if (_log.Count > 1200) _log.RemoveRange(0, _log.Count - 1200);
            }
            try { Console.WriteLine(msg); } catch { }
        }

        private static void SendJson(NetworkStream s, int code, string json)
        {
            Send(s, code, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(json), "no-store");
        }

        private static void Send(NetworkStream s, int code, string contentType, byte[] body, string cacheControl)
        {
            string head = "HTTP/1.1 " + (int)code + " " + Reason((HttpStatusCode)code) + "\r\n" +
                          "Content-Type: " + contentType + "\r\n" +
                          "Content-Length: " + body.Length + "\r\n" +
                          "Cache-Control: " + cacheControl + "\r\n" +
                          "Connection: close\r\n\r\n";
            byte[] hb = Encoding.UTF8.GetBytes(head);
            s.Write(hb, 0, hb.Length);
            s.Write(body, 0, body.Length);
        }
    }

    // ---------- shared JSON / escaping ----------

    internal static class Util
    {
        public static readonly System.Web.Script.Serialization.JavaScriptSerializer Json =
            new System.Web.Script.Serialization.JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 200 };

        // JavaScriptSerializer returns JSON arrays as object[]; absorb both representations
        public static List<object> AsList(object o)
        {
            if (o == null) return null;
            var l = o as List<object>;
            if (l != null) return l;
            var arr = o as object[];
            if (arr != null) return new List<object>(arr);
            return null;
        }

        public static string Esc(string s)
        {
            if (s == null) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\\\"); break;
                    case '"': sb.Append("\\\""); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.ToString();
        }
    }

    // ---------- minimal HTTP request reader ----------

    internal sealed class HttpReq
    {
        public string Method = "";
        public string Path = "";
        public readonly Dictionary<string, string> QueryMap = new Dictionary<string, string>();
        public readonly Dictionary<string, string> Headers = new Dictionary<string, string>();
        public string Body;

        public string Query(string name)
        {
            string v;
            return QueryMap.TryGetValue(name, out v) ? v : null;
        }

        public string Header(string name)
        {
            string v;
            return Headers.TryGetValue(name, out v) ? v : null;
        }

        public static HttpReq Parse(Stream s)
        {
            var br = new ByteReader(s);
            string line = br.ReadLine();
            if (line == null) return null;
            string[] parts = line.Split(' ');
            if (parts.Length < 2) return null;

            var req = new HttpReq { Method = parts[0] };
            string url = parts[1];
            int q = url.IndexOf('?');
            if (q >= 0)
            {
                req.Path = UrlDecode(url.Substring(0, q));
                foreach (string pair in url.Substring(q + 1).Split('&'))
                {
                    int eq = pair.IndexOf('=');
                    if (eq > 0) req.QueryMap[pair.Substring(0, eq)] = UrlDecode(pair.Substring(eq + 1));
                }
            }
            else req.Path = UrlDecode(url);

            while (true)
            {
                string h = br.ReadLine();
                if (h == null || h.Length == 0) break;
                int ci = h.IndexOf(':');
                if (ci > 0) req.Headers[h.Substring(0, ci).ToLowerInvariant()] = h.Substring(ci + 1).Trim();
            }

            int len = 0;
            string cl = req.Header("content-length");
            if (cl != null) int.TryParse(cl, out len);
            if (len > 8 * 1024 * 1024) throw new Exception("body too large");
            if (len > 0) req.Body = br.ReadBody(len);
            return req;
        }

        private static string UrlDecode(string s)
        {
            try { return Uri.UnescapeDataString(s.Replace("+", " ")); }
            catch { return s; }
        }
    }

    internal sealed class ByteReader
    {
        private readonly Stream _s;
        private readonly byte[] _buf = new byte[8192];
        private int _pos, _len;

        public ByteReader(Stream s) { _s = s; }

        private int ReadByte()
        {
            if (_pos >= _len)
            {
                _len = _s.Read(_buf, 0, _buf.Length);
                if (_len <= 0) return -1;
                _pos = 0;
            }
            return _buf[_pos++];
        }

        public string ReadLine()
        {
            var sb = new StringBuilder(128);
            while (true)
            {
                int b = ReadByte();
                if (b == -1) return sb.Length == 0 ? null : sb.ToString();
                if (b == '\n')
                {
                    if (sb.Length > 0 && sb[sb.Length - 1] == '\r') sb.Length--;
                    return sb.ToString();
                }
                sb.Append((char)b);
                if (sb.Length > 16384) throw new Exception("header line too long");
            }
        }

        public string ReadBody(int length)
        {
            var bytes = new byte[length];
            int got = 0;
            // IMPORTANT: ReadLine() may already have read the body (or part of it) into _buf
            // (headers and body coalesced in one socket read). Drain the buffered bytes first,
            // otherwise ReadBody waits forever for bytes that were already consumed.
            while (got < length && _pos < _len)
                bytes[got++] = _buf[_pos++];
            while (got < length)
            {
                int n = _s.Read(bytes, got, length - got);
                if (n <= 0) break;
                got += n;
            }
            return Encoding.UTF8.GetString(bytes, 0, got);
        }
    }
}
