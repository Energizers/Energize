﻿using Discord;
using Discord.WebSocket;
using Energize.Services.TextProcessing;
using Energize.Toolkit;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Energize.Services.Listeners
{
    [Service("Hentai")]
    public class Hentai
    {
        private List<string> _Triggers = new List<string>{
            "hentai",
            "anime"
        };

        private bool HasTrigger(string sentence)
        {
            sentence = sentence.ToLower();
            return this._Triggers.Any(x => sentence.Contains(x));
        }

        [Event("MessageReceived")]
        public async Task OnMessageReceived(SocketMessage msg)
        {
            if (msg.Channel is IGuildChannel && !msg.Author.IsBot)
            {
                if (this.HasTrigger(msg.Content))
                {
                    TextStyle style = ServiceManager.GetService<TextStyle>("TextStyle");
                    WebhookSender sender = ServiceManager.GetService<WebhookSender>("Webhook");

                    Random rand = new Random();
                    string quote = StaticData.HENTAI_QUOTES[rand.Next(0, StaticData.HENTAI_QUOTES.Length - 1)];
                    quote = quote.Replace("{NAME}", msg.Author.Username);
                    quote = style.GetStyleResult(quote, "anime");
                    ITextChannel chan = msg.Channel as ITextChannel;

                    await sender.SendRaw(msg, quote,"Hentai-Chan",
                        "https://dl.dropboxusercontent.com/s/fobfj7jhxfw0mjy/hentai.jpg");
                }
            }
        }
    }
}
