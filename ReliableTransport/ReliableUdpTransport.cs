using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace ReliableTransport
{
    public class ReliableUdpTransport : IDisposable
    {
        private readonly UdpClient _udpClient;
        private readonly ConcurrentDictionary<int, PendingPacket> _pendingPackets;
        private readonly ConcurrentDictionary<int, byte[]> _receivedData;
        private readonly ConcurrentQueue<byte[]> _deliveryQueue;
        private readonly CancellationTokenSource _cts;
        private readonly SemaphoreSlim _windowSemaphore;
        private int _nextSequenceNumber;
        private int _expectedSequenceNumber;
        private int _sendWindowBase;
        private int _receiveWindowBase;
        private readonly HashSet<ushort> _receivedPackets;

        private const int MAX_SEGMENT_SIZE = 4;
        private readonly ConcurrentDictionary<ushort, MessageAssembly> _messageAssemblies;

        private const int MAX_RETRIES = 5;
        private const int TIMEOUT_MS = 500;
        private const int HEADER_SIZE = 6;
        private const int WINDOW_SIZE = 32;

        public ReliableUdpTransport(int port)
        {
            _udpClient = new UdpClient(port);
            _pendingPackets = new ConcurrentDictionary<int, PendingPacket>();
            _receivedData = new ConcurrentDictionary<int, byte[]>();
            _deliveryQueue = new ConcurrentQueue<byte[]>();
            _cts = new CancellationTokenSource();
            _windowSemaphore = new SemaphoreSlim(WINDOW_SIZE);
            _nextSequenceNumber = 0;
            _expectedSequenceNumber = 0;
            _sendWindowBase = 0;
            _receiveWindowBase = 0;
            _receivedPackets = new HashSet<ushort>();
            _messageAssemblies = new ConcurrentDictionary<ushort, MessageAssembly>();

            Task.Run(ReceiveLoop, _cts.Token);
            Task.Run(ProcessDeliveryQueue, _cts.Token);
            Task.Run(RetransmissionLoop, _cts.Token);
        }

        public async Task<bool> SendAsync(byte[] data, IPEndPoint endpoint)
        {
            var segments = SplitIntoSegments(data);
            var baseSequenceNumber = _nextSequenceNumber;

            for (int i = 0; i < segments.Count; i++)
            {
                await _windowSemaphore.WaitAsync();
                var sequenceNumber = _nextSequenceNumber++;
                var seqNumMod = (ushort)(sequenceNumber % ushort.MaxValue);
                Console.WriteLine($"SendAsync: Sending segment {i}/{segments.Count} with sequence number {seqNumMod}");

                var packet = CreatePacket(segments[i], seqNumMod, PacketType.Data, i, segments.Count);
                var pendingPacket = new PendingPacket
                {
                    Data = packet,
                    Endpoint = endpoint,
                    RetryCount = 0,
                    LastSent = DateTime.UtcNow,
                    OriginalData = segments[i],
                    SequenceNumber = sequenceNumber,
                    Acknowledged = false
                };

                _pendingPackets[sequenceNumber] = pendingPacket;
                await SendPacketAsync(packet, endpoint);
            }

            return true;
        }

        private async Task RetransmissionLoop()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                var now = DateTime.UtcNow;
                foreach (var kvp in _pendingPackets.ToArray())
                {
                    var sequenceNumber = kvp.Key;
                    var pendingPacket = kvp.Value;

                    if (pendingPacket.Acknowledged)
                    {
                        if (_pendingPackets.TryRemove(sequenceNumber, out _))
                        {
                            _windowSemaphore.Release();
                            if (sequenceNumber == _sendWindowBase)
                            {
                                AdvanceSendWindow();
                            }
                        }
                        continue;
                    }

                    if (now - pendingPacket.LastSent > TimeSpan.FromMilliseconds(TIMEOUT_MS))
                    {
                        if (pendingPacket.RetryCount >= MAX_RETRIES)
                        {
                            Console.WriteLine($"Max retries reached for packet {sequenceNumber}");
                            if (_pendingPackets.TryRemove(sequenceNumber, out _))
                            {
                                _windowSemaphore.Release();
                                if (sequenceNumber == _sendWindowBase)
                                {
                                    AdvanceSendWindow();
                                }
                            }
                            continue;
                        }

                        pendingPacket.RetryCount++;
                        pendingPacket.LastSent = now;
                        Console.WriteLine($"Retrying packet {sequenceNumber} (attempt {pendingPacket.RetryCount})");
                        await SendPacketAsync(pendingPacket.Data, pendingPacket.Endpoint);
                    }
                }
                await Task.Delay(TIMEOUT_MS / 2);
            }
        }

        private void AdvanceSendWindow()
        {
            int nextBase = _sendWindowBase + 1;
            bool hasAdvanced = false;

            while (!_pendingPackets.ContainsKey((ushort)(nextBase % ushort.MaxValue)))
            {
                _sendWindowBase = nextBase;
                nextBase++;
                hasAdvanced = true;

                if (nextBase >= _nextSequenceNumber)
                    break;
            }

            if (hasAdvanced)
            {
                Console.WriteLine($"Advanced window to {_sendWindowBase % ushort.MaxValue}-{(_sendWindowBase + WINDOW_SIZE) % ushort.MaxValue}");
            }
        }

        public byte[] Receive() => _deliveryQueue.TryDequeue(out var data) ? data : null;

        public async Task<byte[]> ReceiveAsync()
        {
            var startTime = DateTime.UtcNow;
            while (DateTime.UtcNow - startTime < TimeSpan.FromMilliseconds(TIMEOUT_MS * 10))
            {
                var response = Receive();
                if (response != null)
                    return response;
                await Task.Delay(100);
            }
            return Array.Empty<byte>();
        }

        private async Task ProcessDeliveryQueue()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                while (_receivedData.TryGetValue(_expectedSequenceNumber, out var data))
                {
                    _deliveryQueue.Enqueue(data);
                    _receivedData.TryRemove(_expectedSequenceNumber, out _);
                    _expectedSequenceNumber++;
                }
                await Task.Delay(10);
            }
        }

        private async Task ReceiveLoop()
        {
            while (!_cts.Token.IsCancellationRequested)
            {
                try
                {
                    var result = await _udpClient.ReceiveAsync();
                    var packet = result.Buffer;
                    var sequenceNumber = BitConverter.ToUInt16(packet, 0);
                    var flags = (PacketType)BitConverter.ToUInt16(packet, 2);
                    var segmentIndex = packet[4];
                    var totalSegments = packet[5];

                    if (flags == PacketType.Ack)
                    {
                        if (_pendingPackets.TryGetValue(sequenceNumber, out var pendingPacket))
                        {
                            pendingPacket.Acknowledged = true;
                            Console.WriteLine($"ReceiveLoop: Acknowledged packet {sequenceNumber}");
                        }
                    }
                    else if (flags == PacketType.Data)
                    {
                        var ack = CreatePacket(Array.Empty<byte>(), sequenceNumber, PacketType.Ack, 0, 0);
                        await SendPacketAsync(ack, result.RemoteEndPoint);
                        
                        if (IsWithinReceiveWindow(sequenceNumber))
                        {
                            if (!_receivedPackets.Contains(sequenceNumber))
                            {
                                var data = new byte[packet.Length - HEADER_SIZE];
                                Array.Copy(packet, HEADER_SIZE, data, 0, data.Length);

                                var messageBaseSeq = (ushort)(sequenceNumber - segmentIndex);

                                var assembly = _messageAssemblies.GetOrAdd(messageBaseSeq, _ =>
                                {
                                    return new MessageAssembly(totalSegments);
                                });

                                if (assembly.TotalSegments != totalSegments)
                                {
                                    Console.WriteLine($"Warning: Message {messageBaseSeq} has inconsistent segment count - expected {assembly.TotalSegments}, got {totalSegments}");
                                    continue;
                                }

                                assembly.AddSegment(segmentIndex, data);

                                if (assembly.IsComplete())
                                {
                                    var completeMessage = assembly.AssembleMessage();
                                    _deliveryQueue.Enqueue(completeMessage);
                                    _messageAssemblies.TryRemove(messageBaseSeq, out _);
                                    _receiveWindowBase = (ushort)((messageBaseSeq + totalSegments) % ushort.MaxValue);
                                }

                                _receivedPackets.Add(sequenceNumber);
                            }
                        }
                        else
                        {
                            Console.WriteLine($"ReceiveLoop: Packet {sequenceNumber} outside receive window {_receiveWindowBase}-{(_receiveWindowBase + WINDOW_SIZE) % ushort.MaxValue}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error in receive loop: {ex.Message}");
                }
            }
        }

        private bool IsWithinReceiveWindow(ushort sequenceNumber)
        {
            var windowEnd = (ushort)(_receiveWindowBase + WINDOW_SIZE);
            // Handle wrap-around
            if (windowEnd < _receiveWindowBase)
            {
                return sequenceNumber >= _receiveWindowBase || sequenceNumber < windowEnd;
            }
            return sequenceNumber >= _receiveWindowBase && sequenceNumber < windowEnd;
        }

        private List<byte[]> SplitIntoSegments(byte[] data)
        {
            var segments = new List<byte[]>();
            var position = 0;

            while (position < data.Length)
            {
                var remainingBytes = data.Length - position;
                var segmentSize = Math.Min(remainingBytes, MAX_SEGMENT_SIZE);
                var segment = new byte[segmentSize];
                Array.Copy(data, position, segment, 0, segmentSize);
                segments.Add(segment);
                position += segmentSize;
            }

            return segments;
        }

        private byte[] CreatePacket(byte[] data, ushort sequenceNumber, PacketType type, int segmentIndex, int totalSegments)
        {
            var packet = new byte[data.Length + HEADER_SIZE];
            BitConverter.GetBytes(sequenceNumber).CopyTo(packet, 0);
            BitConverter.GetBytes((ushort)type).CopyTo(packet, 2);
            packet[4] = (byte)segmentIndex;
            packet[5] = (byte)totalSegments;
            data.CopyTo(packet, HEADER_SIZE);
            return packet;
        }

        private async Task SendPacketAsync(byte[] packet, IPEndPoint endpoint)
        {
            await _udpClient.SendAsync(packet, packet.Length, endpoint);
        }

        public void Dispose()
        {
            _cts.Cancel();
            _udpClient.Dispose();
            _windowSemaphore.Dispose();
        }
    }

    public class MessageAssembly
    {
        private readonly byte[][] _segments;
        private readonly bool[] _receivedSegments;
        public readonly int TotalSegments;

        public MessageAssembly(int totalSegments)
        {
            TotalSegments = totalSegments;
            _segments = new byte[totalSegments][];
            _receivedSegments = new bool[totalSegments];
        }

        public void AddSegment(int index, byte[] data)
        {
            _segments[index] = data;
            _receivedSegments[index] = true;
        }

        public bool IsComplete()
        {
            return _receivedSegments.All(received => received);
        }

        public byte[] AssembleMessage()
        {
            var totalLength = _segments.Sum(segment => segment.Length);
            var result = new byte[totalLength];
            var position = 0;

            foreach (var segment in _segments)
            {
                Array.Copy(segment, 0, result, position, segment.Length);
                position += segment.Length;
            }

            return result;
        }
    }

    public enum PacketType : ushort
    {
        Data = 1,
        Ack = 2
    }

    public class PendingPacket
    {
        public byte[] Data { get; set; }
        public byte[] OriginalData { get; set; }
        public IPEndPoint Endpoint { get; set; }
        public int RetryCount { get; set; }
        public DateTime LastSent { get; set; }
        public int SequenceNumber { get; set; }
        public bool Acknowledged { get; set; }
    }
}