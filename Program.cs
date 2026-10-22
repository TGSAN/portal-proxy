using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PortalProxy;

internal static class Program
{
    private const int DefaultListenPort = 1080;
    private const int MaxHeaderSize = 64 * 1024;
    private const int BufSize = 65536;
    private static readonly TimeSpan ClientTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(10);

    // 这些路径的 HTTP 请求直接返回 204，不转发
    private static readonly string[] NoContentPaths = ["/gen_204", "/generate_204"];

    // 类似 hosts：匹配的域名改为连接到指定地址，Host 头和 TLS SNI 保持不变
    // 规则格式 (域名, 目标地址)，域名本身及其所有子域名都会命中
    private static readonly (string Domain, string Target)[] HostOverrides =
    [
        ("google.com", "www.gstatic.com"),
        ("googleapis.com", "services.googleapis.cn"),
    ];

    // 访问控制：UA 和域名同时命中白名单才转发，否则返回 403（204 拦截不受此限制）
    private enum UaMatch { Exact, Contains, Prefix }

    // UA 规则 (匹配方式, 值)，匹配方式: Exact 完全一致 / Contains 包含 / Prefix 开头
    private static readonly (UaMatch Mode, string Value)[] UaAllowlist =
    [
        (UaMatch.Exact, "PS Portal"),
        (UaMatch.Contains, "Bengal for arm64"),
        (UaMatch.Prefix, "okhttp/"),
    ];

    // 域名白名单，域名本身及其所有子域名都会命中
    private static readonly string[] DomainAllowlist =
    [
        "google.com",
        "googleapis.com",
        "playstation.com",
        "playstation.net",
        "sony.com",
    ];

    private static readonly byte[] HeadTerminator = "\r\n\r\n"u8.ToArray();
    private static readonly Lock LogLock = new();

    private const string DefaultListenHost = "0.0.0.0";

    private static readonly string Usage =
        $"""
         用法: portal-proxy [选项]

         选项:
           -l, --listen <地址>   监听地址或主机名 (默认 {DefaultListenHost})
           -p, --port <端口>     监听端口 1-65535 (默认 {DefaultListenPort})
           -h, --help            显示此帮助
         """;

    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        string listenHost = DefaultListenHost;
        int listenPort = DefaultListenPort;

        for (int i = 0; i < args.Length; i++)
        {
            string arg = args[i];
            // 支持 --port=8080 形式
            string? inlineValue = null;
            int eq = arg.IndexOf('=');
            if (arg.StartsWith("--", StringComparison.Ordinal) && eq > 0)
            {
                inlineValue = arg[(eq + 1)..];
                arg = arg[..eq];
            }

            switch (arg)
            {
                case "-h" or "--help":
                    Console.WriteLine(Usage);
                    return 0;
                case "-l" or "--listen":
                    if (!TryTakeValue(args, ref i, arg, inlineValue, out listenHost))
                        return 1;
                    break;
                case "-p" or "--port":
                    if (!TryTakeValue(args, ref i, arg, inlineValue, out string portStr))
                        return 1;
                    if (!int.TryParse(portStr, NumberStyles.None, CultureInfo.InvariantCulture, out listenPort)
                        || listenPort is < 1 or > 65535)
                    {
                        Console.Error.WriteLine($"非法端口: {portStr}，应为 1-65535");
                        return 1;
                    }
                    break;
                default:
                    Console.Error.WriteLine($"未知参数: {args[i]}\n\n{Usage}");
                    return 1;
            }
        }

        if (!IPAddress.TryParse(listenHost, out var listenAddr))
        {
            listenAddr = Dns.GetHostAddresses(listenHost)
                .First(a => a.AddressFamily == AddressFamily.InterNetwork);
        }

        using var server = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        server.Bind(new IPEndPoint(listenAddr, listenPort));
        server.Listen(128);
        Log($"HTTP 代理已启动: {listenHost}:{listenPort}");

        while (true)
        {
            var client = await server.AcceptAsync();
            _ = Task.Run(() => HandleClientAsync(client));
        }
    }

    /// <summary>取选项的值：优先用 --name=value 中的值，否则取下一个参数。</summary>
    private static bool TryTakeValue(string[] args, ref int i, string name, string? inlineValue, out string value)
    {
        if (inlineValue is not null)
        {
            value = inlineValue;
            return true;
        }
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine($"参数 {name} 缺少值");
            value = "";
            return false;
        }
        value = args[++i];
        return true;
    }

    private static bool MatchesDomain(string host, string domain) =>
        host == domain || host.EndsWith("." + domain, StringComparison.Ordinal);

    private static string NormalizeHost(string host) => host.ToLowerInvariant().TrimEnd('.');

    /// <summary>返回实际要连接的地址，未命中规则则原样返回。</summary>
    private static string ResolveTarget(string host)
    {
        string h = NormalizeHost(host);
        foreach (var (domain, target) in HostOverrides)
        {
            if (MatchesDomain(h, domain))
                return target;
        }
        return host;
    }

    private static bool IsUaAllowed(string? ua)
    {
        if (ua is null)
            return false;
        foreach (var (mode, value) in UaAllowlist)
        {
            bool hit = mode switch
            {
                UaMatch.Exact => ua == value,
                UaMatch.Contains => ua.Contains(value, StringComparison.Ordinal),
                UaMatch.Prefix => ua.StartsWith(value, StringComparison.Ordinal),
                _ => false,
            };
            if (hit)
                return true;
        }
        return false;
    }

    private static bool IsDomainAllowed(string host)
    {
        string h = NormalizeHost(host);
        return DomainAllowlist.Any(d => MatchesDomain(h, d));
    }

    /// <summary>返回拒绝原因，允许访问时返回 null。</summary>
    private static string? CheckAccess(string host, string? ua)
    {
        if (!IsUaAllowed(ua))
            return "UA 不在白名单";
        if (!IsDomainAllowed(host))
            return $"域名 {host} 不在白名单";
        return null;
    }

    private static void Log(string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        lock (LogLock)
        {
            Console.WriteLine(line);
            Console.Out.Flush();
        }
    }

    private static void CloseQuietly(Socket? sock)
    {
        if (sock is null)
            return;
        try
        {
            sock.Dispose();
        }
        catch
        {
            // 忽略
        }
    }

    private static Task SendAllAsync(Socket sock, ReadOnlyMemory<byte> data, CancellationToken ct = default) =>
        // Socket.SendAsync 对流式套接字会一直发送直到全部写完
        sock.SendAsync(data, SocketFlags.None, ct).AsTask();

    /// <summary>单向转发数据，直到一端关闭。</summary>
    private static async Task PipeAsync(Socket src, Socket dst)
    {
        var buf = new byte[BufSize];
        try
        {
            while (true)
            {
                int n = await src.ReceiveAsync(buf, SocketFlags.None);
                if (n == 0)
                    break;
                await SendAllAsync(dst, buf.AsMemory(0, n));
            }
        }
        catch
        {
            // 连接异常时结束转发
        }
        finally
        {
            // 通知对端写方向结束，另一个方向的转发会随之退出
            try
            {
                dst.Shutdown(SocketShutdown.Send);
            }
            catch
            {
                // 忽略
            }
        }
    }

    /// <summary>双向转发，返回时关闭两端。a 为客户端，b 为上游。</summary>
    private static async Task RelayAsync(Socket a, Socket b)
    {
        await Task.WhenAll(PipeAsync(b, a), PipeAsync(a, b));
        CloseQuietly(a);
        CloseQuietly(b);
    }

    /// <summary>读取请求头，返回 (头部字节, 头部之后已读到的多余字节)，失败返回 null。</summary>
    private static async Task<(byte[] Head, byte[] Body)?> ReadHeadAsync(Socket sock, CancellationToken ct)
    {
        var buf = new MemoryStream();
        var chunk = new byte[BufSize];
        int searchFrom = 0;
        while (true)
        {
            if (TrySplitHead(buf, searchFrom) is { } result)
                return result;
            if (buf.Length > MaxHeaderSize)
                return null;
            // 下次从可能跨块的位置继续查找
            searchFrom = Math.Max(0, (int)buf.Length - (HeadTerminator.Length - 1));
            int n = await sock.ReceiveAsync(chunk, SocketFlags.None, ct);
            if (n == 0)
                return null;
            buf.Write(chunk, 0, n);
        }
    }

    /// <summary>在缓冲区中查找头部结束标记，找到则拆分为头部和剩余部分。</summary>
    private static (byte[] Head, byte[] Body)? TrySplitHead(MemoryStream buf, int searchFrom)
    {
        var span = buf.GetBuffer().AsSpan(0, (int)buf.Length);
        int idx = span[searchFrom..].IndexOf(HeadTerminator);
        if (idx < 0)
            return null;
        idx += searchFrom;
        return (span[..idx].ToArray(), span[(idx + HeadTerminator.Length)..].ToArray());
    }

    /// <summary>连接上游，按 HostOverrides 替换实际连接地址（只改 TCP 目标，不改请求内容）。</summary>
    private static async Task<Socket> ConnectUpstreamAsync(string clientId, string host, int port)
    {
        string target = ResolveTarget(host);
        if (target != host)
            Log($"{clientId}  重定向 {host}:{port} -> {target}:{port}");

        var sock = new Socket(SocketType.Stream, ProtocolType.Tcp);
        try
        {
            using var cts = new CancellationTokenSource(ConnectTimeout);
            await sock.ConnectAsync(target, port, cts.Token);
        }
        catch (OperationCanceledException)
        {
            sock.Dispose();
            throw new TimeoutException("连接超时");
        }
        catch
        {
            sock.Dispose();
            throw;
        }

        if (target != host && sock.RemoteEndPoint is IPEndPoint ep)
        {
            var ip = ep.Address.IsIPv4MappedToIPv6 ? ep.Address.MapToIPv4() : ep.Address;
            Log($"{clientId}  已连接 {ip}");
        }
        return sock;
    }

    private static async Task HandleClientAsync(Socket client)
    {
        string clientId = client.RemoteEndPoint is IPEndPoint rep ? $"{rep.Address}:{rep.Port}" : "?";
        Socket? upstream = null;
        // 读取请求头及发送早期响应时的超时，进入转发后不再限时
        using var clientCts = new CancellationTokenSource(ClientTimeout);
        var ct = clientCts.Token;
        try
        {
            var headResult = await ReadHeadAsync(client, ct);
            if (headResult is not var (head, rest))
            {
                CloseQuietly(client);
                return;
            }

            string[] lines = Encoding.Latin1.GetString(head).Split("\r\n");
            string[] parts = lines[0].Split(' ', 3);
            if (parts.Length != 3)
            {
                CloseQuietly(client);
                return;
            }
            string method = parts[0], target = parts[1], version = parts[2];

            var headers = new List<(string Key, string Value)>();
            foreach (string line in lines.Skip(1))
            {
                int colon = line.IndexOf(':');
                if (colon >= 0)
                    headers.Add((line[..colon].Trim(), line[(colon + 1)..].Trim()));
            }
            string? ua = null;
            foreach (var (k, v) in headers)
            {
                if (k.Equals("user-agent", StringComparison.OrdinalIgnoreCase))
                {
                    // 按 UTF-8 还原，防止非 ASCII 的 UA 乱码
                    ua = Encoding.UTF8.GetString(Encoding.Latin1.GetBytes(v));
                    break;
                }
            }
            Log($"{clientId}  {method} {target}\n    UA: {ua ?? "<无 User-Agent>"}");

            async Task DenyAsync(string reason)
            {
                Log($"{clientId}  {reason}，拒绝连接");
                await SendAllAsync(client,
                    "HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), ct);
                CloseQuietly(client);
            }

            try
            {
                if (method.Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    // HTTPS 隧道：目标为 host:port
                    int sep = target.LastIndexOf(':');
                    string host = (sep >= 0 ? target[..sep] : "").Trim('[', ']');
                    string portStr = sep >= 0 ? target[(sep + 1)..] : target;
                    string? reason = CheckAccess(host, ua);
                    if (reason is not null)
                    {
                        await DenyAsync(reason);
                        return;
                    }
                    int port = portStr.Length == 0 ? 443 : ParsePort(portStr);
                    upstream = await ConnectUpstreamAsync(clientId, host, port);
                    await SendAllAsync(client, "HTTP/1.1 200 Connection Established\r\n\r\n"u8.ToArray(), ct);
                }
                else
                {
                    // 普通 HTTP：目标为绝对 URL，改写为相对路径后转发
                    var url = AbsoluteUrl.Parse(target);
                    if (url.Hostname is null)
                    {
                        await SendAllAsync(client, "HTTP/1.1 400 Bad Request\r\nConnection: close\r\n\r\n"u8.ToArray(), ct);
                        CloseQuietly(client);
                        return;
                    }
                    string path = url.Path.Length > 0 ? url.Path : "/";
                    // 连通性检测请求直接本地返回 204，不访问上游，也不受访问控制限制
                    if (NoContentPaths.Contains(path))
                    {
                        Log($"{clientId}  已拦截，直接返回 204");
                        await SendAllAsync(client,
                            "HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray(), ct);
                        CloseQuietly(client);
                        return;
                    }
                    string? reason = CheckAccess(url.Hostname, ua);
                    if (reason is not null)
                    {
                        await DenyAsync(reason);
                        return;
                    }
                    if (url.Query.Length > 0)
                        path += "?" + url.Query;
                    // 每个连接只处理一个请求，强制 Connection: close 以保证正确性
                    string[] skip = ["proxy-connection", "connection", "keep-alive", "proxy-authorization"];
                    var sb = new StringBuilder();
                    sb.Append($"{method} {path} {version}\r\n");
                    foreach (var (k, v) in headers)
                    {
                        if (!skip.Contains(k.ToLowerInvariant()))
                            sb.Append($"{k}: {v}\r\n");
                    }
                    sb.Append("Connection: close\r\n\r\n");
                    // url.Port 为 0 时与原版一致按 80 处理
                    int port = url.Port is int p && p != 0 ? p : 80;
                    upstream = await ConnectUpstreamAsync(clientId, url.Hostname, port);
                    await SendAllAsync(upstream, Encoding.Latin1.GetBytes(sb.ToString()));
                }
            }
            catch (Exception e) when (e is SocketException or IOException or TimeoutException
                                          or FormatException or ArgumentOutOfRangeException)
            {
                Log($"{clientId}  连接目标失败: {e.Message}");
                await SendAllAsync(client, "HTTP/1.1 502 Bad Gateway\r\nConnection: close\r\n\r\n"u8.ToArray(), ct);
                CloseQuietly(client);
                CloseQuietly(upstream);
                return;
            }

            // 请求头之后已读到的数据（如 POST 请求体）一并转发
            if (rest.Length > 0)
                await SendAllAsync(upstream, rest);
            await RelayAsync(client, upstream);
        }
        catch (Exception e)
        {
            string msg = e is OperationCanceledException ? "超时" : e.Message;
            Log($"{clientId}  处理出错: {msg}");
            CloseQuietly(client);
            CloseQuietly(upstream);
        }
    }

    /// <summary>解析端口号，非法时抛出 FormatException。</summary>
    private static int ParsePort(string s)
    {
        if (!int.TryParse(s.Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int port)
            || port is < 0 or > 65535)
            throw new FormatException($"非法端口: {s}");
        return port;
    }

    /// <summary>按 Python urllib.parse.urlsplit 的规则拆分绝对 URL，不对路径做任何规范化。</summary>
    private sealed record AbsoluteUrl(string? Hostname, int? Port, string Path, string Query)
    {
        public static AbsoluteUrl Parse(string url)
        {
            string rest = url;

            // scheme: 首字符为字母，其余为字母、数字或 + - .
            int colon = rest.IndexOf(':');
            if (colon > 0 && char.IsAsciiLetter(rest[0])
                && rest[..colon].All(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '-' or '.'))
            {
                rest = rest[(colon + 1)..];
            }

            string netloc = "";
            if (rest.StartsWith("//", StringComparison.Ordinal))
            {
                int end = rest.IndexOfAny(['/', '?', '#'], 2);
                if (end < 0)
                    end = rest.Length;
                netloc = rest[2..end];
                rest = rest[end..];
            }

            int hash = rest.IndexOf('#');
            if (hash >= 0)
                rest = rest[..hash];
            string query = "";
            int q = rest.IndexOf('?');
            if (q >= 0)
            {
                query = rest[(q + 1)..];
                rest = rest[..q];
            }

            // 去掉 userinfo 后拆分主机与端口
            string hostinfo = netloc[(netloc.LastIndexOf('@') + 1)..];
            string hostname, portStr;
            int open = hostinfo.IndexOf('[');
            if (open >= 0)
            {
                string bracketed = hostinfo[(open + 1)..];
                int close = bracketed.IndexOf(']');
                hostname = close >= 0 ? bracketed[..close] : bracketed;
                string after = close >= 0 ? bracketed[(close + 1)..] : "";
                int pc = after.IndexOf(':');
                portStr = pc >= 0 ? after[(pc + 1)..] : "";
            }
            else
            {
                int pc = hostinfo.IndexOf(':');
                hostname = pc >= 0 ? hostinfo[..pc] : hostinfo;
                portStr = pc >= 0 ? hostinfo[(pc + 1)..] : "";
            }

            int? port = null;
            if (portStr.Length > 0)
            {
                if (!portStr.All(char.IsAsciiDigit) || !int.TryParse(portStr, out int p) || p > 65535)
                    throw new FormatException($"非法端口: {portStr}");
                port = p;
            }

            return new AbsoluteUrl(hostname.Length > 0 ? hostname.ToLowerInvariant() : null, port, rest, query);
        }
    }
}
