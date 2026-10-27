using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Web.Script.Serialization;
using System.Windows.Forms;
using Microsoft.VisualBasic;

namespace MusicBeePlugin
{
    // MusicBee keeps playing (muted) and stays in charge; the Chromecast mirrors its track, play/pause, seek and volume.
    public partial class Plugin
    {
        MusicBeeApiInterface mb;
        PluginInfo about = new PluginInfo();
        Dictionary<string, IPEndPoint> devices = new Dictionary<string, IPEndPoint>();
        ContextMenuStrip popup;
        Cast cast;
        TvRemote remote;
        readonly object remoteLock = new object();
        X509Certificate2 certificate;
        TcpListener http;
        System.Threading.Timer timer;
        string file;      // the web server only serves the current track
        byte[] art;       // and its cover
        int version;      // part of the URL, bumped per track
        int lastPos, lastTick, ticks;
        bool wasMuted, connecting;
        static string logPath;
        static readonly object logLock = new object();

        // What the Chromecast said and asked for, to find out why something didn't play. Starts empty every time MusicBee starts.
        internal static void Log(string message)
        {
            lock (logLock)
                try { File.AppendAllText(logPath, DateTime.Now.ToString("HH:mm:ss.fff ") + message + Environment.NewLine); }
                catch { }
        }

        public PluginInfo Initialise(IntPtr apiInterfacePtr)
        {
            mb = new MusicBeeApiInterface();
            mb.Initialise(apiInterfacePtr);
            logPath = Path.Combine(mb.Setting_GetPersistentStoragePath(), "Chromecast.log");
            try { File.Delete(logPath); } catch { }
            about.PluginInfoVersion = PluginInfoVersion;
            about.Name = "Chromecast";
            about.Description = "Play music on a Chromecast";
            about.Author = "Martin Reinok";
            about.TargetApplication = "";
            about.Type = PluginType.General;
            about.VersionMajor = 3;
            about.MinInterfaceVersion = MinInterfaceVersion;
            about.MinApiRevision = MinApiRevision;
            about.ReceiveNotifications = ReceiveNotificationFlags.PlayerEvents;

            var menu = (ToolStripMenuItem)mb.MB_AddMenuItem("mnuTools/Chromecast", null, null);
            menu.DropDownItems.Add("Searching...");   // makes it a submenu
            menu.DropDownOpening += (s, e) => Refresh(menu.DropDownItems);
            mb.MB_RegisterCommand("Chromecast", (s, e) =>
            {
                if (popup == null) popup = new ContextMenuStrip();
                Refresh(popup.Items);
                popup.Show(Cursor.Position);
            });
            mb.MB_RegisterCommand("Chromecast: Volume Up", (s, e) => PressKey(TvRemote.VolumeUp));
            mb.MB_RegisterCommand("Chromecast: Volume Down", (s, e) => PressKey(TvRemote.VolumeDown));
            return about;
        }

        public bool Configure(IntPtr panelHandle) { return false; }
        public void SaveSettings() { }
        public void Uninstall() { }
        public void Close(PluginCloseReason reason) { Disconnect(); }

        public void ReceiveNotification(string sourceFileUrl, NotificationType type)
        {
            var c = cast;
            if (c == null) return;
            if (!mb.Player_GetMute()) mb.Player_SetMute(true);   // touching the volume unmutes MusicBee; the sound belongs on the TV
            var state = mb.Player_GetPlayState();
            switch (type)
            {
                case NotificationType.TrackChanged:
                    Load(c);
                    break;
                case NotificationType.PlayStateChanged:
                    if (state == PlayState.Playing || state == PlayState.Paused || state == PlayState.Stopped)
                        c.Media(new { type = state == PlayState.Playing ? "PLAY" : "PAUSE", mediaSessionId = c.MediaSession });
                    break;
                case NotificationType.VolumeLevelChanged:
                    c.SetVolume(mb.Player_GetVolume());
                    break;
            }
        }

        // Show the devices we know right away and update the list when discovery finishes.
        void Refresh(ToolStripItemCollection items)
        {
            var window = Control.FromHandle(mb.MB_GetWindowHandle());
            Fill(items, true);
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    var found = Discover();
                    window.BeginInvoke((Action)(() => { devices = found; Fill(items, false); }));
                }
                catch { }   // no network, or MusicBee is closing
            });
        }

        void Fill(ToolStripItemCollection items, bool searching)
        {
            var old = items.Cast<ToolStripItem>().ToList();
            foreach (var device in devices.OrderBy(d => d.Key))
            {
                var name = device.Key;
                var endpoint = device.Value;
                items.Add(new ToolStripMenuItem(name, null, (s, e) => Connect(name, endpoint)) { Checked = cast != null && cast.Name == name });
            }
            if (devices.Count == 0) items.Add(searching ? "Searching..." : "No Chromecasts found").Enabled = false;
            if (cast != null)
            {
                items.Add(new ToolStripSeparator());
                items.Add("Stop casting", null, (s, e) => Disconnect());
            }
            // Remove the old items last: emptying an open menu, even for a moment, makes WinForms move it to the top-left corner of the screen.
            foreach (var item in old) items.Remove(item);
        }

        void Connect(string name, IPEndPoint endpoint)
        {
            if (connecting || cast != null && cast.Name == name) return;
            Disconnect();
            connecting = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    Log("Connecting to " + name + " at " + endpoint);
                    var c = new Cast(name, endpoint);
                    c.StateChanged = OnCastStateChanged;
                    c.Restarted = () => Load(c);
                    c.Closed = () =>
                    {
                        if (cast != c) return;   // we hung up ourselves
                        if (mb.Player_GetPlayState() == PlayState.Playing) mb.Player_PlayPause();
                        Disconnect();
                    };
                    if (http == null) StartServer();
                    wasMuted = mb.Player_GetMute();
                    mb.Player_SetMute(true);
                    cast = c;
                    Load(c);
                    if (timer == null) timer = new System.Threading.Timer(Tick, null, 1000, 1000);
                }
                catch (Exception e)
                {
                    Log("Could not connect: " + e);
                    MessageBox.Show("Could not connect to " + name + ":\n" + e.Message, "Chromecast");
                }
                finally { connecting = false; }
            });
        }

        void Disconnect()
        {
            var r = Interlocked.Exchange(ref remote, null);
            if (r != null) r.Close();
            var c = Interlocked.Exchange(ref cast, null);
            if (c == null) return;
            Log("Disconnecting from " + c.Name);
            c.Close();
            mb.Player_SetMute(wasMuted);
        }

        // Paused or resumed from the TV remote or a phone.
        void OnCastStateChanged(string playerState)
        {
            var state = mb.Player_GetPlayState();
            if (playerState == "PAUSED" && state == PlayState.Playing || playerState == "PLAYING" && state == PlayState.Paused)
                mb.Player_PlayPause();
        }

        void Load(Cast c)
        {
            string path = mb.NowPlaying_GetFileUrl(), artwork = mb.NowPlaying_GetArtwork();
            if (string.IsNullOrEmpty(path)) return;
            file = path;
            art = string.IsNullOrEmpty(artwork) ? null : Convert.FromBase64String(artwork);
            var url = "http://" + c.LocalIp + ":" + ((IPEndPoint)http.LocalEndpoint).Port + "/" + Interlocked.Increment(ref version) + "/";
            var state = mb.Player_GetPlayState();
            lastPos = mb.Player_GetPosition();
            lastTick = Environment.TickCount;
            c.Media(new
            {
                type = "LOAD",
                autoplay = state != PlayState.Paused && state != PlayState.Stopped,
                currentTime = lastPos / 1000.0,
                media = new
                {
                    contentId = url + "audio",
                    contentType = Mime(path),
                    streamType = "BUFFERED",
                    metadata = new
                    {
                        metadataType = 3,   // music track
                        title = mb.NowPlaying_GetFileTag(MetaDataType.TrackTitle),
                        artist = mb.NowPlaying_GetFileTag(MetaDataType.Artist),
                        albumName = mb.NowPlaying_GetFileTag(MetaDataType.Album),
                        images = art == null ? new object[0] : new object[] { new { url = url + "art" } }
                    }
                }
            });
        }

        void Tick(object _)
        {
            var c = cast;
            if (c == null) return;
            try
            {
                if (++ticks % 5 == 0) c.Ping();
                if (mb.NowPlaying_GetFileUrl() != file) return;   // track change in progress, Load will catch up
                // MusicBee has no seek event, so watch for its position jumping.
                int pos = mb.Player_GetPosition(), now = Environment.TickCount;
                int expected = lastPos + (mb.Player_GetPlayState() == PlayState.Playing ? now - lastTick : 0);
                if (Math.Abs(pos - expected) > 2000) c.Media(new { type = "SEEK", mediaSessionId = c.MediaSession, currentTime = pos / 1000.0 });
                lastPos = pos;
                lastTick = now;
            }
            catch { }   // an exception on a timer thread would take MusicBee down with it
        }

        // Volume keys go through the TV's remote control service, and the TV passes them on to the sound system over HDMI-CEC.
        void PressKey(int key)
        {
            var c = cast;
            if (c == null) return;
            var window = Control.FromHandle(mb.MB_GetWindowHandle());
            ThreadPool.QueueUserWorkItem(_ =>
            {
                lock (remoteLock)
                {
                    try
                    {
                        if (remote == null || remote.IsClosed) remote = ConnectRemote(c, window);
                        remote.Press(key);
                    }
                    catch (Exception e)
                    {
                        Log("Volume key failed: " + e);
                        MessageBox.Show("Could not change the volume on " + c.Name + ":\n" + e.Message, "Chromecast");
                    }
                }
            });
        }

        TvRemote ConnectRemote(Cast c, Control window)
        {
            if (certificate == null) certificate = Certificate();
            try { return new TvRemote(c.Address, certificate); }
            catch (Exception) { }   // not paired yet
            TvRemote.Pair(c.Address, certificate, () => (string)window.Invoke((Func<string>)(() =>
                Interaction.InputBox("Enter the code shown on " + c.Name + ":", "Pair with " + c.Name))));
            return new TvRemote(c.Address, certificate);
        }

        // Who we are to the TV's remote control service; the TV remembers it after pairing.
        X509Certificate2 Certificate()
        {
            var path = Path.Combine(mb.Setting_GetPersistentStoragePath(), "Chromecast.pfx");
            if (!File.Exists(path))
            {
                var request = new CertificateRequest("CN=MusicBee", RSA.Create(2048), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                File.WriteAllBytes(path, request.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddYears(20)).Export(X509ContentType.Pfx));
            }
            return new X509Certificate2(path);
        }

        static string Mime(string path)
        {
            var ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
            return ext == "mp3" ? "audio/mpeg" : ext == "m4a" ? "audio/mp4" : ext == "ogg" || ext == "opus" ? "audio/ogg" : "audio/" + ext;
        }

        // Tiny HTTP server for the Chromecast to fetch /<version>/audio and /<version>/art from.
        void StartServer()
        {
            http = new TcpListener(IPAddress.Any, 0);
            http.Start();
            new Thread(() =>
            {
                while (true)
                {
                    var client = http.AcceptTcpClient();
                    new Thread(() => Serve(client)) { IsBackground = true }.Start();
                }
            }) { IsBackground = true }.Start();
        }

        void Serve(TcpClient client)
        {
            try
            {
                using (client)
                {
                    var stream = client.GetStream();
                    var reader = new StreamReader(stream);
                    var request = reader.ReadLine();
                    var path = request.Split(' ')[1].Split('/');
                    long from = 0, to = long.MaxValue;
                    bool partial = false;
                    for (string line; !string.IsNullOrEmpty(line = reader.ReadLine()); )
                    {
                        var range = Regex.Match(line, @"^Range:\s*bytes=(\d+)-(\d*)", RegexOptions.IgnoreCase);
                        if (!range.Success) continue;
                        partial = true;
                        from = long.Parse(range.Groups[1].Value);
                        if (range.Groups[2].Length > 0) to = long.Parse(range.Groups[2].Value);
                    }

                    string file = this.file;
                    byte[] art = this.art;
                    bool isArt = path.Length == 3 && path[2] == "art";
                    if (path.Length != 3 || path[1] != version.ToString() || isArt && art == null)
                    {
                        Log("HTTP " + request + " -> 404");
                        Write(stream, "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
                        return;
                    }
                    using (var data = isArt ? new MemoryStream(art) : (Stream)File.OpenRead(file))
                    {
                        to = Math.Min(to, data.Length - 1);
                        Log("HTTP " + request + " -> bytes " + from + "-" + to + " of " + data.Length);
                        Write(stream, (partial ? "HTTP/1.1 206 Partial Content\r\nContent-Range: bytes " + from + "-" + to + "/" + data.Length : "HTTP/1.1 200 OK")
                            + "\r\nContent-Type: " + (isArt ? (art[0] == 0x89 ? "image/png" : "image/jpeg") : Mime(file))
                            + "\r\nContent-Length: " + (to - from + 1)
                            + "\r\nAccept-Ranges: bytes\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n\r\n");
                        data.Position = from;
                        var buffer = new byte[65536];
                        for (long left = to - from + 1; left > 0; )
                        {
                            int n = data.Read(buffer, 0, (int)Math.Min(buffer.Length, left));
                            if (n <= 0) break;
                            stream.Write(buffer, 0, n);
                            left -= n;
                        }
                    }
                }
            }
            catch (Exception e) { Log("HTTP ended early: " + e.Message); }   // normal when the Chromecast seeks or skips
        }

        static void Write(Stream stream, string text)
        {
            var bytes = Encoding.ASCII.GetBytes(text);
            stream.Write(bytes, 0, bytes.Length);
        }

        // mDNS: ask for _googlecast._tcp.local and collect friendly name -> address from the answers.
        static Dictionary<string, IPEndPoint> Discover()
        {
            var found = new Dictionary<string, IPEndPoint>();
            var query = new List<byte> { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0 };   // header with one question
            foreach (var label in "_googlecast._tcp.local".Split('.'))
            {
                query.Add((byte)label.Length);
                query.AddRange(Encoding.ASCII.GetBytes(label));
            }
            query.AddRange(new byte[] { 0, 0, 12, 0, 1 });                        // end of name, type PTR, class IN
            using (var udp = new UdpClient(0))
            {
                // Ask on every network adapter, the Chromecast may not be behind the default one.
                foreach (var local in Dns.GetHostAddresses(Dns.GetHostName()).Where(a => a.AddressFamily == AddressFamily.InterNetwork))
                {
                    try
                    {
                        udp.Client.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, local.GetAddressBytes());
                        udp.Send(query.ToArray(), query.Count, new IPEndPoint(IPAddress.Parse("224.0.0.251"), 5353));
                    }
                    catch (SocketException) { }
                }
                for (var end = DateTime.Now.AddSeconds(2); DateTime.Now < end; )
                {
                    udp.Client.ReceiveTimeout = Math.Max(1, (int)(end - DateTime.Now).TotalMilliseconds);
                    var from = new IPEndPoint(IPAddress.Any, 0);
                    byte[] answer;
                    try { answer = udp.Receive(ref from); }
                    catch (SocketException) { break; }
                    try { ReadAnswer(answer, from.Address, found); }
                    catch (IndexOutOfRangeException) { }
                }
            }
            return found;
        }

        static void ReadAnswer(byte[] b, IPAddress ip, Dictionary<string, IPEndPoint> found)
        {
            var names = new Dictionary<string, string>();
            var ports = new Dictionary<string, int>();
            int i = 12;
            for (int questions = b[4] << 8 | b[5]; questions > 0; questions--)
            {
                ReadName(b, ref i);
                i += 4;
            }
            for (int records = (b[6] << 8 | b[7]) + (b[8] << 8 | b[9]) + (b[10] << 8 | b[11]); records > 0; records--)
            {
                var name = ReadName(b, ref i);
                int type = b[i] << 8 | b[i + 1], start = i + 10, end = start + (b[i + 8] << 8 | b[i + 9]);
                if (type == 33)   // SRV: priority, weight, port, target
                    ports[name] = b[start + 4] << 8 | b[start + 5];
                if (type == 16)   // TXT: length-prefixed key=value strings
                    for (int j = start; j < end; j += b[j] + 1)
                    {
                        var entry = Encoding.UTF8.GetString(b, j + 1, b[j]);
                        if (entry.StartsWith("fn=")) names[name] = entry.Substring(3);
                    }
                i = end;
            }
            foreach (var n in names) found[n.Value] = new IPEndPoint(ip, ports.ContainsKey(n.Key) ? ports[n.Key] : 8009);
        }

        static string ReadName(byte[] b, ref int i)
        {
            var labels = new List<string>();
            int resume = -1;
            for (int jumps = 0; b[i] != 0 && jumps < 64; )
            {
                if (b[i] >= 0xC0)   // pointer to a name earlier in the packet
                {
                    if (resume < 0) resume = i + 2;
                    i = (b[i] & 0x3F) << 8 | b[i + 1];
                    jumps++;
                }
                else
                {
                    labels.Add(Encoding.UTF8.GetString(b, i + 1, b[i]));
                    i += b[i] + 1;
                }
            }
            i = resume < 0 ? i + 1 : resume;
            return string.Join(".", labels);
        }
    }

    // Just enough of the Cast v2 protocol: JSON messages wrapped in protobuf, each prefixed with a 4-byte length.
    class Cast : Connection
    {
        const string ConnectionNamespace = "urn:x-cast:com.google.cast.tp.connection";
        const string HeartbeatNamespace = "urn:x-cast:com.google.cast.tp.heartbeat";
        const string ReceiverNamespace = "urn:x-cast:com.google.cast.receiver";
        const string MediaNamespace = "urn:x-cast:com.google.cast.media";
        const string AppId = "CC1AD845";   // Google's Default Media Receiver
        static readonly JavaScriptSerializer Json = new JavaScriptSerializer();

        public readonly string Name;
        public readonly IPAddress Address;
        public readonly IPAddress LocalIp;   // our address as the Chromecast sees it
        public string Transport, Session;
        public int MediaSession;
        public Action<string> StateChanged;
        public Action Restarted;   // the TV restarted the player, so the track has to be loaded again
        public Action Closed;
        int requestId;
        bool started;
        System.Threading.Timer lost;

        public Cast(string name, IPEndPoint endpoint) : base(endpoint, null)
        {
            Name = name;
            Address = endpoint.Address;
            LocalIp = ((IPEndPoint)tcp.Client.LocalEndPoint).Address;
            new Thread(ReadLoop) { IsBackground = true }.Start();
            Send(ConnectionNamespace, "receiver-0", new { type = "CONNECT" });
            Send(ReceiverNamespace, "receiver-0", new { type = "LAUNCH", appId = AppId });
            for (int i = 0; i < 300 && Transport == null; i++) Thread.Sleep(100);   // waking a TV from standby takes a while
            if (Transport != null) return;
            tcp.Close();
            throw new TimeoutException("The media player did not start.");
        }

        public void Media(object message)
        {
            var transport = Transport;
            if (transport != null) Send(MediaNamespace, transport, message);
        }

        public void SetVolume(float level) { Send(ReceiverNamespace, "receiver-0", new { type = "SET_VOLUME", volume = new { level } }); }

        public void Ping() { Send(HeartbeatNamespace, "receiver-0", new { type = "PING" }); }

        public override void Close()
        {
            if (Session != null) Send(ReceiverNamespace, "receiver-0", new { type = "STOP", sessionId = Session });
            base.Close();
        }

        void Send(string ns, string destination, object message)
        {
            var payload = "{\"requestId\":" + Interlocked.Increment(ref requestId) + "," + Json.Serialize(message).Substring(1);
            if (ns != HeartbeatNamespace) Plugin.Log("> " + payload);
            // CastMessage: protocol version, source, destination, namespace, payload type (string), payload
            var body = Join(Field(1, 0), Field(2, "sender-0"), Field(3, destination), Field(4, ns), Field(5, 0), Field(6, payload));
            Write(Join(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(body.Length)), body));
        }

        void ReadLoop()
        {
            try
            {
                while (true)
                {
                    var header = Read(4);
                    var message = Parse(Read(header[0] << 24 | header[1] << 16 | header[2] << 8 | header[3]));
                    string ns = Text(message, 4), payload = Text(message, 6) ?? "";
                    if (ns != HeartbeatNamespace) Plugin.Log("< " + payload);
                    Handle(Text(message, 2), ns, payload);
                }
            }
            catch (Exception e) { Plugin.Log("Connection to " + Name + " closed: " + e.Message); }
            tcp.Close();
            if (Closed != null) Closed();
        }

        void Handle(string source, string ns, string payload)
        {
            if (ns == HeartbeatNamespace && payload.Contains("\"PING\""))
                Send(HeartbeatNamespace, source, new { type = "PONG" });
            else if (ns == ReceiverNamespace && payload.Contains("RECEIVER_STATUS"))
            {
                var transport = payload.Contains(AppId) ? Find(payload, "transportId") : null;
                if (transport == Transport) return;
                Transport = transport;
                if (transport == null)
                {
                    // Stopped from a phone or the TV, unless the TV is only restarting the player.
                    lost = new System.Threading.Timer(_ => { if (Transport == null) tcp.Close(); }, null, 10000, Timeout.Infinite);
                    return;
                }
                // A new player session, the first one or a restart (e.g. while the TV wakes up).
                Session = Find(payload, "sessionId");
                Send(ConnectionNamespace, transport, new { type = "CONNECT" });
                if (started && Restarted != null) Restarted();
                started = true;
            }
            else if (ns == MediaNamespace)
            {
                var id = Find(payload, "mediaSessionId");
                if (id != null) MediaSession = int.Parse(id);
                var state = Find(payload, "playerState");
                if (state != null && StateChanged != null) StateChanged(state);
            }
        }

        static string Find(string json, string key)
        {
            var match = Regex.Match(json, "\"" + key + "\"\\s*:\\s*\"?([^\",}]+)");
            return match.Success ? match.Groups[1].Value : null;
        }
    }

    // Android TV Remote protocol v2, the one the Google TV phone app uses. Here it only presses the volume keys.
    class TvRemote : Connection
    {
        public const int VolumeUp = 24, VolumeDown = 25;
        const int Features = 622;
        public bool IsClosed;
        bool ready;

        public TvRemote(IPAddress address, X509Certificate2 certificate) : base(new IPEndPoint(address, 6466), certificate)
        {
            new Thread(ReadLoop) { IsBackground = true }.Start();
            for (int i = 0; i < 50 && !ready && !IsClosed; i++) Thread.Sleep(100);
            if (ready) return;
            Close();
            throw new IOException("The TV did not accept the remote control connection.");
        }

        public void Press(int key) { WriteDelimited(Field(10, Field(1, key), Field(2, 3))); }   // key_inject, short press

        void ReadLoop()
        {
            try
            {
                while (true)
                {
                    var message = ReadDelimited();
                    if (message.ContainsKey(1))        // configure: introduce ourselves
                        WriteDelimited(Field(1, Field(1, Features), Field(2, Field(1, "MusicBee"), Field(2, "MusicBee"), Field(3, 1), Field(4, "1"), Field(5, "musicbee"), Field(6, "1.0"))));
                    else if (message.ContainsKey(2))   // set_active
                    {
                        WriteDelimited(Field(2, Field(1, Features)));
                        ready = true;
                    }
                    else if (message.ContainsKey(8))   // ping
                        WriteDelimited(Field(9, Field(1, Number(Parse((byte[])message[8]), 1))));
                }
            }
            catch { }
            IsClosed = true;
            Close();
        }

        // One-time pairing on port 6467: the TV shows a code, the user types it in, and the TV trusts our certificate from then on.
        public static void Pair(IPAddress address, X509Certificate2 certificate, Func<string> askForCode)
        {
            var pairing = new Connection(new IPEndPoint(address, 6467), certificate);
            try
            {
                var sixHexDigits = Field(1, Field(1, 3), Field(2, 6));
                Request(pairing, Field(10, Field(1, "atvremote"), Field(2, "MusicBee")));   // pairing request: service, client name
                Request(pairing, Field(20, sixHexDigits, Field(3, 1)));                      // options: we enter the code
                Request(pairing, Field(30, sixHexDigits, Field(2, 1)));                      // configuration: now the TV shows it
                var code = (askForCode() ?? "").Trim();
                if (!Regex.IsMatch(code, "^[0-9A-Fa-f]{6}$")) throw new Exception("Pairing cancelled.");
                // The secret proves we saw the code: a hash of both public keys and the code's last four digits.
                var client = certificate.GetRSAPublicKey().ExportParameters(false);
                var server = new X509Certificate2(pairing.RemoteCertificate).GetRSAPublicKey().ExportParameters(false);
                var digits = Enumerable.Range(1, 2).Select(i => Convert.ToByte(code.Substring(i * 2, 2), 16)).ToArray();
                var secret = SHA256.Create().ComputeHash(Join(client.Modulus, client.Exponent, server.Modulus, server.Exponent, digits));
                if (secret[0] != Convert.ToByte(code.Substring(0, 2), 16)) throw new Exception("That is not the code shown on the TV.");
                Request(pairing, Field(40, Field(1, secret)));
            }
            finally { pairing.Close(); }
        }

        static void Request(Connection pairing, byte[] content)
        {
            pairing.WriteDelimited(Join(Field(1, 2), Field(2, 200), content));   // protocol version 2, status OK
            if (Number(pairing.ReadDelimited(), 2) != 200) throw new Exception("The TV refused to pair.");
        }
    }

    // A TLS connection carrying protobuf messages, the transport of both protocols above.
    class Connection
    {
        protected readonly TcpClient tcp = new TcpClient();
        readonly SslStream ssl;

        public Connection(IPEndPoint endpoint, X509Certificate2 certificate)
        {
            tcp.Connect(endpoint);
            try
            {
                // Both ends use self-signed certificates.
                ssl = new SslStream(tcp.GetStream(), false, (a, b, c, d) => true, (a, b, c, d, e) => certificate);
                var certificates = certificate == null ? null : new X509CertificateCollection(new X509Certificate[] { certificate });
                ssl.AuthenticateAsClient(endpoint.Address.ToString(), certificates, SslProtocols.Tls12, false);
            }
            catch
            {
                tcp.Close();
                throw;
            }
        }

        public X509Certificate RemoteCertificate { get { return ssl.RemoteCertificate; } }

        public virtual void Close() { tcp.Close(); }

        public void Write(byte[] bytes)
        {
            try { lock (ssl) ssl.Write(bytes, 0, bytes.Length); }
            catch { tcp.Close(); }
        }

        public byte[] Read(int count)
        {
            var bytes = new byte[count];
            for (int read = 0; read < count; )
            {
                int n = ssl.Read(bytes, read, count - read);
                if (n <= 0) throw new IOException("Connection closed.");
                read += n;
            }
            return bytes;
        }

        // Messages prefixed with their length as a varint.
        public void WriteDelimited(byte[] message) { Write(Join(Varint(message.Length), message)); }

        public Dictionary<int, object> ReadDelimited()
        {
            int length = 0;
            for (int shift = 0; ; shift += 7)
            {
                var b = Read(1)[0];
                length |= (b & 0x7F) << shift;
                if (b < 0x80) return Parse(Read(length));
            }
        }

        public static byte[] Join(params byte[][] parts) { return parts.SelectMany(p => p).ToArray(); }

        public static byte[] Varint(long value)
        {
            var bytes = new List<byte>();
            for (; value >= 0x80; value >>= 7) bytes.Add((byte)(value | 0x80));
            bytes.Add((byte)value);
            return bytes.ToArray();
        }

        public static byte[] Field(int number, long value) { return Join(Varint(number << 3), Varint(value)); }

        public static byte[] Field(int number, string value) { return Field(number, Encoding.UTF8.GetBytes(value)); }

        public static byte[] Field(int number, params byte[][] content)
        {
            var bytes = Join(content);
            return Join(Varint(number << 3 | 2), Varint(bytes.Length), bytes);
        }

        // Field number -> value: a number, or the bytes of a string or nested message.
        public static Dictionary<int, object> Parse(byte[] message)
        {
            var fields = new Dictionary<int, object>();
            for (int i = 0; i < message.Length; )
            {
                long tag = ReadVarint(message, ref i);
                if ((tag & 7) == 0)
                    fields[(int)(tag >> 3)] = ReadVarint(message, ref i);
                else if ((tag & 7) == 2)
                {
                    int length = (int)ReadVarint(message, ref i);
                    fields[(int)(tag >> 3)] = message.Skip(i).Take(length).ToArray();
                    i += length;
                }
                else
                    i += (tag & 7) == 1 ? 8 : 4;   // fixed-size numbers, not used by either protocol
            }
            return fields;
        }

        public static long Number(Dictionary<int, object> message, int field)
        {
            object value;
            return message.TryGetValue(field, out value) && value is long ? (long)value : 0;
        }

        public static string Text(Dictionary<int, object> message, int field)
        {
            object value;
            return message.TryGetValue(field, out value) && value is byte[] ? Encoding.UTF8.GetString((byte[])value) : null;
        }

        static long ReadVarint(byte[] bytes, ref int i)
        {
            long value = 0;
            for (int shift = 0; ; shift += 7)
            {
                value |= (long)(bytes[i] & 0x7F) << shift;
                if (bytes[i++] < 0x80) return value;
            }
        }
    }
}
