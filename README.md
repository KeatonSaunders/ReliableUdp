# Reliable Udp

A C# implementation of bidrectional, reliable (all data arrives once and in order) communication over UDP sockets, with support for message segmentation and reassembly.

## Features

- Reliable packet delivery with acknowledgments
- Automatic message segmentation and reassembly
- Sliding window flow control (32 packets)
- Configurable retransmission (5 retries, 500ms timeout)
- Message sequencing and reordering
- Support for large messages via segmentation
- Concurrent packet handling
- Automatic handling of packet loss and duplicates
- Window-based flow control for both sending and receiving

## Usage

```csharp
// Initialize transport on specific port
using var transport = new ReliableUdpTransport(port);

// Send data
await transport.SendAsync(data, endpoint);

// Receive data
byte[] received = await transport.ReceiveAsync();
```

## Potential Improvements

- Dynamic retry timeout based on RTT and variance
- Congestion control implementation
- Dynamic window sizing based on network conditions
- Compression support for message payloads
- Connection handshake for session establishment
- Quality of Service (QoS) priority levels
- Channel multiplexing
- Heartbeat mechanism for connection health monitoring

Written as a solution in the CSPrimer Computer Networking module.
