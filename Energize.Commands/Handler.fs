﻿namespace Energize.Commands

open Command

[<CommandModule("Core")>]
module CommandHandler =
    open Discord.WebSocket
    open ImageUrlProvider
    open System.Text.RegularExpressions
    open Discord
    open Cache
    open Discord.Rest
    open Context
    open System.Threading.Tasks
    open System
    open AsyncHelper
    open Energize.Essentials
    open System.Reflection
    open Energize.Interfaces.Services
    open System.Diagnostics
    open Energize.Interfaces.Services.Senders

    type private CommandHandlerState =
        {
            client : DiscordShardedClient
            restClient : DiscordRestClient
            caches : Map<uint64, CommandCache>
            commands : Map<string, Command>
            logger : Logger
            messageSender : MessageSender
            prefix : string
            separator: char
            serviceManager : IServiceManager
            commandCache : (uint64 * IUserMessage list) list
        }

    // I had to.
    let mutable private handlerState : CommandHandlerState option = None

    let private registerCmd (state : CommandHandlerState) (cmd : Command) =
        Some { state with commands = state.commands.Add (cmd.name, cmd) }

    let private postCmdHelp (cmd : Command) (ctx : CommandContext) (iswarn : bool) =
        let fields = [
            ctx.embedField "Usage" (sprintf "`%s`" cmd.usage) false
            ctx.embedField "Help" (sprintf "`%s`" cmd.help) false
        ]
        let builder = EmbedBuilder()
        ctx.messageSender.BuilderWithAuthor(ctx.message, builder)
        builder
            .WithFields(fields)
            .WithColor(if iswarn then ctx.messageSender.ColorWarning else ctx.messageSender.ColorGood)
            .WithFooter(sprintf (if iswarn then "bad usage [ %s ]" else "help [ %s ]") cmd.name)
            |> ignore
        ctx.sendEmbed (builder.Build())

    [<Command("help", "This command", "help <cmd|nothing>")>]
    let help (ctx : CommandContext) = async {
        return
            if ctx.hasArguments then
                let cmdName = ctx.arguments.[0].Trim()
                match handlerState.Value.commands |> Map.tryFind cmdName with
                | Some cmd ->
                    [ postCmdHelp cmd ctx false ]
                | None ->
                    let warning = sprintf "Could not find any command named \'%s\'" cmdName
                    [ ctx.sendWarn None warning ]
            else
                let paginator = ctx.serviceManager.GetService<IPaginatorSenderService>("Paginator")
                let commands = 
                    handlerState.Value.commands 
                    |> Map.toSeq 
                    |> Seq.groupBy (fun (_, cmd) -> cmd.moduleName) 
                    |> Seq.filter (fun (name, _) -> not (name.Equals("Deprecated"))) 
                    |> Seq.sortBy (fun (name, _) -> name)
                [ awaitResult (paginator.SendPaginator(ctx.message, ctx.commandName, commands, Action<string * seq<string * Command>, EmbedBuilder>(fun (moduleName, cmds) builder ->
                    let cmdsDisplay = 
                        cmds |> Seq.map (fun (cmdName, _) -> sprintf "`%s`" cmdName)
                    builder.WithFields(ctx.embedField moduleName (String.Join(',', cmdsDisplay)) true)
                    |> ignore
                ))) ]
    }

    let private enableCmd (state : CommandHandlerState) (cmdName : string) (enabled : bool) = 
        match state.commands |> Map.tryFind cmdName with
        | Some cmd -> cmd.isEnabled <- enabled
        | None -> ()
    
    [<CommandConditions(CommandCondition.OwnerOnly)>]
    [<CommandParameters(2)>]
    [<Command("enable", "Enables or disables a command", "enable <cmd>,<value>")>]
    let enable (ctx : CommandContext) = async {
        let cmdName = ctx.arguments.[0].Trim()
        let value = int (ctx.arguments.[1].Trim())
        return
            match handlerState with
            | Some state ->
                if state.commands |> Map.containsKey cmdName then
                    if value.Equals(0) then
                        enableCmd state cmdName false
                        [ ctx.sendOK None (sprintf "Successfully disabled command \'%s\'" cmdName) ]
                    else
                        enableCmd state cmdName true
                        [ ctx.sendOK None (sprintf "Successfully enabled command \'%s\'" cmdName) ]
                else
                    [ ctx.sendWarn None (sprintf "Could not find any command named \'%s\'" cmdName) ]
            | None -> []
    }

    let private loadCmd (state : CommandHandlerState) (callback : CommandCallback) (moduleType : Type) =
        let infoAtr = callback.Method.GetCustomAttribute<CommandAttribute>()
        let moduleAtr = moduleType.GetCustomAttribute<CommandModuleAttribute>()
        let paramAtr = callback.Method.GetCustomAttributes<CommandParametersAttribute>() |> Seq.tryHead
        let permsAtr = callback.Method.GetCustomAttributes<CommandPermissionsAttribute>() |> Seq.tryHead
        let condsAtr = callback.Method.GetCustomAttributes<CommandConditionsAttribute>() |> Seq.tryHead
        let paramCount = match paramAtr with Some atr -> atr.parameters | None -> 0
        let permissions = match permsAtr with Some atr -> atr.permissions | None -> []
        let conditions = match condsAtr with Some atr -> atr.conditions | None -> []

        let cmd : Command =
            {
                name = infoAtr.name
                callback = callback
                isEnabled = true
                usage = infoAtr.usage
                help = infoAtr.help
                moduleName = moduleAtr.name
                parameters = paramCount
                permissions = permissions
                conditions = conditions
            }

        registerCmd state cmd

    let private loadCmds (state : CommandHandlerState) =
        let moduleTypes = 
            Assembly.GetExecutingAssembly().GetTypes() 
            |> Seq.rev
            |> Seq.filter 
                (fun t ->
                    let atr = t.GetCustomAttributes<CommandModuleAttribute>() |> Seq.tryHead
                    if t.FullName.StartsWith("Energize.Commands") && atr.IsSome then
                        state.logger.Nice("Commands", ConsoleColor.Green, sprintf "Registered command module [ %s ]" atr.Value.name)
                        true
                    else
                        false
                )

        for moduleType in moduleTypes do
            let funcs = moduleType.GetMethods() |> Seq.filter (fun func -> Attribute.IsDefined(func, typedefof<CommandAttribute>))
            try 
                for func in funcs do
                    let dlg = func.CreateDelegate(typedefof<CommandCallback>) :?> CommandCallback
                    handlerState <- loadCmd (match handlerState with Some s -> s | None -> state) dlg moduleType
            with ex ->
                state.logger.Danger(ex.ToString())

    let Initialize (client : DiscordShardedClient) (restClient : DiscordRestClient) (logger : Logger) 
        (messageSender : MessageSender) (prefix : string) (separator : char) (serviceManager : IServiceManager) =
        let newState : CommandHandlerState =
            {
                client = client
                restClient = restClient
                caches = Map.empty
                commands = Map.empty
                logger = logger
                messageSender = messageSender
                prefix = prefix
                separator = separator
                serviceManager = serviceManager
                commandCache = List.empty
            }
        
        logger.Nice("Commands", ConsoleColor.Yellow, sprintf "Registering commands with prefix \'%s\'" prefix)
        loadCmds newState
        logger.Notify("Commands Initialized")

    let private getChannelCache (state : CommandHandlerState) (id : uint64) : CommandCache =
        match state.caches |> Map.tryFind id with
        | Some cache ->
            cache
        | None ->
            let cache = 
                {
                    lastMessage = None
                    lastDeletedMessage = None
                    lastImageUrl = None
                }
            let newCaches = state.caches |> Map.add id cache
            handlerState <- Some { state with caches = newCaches }
            cache

    let private startsWithBotMention (state : CommandHandlerState) (input : string) : bool =
        match state.client.CurrentUser with
        | null -> false
        | _ -> Regex.IsMatch(input,"^<@!?" + state.client.CurrentUser.Id.ToString() + ">")

    let private getPrefixLength (state : CommandHandlerState) (input : string) : int =
        if startsWithBotMention state input then
            state.client.CurrentUser.Mention.Length
        else
            state.prefix.Length

    let private sanitizeInput (input : string) =
        input
            .Replace('\n', ' ')
            .Replace('\t', ' ')
    
    let private getCmdName (state : CommandHandlerState) (input : string) : string =
        let offset = getPrefixLength state input
        if offset >= input.Length then String.Empty else (sanitizeInput input).[offset..].Split(' ').[0]

    let private getCmdArgs (state : CommandHandlerState) (input : string) : string list =
        let offset = (getPrefixLength state input) + (getCmdName state input).Length
        let args = input.[offset..].TrimStart().Split(state.separator) |> Array.toList
        if args.[0] |> String.IsNullOrWhiteSpace && args.Length.Equals(1) then
            []
        else
            args |> List.map (fun arg -> arg.Trim())
       
    let private buildCmdContext (state : CommandHandlerState) (cmdName : string) (msg : SocketMessage) (args : string list)
        (isPrivate : bool): CommandContext =
        let users = 
            if isPrivate then
                List.empty
            else
                let chan = msg.Channel :?> IGuildChannel
                seq { for u in (chan.Guild :?> SocketGuild).Users -> u :> IGuildUser }
                |> Seq.toList
        {
            client = state.client
            restClient = state.restClient
            message = msg
            prefix = state.prefix
            arguments = args
            cache = getChannelCache state msg.Channel.Id
            messageSender = state.messageSender
            logger = state.logger
            isPrivate = isPrivate
            commandName = cmdName
            serviceManager = state.serviceManager
            random = Random()
            guildUsers = users
            commandCount = state.commands.Count
        }

    let private registerCmdCacheEntry (msgId : uint64) (msgs : IUserMessage list) =
        match handlerState with
        | Some state ->
            let newCommandCache = 
                let cache = (msgId, msgs |> List.filter (fun msg -> match msg with null -> false | _ -> true)) :: state.commandCache
                if cache.Length > 50 then cache.[..50] else cache
            handlerState <- Some { state with commandCache = newCommandCache }
        | None -> ()
    
    let private reportCmdError (state : CommandHandlerState) (ex : exn) (msg : SocketMessage) (cmd : Command) (input : string) =
        let webhook = state.serviceManager.GetService<IWebhookSenderService>("Webhook")
        let realEx = match ex.InnerException with null -> ex | exIn -> exIn
        state.logger.Warning(realEx.ToString())
        
        let err = sprintf "Something went wrong when using \'%s\' a report has been sent" cmd.name
        let msgs = [ awaitResult (state.messageSender.Warning(msg, "internal Error", err)) ]
        registerCmdCacheEntry msg.Id msgs
        
        let args = input.Trim()
        let argDisplay = if String.IsNullOrWhiteSpace args then "none" else args
        let frame = StackTrace(realEx, true).GetFrame(0)
        let source = sprintf "@File: %s | Method: %s | Line: %d" (frame.GetFileName()) (frame.GetMethod().Name) (frame.GetFileLineNumber())
        let builder = EmbedBuilder()
        builder
            .WithDescription(sprintf "**USER:** %s\n**COMMAND:** %s\n**ARGS:** %s\n**ERROR:** %s" (msg.Author.ToString()) cmd.name argDisplay realEx.Message)
            .WithTimestamp(msg.CreatedAt)
            .WithFooter(source)
            .WithColor(state.messageSender.ColorDanger)
            |> ignore
        match state.client.GetChannel(Config.Instance.Discord.FeedbackChannelID) with
        | null -> ()
        | c ->
            let chan = c :> IChannel :?> ITextChannel
            awaitIgnore (webhook.SendEmbed(chan, builder.Build(), msg.Author.Username, msg.Author.GetAvatarUrl(ImageFormat.Auto))) 

    let private handleTimeOut (state : CommandHandlerState) (msg : SocketMessage) (cmd : Command) (ctx : CommandContext) : Task<Task> =
        async {
            let asyncOp = cmd.callback.Invoke(ctx)
            let tcallback = (toTaskResult asyncOp).ContinueWith(fun (t : Task<_>) -> 
                if t.Status.Equals(TaskStatus.Faulted) then  
                    reportCmdError state t.Exception msg cmd ctx.input
                else
                    registerCmdCacheEntry msg.Id (awaitResult t)
            )
            if not tcallback.IsCompleted then
                let tres = awaitResult (Task.WhenAny(tcallback, Task.Delay(10000)))
                if not tcallback.IsCompleted then
                    awaitResult (state.messageSender.Warning(msg, "time out", sprintf "Your command \'%s\' is timing out!" cmd.name)) |> ignore
                    state.logger.Nice("Commands", ConsoleColor.Yellow, sprintf "Time out of command <%s>" cmd.name)
                return tres
            else
                return Task.CompletedTask
        } |> toTaskResult

    let private logCmd (ctx : CommandContext) =
        let color = if ctx.isPrivate then ConsoleColor.Blue else ConsoleColor.Cyan
        let head = if ctx.isPrivate then "DMCommands" else "Commands"
        let action = "used"
        let where = 
            if not ctx.isPrivate then
                let chan = ctx.message.Channel :?> IGuildChannel
                (sprintf "(%s - #%s) " chan.Guild.Name chan.Name)
            else 
                String.Empty 
        let cmdLog = sprintf "%s %s <%s>" ctx.message.Author.Username action ctx.commandName
        let args = if ctx.hasArguments then (sprintf " => [ %s ]" (String.Join(", ", ctx.arguments))) else " with no args"
        ctx.logger.Nice(head, color, where + cmdLog + args)

    let private runCmd (state : CommandHandlerState) (msg : SocketMessage) (cmd : Command) (input : string) (isPrivate : bool) =
        await (state.messageSender.TriggerTyping(msg.Channel))
        let args = getCmdArgs state input
        let ctx = buildCmdContext state cmd.name msg args isPrivate
        if args.Length >= cmd.parameters then
            let task = handleTimeOut state msg cmd ctx
            task.ConfigureAwait(false) |> ignore
            logCmd ctx
        else
            let msgs = [ postCmdHelp cmd ctx true ]
            registerCmdCacheEntry msg.Id msgs
            logCmd ctx
    
    let private hasPermissions (msg : SocketMessage) (cmd : Command) =
        if cmd.permissions.Length > 0 then
            if Context.isPrivate msg then
                (true, [])
            else
                let guild = (msg.Channel :?> IGuildChannel).Guild
                let botUser = awaitResult (guild.GetUserAsync(Config.Instance.Discord.BotID))
                let botPerms = botUser.GuildPermissions 
                let hasAllPerms = cmd.permissions |> List.forall (fun perm -> botPerms.Has(perm))
                let missingPerms = cmd.permissions |> List.filter (fun perm -> not (botPerms.Has(perm)))
                (hasAllPerms, missingPerms)
        else
            (true, [])

    let private hasConditions (msg : SocketMessage) (cmd : Command) =
        if cmd.conditions.Length > 0 then
            let conds = 
                cmd.conditions |> List.map (fun cond ->
                    let valid = 
                        match cond with
                        | CommandCondition.OwnerOnly ->
                            msg.Author.Id.Equals(Config.Instance.Discord.OwnerID)
                        | CommandCondition.GuildOnly ->
                            not (Context.isPrivate msg)
                        | CommandCondition.NsfwOnly ->
                            Context.isNSFW msg
                        | CommandCondition.AdminOnly ->
                            Context.isAuthorAdmin msg
                        | _ -> false
                    (cond, valid)
                )
            let hasAllConditions = conds |> List.forall (fun (_, isvalid) -> isvalid)
            let missingConds = conds |> List.filter (fun (perm, isvalid) -> not isvalid) |> List.map (fun (perm, _) -> perm)
            (hasAllConditions, missingConds)
        else
            (true, [])

    let private handleCmd (state : CommandHandlerState) (msg : SocketMessage) (cmd : Command) (input : string) =
        let author = msg.Author.ToString()
        let hasPermissions, missingPerms = hasPermissions msg cmd
        let hasConditions, missingConds = hasConditions msg cmd
        match cmd with
        | cmd when not (cmd.isEnabled) ->
            state.logger.Nice("Commands", ConsoleColor.Red, sprintf "%s tried to use a disabled command <%s>" author cmd.name)
            awaitIgnore (state.messageSender.Warning(msg, "disabled command", "This is a disabled feature for now")) 
        | cmd when not hasPermissions ->
            state.logger.Nice("Commands", ConsoleColor.Red, sprintf "%s tried to use a command with missing permissions <%s>" author cmd.name)
            let permDisplay = String.Join(", ", missingPerms |> List.map (fun perm -> sprintf "`%s`" (perm.ToString())))
            awaitIgnore (state.messageSender.Warning(msg, "missing permissions", sprintf "Missing the following permissions:\n%s" permDisplay))
        | cmd when not hasConditions ->
            state.logger.Nice("Commands", ConsoleColor.Red, sprintf "%s tried to use a command with unmet conditions <%s>" author cmd.name)
            let condDisplay = String.Join(", ", missingConds |> List.map (fun perm -> sprintf "`%s`" (perm.ToString())))
            awaitIgnore (state.messageSender.Warning(msg, "unmet conditions", sprintf "The following conditions were not met:\n%s" condDisplay))
        | cmd ->
            runCmd state msg cmd input (Context.isPrivate msg)

    let private deleteCmdMsgs (cmdMsgId : uint64) = 
        match handlerState with
        | Some state ->
            match state.commandCache |> List.tryFind (fun (id, _) -> cmdMsgId.Equals(id)) with
            | Some (id, msgs) -> 
                try
                    seq { for msg in msgs -> match msg with null -> Task.CompletedTask | _ -> msg.DeleteAsync() } 
                    |> Task.WhenAll 
                    |> await
                with _ ->
                    let ex = "A message was removed but the command message associated could not be removed"
                    state.logger.Nice("Commands", ConsoleColor.Yellow, ex)
                let newCmdCache = state.commandCache |> List.except [ (id, msgs) ]
                handlerState <- Some { state with commandCache = newCmdCache }
            | None -> ()
        | None -> ()

    let private isBlacklisted (id : uint64) =
        let ids = Config.Instance.Discord.Blacklist.IDs |> Seq.toList
        match ids |> List.tryFind (fun i -> i.Equals(id)) with
        | Some _ -> true
        | None -> false

    let HandleMessageDeleted (cache : Cacheable<IMessage, uint64>) (chan : ISocketMessageChannel) =
        match handlerState with
        | Some state when cache.HasValue && not (isBlacklisted cache.Value.Author.Id) ->
            let oldCache = getChannelCache state chan.Id
            let newCache = { oldCache with lastDeletedMessage = Some (cache.Value :?> SocketMessage) }
            let newCaches = state.caches.Add(chan.Id, newCache)
            handlerState <- Some { state with caches = newCaches }
            deleteCmdMsgs cache.Id
        | Some _ -> deleteCmdMsgs cache.Id
        | _ -> printfn "COMMAND HANDLER WAS NOT INITIALIZED ??!"

    let private updateChannelCache (msg : SocketMessage) (cb : CommandCache -> CommandCache) =
        match handlerState with
        | Some state ->
            let oldCache = getChannelCache state msg.Channel.Id
            let newCache = cb oldCache
            let newCaches = state.caches.Add(msg.Channel.Id, newCache)
            handlerState <- Some { state with caches = newCaches }
        | None -> ()
     
    let private findAndRunCmd (state : CommandHandlerState) (msg : SocketMessage) (content : string) (botMention : bool) =
        let cmdName = getCmdName state content
        match state.commands |> Map.tryFind cmdName with
        | Some cmd -> handleCmd state msg cmd content
        | None when botMention -> 
            let showCmd cmdName = state.prefix + cmdName
            let helper =
                sprintf "Hey there %s, looking for something? Use %s or %s!" msg.Author.Mention (showCmd "help") (showCmd "info")
            awaitIgnore (state.messageSender.SendRaw(msg, helper))
        | None -> ()

    let HandleMessageReceived (msg : SocketMessage) =
        match handlerState with
        | Some state when not (isBlacklisted msg.Author.Id) ->
            updateChannelCache msg (fun oldCache -> 
                let lastUrl = match getLastImgUrl msg with Some url -> Some url | None -> oldCache.lastImageUrl
                let lastMsg = if msg.Author.IsBot then oldCache.lastMessage else Some msg
                { oldCache with lastImageUrl = lastUrl; lastMessage = lastMsg }
            )
            match msg.Content with
            | content when msg.Author.IsBot || msg.Author.IsWebhook || content |> String.IsNullOrWhiteSpace -> () // to the trash it goes
            | content when startsWithBotMention state content ->
                findAndRunCmd state msg content true
            | content when content.ToLower().StartsWith(state.prefix) ->
                findAndRunCmd state msg content false
            | _ -> ()
        | Some _ -> ()
        | None -> printfn "COMMAND HANDLER WAS NOT INITIALIZED ??!"

    let HandleMessageUpdated _ (msg : SocketMessage) _ =
        match handlerState with
        | Some _ when not (isBlacklisted msg.Author.Id) ->
            let diff = DateTime.Now.ToUniversalTime() - msg.Timestamp.DateTime
            if diff.TotalHours < 1.0 then
                deleteCmdMsgs msg.Id
                HandleMessageReceived msg
        | Some _ -> ()
        | None -> printfn "COMMAND HANDLER WAS NOT INITIALIZED ??!"