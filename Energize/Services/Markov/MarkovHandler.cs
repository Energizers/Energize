﻿using Discord;
using Discord.WebSocket;
using Energize.Toolkit;
using System.Threading.Tasks;

namespace Energize.Services.Markov
{
    [Service("Markov")]
    public class MarkovHandler
    {
        private string _Prefix;
        private Logger _Log;
        private char[] _Separators = { ' ', '.', ',', '!', '?', ';', '_' };
        private int _MaxDepth = 2;

        public MarkovHandler(EnergizeClient client)
        {
            this._Prefix = client.Prefix;
            this._Log = client.Log;
        }

        public void Learn(string content,ulong id, Logger log)
        {
            MarkovChain chain = new MarkovChain();
            try
            {   
                chain.Learn(content);
            }
            //Yeah fuck that this log is too verbose
            catch{ }
        }

        public string Generate(string data)
        {
            MarkovChain chain = new MarkovChain();

            if (data == "")
            {
                return chain.Generate(40);
            }
            else
            {
                data = data.ToLower();
                string firstpart = "";
                string[] parts = data.Split(_Separators);
                if(parts.Length > _MaxDepth)
                {
                    firstpart = string.Join(' ',parts,parts.Length - _MaxDepth,_MaxDepth);
                    return data + " " + chain.Generate(firstpart,40).TrimStart();
                }
                else
                {
                    firstpart = string.Join(' ',parts);
                    return firstpart + " " + chain.Generate(firstpart,40).TrimStart();
                }
            }
        }

        [Event("MessageReceived")]
        public async Task OnMessageReceived(SocketMessage msg)
        {
            ITextChannel chan = msg.Channel as ITextChannel;
            if (!msg.Author.IsBot && !chan.IsNsfw && !msg.Content.StartsWith(this._Prefix))
            {
                ulong id = 0;
                if (msg.Channel is IGuildChannel)
                {
                    IGuildChannel guildchan = msg.Channel as IGuildChannel;
                    id = guildchan.Guild.Id;
                }
                this.Learn(msg.Content, id, this._Log);
            }
        }
    }
}
