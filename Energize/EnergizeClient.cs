﻿using Discord;
using Discord.Rest;
using Discord.WebSocket;
using DiscordBotsList.Api;
using DiscordBotsList.Api.Objects;
using Energize.Essentials;
using Energize.Services;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Energize
{
    public class EnergizeClient
    {
#if DEBUG
        private readonly bool IsDevEnv = true;
#else
        private readonly bool IsDevEnv = false;
#endif
        private readonly string Token;
        private readonly AuthDiscordBotListApi DiscordBotList;

        public EnergizeClient(string token, string prefix, char separator)
        {
            Console.Clear();
            Console.Title = "Energize's Logs";

            this.Token = token;
            this.Prefix = prefix;
            this.Separator = separator;
            this.Logger = new Logger();
            this.MessageSender = new MessageSender(this.Logger);
            this.DiscordClient = new DiscordShardedClient(new DiscordSocketConfig
            {
                MessageCacheSize = 100,
                ExclusiveBulkDelete = false,
                LogLevel = LogSeverity.Verbose,
            });
            this.DiscordRestClient = new DiscordRestClient();
            this.ServiceManager = new ServiceManager(this);

            if (this.HasToken)
            {
                AppDomain.CurrentDomain.UnhandledException += (sender, args) =>
                {
                    Exception e = (Exception)args.ExceptionObject;
                    this.Logger.LogTo("crash.log", e.ToString());
                };

                this.DiscordClient.Log += async log => this.Logger.LogTo("dnet_socket.log", log.Message);
                this.DiscordRestClient.Log += async log => this.Logger.LogTo("dnet_rest.log", log.Message);

                this.DiscordBotList = new AuthDiscordBotListApi(Config.Instance.Discord.BotID, Config.Instance.Discord.BotListToken);
                this.DisplayAsciiArt();

                this.Logger.Nice("Config", ConsoleColor.Yellow, $"Environment => [ {this.Environment} ]");
                this.Logger.Notify("Initializing");

                try
                {
                    this.ServiceManager.InitializeServices();
                }
                catch (Exception e)
                {
                    this.Logger.Nice("Init", ConsoleColor.Red, $"Something went wrong: {e}");
                }
            }
            else
            {
                this.Logger.Warning("No token was used! You NEED a token to connect to Discord!");
            }
        }

        private void DisplayAsciiArt()
        {
            ConsoleColor[] colors =
            {
                ConsoleColor.Blue, ConsoleColor.Cyan, ConsoleColor.Green,
                ConsoleColor.Yellow, ConsoleColor.Red, ConsoleColor.Magenta,
            };

            string[] lines = StaticData.Instance.AsciiArt.Split('\n');
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];
                ConsoleColor col = colors[i];
                Console.ForegroundColor = col;
                Console.WriteLine($"\t{line}");
            }

            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("\n" + new string('-', 70));
        }

        public string Prefix { get; }
        public char Separator { get; }
        public DiscordShardedClient DiscordClient { get; }
        public DiscordRestClient DiscordRestClient { get; }
        public Logger Logger { get; }
        public MessageSender MessageSender { get; }
        public ServiceManager ServiceManager { get; }

        public string Environment => this.IsDevEnv ? "DEVELOPMENT" : "PRODUCTION"; 
        public bool HasToken => !string.IsNullOrWhiteSpace(this.Token); 

        private async Task<(bool, int)> UpdateBotWebsites()
        {
            int serverCount = this.DiscordClient.Guilds.Count;
            bool success = true;
            if (this.IsDevEnv) return (true, serverCount);
         
            try
            {
                var obj = new { guildCount = serverCount };
                if (JsonHelper.TrySerialize(obj, this.Logger, out string json))
                {
                    string endpoint = $"https://discord.bots.gg/api/v1/bots/{Config.Instance.Discord.BotID}/stats";
                    await HttpHelper.PostAsync(endpoint, json, this.Logger, null, req => {
                        req.Headers[System.Net.HttpRequestHeader.Authorization] = Config.Instance.Discord.BotsToken;
                        req.ContentType = "application/json";
                    });
                }

                IDblSelfBot me = await this.DiscordBotList.GetMeAsync();
                await me.UpdateStatsAsync(serverCount);
            }
            catch
            {
                success = false;
            }

            return (success, serverCount);
        }

        private async Task UpdateActivity()
        {
            StreamingGame game = Config.Instance.Maintenance
                ? new StreamingGame("maintenance", Config.Instance.URIs.TwitchURL)
                : new StreamingGame($"{this.Prefix}help | {this.Prefix}info | {this.Prefix}docs",
                    Config.Instance.URIs.TwitchURL);
            await this.DiscordClient.SetActivityAsync(game);
        }

        private async Task NotifyCaughtExceptionsAsync()
        {
            RestChannel chan = await this.DiscordRestClient.GetChannelAsync(Config.Instance.Discord.BugReportChannelID);
            if (chan == null) return;

            IEnumerable<IGrouping<Exception, EventHandlerException>> exs = this.ServiceManager.TakeCaughtExceptions();
            foreach(IGrouping<Exception, EventHandlerException> grouping in exs)
            {
                EventHandlerException ex = grouping.FirstOrDefault();
                if (ex == null) continue;

                EmbedBuilder builder = new EmbedBuilder();
                builder
                    .WithField("Message", ex.Error.Message)
                    .WithField("File", ex.FileName)
                    .WithField("Method", ex.MethodName)
                    .WithField("Line", ex.Line)
                    .WithField("Occurences", grouping.Count())
                    .WithColorType(EmbedColorType.Warning)
                    .WithFooter("event handler error");

                await this.MessageSender.Send(chan, builder.Build());
            }
        }

        public async Task InitializeAsync()
        {
            if (!this.HasToken) return;

            try
            {
                await this.DiscordClient.LoginAsync(TokenType.Bot, this.Token);
                await this.DiscordClient.StartAsync();
                await this.DiscordRestClient.LoginAsync(TokenType.Bot, this.Token);
                await this.UpdateActivity();

                Timer updateTimer = new Timer(async arg =>
                {
                    long mb = Process.GetCurrentProcess().WorkingSet64 / 1024L / 1024L; //b to mb
                    GC.Collect();

                    (bool success, int servercount) = await this.UpdateBotWebsites();
                    string log = success
                        ? $"Collected {mb}MB of garbage, updated server count ({servercount})"
                        : $"Collected {mb}MB of garbage, did NOT update server count, API might be down";
                    this.Logger.Nice("Update", ConsoleColor.Gray, log);

                    await this.UpdateActivity();
                    await this.NotifyCaughtExceptionsAsync();
                });

                const int hour = 1000 * 60 * 60;
                updateTimer.Change(10000, hour);

                await this.ServiceManager.InitializeServicesAsync();
            }
            catch (Exception ex)
            {
                this.Logger.Nice("Init", ConsoleColor.Red, $"Something went wrong: {ex}");
            }
        }
    }
}