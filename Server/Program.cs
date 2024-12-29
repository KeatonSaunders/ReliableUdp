using ReliableTransport;
using System.Net;
using System.Text;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("Starting server on port 8000...");
        using (var transport = new ReliableUdpTransport(8000))
        {
            while (true)
            {
                var data = transport.Receive();
                if (data != null)
                {
                    var message = Encoding.UTF8.GetString(data);
                    Console.WriteLine($"Received: {message}");

                    // Echo back
                    var response = Encoding.UTF8.GetBytes($"Echo: {message}");
                    var clientEndpoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 7000);
                    await transport.SendAsync(response, clientEndpoint);
                }
                await Task.Delay(100); // Small delay to prevent tight loop
            }
        }
    }
}