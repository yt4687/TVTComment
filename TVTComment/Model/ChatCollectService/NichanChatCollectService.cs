﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace TVTComment.Model.ChatCollectService
{
    abstract class NichanChatCollectService : OnceASecondChatCollectService
    {
        public class ChatPostObject : BasicChatPostObject
        {
            public string ThreadUri { get; }

            public ChatPostObject(string threadUri) : base("")
            {
                this.ThreadUri = threadUri;
            }
        }

        protected abstract string TypeName { get; }
        protected abstract Task<Nichan.Thread> GetThread(string url);

        public override string Name => this.TypeName + this.threadSelector switch
        {
            NichanUtils.AutoNichanThreadSelector auto => " ([自動])",
            NichanUtils.FuzzyNichanThreadSelector fuzzy => $" ([類似] {fuzzy.ThreadTitleExample})",
            NichanUtils.KeywordNichanThreadSelector keyword => $" ([キーワード] {string.Join(", ", keyword.Keywords)})",
            NichanUtils.FixedNichanThreadSelector fix => $" ([固定] {string.Join(", ", fix.Uris)})",
            _ => "",
        };
        public override string GetInformationText()
        {
            lock (threads)
            {
                if (threads.Count == 0)
                    return "対応スレがありません";
                else
                    return
                        $"遅延: {this.chatTimes.Select(x => x.RetrieveTime - x.PostTime).DefaultIfEmpty(TimeSpan.Zero).Max().TotalSeconds}秒\n" +
                        string.Join("\n", threads.Select(pair => $"{pair.Value.Title ?? "[スレタイ不明]"}  ({pair.Value.ResCount})  {pair.Key}"));
            }
        }
        public override ChatCollectServiceEntry.IChatCollectServiceEntry ServiceEntry { get; }
        public override bool CanPost => true;

        public IEnumerable<Nichan.Thread> CurrentThreads
        {
            get
            {
                lock (this.threads)
                    return this.threads.Select(x => new Nichan.Thread()
                    {
                        Uri = new Uri(x.Key),
                        Title = x.Value.Title,
                        ResCount = x.Value.ResCount,
                    }).ToArray();
            }
        }

        protected readonly TimeSpan resCollectInterval, threadSearchInterval;
        protected readonly NichanUtils.INichanThreadSelector threadSelector;

        private readonly Task resCollectTask;
        private readonly CancellationTokenSource cancel = new CancellationTokenSource();

        /// <summary>
        /// <see cref="ChatCollectException"/>を送出したか
        /// </summary>
        private bool errored=false;

        private ChannelInfo currentChannel;
        private DateTime? currentTime;

        private ConcurrentQueue<Chat> chats = new ConcurrentQueue<Chat>();
        /// <summary>
        /// キーはスレッドのURI
        /// </summary>
        private SortedList<string, (string Title, int ResCount)> threads = new SortedList<string, (string, int)>();
        private List<Chat> chatBuffer = new List<Chat>();
        private List<(DateTime PostTime, DateTime RetrieveTime)> chatTimes = new List<(DateTime, DateTime)>();

        /// <summary>
        /// <see cref="NichanChatCollectService"/>を初期化する
        /// </summary>
        /// <param name="serviceEntry">自分を生んだ<seealso cref="IChatCollectServiceEntry"/></param>
        /// <param name="chatColor">コメント色 <seealso cref="Color.Empty"/>ならランダム色</param>
        /// <param name="resCollectInterval">レスを集める間隔 1秒未満にしても1秒になる</param>
        /// <param name="threadSearchInterval">レスを集めるスレッドのリストを更新する間隔 <paramref name="resCollectInterval"/>未満にしても同じ間隔になる</param>
        /// <param name="threadSelector">レスを集めるスレッドを決める<seealso cref="NichanUtils.INichanThreadSelector"/></param>
        public NichanChatCollectService(
            ChatCollectServiceEntry.IChatCollectServiceEntry serviceEntry,
            TimeSpan resCollectInterval, TimeSpan threadSearchInterval,
            NichanUtils.INichanThreadSelector threadSelector
        ):base(TimeSpan.FromSeconds(10))
        {
            ServiceEntry = serviceEntry;

            if (resCollectInterval < TimeSpan.FromSeconds(1))
                resCollectInterval = TimeSpan.FromSeconds(1);
            if (threadSearchInterval < resCollectInterval)
                threadSearchInterval = resCollectInterval;
            this.resCollectInterval = resCollectInterval;
            this.threadSearchInterval = threadSearchInterval;

            this.threadSelector = threadSelector;

            resCollectTask = Task.Run(() => resCollectLoop(cancel.Token),cancel.Token);
        }

        private async Task resCollectLoop(CancellationToken cancellationToken)
        {
            int webExceptionCount = 0;
            int count = (int)(threadSearchInterval.TotalMilliseconds / resCollectInterval.TotalMilliseconds);
            for (int i = count; !cancellationToken.IsCancellationRequested; i++)
            {
                try
                {
                    if (i == count)
                    {
                        //スレ一覧を更新する
                        i = 0;
                        if (currentChannel != null && currentTime != null)
                        {
                            IEnumerable<string> threadUris = threadSelector.Get(currentChannel, currentTime.Value);

                            lock (this.threads)
                            {
                                //新しいスレ一覧に入ってないものを消す
                                foreach (string uri in this.threads.Keys.Where(x => !threadUris.Contains(x)).ToList())
                                    this.threads.Remove(uri);
                                //新しいスレ一覧で追加されたものを追加する
                                foreach (string uri in threadUris)
                                    if (!this.threads.ContainsKey(uri))
                                        this.threads.Add(uri, (null, 0));
                            }
                        }
                        else
                            i = count - 1;//取得対象のチャンネル、時刻が設定されていなければやり直す
                    }

                    KeyValuePair<string, (string Title, int ResCount)>[] copiedThreads;
                    lock(this.threads)
                        copiedThreads = this.threads.ToArray();
                    foreach (var pair in copiedThreads.ToArray())
                    {
                        Nichan.Thread thread = await this.GetThread(pair.Key);
                        int fromResIdx = thread.Res.FindLastIndex(res => res.Number <= pair.Value.ResCount);
                        fromResIdx++;

                        for (int resIdx = fromResIdx; resIdx < thread.Res.Count; resIdx++)
                        {
                            //1001から先のレスは返さない
                            if (thread.Res[resIdx].Number > 1000)
                                break;
                            foreach (XElement elem in thread.Res[resIdx].Text.Elements("br").ToArray())
                            {
                                elem.ReplaceWith("\n");
                            }
                            chats.Enqueue(new Chat(
                                thread.Res[resIdx].Date.Value, thread.Res[resIdx].Text.Value, Chat.PositionType.Normal, Chat.SizeType.Normal,
                                Color.White, thread.Res[resIdx].UserId, thread.Res[resIdx].Number
                            ));
                        }

                        var pairValue = pair.Value;
                        if (pair.Value.ResCount == 0)
                            pairValue.Title = thread.Title;
                        pairValue.ResCount = thread.Res[^1].Number;
                        lock(this.threads)
                            this.threads[pair.Key] = pairValue;
                    }
                    await Task.Delay(1000, cancellationToken);
                }
                catch(WebException)
                {
                    webExceptionCount++;
                    if (webExceptionCount < 10)
                        continue;
                    else
                        throw;
                }
            }

        }

        protected override IEnumerable<Chat> GetOnceASecond(ChannelInfo channel, DateTime time)
        {
            currentChannel = channel;
            currentTime = time;

            AggregateException e = resCollectTask.Exception;
            if(e!=null)
            {
                errored = true;
                if (e.InnerExceptions.Count == 1 && e.InnerExceptions[0] is ChatCollectException)
                    throw e.InnerExceptions[0];
                else
                    throw new ChatCollectException($"スレ巡回スレッド内で予期しないエラー発生\n\n{e}",e);
            }

            var newChats = this.chats.Where(x => (time - x.Time).Duration() < TimeSpan.FromSeconds(15)).ToArray();//投稿から15秒以内のレスのみ返す
            // 新たに収集したChatをchatBufferに移す
            this.chats.Clear();
            this.chatBuffer.AddRange(newChats);
            // 直近15秒のChatの投稿時刻と取得時刻をchatTimesに記憶
            this.chatTimes.AddRange(newChats.Select(x => (x.Time, time)));
            this.chatTimes.RemoveAll(x => x.RetrieveTime + TimeSpan.FromSeconds(15) < time);
            // 団子になるのを防ぐため、chatTimes内の時刻差の最大値分だけ遅延してChatを返す
            var delay = this.chatTimes.Select(x => x.RetrieveTime - x.PostTime).DefaultIfEmpty(TimeSpan.Zero).Max();
            var ret = this.chatBuffer.Where(x => x.Time + delay <= time).ToArray();
            foreach(var chat in ret)
            {
                this.chatBuffer.Remove(chat);
            }
            return ret;
        }

        public override async Task PostChat(BasicChatPostObject basicChatPostObject)
        {
            var chatPostObject = (ChatPostObject)basicChatPostObject;

            string threadUri = chatPostObject.ThreadUri;

            var uri = new UriBuilder(threadUri);
            var pathes = uri.Path.Split('/').ToArray();
            var readcgiIdx = Array.IndexOf(pathes, "read.cgi");
            uri.Path = string.Join('/', pathes[..(readcgiIdx + 3)].Append("1"));
            uri.Fragment = "1";

            Process.Start(new ProcessStartInfo("cmd", $"/c start {uri.ToString().Replace("&", "^&")}"));
        }

        public override void Dispose()
        {
            cancel.Cancel();
            try
            {
                resCollectTask.Wait();
            }
            catch(AggregateException e)when(e.InnerExceptions.All(innerE=>innerE is OperationCanceledException))
            { }
            catch(AggregateException)
            {
                if (!errored) throw;
            }
        }
    }

    class HTMLNichanChatCollectService : NichanChatCollectService
    {
        protected override string TypeName => "2chHTML";

        private static readonly HttpClient httpClient = new HttpClient();

        public HTMLNichanChatCollectService(
            ChatCollectServiceEntry.IChatCollectServiceEntry serviceEntry,
            TimeSpan resCollectInterval,
            TimeSpan threadSearchInterval,
            NichanUtils.INichanThreadSelector threadSelector
        ) : base(serviceEntry, resCollectInterval, threadSearchInterval, threadSelector)
        {
        }

        protected override async Task<Nichan.Thread> GetThread(string url)
        {
            string response;
            try
            {
                response = await httpClient.GetStringAsync(url).ConfigureAwait(false);
            }
            catch (HttpRequestException e)
            {
                if (e.StatusCode == null)
                    throw new ChatCollectException($"サーバーとの通信でエラーが発生しました\nURL: {url}", e);
                else
                    throw new ChatCollectException($"サーバーからエラーが返されました\nURL: {url}\nHTTPステータスコード: {e.StatusCode}", e);
            }

            using var textReader = new StringReader(response);
            Nichan.Thread thread;
            try
            {
                thread = Nichan.ThreadParser.ParseFromStream(textReader);
            }
            catch(Nichan.ThreadParserException e)
            {
                throw new ChatCollectException($"対応していないHTMLのドキュメント構造です\nURL: {url}", e);
            }
            thread.Uri = new Uri(url);

            return thread;
        }
    }

    class DATNichanChatCollectService : NichanChatCollectService
    {
        protected override string TypeName => "2chDAT";

        public DATNichanChatCollectService(
            ChatCollectServiceEntry.IChatCollectServiceEntry serviceEntry,
            TimeSpan resCollectInterval,
            TimeSpan threadSearchInterval,
            NichanUtils.INichanThreadSelector threadSelector,
            Nichan.ApiClient nichanApiClient
        ) : base(serviceEntry, resCollectInterval, threadSearchInterval, threadSelector)
        {
            this.apiClient = nichanApiClient;
        }

        protected override async Task<Nichan.Thread> GetThread(string url)
        {
            var uri = new Uri(url);
            var server = uri.Host.Split('.')[0];
            var pathes = uri.Segments.SkipWhile(x => x != "read.cgi/").ToArray();
            var board = pathes[1][..^1];
            var threadID = pathes[2][..^1];

            if (!this.threadLoaders.TryGetValue((server, board, threadID), out var threadLoader))
            {
                threadLoader = new Nichan.DatThreadLoader(server, board, threadID);
                this.threadLoaders.Add((server, board, threadID), threadLoader);
            }

            try
            {
                await threadLoader.Update(this.apiClient);
            }
            catch (Nichan.AuthorizationApiClientException e)
            {
                throw new ChatCollectException("API認証に問題があります。API設定を確認してください", e);
            }
            catch (Nichan.ResponseApiClientException e)
            {
                throw new ChatCollectException("サーバーからの返信にエラーがあります", e);
            }
            catch (Nichan.NetworkApiClientException e)
            {
                throw new ChatCollectException("サーバーに接続できません", e);
            }
            catch (Nichan.DatFormatDatThreadLoaderException e)
            {
                throw new ChatCollectException($"取得したDATのフォーマットが不正です\n\n{e.DatString}", e);
            }

            threadLoader.Thread.Uri = new Uri(url);
            return threadLoader.Thread;
        }

        private Nichan.ApiClient apiClient;
        private Dictionary<(string server, string board, string thread), Nichan.DatThreadLoader> threadLoaders = new Dictionary<(string, string, string), Nichan.DatThreadLoader>();
    }
}
