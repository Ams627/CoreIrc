using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Tasks;


namespace CoreIrc
{
    class IrcConnection
    {
        string _server;
        int _port;
        string _username, _fullname;
        Socket _socket;

        public IrcConnection(string username, string fullname, string server, int port)
        {
            _server = server;
            _port = port;
            _username = username;
            _fullname = fullname;
        }

        public async Task Connect()
        {
            IPHostEntry ipHostInfo = Dns.GetHostEntry(_server);
            IPAddress ipAddress = ipHostInfo.AddressList[0];
            IPEndPoint remoteEP = new IPEndPoint(ipAddress, _port);
            _socket = new Socket(ipAddress.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            _socket.Connect(remoteEP);
            var str1 = $"NICK {_username}\r\n";
            var str2 = $"USER {_username} * * {_fullname}\r\n";

            var utf8 = new UTF8Encoding();
            byte[] b1 = utf8.GetBytes(str1);
            byte[] b2 = utf8.GetBytes(str2);

            _socket.Send(b1, SocketFlags.None);
            _socket.Send(b2, SocketFlags.None);

            var pipe = new Pipe();
            Task writing = FillPipeAsync(_socket, pipe.Writer);
            Task reading = ReadPipeAsync(pipe.Reader);
            await Task.WhenAll(reading, writing);
        }


        // 1. get the result from the pipe
        // 2. get the buffer from the result => ReadOnlySequence<byte>
        // 3. repeatedly get the position marker from the sequence (we're looking for '\n') until the position marker is null
        private async Task ReadPipeAsync(PipeReader reader)
        {
            while (true)
            {
                ReadResult result = await reader.ReadAsync();
                ReadOnlySequence<byte> buffer = result.Buffer;
                SequencePosition? position;

                do
                {
                    // Look for a EOL in the buffer
                    position = buffer.PositionOf((byte)'\r');
                    
                    if (position != null)
                    {
                        try
                        {
                            // Process the line:
                            ProcessLine(buffer.Slice(0, position.Value));
                            // Skip the line + the \n character (basically position)
                            buffer = buffer.Slice(buffer.GetPosition(2, position.Value));
                        }
                        catch(Exception ex)
                        {
                            Console.WriteLine($"{ex}");
                        }
                    }
                }
                while (position != null);

                // Tell the PipeReader how much of the buffer we have consumed
                reader.AdvanceTo(buffer.Start, buffer.End);

                // Stop reading if there's no more data coming
                if (result.IsCompleted)
                {
                    break;
                }
            }

            // Mark the PipeReader as complete
            reader.Complete();
        }

        private void ProcessLine(ReadOnlySequence<byte> seq)
        {
            var str = Encoding.UTF8.GetString(seq.ToArray());
            Console.WriteLine($"{str}");
            var reader = new SequenceReader<byte>(seq);
            var command = new ReadOnlySpan<byte>();

            var pingCommand = Encoding.ASCII.GetBytes("PING");
            var pongCommand = Encoding.ASCII.GetBytes("PONG ");
            var crLf = new byte[] { 13, 10 };

            if (reader.TryReadTo(out command, 32, advancePastDelimiter:true))
            {
                if (command.SequenceEqual(pingCommand))
                {
                    var buffers = new ArraySegment<byte>[] { pongCommand, seq.Slice(reader.Position).ToArray(), crLf};
                    SocketTaskExtensions.SendAsync(_socket, buffers, SocketFlags.None);
                }
            }
        }

        private async Task FillPipeAsync(Socket socket, PipeWriter writer)
        {
            const int minBufSize = 4096;
            while (true)
            {
                Memory<byte> memory = writer.GetMemory(minBufSize);
                int bytesRead = await socket.ReceiveAsync(memory, SocketFlags.None);
                if (bytesRead == 0)
                {
                    break;
                }
                writer.Advance(bytesRead);

                FlushResult result = await writer.FlushAsync();
                if (result.IsCompleted)
                {
                    break;
                }
            }
            writer.Complete();
        }
    }

    class Program
    {
        static async Task Main(string[] args)
        {
            try
            {
                var connect = new IrcConnection("JohnP989", "John Pebble", "chicago.il.us.undernet.org", 6667);
                await connect.Connect();
            }
            catch (Exception ex)
            {
                var fullname = System.Reflection.Assembly.GetEntryAssembly().Location;
                var progname = Path.GetFileNameWithoutExtension(fullname);
                Console.Error.WriteLine(progname + ": Error: " + ex.Message);
            }

        }
    }
}
