using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using App.Metrics;

using DSharpPlus;
using DSharpPlus.Entities;
using DSharpPlus.Exceptions;

using PluralKit.Core;

using Serilog;

namespace PluralKit.Bot
{
    public class ProxyService
    {
        private static readonly TimeSpan MessageDeletionDelay = TimeSpan.FromMilliseconds(1000);

        private readonly LogChannelService _logChannel;
        private readonly IDatabase _db;
        private readonly ModelRepository _repo;
        private readonly ILogger _logger;
        private readonly WebhookExecutorService _webhookExecutor;
        private readonly ProxyMatcher _matcher;
        private readonly IMetrics _metrics;

        public ProxyService(LogChannelService logChannel, ILogger logger,
                            WebhookExecutorService webhookExecutor, IDatabase db, ProxyMatcher matcher, IMetrics metrics, ModelRepository repo)
        {
            _logChannel = logChannel;
            _webhookExecutor = webhookExecutor;
            _db = db;
            _matcher = matcher;
            _metrics = metrics;
            _repo = repo;
            _logger = logger.ForContext<ProxyService>();
        }

        public async Task<bool> HandleIncomingMessage(DiscordClient shard, DiscordMessage message, MessageContext ctx, bool allowAutoproxy)
        {
            if (!ShouldProxy(message, ctx)) return false;

            // Fetch members and try to match to a specific member
            await using var conn = await _db.Obtain();

            List<ProxyMember> members;
            using (_metrics.Measure.Timer.Time(BotMetrics.ProxyMembersQueryTime))
                members = (await _repo.GetProxyMembers(conn, message.Author.Id, message.Channel.GuildId)).ToList();
            
            if (!_matcher.TryMatch(ctx, members, out var match, message.Content, message.Attachments.Count > 0,
                allowAutoproxy)) return false;

            // Permission check after proxy match so we don't get spammed when not actually proxying
            if (!await CheckBotPermissionsOrError(message.Channel)) return false;
            if (!CheckProxyNameBoundsOrError(match.Member.ProxyName(ctx))) return false;
            
            // Check if the sender account can mention everyone/here + embed links
            // we need to "mirror" these permissions when proxying to prevent exploits
            var senderPermissions = message.Channel.PermissionsInSync(message.Author);
            var allowEveryone = (senderPermissions & Permissions.MentionEveryone) != 0;
            var allowEmbeds = (senderPermissions & Permissions.EmbedLinks) != 0;

            // Everything's in order, we can execute the proxy!
            await ExecuteProxy(shard, conn, message, ctx, match, allowEveryone, allowEmbeds);
            return true;
        }

        private bool ShouldProxy(DiscordMessage msg, MessageContext ctx)
        {
            // Make sure author has a system
            if (ctx.SystemId == null) return false;
            
            // Make sure channel is a guild text channel and this is a normal message
            if (msg.Channel.Type != ChannelType.Text || msg.MessageType != MessageType.Default) return false;
            
            // Make sure author is a normal user
            if (msg.Author.IsSystem == true || msg.Author.IsBot || msg.WebhookMessage) return false;
            
            // Make sure proxying is enabled here
            if (!ctx.ProxyEnabled || ctx.InBlacklist) return false;
            
            // Make sure we have either an attachment or message content
            var isMessageBlank = msg.Content == null || msg.Content.Trim().Length == 0;
            if (isMessageBlank && msg.Attachments.Count == 0) return false;
            
            // All good!
            return true;
        }

        private async Task ExecuteProxy(DiscordClient shard, IPKConnection conn, DiscordMessage trigger, MessageContext ctx,
                                        ProxyMatch match, bool allowEveryone, bool allowEmbeds)
        {
            // Send the webhook
            var content = match.ProxyContent;
            if (!allowEmbeds) content = content.BreakLinkEmbeds();
            var proxyMessage = await _webhookExecutor.ExecuteWebhook(trigger.Channel, match.Member.ProxyName(ctx),
                match.Member.ProxyAvatar(ctx),
                content, trigger.Attachments, allowEveryone);

            await HandleProxyExecutedActions(shard, conn, ctx, trigger, proxyMessage, match);
        }

        private async Task HandleProxyExecutedActions(DiscordClient shard, IPKConnection conn, MessageContext ctx,
                                                      DiscordMessage triggerMessage, DiscordMessage proxyMessage,
                                                      ProxyMatch match)
        {
            Task SaveMessageInDatabase() => _repo.AddMessage(conn, new PKMessage
            {
                Channel = triggerMessage.ChannelId,
                Guild = triggerMessage.Channel.GuildId,
                Member = match.Member.Id,
                Mid = proxyMessage.Id,
                OriginalMid = triggerMessage.Id,
                Sender = triggerMessage.Author.Id
            });
            
            Task LogMessageToChannel() => _logChannel.LogMessage(shard, ctx, match, triggerMessage, proxyMessage.Id).AsTask();
            
            async Task DeleteProxyTriggerMessage()
            {
                // Wait a second or so before deleting the original message
                await Task.Delay(MessageDeletionDelay);
                try
                {
                    await triggerMessage.DeleteAsync();
                }
                catch (NotFoundException)
                {
                    _logger.Debug("Trigger message {TriggerMessageId} was already deleted when we attempted to; deleting proxy message {ProxyMessageId} also", 
                        triggerMessage.Id, proxyMessage.Id);
                    await HandleTriggerAlreadyDeleted(proxyMessage);
                    // Swallow the exception, we don't need it
                }
            }
            
            // Run post-proxy actions (simultaneously; order doesn't matter)
            // Note that only AddMessage is using our passed-in connection, careful not to pass it elsewhere and run into conflicts
            await Task.WhenAll(
                DeleteProxyTriggerMessage(),
                SaveMessageInDatabase(),
                LogMessageToChannel()
            );
        }

        private async Task HandleTriggerAlreadyDeleted(DiscordMessage proxyMessage)
        {
            // If a trigger message is deleted before we get to delete it, we can assume a mod bot or similar got to it
            // In this case we should also delete the now-proxied message.
            // This is going to hit the message delete event handler also, so that'll do the cleanup for us

            try
            {
                await proxyMessage.DeleteAsync();
            }
            catch (NotFoundException) { }
            catch (UnauthorizedException) { }
        }

        private async Task<bool> CheckBotPermissionsOrError(DiscordChannel channel)
        {
            var permissions = channel.BotPermissions();

            // If we can't send messages at all, just bail immediately.
            // 2020-04-22: Manage Messages does *not* override a lack of Send Messages.
            if ((permissions & Permissions.SendMessages) == 0) return false;

            if ((permissions & Permissions.ManageWebhooks) == 0)
            {
                // todo: PKError-ify these
                await channel.SendMessageFixedAsync(
                    $"{Emojis.Error} PluralKit does not have the *Manage Webhooks* permission in this channel, and thus cannot proxy messages. Please contact a server administrator to remedy this.");
                return false;
            }

            if ((permissions & Permissions.ManageMessages) == 0)
            {
                await channel.SendMessageFixedAsync(
                    $"{Emojis.Error} PluralKit does not have the *Manage Messages* permission in this channel, and thus cannot delete the original trigger message. Please contact a server administrator to remedy this.");
                return false;
            }

            return true;
        }

        private bool CheckProxyNameBoundsOrError(string proxyName)
        {
            if (proxyName.Length < 2) throw Errors.ProxyNameTooShort(proxyName);
            if (proxyName.Length > Limits.MaxProxyNameLength) throw Errors.ProxyNameTooLong(proxyName);

            // TODO: this never returns false as it throws instead, should this happen?
            return true;
        }
    }
}