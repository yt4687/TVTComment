﻿using ObservableUtils;
using System;
using System.Collections.Generic;
using System.Configuration;
using System.Threading.Tasks;

namespace TVTComment.Model.ChatService
{
    class NiconicoChatServiceSettings
    {
        public string UserId { get; set; } = "";
        public string Password { get; set; } = "";
    }

    class NiconicoChatService : IChatService
    {
        public string Name => "ニコニコ";
        public IReadOnlyList<ChatCollectServiceEntry.IChatCollectServiceEntry> ChatCollectServiceEntries { get; }
        public IReadOnlyList<IChatTrendServiceEntry> ChatTrendServiceEntries { get; }

        //このChatServiceに行われた設定変更が子のChatServiceEntryに伝わるようにするためにObservableValueで包む
        //private ObservableValue<Dictionary<uint, int>> jkIdTable = new ObservableValue<Dictionary<uint, int>>();
        private ObservableValue<NiconicoUtils.NiconicoLoginSession> loginSession = new ObservableValue<NiconicoUtils.NiconicoLoginSession>();

        private NiconicoUtils.JkIdResolver jkIdResolver;
        private NiconicoUtils.LiveIdResolver liveIdResolver;
        private NiconicoChatServiceSettings settings;

        public string UserId
        {
            get { return this.settings.UserId; }
        }
        public string UserPassword
        {
            get { return this.settings.Password; }
        }
        public bool IsLoggedin { get; private set; }

        public NiconicoChatService(
            NiconicoChatServiceSettings settings, ChannelDatabase channelDatabase,
            string jikkyouIdTableFilePath, string liveIdTableFilePath
        )
        {
            this.settings = settings;
            this.jkIdResolver = new NiconicoUtils.JkIdResolver(channelDatabase, new NiconicoUtils.JkIdTable(jikkyouIdTableFilePath));
            this.liveIdResolver = new NiconicoUtils.LiveIdResolver(channelDatabase, new NiconicoUtils.LiveIdTable(liveIdTableFilePath));

            try
            {
                if(!string.IsNullOrWhiteSpace(UserId) && !string.IsNullOrWhiteSpace(UserPassword))
                    SetUser(UserId, UserPassword).Wait();
            }
            catch (AggregateException e)
            when (e.InnerExceptions.Count == 1 && e.InnerExceptions[0] is NiconicoUtils.NiconicoLoginSessionException)
            { }

            ChatCollectServiceEntries = new ChatCollectServiceEntry.IChatCollectServiceEntry[] {
                new ChatCollectServiceEntry.NewNiconicoJikkyouChatCollectServiceEntry(this, this.liveIdResolver, this.loginSession),
                new ChatCollectServiceEntry.NiconicoLiveChatCollectServiceEntry(this, this.loginSession),
                new ChatCollectServiceEntry.TsukumijimaJikkyoApiChatCollectServiceEntry(this, this.jkIdResolver),
            };
            ChatTrendServiceEntries = new IChatTrendServiceEntry[0];
        }

        /// <summary>
        /// ニコニコのユーザーを指定しログインする
        /// 失敗した場合オブジェクトの状態は変化しない
        /// </summary>
        /// <param name="userId">ニコニコのユーザーID</param>
        /// <param name="userPassword">ニコニコのパスワード</param>
        /// <exception cref="ArgumentException"><paramref name="userId"/>または<paramref name="userPassword"/>がnull若しくはホワイトスペースだった時</exception>
        /// <exception cref="NiconicoUtils.NiconicoLoginSessionException">ログインに失敗した時</exception>
        public async Task SetUser(string userId, string userPassword)
        {
            if (string.IsNullOrWhiteSpace(userId))
                throw new ArgumentException($"{nameof(userId)} must not be null nor white space", nameof(userId));
            if (string.IsNullOrWhiteSpace(userPassword))
                throw new ArgumentException($"{nameof(userPassword)} must not be null nor white space", nameof(userPassword));

            //ログインしてみる
            var tmpSession = new NiconicoUtils.NiconicoLoginSession(userId, userPassword);
            await tmpSession.Login().ConfigureAwait(false);

            //成功したら設定、セッションを置き換える
            this.IsLoggedin = true;
            this.settings.UserId = userId;
            this.settings.Password = userPassword;
            try
            {
                await (this.loginSession.Value?.Logout() ?? Task.CompletedTask);
            }
            catch(NiconicoUtils.NiconicoLoginSessionException)
            { }
            this.loginSession.Value = tmpSession;
        }

        public void Dispose(){ }
    }
}
