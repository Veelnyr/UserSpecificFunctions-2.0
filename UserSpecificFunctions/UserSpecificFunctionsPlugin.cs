using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Xna.Framework;
using Newtonsoft.Json;
using Terraria;
using TerrariaApi.Server;
using TShockAPI;
using TShockAPI.Hooks;
using UserSpecificFunctions.Database;
using UserSpecificFunctions.Extensions;
using UserSpecificFunctions.Permissions;

namespace UserSpecificFunctions
{
    [ApiVersion(2, 1)]
    public sealed class UserSpecificFunctionsPlugin : TerrariaPlugin
    {
        private static readonly string ConfigPath = Path.Combine(TShock.SavePath, "UserSpecificFunctions.json");
        private UserSpecificFunctionsConfig _config;
        private DatabaseManager _database;
        private static Regex tagPattern = new Regex("(?<!\\\\)\\[(?<tag>[ac]{1,10})(\\/(?<options>[^:]+))?:(?<text>.+?)(?<!\\\\)\\]", RegexOptions.Compiled);

        private DateTime[] Times = new DateTime[256];
        private double[] Spams = new double[256];
        private string[] LastMsg = new string[256];

        public override string Author => "Ivan & Zaicon & Veelnyr";
        public override string Description => "N/A";
        public override string Name => "User Specific Functions";
        public override Version Version => Assembly.GetExecutingAssembly().GetName().Version;
        public UserSpecificFunctionsPlugin(Main game) : base(game) { }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _database.Dispose();
                File.WriteAllText(ConfigPath, JsonConvert.SerializeObject(_config, Formatting.Indented));

                AccountHooks.AccountDelete -= OnAccountDelete;
                GeneralHooks.ReloadEvent -= OnReload;
                PlayerHooks.PlayerPermission -= OnPlayerPermission;
                ServerApi.Hooks.ServerChat.Deregister(this, OnServerChat);
                ServerApi.Hooks.ServerLeave.Deregister(this, OnLeave);
                PlayerHooks.PlayerCommand -= OnPlayerCommand;

                Commands.ChatCommands.RemoveAll(c => c.CommandDelegate == UsCommand);
                Commands.ChatCommands.RemoveAll(c => c.CommandDelegate == PermissionCommand);
            }

            base.Dispose(disposing);
        }

        public override void Initialize()
        {
            _config = UserSpecificFunctionsConfig.ReadOrCreate(ConfigPath);
            _database = new DatabaseManager();
            _database.Load();

            AccountHooks.AccountDelete += OnAccountDelete;
            GeneralHooks.ReloadEvent += OnReload;
            PlayerHooks.PlayerPermission += OnPlayerPermission;
            ServerApi.Hooks.ServerChat.Register(this, OnServerChat);
            ServerApi.Hooks.ServerLeave.Register(this, OnLeave);
            PlayerHooks.PlayerCommand += OnPlayerCommand;

            Commands.ChatCommands.Add(new Command("us.cmd", UsCommand, "us"));
            Commands.ChatCommands.Add(new Command("permission.cmd", PermissionCommand, "permission"));

            Action<Command> Add = c =>
            {
                Commands.ChatCommands.RemoveAll(c2 => c2.Names.Exists(s2 => c.Names.Contains(s2)));
                Commands.ChatCommands.Add(c);
            };
            Add(new Command(TShockAPI.Permissions.cantalkinthird, ThirdPerson, "me"));
            Add(new Command(TShockAPI.Permissions.whisper, Reply, "reply", "r"));
            Add(new Command(TShockAPI.Permissions.whisper, Whisper, "whisper", "w", "tell"));
        }

        private void OnAccountDelete(AccountDeleteEventArgs e)
        {
            _database.Remove(e.Account);
        }

        private void OnPlayerPermission(PlayerPermissionEventArgs e)
        {
            if (!e.Player.IsLoggedIn)
            {
                e.Result = PermissionHookResult.Unhandled;
                return;
            }

            var playerInfo = _database.Get(e.Player.Account);
            if (playerInfo == null)
            {
                e.Result = PermissionHookResult.Unhandled;
                return;
            }

            if (playerInfo.Permissions.Contains(e.Permission))
            {
                e.Result = !playerInfo.Permissions.Negated(e.Permission)
                    ? PermissionHookResult.Granted
                    : PermissionHookResult.Denied;
            }
            else
            {
                e.Result = PermissionHookResult.Unhandled;
            }
        }

        private void OnReload(ReloadEventArgs e)
        {
            _config = UserSpecificFunctionsConfig.ReadOrCreate(ConfigPath);
            _database.Load();
        }

        private void OnLeave(LeaveEventArgs e)
        {
            Spams[e.Who] = 0.0;
            Times[e.Who] = DateTime.Now.AddSeconds(-_config.Time);
            LastMsg[e.Who] = "";
        }

        private void OnServerChat(ServerChatEventArgs e)
        {
            if (e.Handled || e.Text.StartsWith(TShock.Config.CommandSpecifier) || e.Text.StartsWith(TShock.Config.CommandSilentSpecifier))
            {
                return;
            }
            var player = TShock.Players[e.Who];
            if (player == null)
            {
                return;
            }

            string text = e.Text;

            //anti spam
            if (!player.HasPermission("antispam.ignore"))
            {
                if ((DateTime.Now - Times[e.Who]).TotalSeconds > _config.Time)
                {
                    Spams[e.Who] = 0.0;
                    Times[e.Who] = DateTime.Now;
                }

                if (text == LastMsg[e.Who])
                {
                    Spams[e.Who] += _config.RepeatMsgWeight;
                }
                else
                {
                    LastMsg[e.Who] = text;
                }

                if ((double)text.Count(Char.IsUpper) / text.Length >= _config.CapsRatio)
                {
                    Spams[e.Who] += _config.CapsWeight;
                    text = text.ToLower();
                }
                else if (text.Trim().Length <= _config.ShortLength)
                    Spams[e.Who] += _config.ShortWeight;
                else
                    Spams[e.Who] += _config.NormalWeight;

                if (SpamCheck(e.Who))
                {
                    e.Handled = true;
                    return;
                }
            }

            if (!player.HasPermission(TShockAPI.Permissions.canchat) || player.mute)
            {
                return;
            }

            //tag filter, send message
            var playerData = player.Account == null ? null : _database.Get(player.Account);
            if (playerData != null)
            {
                var prefix = playerData.ChatData.Prefix ?? player.Group.Prefix;
                var suffix = playerData.ChatData.Suffix ?? player.Group.Suffix;
                var chatColor = playerData.ChatData.Color?.ParseColor() ?? player.Group.ChatColor.ParseColor();

                var message = string.Format(TShock.Config.ChatFormat, player.Group.Name, prefix, player.Name, suffix, player.HasPermission("antispam.ignore") ? text : RemoveTags(text));

                TSPlayer.All.SendMessage(message, chatColor);
                TSPlayer.Server.SendMessage(message, chatColor);
                TShock.Log.Info($"Broadcast: {message}");
            }
            else
            {
                Color chatColor = player.Group.ChatColor.ParseColor();
                var message = string.Format(TShock.Config.ChatFormat, player.Group.Name, player.Group.Prefix, player.Name, player.Group.Suffix, player.HasPermission("antispam.ignore") ? text : RemoveTags(text));
                TSPlayer.All.SendMessage(message, chatColor);
                TSPlayer.Server.SendMessage(message, chatColor);
                TShock.Log.Info($"Broadcast: {message}");
            }
            e.Handled = true;
        }

        void OnPlayerCommand(PlayerCommandEventArgs e)
        {
            if (!e.Handled && e.Player.RealPlayer && !e.Player.HasPermission("antispam.ignore"))
            {
                var plr = e.Player;
                if ((DateTime.Now - Times[plr.Index]).TotalSeconds > _config.Time)
                {
                    Spams[plr.Index] = 0.0;
                    Times[plr.Index] = DateTime.Now;
                }

                switch (e.CommandName)
                {
                    case "me":
                    case "r":
                    case "reply":
                    case "tell":
                    case "w":
                    case "whisper":
                        string text = e.CommandText.Substring(e.CommandName.Length);
                        if (text == LastMsg[plr.Index])
                        {
                            Spams[plr.Index] += _config.RepeatMsgWeight;
                        }
                        else
                        {
                            LastMsg[plr.Index] = text;
                        }

                        if ((double)text.Count(Char.IsUpper) / text.Length >= _config.CapsRatio)
                        {
                            Spams[plr.Index] += _config.CapsWeight;
                            text = text.ToLower();
                        }
                        else if (text.Trim().Length <= _config.ShortLength)
                            Spams[plr.Index] += _config.ShortWeight;
                        else
                            Spams[plr.Index] += _config.NormalWeight;
                        break;

                    default:
                        Spams[plr.Index] += _config.CommandWeight;
                        break;
                }

                if (SpamCheck(plr.Index))
                {
                    e.Handled = true;
                }
            }
        }

        private bool SpamCheck(int id)
        {
            if (Spams[id] > _config.KickThreshold)
            {
                TShock.Players[id].Kick(_config.SpamKickReason, true);
                return true;
            }
            else if (Spams[id] > _config.Threshold)
            {
                Times[id] = DateTime.Now;
                TShock.Players[id].SendErrorMessage(_config.SpamWarningMsg);
                return true;
            }
            return false;
        }

        private string RemoveTags(string message)
        {
            while (true)
            {
                Match tag = tagPattern.Match(message);
                if (!tag.Success)
                {
                    break;
                }
                message = message.Remove(tag.Index, tag.Length).Insert(tag.Index, tag.Groups["text"].Value);
            }
            return message;
        }

        private void UsCommand(CommandArgs e)
        {
            var player = e.Player;
            if (e.Parameters.Count == 0)
            {
                player.SendInfoMessage("Available commands:");
                player.SendInfoMessage($"{Commands.Specifier}us prefix <player name> <prefix>");
                player.SendInfoMessage($"{Commands.Specifier}us suffix <player name> <suffix>");
                player.SendInfoMessage($"{Commands.Specifier}us color <player name> <color>");
                player.SendInfoMessage($"{Commands.Specifier}us read <player name>");
                player.SendInfoMessage($"{Commands.Specifier}us remove <player name> <prefix/suffix/color/all>");
                return;
            }

            var command = e.Parameters[0];
            if (command.Equals("color", StringComparison.CurrentCultureIgnoreCase))
            {
                if (e.Parameters.Count != 3)
                {
                    player.SendErrorMessage($"Invalid syntax! Proper syntax: {Commands.Specifier}us color <player name> <rrr,ggg,bbb>");
                    return;
                }

                var username = e.Parameters[1];
                var user = TShock.UserAccounts.GetUserAccountByName(username);
                if (user == null)
                {
                    player.SendErrorMessage($"Couldn't find any users under the name of '{username}'.");
                    return;
                }
                if (user.Name != player.Account?.Name && !e.Player.HasPermission("us.setother"))
                {
                    e.Player.SendErrorMessage("You do not have permission to modify another user's chat data.");
                    return;
                }

                var color = e.Parameters[2].Split(',');
                var target = _database.Get(user);
                if (color.Length != 3 || !byte.TryParse(color[0], out byte _) || !byte.TryParse(color[1], out byte _) ||
                    !byte.TryParse(color[2], out byte _))
                {
                    player.SendErrorMessage("Invalid color format.");
                    return;
                }

                if (target == null)
                {
                    target = new PlayerMetadata(user.ID, new ChatInformation(color: e.Parameters[2]), new PermissionCollection());
                    _database.Add(target);
                }
                else
                {
                    target.ChatData.Color = e.Parameters[2];
                    _database.Update(target);
                }

                player.SendSuccessMessage($"Successfully set {user.Name}'s color.");
            }
            else if (command.Equals("prefix", StringComparison.CurrentCultureIgnoreCase))
            {
                if (e.Parameters.Count != 3)
                {
                    player.SendErrorMessage($"Invalid syntax! Proper syntax: {Commands.Specifier}us prefix <player name> <prefix>");
                    return;
                }

                var username = e.Parameters[1];
                var user = TShock.UserAccounts.GetUserAccountByName(username);
                if (user == null)
                {
                    player.SendErrorMessage($"Couldn't find any users under the name of '{username}'.");
                    return;
                }
                if (user.Name != player.Account?.Name && !e.Player.HasPermission("us.setother"))
                {
                    e.Player.SendErrorMessage("You do not have permission to modify another user's chat data.");
                    return;
                }

                e.Parameters.RemoveRange(0, 2);
                var prefix = string.Join(" ", e.Parameters);
                if (prefix.Length > _config.MaximumPrefixLength)
                {
                    player.SendErrorMessage($"The prefix cannot contain more than {_config.MaximumPrefixLength} characters.");
                    return;
                }

                var target = _database.Get(user);
                if (target == null)
                {
                    target = new PlayerMetadata(user.ID, new ChatInformation(prefix), new PermissionCollection());
                    _database.Add(target);
                }
                else
                {
                    target.ChatData.Prefix = prefix;
                    _database.Update(target);
                }

                player.SendSuccessMessage($"Successfully set {user.Name}'s prefix.");
            }
            else if (command.Equals("read", StringComparison.CurrentCultureIgnoreCase))
            {
                if (e.Parameters.Count != 2)
                {
                    player.SendErrorMessage($"Invalid syntax! Proper syntax: {Commands.Specifier}us read <player name>");
                    return;
                }

                var username = e.Parameters[1];
                var user = TShock.UserAccounts.GetUserAccountByName(username);
                if (user == null)
                {
                    player.SendErrorMessage($"Couldn't find any users under the name of '{username}'.");
                    return;
                }

                var target = _database.Get(user);
                if (target == null)
                {
                    player.SendErrorMessage("This user has no chat data to display.");
                    return;
                }

                player.SendInfoMessage($"Username: {user.Name}");
                player.SendMessage($"  * Prefix: {target.ChatData.Prefix ?? "None"}", Color.LawnGreen);
                player.SendMessage($"  * Suffix: {target.ChatData.Suffix ?? "None"}", Color.LawnGreen);
                player.SendMessage($"  * Chat color: {target.ChatData.Color ?? "None"}", Color.LawnGreen);
            }
            else if (command.Equals("remove", StringComparison.CurrentCultureIgnoreCase))
            {
                if (e.Parameters.Count != 3)
                {
                    player.SendErrorMessage($"Invalid syntax! Proper syntax: {Commands.Specifier}us remove <player name> <prefix/suffix/color/all>");
                    return;
                }

                var username = e.Parameters[1];
                var user = TShock.UserAccounts.GetUserAccountByName(username);
                if (user == null)
                {
                    player.SendErrorMessage($"Couldn't find any users under the name of '{username}'.");
                    return;
                }
                if (user.Name != player.Account?.Name && !player.HasPermission("us.setother"))
                {
                    player.SendErrorMessage("You do not have permission to modify another user's chat data.");
                    return;
                }

                var target = _database.Get(user);
                if (target == null)
                {
                    player.SendErrorMessage($"No information found for user '{user.Name}'.");
                    return;
                }

                var inputOption = e.Parameters[2];
                switch (inputOption.ToLowerInvariant())
                {
                    case "all":
                        if (!player.HasPermission("us.resetall"))
                        {
                            player.SendErrorMessage("You do not have access to this command.");
                            return;
                        }

                        target.ChatData = new ChatInformation();
                        player.SendSuccessMessage("Reset successful.");
                        break;
                    case "color":
                        if (!player.HasPermission("us.remove.color"))
                        {
                            player.SendErrorMessage("You do not have access to this command.");
                            return;
                        }

                        target.ChatData.Color = null;
                        player.SendSuccessMessage($"Modified {user.Name}'s chat color successfully.");
                        break;
                    case "prefix":
                        if (!player.HasPermission("us.remove.prefix"))
                        {
                            player.SendErrorMessage("You do not have access to this command.");
                            return;
                        }

                        target.ChatData.Prefix = null;
                        player.SendSuccessMessage($"Modified {user.Name}'s chat prefix successfully.");
                        break;
                    case "suffix":
                        if (!player.HasPermission("us.remove.suffix"))
                        {
                            player.SendErrorMessage("You do not have access to this command.");
                            return;
                        }

                        target.ChatData.Suffix = null;
                        player.SendSuccessMessage($"Modified {user.Name}'s chat suffix successfully.");
                        break;
                    default:
                        player.SendErrorMessage("Invalid option!");
                        break;
                }
                _database.Update(target);
            }
            else if (command.Equals("suffix", StringComparison.CurrentCultureIgnoreCase))
            {
                if (e.Parameters.Count != 3)
                {
                    player.SendErrorMessage($"Invalid syntax! Proper syntax: {Commands.Specifier}us suffix <player name> <suffix>");
                    return;
                }

                var username = e.Parameters[1];
                var user = TShock.UserAccounts.GetUserAccountByName(username);
                if (user == null)
                {
                    player.SendErrorMessage($"Couldn't find any users under the name of '{username}'.");
                    return;
                }
                if (user.Name != player.Account.Name && !player.HasPermission("us.setother"))
                {
                    player.SendErrorMessage("You do not have permission to modify another user's chat data.");
                    return;
                }

                e.Parameters.RemoveRange(0, 2);
                var suffix = string.Join(" ", e.Parameters);
                if (suffix.Length > _config.MaximumSuffixLength)
                {
                    player.SendErrorMessage($"The suffix cannot contain more than {_config.MaximumSuffixLength} characters.");
                    return;
                }

                var target = _database.Get(user);
                if (target == null)
                {
                    target = new PlayerMetadata(user.ID, new ChatInformation(suffix: suffix), new PermissionCollection());
                    _database.Add(target);
                }
                else
                {
                    target.ChatData.Suffix = suffix;
                    _database.Update(target);
                }

                player.SendSuccessMessage($"Successfully set {user.Name}'s suffix.");
            }
            else
            {
                player.SendErrorMessage($"Invalid sub-command! {Commands.Specifier}us <prefix/suffix/color/read/remove>");
            }
        }

        private void PermissionCommand(CommandArgs e)
        {
            var player = e.Player;
            if (e.Parameters.Count == 0)
            {
                player.SendInfoMessage("Available commands:");
                player.SendInfoMessage($"{Commands.Specifier}permission add <player name> <permissions>");
                player.SendInfoMessage($"{Commands.Specifier}permission remove <player name> <permissions>");
                player.SendInfoMessage($"{Commands.Specifier}permission list <player name> [page]");
                return;
            }

            var command = e.Parameters[0];
            if (command.Equals("add", StringComparison.CurrentCultureIgnoreCase))
            {
                if (e.Parameters.Count < 3)
                {
                    player.SendErrorMessage($"Invalid syntax! Proper syntax: {Commands.Specifier}permission add <player name> <permission1 permission2 permissionN>");
                    return;
                }

                var username = e.Parameters[1];
                var user = TShock.UserAccounts.GetUserAccountByName(username);
                if (user == null)
                {
                    player.SendErrorMessage($"Couldn't find any users under the name of '{username}'.");
                    return;
                }

                e.Parameters.RemoveRange(0, 2);
                var target = _database.Get(user);
                if (target == null)
                {
                    target = new PlayerMetadata(user.ID, new ChatInformation(), new PermissionCollection(e.Parameters));
                    _database.Add(target);
                }
                else
                {
                    e.Parameters.ForEach(p => target.Permissions.Add(p));
                    _database.Update(target);
                }

                player.SendSuccessMessage($"Modified {user.Name}'s permissions successfully.");
            }
            else if (command.Equals("list", StringComparison.CurrentCultureIgnoreCase))
            {
                if (e.Parameters.Count != 2)
                {
                    player.SendErrorMessage($"Invalid syntax! Proper syntax: {Commands.Specifier}permission list <player name>");
                    return;
                }

                var username = e.Parameters[1];
                var user = TShock.UserAccounts.GetUserAccountByName(username);
                if (user == null)
                {
                    player.SendErrorMessage($"Couldn't find any users under the name of '{username}'.");
                    return;
                }

                var target = _database.Get(user);
                if (target == null || target.Permissions.Count == 0)
                {
                    player.SendInfoMessage("This player has no permissions to list.");
                    return;
                }

                player.SendInfoMessage($"{user.Name}'s permissions: {target.Permissions}");
            }
            else if (command.Equals("remove", StringComparison.CurrentCultureIgnoreCase))
            {
                if (e.Parameters.Count < 3)
                {
                    player.SendErrorMessage($"Invalid syntax! Proper syntax: {Commands.Specifier}permission remove <player name> <permission1 permission2 permissionN>");
                    return;
                }

                var username = e.Parameters[1];
                var user = TShock.UserAccounts.GetUserAccountByName(username);
                if (user == null)
                {
                    player.SendErrorMessage($"Couldn't find any users under the name of '{username}'.");
                    return;
                }

                e.Parameters.RemoveRange(0, 2);
                var target = _database.Get(user);
                if (target == null || target.Permissions.Count == 0)
                {
                    player.SendInfoMessage("This user has no permissions to remove.");
                    return;
                }

                e.Parameters.ForEach(p => target.Permissions.Remove(p));
                _database.Update(target);
                player.SendSuccessMessage($"Modified {user.Name}'s permissions successfully.");
            }
            else
            {
                player.SendErrorMessage($"Invalid sub-command! {Commands.Specifier}permission <add/remove/list>");
            }
        }

        private void ThirdPerson(CommandArgs args)
        {
            string msg = string.Join(" ", args.Parameters);
            TSPlayer plr = args.Player;
            if (string.IsNullOrWhiteSpace(msg))
            {
                plr.SendErrorMessage("Invalid syntax! Proper syntax: /me <text>");
                return;
            }

            if (plr.mute)
            {
                plr.SendErrorMessage("You are muted!");
            }
            else
            {
                if (!plr.HasPermission("antispam.ignore"))
                {
                    msg = RemoveTags(msg);
                    if ((double)msg.Count(Char.IsUpper) / msg.Length >= _config.CapsRatio)
                    {
                        msg = msg.ToLower();
                    }
                }
                TSPlayer.All.SendMessage(string.Format("*{0} {1}", plr.Name, msg), 205, 133, 63);
            }
        }

        private void Whisper(CommandArgs args)
        {
            if (args.Parameters.Count < 2)
            {
                args.Player.SendErrorMessage("Invalid syntax! Proper syntax: /whisper <player> <text>");
                return;
            }

            var players = TSPlayer.FindByNameOrID(args.Parameters[0]);
            if (players.Count == 0)
            {
                args.Player.SendErrorMessage("Invalid player!");
            }
            else if (players.Count > 1)
            {
                args.Player.SendMultipleMatchError(players.Select(p => p.Name));
            }
            else if (args.Player.mute)
            {
                args.Player.SendErrorMessage("You are muted.");
            }
            else
            {
                var plr = players[0];
                var msg = string.Join(" ", args.Parameters.ToArray(), 1, args.Parameters.Count - 1);
                if (!args.Player.HasPermission("antispam.ignore"))
                {
                    msg = RemoveTags(msg);
                    if ((double)msg.Count(Char.IsUpper) / msg.Length >= _config.CapsRatio)
                    {
                        msg = msg.ToLower();
                    }
                }
                plr.SendMessage(String.Format("<From {0}> {1}", args.Player.Name, msg), Color.MediumPurple);
                args.Player.SendMessage(String.Format("<To {0}> {1}", plr.Name, msg), Color.MediumPurple);
                plr.LastWhisper = args.Player;
                args.Player.LastWhisper = plr;
            }
        }

        private void Reply(CommandArgs args)
        {
            if (args.Player.mute)
            {
                args.Player.SendErrorMessage("You are muted.");
            }
            else if (args.Player.LastWhisper != null)
            {
                var msg = string.Join(" ", args.Parameters);
                if (!args.Player.HasPermission("antispam.ignore"))
                {
                    msg = RemoveTags(msg);
                    if ((double)msg.Count(Char.IsUpper) / msg.Length >= _config.CapsRatio)
                    {
                        msg = msg.ToLower();
                    }
                }
                args.Player.LastWhisper.SendMessage(String.Format("<From {0}> {1}", args.Player.Name, msg), Color.MediumPurple);
                args.Player.SendMessage(String.Format("<To {0}> {1}", args.Player.LastWhisper.Name, msg), Color.MediumPurple);
            }
            else
            {
                args.Player.SendErrorMessage("You haven't previously received any whispers. Please use /whisper to whisper to other people.");
            }
        }
    }
}