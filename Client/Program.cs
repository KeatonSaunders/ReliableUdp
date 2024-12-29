using ReliableTransport;
using System.Net;
using System.Text;

class Program
{
    static async Task Main(string[] args)
    {
        Console.WriteLine("Starting client...");
        using (var transport = new ReliableUdpTransport(0))
        {
            var serverEndpoint = new IPEndPoint(IPAddress.Parse("127.0.0.1"), 7000);
            var messageCount = 1;

            while (true)
            {
                try
                {
                    var message = $"Message {messageCount}";
                    var data = Encoding.UTF8.GetBytes(message);
                    Console.WriteLine($"\nSending: {message}");

                    bool success = await transport.SendAsync(data, serverEndpoint);
                    if (!success)
                    {
                        Console.WriteLine("Failed to send message after retries!");
                        await Task.Delay(1000);
                        continue;
                    }

                    byte[] response = await transport.ReceiveAsync();
                    if (response.Length > 0)
                    {
                        Console.WriteLine($"Received: {Encoding.UTF8.GetString(response)}");
                        messageCount++;
                    }
                    else
                    {
                        Console.WriteLine("No response received within timeout!");
                    }

                    await Task.Delay(2000);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error: {ex.Message}");
                    await Task.Delay(1000);
                }
            }
        }
    }
}