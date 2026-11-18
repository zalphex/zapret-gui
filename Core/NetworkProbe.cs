using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;

public static class FlowProbe
{
    public static int Icmp(string host)
    {
        try
        {
            using (var ping = new Ping())
            {
                var reply = ping.Send(host, 1450);
                return reply.Status == IPStatus.Success ? (int)reply.RoundtripTime : -1;
            }
        }
        catch
        {
            return -1;
        }
    }

    internal sealed class HttpResult
    {
        internal int Time = -1;
        internal int Code;
        internal string Error;
        internal bool Success
        {
            get
            {
                return Code >= 200 && Code < 400;
            }
        }

        internal string Describe()
        {
            if (Success)
                return Time + " мс";
            if (Code != 0)
                return "HTTP " + Code + " · " + Time + " мс";
            return Error ?? "нет ответа";
        }
    }

    internal static HttpResult Request(string url)
    {
        var timer = Stopwatch.StartNew();
        var request = (HttpWebRequest)WebRequest.Create(url);
        request.Proxy = null;
        request.KeepAlive = false;
        request.AllowAutoRedirect = false;
        request.Timeout = 3500;
        request.ReadWriteTimeout = 3500;
        request.UserAgent = "zapretgui/" + Paths.Version;
        try
        {
            using (var reply = (HttpWebResponse)request.GetResponse())
                return new HttpResult
                {
                    Time = (int)timer.ElapsedMilliseconds,
                    Code = (int)reply.StatusCode
                };
        }
        catch (WebException ex)
        {
            using (var reply = ex.Response as HttpWebResponse)
                if (reply != null)
                    return new HttpResult
                    {
                        Time = (int)timer.ElapsedMilliseconds,
                        Code = (int)reply.StatusCode
                    };
            string explanation;
            switch (ex.Status)
            {
                case WebExceptionStatus.Timeout:
                    explanation = "тайм-аут";
                    break;
                case WebExceptionStatus.NameResolutionFailure:
                    explanation = "ошибка DNS";
                    break;
                case WebExceptionStatus.ConnectFailure:
                    explanation = "ошибка подключения";
                    break;
                case WebExceptionStatus.TrustFailure:
                case WebExceptionStatus.SecureChannelFailure:
                    explanation = "ошибка TLS";
                    break;
                default:
                    explanation = "нет ответа (" + ex.Status + ")";
                    break;
            }

            return new HttpResult
            {
                Error = explanation
            };
        }
    }

    public static int Https(string url)
    {
        var result = Request(url);
        return result.Success ? result.Time : -1;
    }

    public static int Telegram(int port, string hexSecret, short dc)
    {
        byte[] secret = new byte[16];
        for (int i = 0; i < secret.Length; i++)
            secret[i] = Convert.ToByte(hexSecret.Substring(i * 2, 2), 16);
        var watch = Stopwatch.StartNew();
        using (var random = RandomNumberGenerator.Create())
        using (var client = new TcpClient(AddressFamily.InterNetwork))
        {
            var pending = client.BeginConnect(IPAddress.Loopback, port, null, null);
            using (var ready = pending.AsyncWaitHandle)
                if (!ready.WaitOne(2000))
                    return -1;
            client.EndConnect(pending);
            client.ReceiveTimeout = 4800;
            client.SendTimeout = 2000;
            using (var stream = client.GetStream())
            {
                byte[] header = new byte[64];
                do
                {
                    random.GetBytes(header);
                }
                while (Reserved(header));
                byte[] key = new byte[48];
                Buffer.BlockCopy(header, 8, key, 0, key.Length);
                for (int i = 56; i < 60; i++)
                    header[i] = 0xdd;
                Buffer.BlockCopy(BitConverter.GetBytes(dc), 0, header, 60, 2);
                using (var tx = new CounterMode(key, secret))
                {
                    Array.Reverse(key);
                    using (var rx = new CounterMode(key, secret))
                    {
                        var encryptedHeader = tx.Apply(header);
                        Buffer.BlockCopy(encryptedHeader, 56, header, 56, 8);
                        byte[] nonce = new byte[16];
                        random.GetBytes(nonce);
                        byte[] padding = new byte[1 + (header[62] % 15)];
                        random.GetBytes(padding);
                        byte[] packet;
                        using (var output = new MemoryStream())
                        using (var writer = new BinaryWriter(output))
                        {
                            writer.Write(40 + padding.Length); // Padded intermediate frame
                            writer.Write(0L); // Unencrypted authorization message
                            writer.Write((long)((DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalSeconds * 4294967296.0) & ~3L);
                            writer.Write(20);
                            writer.Write(0xbe7e8ef1u); // req_pq_multi
                            writer.Write(nonce);
                            writer.Write(padding);
                            packet = tx.Apply(output.ToArray());
                        }

                        stream.Write(header, 0, header.Length);
                        stream.Write(packet, 0, packet.Length);
                        int length = BitConverter.ToInt32(rx.Apply(Read(stream, 4)), 0);
                        if (length < 56 || length > 4096)
                            return -1;
                        byte[] body = rx.Apply(Read(stream, length));
                        if (BitConverter.ToInt64(body, 0) != 0 || BitConverter.ToUInt32(body, 20) != 0x05162463u)
                            return -1; // resPQ
                        for (int i = 0; i < nonce.Length; i++)
                            if (body[24 + i] != nonce[i])
                                return -1;
                        return (int)watch.ElapsedMilliseconds;
                    }
                }
            }
        }
    }

    private static bool Reserved(byte[] header)
    {
        uint tag = BitConverter.ToUInt32(header, 0);
        return header[0] == 0xef || tag == 0x44414548 || tag == 0x54534f50 || tag == 0x20544547 || tag == 0x4954504f || tag == 0xeeeeeeee || tag == 0xdddddddd || tag == 0x02010316 || BitConverter.ToUInt32(header, 4) == 0;
    }

    private static byte[] Read(NetworkStream stream, int length)
    {
        byte[] buffer = new byte[length];
        int offset = 0;
        var watch = Stopwatch.StartNew();
        while (offset < length)
        {
            if (watch.ElapsedMilliseconds >= 4800)
                throw new IOException("Timeout");
            int count = stream.Read(buffer, offset, length - offset);
            if (count == 0)
                throw new EndOfStreamException();
            offset += count;
        }

        return buffer;
    }

    private sealed class CounterMode : IDisposable
    {
        private readonly Aes aes = Aes.Create();
        private readonly ICryptoTransform cipher;
        private readonly byte[] counter = new byte[16];
        private readonly byte[] block = new byte[16];
        private int index = 16;
        public CounterMode(byte[] prekeyAndIv, byte[] secret)
        {
            byte[] material = new byte[48];
            Buffer.BlockCopy(prekeyAndIv, 0, material, 0, 32);
            Buffer.BlockCopy(secret, 0, material, 32, 16);
            Buffer.BlockCopy(prekeyAndIv, 32, counter, 0, 16);
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.None;
            using (var sha = SHA256.Create())
                aes.Key = sha.ComputeHash(material);
            cipher = aes.CreateEncryptor();
        }

        public byte[] Apply(byte[] input)
        {
            byte[] output = new byte[input.Length];
            for (int i = 0; i < input.Length; i++)
            {
                if (index == 16)
                {
                    cipher.TransformBlock(counter, 0, 16, block, 0);
                    for (int j = 15; j >= 0; j--)
                        if (++counter[j] != 0)
                            break;
                    index = 0;
                }

                output[i] = (byte)(input[i] ^ block[index++]);
            }

            return output;
        }

        public void Dispose()
        {
            cipher.Dispose();
            aes.Dispose();
        }
    }
}
