﻿using Discord;
using Discord.WebSocket;
using Energize.Essentials;
using HtmlAgilityPack;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Energize.Services.Listeners.Extendability.ExtendableMessageProviders
{
    class FAArtworkProvider : BaseProvider
    {
        private readonly Logger Logger;

        public FAArtworkProvider(Logger logger, string domain, string pattern) : base(domain, pattern)
        {
            this.Logger = logger;
        }

        private string Sanitize(string input)
            => input.Replace("&nbsp;", string.Empty).Trim();

        private bool TryGetNodeAt(HtmlNodeCollection collection, int index, out HtmlNode node)
        {
            if (collection.Count >= index + 1)
            {
                node = collection[index];
                return true;
            }

            node = null;
            return false;
        }

        private string GetNodeValueAt(HtmlNodeCollection collection, int index)
            => this.TryGetNodeAt(collection, index, out HtmlNode node) ? this.Sanitize(node.InnerText) : "N/A";

        public override async Task BuildEmbedsAsync(List<Embed> embeds, IUserMessage msg, SocketReaction reaction)
        {
            foreach(Match match in this.Matches(msg.Content))
            {
                string html = await HttpClient.GetAsync(match.Value, this.Logger);
                if (string.IsNullOrWhiteSpace(html)) continue;

                HtmlDocument doc = new HtmlDocument();
                doc.LoadHtml(html);

                HtmlNode statsNode = doc.DocumentNode.SelectSingleNode("//td[@class='alt1 stats-container']");
                HtmlNode informationNode = doc.DocumentNode.SelectSingleNode("//div[@class='classic-submission-title information']");
                HtmlNode avatarNode = doc.DocumentNode.SelectSingleNode("//img[@class='avatar']");

                if (statsNode == null || informationNode == null || avatarNode == null) continue;

                string title = this.GetNodeValueAt(informationNode.ChildNodes, 1);
                string author = this.GetNodeValueAt(informationNode.ChildNodes, 3);
                string category = this.GetNodeValueAt(statsNode.ChildNodes, 10);
                string theme = this.GetNodeValueAt(statsNode.ChildNodes, 14);
                string species = this.GetNodeValueAt(statsNode.ChildNodes, 18);
                string gender = this.GetNodeValueAt(statsNode.ChildNodes, 22);
                string authorUrl = $"https://www.furaffinity.net/user/{author.ToLower()}";

                EmbedBuilder builder = new EmbedBuilder();
                builder
                    .WithAuthorNickname(msg)
                    .WithColorType(EmbedColorType.Good)
                    .WithTitle($"**{title}**")
                    .WithUrl(match.Value)
                    .WithField("Author", author)
                    .WithField("Category", category)
                    .WithField("Theme", theme)
                    .WithField("Species", species)
                    .WithField("Gender", gender)
                    .WithField("Author Page", authorUrl);

                string avatarUrl = avatarNode.GetAttributeValue("src", null);
                if (!string.IsNullOrWhiteSpace(avatarUrl))
                {
                    avatarUrl = $"https:{avatarUrl}";
                    builder.WithThumbnailUrl(avatarUrl);
                }

                embeds.Add(builder.Build());
            }
        }
    }
}