using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Collections.Concurrent;

class Program
{
    static async Task Main(string[] args)
    {
        Console.Write("Enter your port (e.g. 6000): ");
        int port = int.Parse(Console.ReadLine()!);

        Console.Write("Enter your nickname: ");
        string nickname = Console.ReadLine()!;

        var node = new ChatNode(port, nickname);
        await node.StartAsync();
    }
}

class ChatNode
{
    private readonly int _port;
    private readonly string _nickname;
    private readonly ConcurrentDictionary<IPEndPoint, TcpClient> _clients = new();
    private TcpListener _listener;
    private CancellationTokenSource _cts = new();
    private readonly HashSet<Guid> _processedMessages = new();

    public ChatNode(int port, string nickname)
    {
        _port = port;
        _nickname = nickname;
        _listener = new TcpListener(IPAddress.Loopback, _port);
    }

    public async Task StartAsync()
    {
        _listener.Start();
        Console.WriteLine($"Chat started on port {_port}");

        var tasks = new List<Task>
        {
            ListenForConnections(),
            ConnectToPeers(),
            HandleUserInput()
        };

        await Task.WhenAll(tasks);
    }

    private async Task ListenForConnections()
    {
        while (!_cts.IsCancellationRequested)
        {
            var client = await _listener.AcceptTcpClientAsync();
            var remoteEP = (IPEndPoint)client.Client.RemoteEndPoint!;

            if (!_clients.ContainsKey(remoteEP))
            {
                _clients.TryAdd(remoteEP, client);
                _ = ReceiveMessages(client);
                Console.WriteLine($"[System] Connected with {remoteEP.Port}");
            }
            else
            {
                client.Dispose();
            }
        }
    }

    private async Task ConnectToPeers()
    {
        while (!_cts.IsCancellationRequested)
        {
            foreach (var port in GetPeerPorts())
            {
                var endpoint = new IPEndPoint(IPAddress.Loopback, port);
                if (_clients.ContainsKey(endpoint)) continue;

                try
                {
                    var client = new TcpClient();
                    await client.ConnectAsync(endpoint);

                    if (!_clients.ContainsKey(endpoint))
                    {
                        _clients.TryAdd(endpoint, client);
                        _ = ReceiveMessages(client);
                        Console.WriteLine($"[System] Connected to {port}");
                    }
                    else
                    {
                        client.Dispose();
                    }
                }
                catch { }
            }
            await Task.Delay(2000);
        }
    }

    private async Task ReceiveMessages(TcpClient client)
    {
        var buffer = new byte[1024];
        var stream = client.GetStream();

        try
        {
            while (!_cts.IsCancellationRequested)
            {
                int bytesRead = await stream.ReadAsync(buffer, _cts.Token);
                if (bytesRead == 0) break;

                var message = Encoding.UTF8.GetString(buffer, 0, bytesRead);
                var parts = message.Split('|');

                if (parts.Length == 2 && Guid.TryParse(parts[0], out var messageId))
                {
                    lock (_processedMessages)
                    {
                        if (!_processedMessages.Add(messageId)) continue;
                    }

                    Console.WriteLine(parts[1]);
                }
            }
        }
        catch
        {
            RemoveClient(client);
        }
    }

    private async Task HandleUserInput()
    {
        while (!_cts.IsCancellationRequested)
        {
            var input = Console.ReadLine();
            if (string.IsNullOrEmpty(input)) continue;

            var messageId = Guid.NewGuid();
            var message = $"{messageId}|[{DateTime.Now:HH:mm}] {_nickname}: {input}";
            await BroadcastMessage(message);
        }
    }

    private async Task BroadcastMessage(string message)
    {
        var data = Encoding.UTF8.GetBytes(message);

        foreach (var client in _clients.Values.ToList())
        {
            try
            {
                await client.GetStream().WriteAsync(data, _cts.Token);
            }
            catch
            {
                RemoveClient(client);
            }
        }
    }

    private void RemoveClient(TcpClient client)
    {
        var endpoint = (IPEndPoint)client.Client.RemoteEndPoint!;
        _clients.TryRemove(endpoint, out _);
        client.Dispose();
        Console.WriteLine($"[System] Disconnected from {endpoint.Port}");
    }

    private IEnumerable<int> GetPeerPorts()
    {
        return Enumerable.Range(6000, 5).Where(p => p != _port);
    }
}