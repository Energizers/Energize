﻿using Discord;
using Discord.WebSocket;
using Energize.Essentials;
using Energize.Interfaces.DatabaseModels;
using Energize.Interfaces.Services.Database;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Energize.Services.Listeners
{
    [Service("Administration")]
    class AdministrationService : ServiceImplementationBase
    {
        private readonly MessageSender MessageSender;
        private readonly ServiceManager ServiceManager;

        public AdministrationService(EnergizeClient client)
        {
            this.MessageSender = client.MessageSender;
            this.ServiceManager = client.ServiceManager;
        }

        private bool IsInviteMessage(IMessage msg)
        {
            string pattern = @"discord\.gg\/.+\s?";
            if (msg.Channel is IGuildChannel)
                return Regex.IsMatch(msg.Content, pattern) && msg.Author.Id != Config.Instance.Discord.BotID;
            else
                return false;
        }

        [Event("MessageReceived")]
        public async Task OnMessageReceived(SocketMessage msg)
        {
            if (!this.IsInviteMessage(msg)) return;
            IGuildChannel chan = (IGuildChannel)msg.Channel;
            IDatabaseService db = this.ServiceManager.GetService<IDatabaseService>("Database");
            using (IDatabaseContext ctx = await db.GetContext())
            {
                IDiscordGuild dbguild = await ctx.Instance.GetOrCreateGuild(chan.GuildId);
                if (!dbguild.ShouldDeleteInvites) return;

                try
                {
                    await msg.DeleteAsync();
                    await this.MessageSender.Warning(msg, "invite checker", "Your message was removed.");
                }
                catch
                {
                    await this.MessageSender.Warning(msg, "invite checker", "Couldn't delete the invite message. Permissions missing.");
                }
            }
        }
    }
}
