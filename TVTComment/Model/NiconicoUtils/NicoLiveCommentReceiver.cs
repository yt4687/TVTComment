﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TVTComment.Model.NiconicoUtils
{
    class NicoLiveCommentReceiverException : Exception
    {
        public NicoLiveCommentReceiverException() { }
        public NicoLiveCommentReceiverException(string message) : base(message) { }
        public NicoLiveCommentReceiverException(string message, Exception inner) : base(message, inner) { }
    }

    class InvalidPlayerStatusNicoLiveCommentReceiverException : NicoLiveCommentReceiverException
    {
        public string PlayerStatus { get; }
        public InvalidPlayerStatusNicoLiveCommentReceiverException(string playerStatus)
        {
            this.PlayerStatus = playerStatus;
        }
    }

    class NetworkNicoLiveCommentReceiverException : NicoLiveCommentReceiverException
    {
        public NetworkNicoLiveCommentReceiverException(Exception inner) : base(null, inner)
        {
        }
    }
    class ConnectionClosedNicoLiveCommentReceiverException : NicoLiveCommentReceiverException
    {
        public ConnectionClosedNicoLiveCommentReceiverException()
        {
        }
    }

    class NicoLiveCommentReceiver : IDisposable
    {
        public NiconicoLoginSession NiconicoLoginSession { get; }
        
        public NicoLiveCommentReceiver(NiconicoLoginSession niconicoLoginSession)
        {
            this.NiconicoLoginSession = niconicoLoginSession;

            var handler = new HttpClientHandler();
            handler.CookieContainer.Add(niconicoLoginSession.Cookie);
            this.httpClient = new HttpClient(handler);
        }

        /// <summary>
        /// 受信した<see cref="NiconicoCommentXmlTag"/>を無限非同期イテレータで返す
        /// </summary>
        /// <exception cref="OperationCanceledException"></exception>
        /// <exception cref="InvalidPlayerStatusNicoLiveCommentReceiverException"></exception>
        /// <exception cref="NetworkNicoLiveCommentReceiverException"></exception>
        /// <exception cref="ConnectionClosedNicoLiveCommentReceiverException"></exception>
        public async IAsyncEnumerable<NiconicoCommentXmlTag> Receive(string liveId, [EnumeratorCancellation]CancellationToken cancellationToken)
        {
            using var _  = cancellationToken.Register(() =>
            {
                this.httpClient.CancelPendingRequests();
            });

            for (int disconnectedCount = 0; disconnectedCount < 5; ++disconnectedCount)
            {
                string str;
                try
                {
                    str = await this.httpClient.GetStringAsync($"http://live.nicovideo.jp/api/getplayerstatus/{liveId}").ConfigureAwait(false);
                }
                // httpClient.CancelPendingRequestsが呼ばれた、もしくはタイムアウト
                catch (TaskCanceledException e)
                {
                    if (cancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException(null, e, cancellationToken);
                    throw new NetworkNicoLiveCommentReceiverException(e);
                }
                catch(HttpRequestException e)
                {
                    throw new NetworkNicoLiveCommentReceiverException(e);
                }
                var playerStatus = XDocument.Parse(str).Root;

                string threadId = playerStatus.Element("ms")?.Element("thread")?.Value;
                string ms = playerStatus.Element("ms")?.Element("addr")?.Value;
                string msPort = playerStatus.Element("ms")?.Element("port")?.Value;
                if(threadId == null || ms == null || msPort == null)
                {
                    throw new InvalidPlayerStatusNicoLiveCommentReceiverException(str);
                }

                using var tcpClinet = new TcpClient(ms, int.Parse(msPort));
                var socketStream = tcpClinet.GetStream();
                using var socketReader = new StreamReader(socketStream, Encoding.UTF8);

                using var __ = cancellationToken.Register(() =>
                {
                    socketReader.Dispose(); // socketReader.ReadAsyncを強制終了
                });

                string body = $"<thread res_from=\"-10\" version=\"20061206\" thread=\"{threadId}\" scores=\"1\" />\0";
                byte[] bodyEncoded = Encoding.UTF8.GetBytes(body);
                try
                {
                    await socketStream.WriteAsync(bodyEncoded, 0, bodyEncoded.Length, cancellationToken).ConfigureAwait(false);
                }
                catch(Exception e) when(e is ObjectDisposedException || e is SocketException || e is IOException)
                {
                    if (cancellationToken.IsCancellationRequested)
                        throw new OperationCanceledException(null, e, cancellationToken);
                    if (e is ObjectDisposedException)
                        throw;
                    else
                        throw new NetworkNicoLiveCommentReceiverException(e);
                }

                //コメント受信ループ
                while (true)
                {
                    char[] buf = new char[2048];
                    int receivedByte;
                    try
                    {
                        receivedByte = await socketReader.ReadAsync(buf, 0, buf.Length).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is ObjectDisposedException || e is SocketException || e is IOException)
                    {
                        if (cancellationToken.IsCancellationRequested)
                            throw new OperationCanceledException(null, e, cancellationToken);
                        if (e is ObjectDisposedException)
                            throw;
                        else
                            throw new NetworkNicoLiveCommentReceiverException(e);
                    }
                    if (receivedByte == 0)
                        break; // 4時リセットかもしれない→もう一度試す

                    this.parser.Push(new string(buf[..receivedByte]));
                    while(this.parser.DataAvailable())
                        yield return this.parser.Pop();
                }
            }
            throw new ConnectionClosedNicoLiveCommentReceiverException();
        }

        public void Dispose()
        {
            this.httpClient.Dispose();
        }

        private readonly HttpClient httpClient;
        private readonly NiconicoCommentXmlParser parser = new NiconicoCommentXmlParser(true);
    }
}
