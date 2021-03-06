﻿using Kiwana.Config;
using Kiwana.Objects;
using Kiwana.Api;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Kiwana.Plugins;
using System.Xml.Serialization;
using System.Xml;
using Kiwana.Api.Config;
using System.Threading;
using System.Xml.Schema;

namespace Kiwana
{
    public class Client
    {
        public bool Initialized = false;
        public bool Running = true;

        private TcpClient _ircConnection;
        private BotConfig _config;
        private NetworkStream _networkStream;
        private StreamReader _streamReader;
        private StreamWriter _streamWriter;

        private List<PluginInformation> _plugins = new List<PluginInformation>();

        public Dictionary<string, Channel> Channels = new Dictionary<string, Channel>();
        public Dictionary<string, User> Users = new Dictionary<string, User>();

        private Regex _prefixRegex;
        private Regex _serverCommandRegex;
        private Regex _consoleCommandRegex;

        private Random random = new Random();

        public DateTime LastSend = new DateTime();

        public Client(string config)
        {
            XmlSerializer serializer = new XmlSerializer(typeof(BotConfig));

            XmlSchema schema = new XmlSchema();
            schema.SourceUri = "Config/BotConfig.xsd";

            XmlReader reader = XmlReader.Create(config);
            reader.Settings.Schemas.Add(schema);

            _config = (BotConfig)serializer.Deserialize(reader);
        }

        public void Init()
        {
            foreach (string pluginFolder in _config.PluginFolders)
            {
                _plugins.AddRange(PluginManager.ScanPluginFolder(pluginFolder));
            }

            Console.WriteLine("Initializing Plugins ...");
            foreach (PluginInformation plugin in _plugins)
            {
                Console.Write("  - " + plugin.Name + " ... ");
                try
                {
                    plugin.Instance.Init();
                    Console.WriteLine("OK");
                }
                catch (Exception e)
                {
                    Console.WriteLine("Failure");
                    throw new PluginInitializationFailedException("Plugin " + plugin.Name + " failed initalization.", e);
                }
            }

            _prefixRegex = new Regex(@"(?<=" + Util.JoinStringList(_config.Prefixes, "|") + ").+");

            //Add all the listeners to the NewLine event
            NewLine += _handleLine;
            foreach (PluginInformation plugin in _plugins)
            {
                _config.Commands.AddRange(plugin.Config.Commands);
                plugin.Instance.SendDataEvent += SendData;
                NewLine += plugin.Instance.HandleLine;
            }

            string serverCommandRegex = @"";
            string consoleCommandRegex = @"";
            for (int i = 0; i < _config.Commands.Count; i++)
            {
                Command command = _config.Commands[i];

                if (command.ConsoleServer == ConsoleServer.Server || command.ConsoleServer == ConsoleServer.Both)
                {
                    serverCommandRegex += command.Name + "|";
                    string alias = Util.JoinStringList(command.Alias, "|");
                    if (!String.IsNullOrEmpty(alias))
                    {
                        serverCommandRegex += alias;
                        if (i < _config.Commands.Count - 1)
                        {
                            serverCommandRegex += "|";
                        }
                    }
                }

                if (command.ConsoleServer == ConsoleServer.Console || command.ConsoleServer == ConsoleServer.Both)
                {
                    consoleCommandRegex += command.Name + "|";
                    string alias = Util.JoinStringList(command.Alias, "|");
                    if (!String.IsNullOrEmpty(alias))
                    {
                        consoleCommandRegex += alias;
                        if (i < _config.Commands.Count - 1)
                        {
                            consoleCommandRegex += "|";
                        }
                    }
                }
            }
            _serverCommandRegex = new Regex(serverCommandRegex);
            _consoleCommandRegex = new Regex(consoleCommandRegex);

            Initialized = true;
        }

        public void SendData(string command, string argument = "")
        {
            TimeSpan sinceLastSend = new DateTime() - LastSend;
            if (sinceLastSend.TotalMilliseconds < _config.MessageInterval)
            {
                Thread.Sleep((int)_config.MessageInterval - (int)sinceLastSend.TotalMilliseconds);
            }

            if (argument == "")
            {
                _streamWriter.WriteLine(command);
                _streamWriter.Flush();
            }
            else
            {
                _streamWriter.WriteLine(command + " " + argument);
                _streamWriter.Flush();
            }

            Console.WriteLine(command + " " + argument);
            LastSend = new DateTime();
        }

        public async Task Work()
        {
            if (!Initialized)
            {
                Init();
            }

            Console.WriteLine("Trying to establish Connection to " + _config.Server.Url + ":" + _config.Server.Port + " ... ");
            try
            {
                _ircConnection = new TcpClient(_config.Server.Url, _config.Server.Port);
                _networkStream = _ircConnection.GetStream();
                _streamReader = new StreamReader(_networkStream);
                _streamWriter = new StreamWriter(_networkStream);

                SendData("PASS", _config.Server.Login.Password);
                SendData("NICK", _config.Server.Login.Name);
                SendData("USER", _config.Server.Login.Nick + " Owner Banane9 :" + _config.Server.Login.Name);

                Console.WriteLine("Success");
                Running = true;
            }
            catch
            {
                Console.WriteLine("Failure");
                Running = false;
            }
            
            while(Running)
            {
                try
                {
                    ParseLine(_streamReader.ReadLine());
                }
                catch (Exception ex)
                {
                    Console.WriteLine("Exception " + ex.Message);
                    Console.WriteLine(ex.StackTrace);
                }
            }
        }

        public string GetNormalizedCommand(string cmd, bool console)
        {
            string command = "";

            //Is it a valid command from the console or from the server
            if ((_consoleCommandRegex.IsMatch(cmd) && console) || (_serverCommandRegex.IsMatch(cmd) && !console))
            {
                foreach (Command commandToCheck in _config.Commands)
                {
                    if (cmd == commandToCheck.Name)
                    {
                        command = cmd;
                        break;
                    }

                    string regexString = "^(" + Util.JoinStringList(commandToCheck.Alias, "|") + ")$";

                    if (!String.IsNullOrEmpty(regexString))
                    {
                        Regex regex = new Regex(regexString);
                        if (regex.IsMatch(cmd))
                        {
                            command = regex.Replace(cmd, commandToCheck.Name);
                        }

                        if (!String.IsNullOrEmpty(command)) break;
                    }
                }
            }

            return command;
        }

        public void ParseLine(string line, bool console = false)
        {
            //Console.WriteLine(line);
            List<string> ex = line.Split(' ').ToList();

            if (ex.Count < 2) return;

            if (!console)
            {
                if (ex.Count == 2)
                {
                    if (ex[0] == "PING")
                    {
                        Console.WriteLine("PING " + ex[1]);
                        SendData("PONG", ex[1]);
                    }
                }

                if (ex.Count > 6)
                {
                    if (Util.ServerRegex.IsMatch(ex[0]))
                    {
                        if (ex[3] == "=")
                        {
                            //If the bot is in the channel
                            if (Channels.ContainsKey(ex[4].ToLower()))
                            {
                                Console.WriteLine("Users in " + ex[4] + ": " + Util.MessageRegex.Match(ex[5]).Value + ", " + Util.JoinStringList(ex, ", ", 6));

                                //List for the Users in the Channel
                                List<string> userList = new List<string>(ex.Count - 6);

                                //Add the usernames from the list; first name needs to get the colon at the start stripped.
                                userList.Add(Util.MessageRegex.Match(ex[5]).Value);
                                foreach (string userName in ex.GetRange(6, ex.Count - 6))
                                {
                                    userList.Add(userName);
                                }

                                //Set it in the dictionary
                                Channels[ex[4].ToLower()].Users = userList;
                            }
                        }
                    }
                }

                if (ex.Count > 5)
                {
                    if (Util.HostMaskRegex.IsMatch(ex[4]))
                    {
                        if (Channels.ContainsKey(ex[3].ToLower()))
                        {
                            string motdSetter = Util.NickRegex.Match(ex[4]).Value;
                            Channels[ex[3].ToLower()].MotdSetter = motdSetter;
                            Channels[ex[3].ToLower()].MotdSetDate = Util.UnixToDateTime(long.Parse(ex[5]));

                            Console.WriteLine("Motd of " + ex[3] + " was set by " + motdSetter + " at " + Channels[ex[3].ToLower()].MotdSetDate.Hour + ":" + Channels[ex[3].ToLower()].MotdSetDate.Minute + " on " + Channels[ex[3].ToLower()].MotdSetDate.Day + "." + Channels[ex[3].ToLower()].MotdSetDate.Month + "." + Channels[ex[3].ToLower()].MotdSetDate.Year);
                        }
                    }
                    else if (ex[1] == "NOTICE" && ex[4] == "ACC")
                    {
                        if (Util.HostMaskRegex.Match(ex[0]).Value.ToLower() == _config.Permissions.Authenticator.HostMask.ToLower())
                        {
                            string nick = Util.MessageRegex.Match(ex[3]).Value;
                            //Status = ex[5]

                            if (Users[nick].AuthenticationRequested)
                            {
                                Users[nick].AuthenticationRequested = false;

                                if (ex[_config.Permissions.Authenticator.MessagePosition] == _config.Permissions.Authenticator.AuthenticationCode)
                                {
                                    Users[nick].Authenticated = true;

                                    foreach (UserGroup group in _config.Permissions.UserGroups)
                                    {
                                        if (group.Users.Contains(nick))
                                        {
                                            Users[nick].Rank = group.Rank;
                                            break;
                                        }
                                    }

                                    Console.WriteLine("User " + nick + " is authenticated and has rank [" + Users[nick].Rank + "].");
                                }
                                else
                                {
                                    Console.WriteLine("User " + nick + " is not authenticated.");
                                }
                            }
                        }
                    }
                }

                if (ex.Count > 4)
                {
                    if (Util.MessageRegex.IsMatch(ex[4]) && ex[4] != ":End")
                    {
                        if (Channels.ContainsKey(ex[3].ToLower()))
                        {
                            Channels[ex[3].ToLower()].Motd = Util.MessageRegex.Match(ex[4] + " " + Util.JoinStringList(ex, " ", 5)).Value;

                            Console.WriteLine("Motd of " + ex[3] + " is: " + Channels[ex[3].ToLower()].Motd);
                        }
                    }
                }

                if (ex.Count == 3)
                {
                    if (ex[1] == "JOIN")
                    {
                        if (Channels.ContainsKey(ex[2].ToLower()))
                        {
                            Channels[ex[2].ToLower()].Users.Add(Util.NickRegex.Match(ex[0]).Value);

                            Console.WriteLine(ex[2] + " " + Util.NickRegex.Match(ex[0]).Value + " joined the channel.");
                        }
                    }
                    else if (ex[1] == "NICK")
                    {
                        string oldNick = Util.NickRegex.Match(ex[0]).Value;
                        string newNick = Util.MessageRegex.Match(ex[2]).Value;

                        if (Users.ContainsKey(oldNick))
                        {
                            Users.Add(newNick, Users[oldNick]);
                            Users.Remove(oldNick);
                        }

                        foreach (string channel in Channels.Keys)
                        {
                            if (Channels[channel].Users.Contains(oldNick))
                            {
                                Channels[channel].Users.Add(newNick);
                                Channels[channel].Users.Remove(oldNick);
                            }
                        }
                    }
                }

                if (ex.Count > 2)
                {
                    if (ex[1] == "PART")
                    {
                        string channel = ex[2].ToLower();
                        if (Channels.ContainsKey(channel))
                        {
                            string nick = Util.NickRegex.Match(ex[0]).Value;
                            if (Channels[channel].Users.Contains(nick))
                            {
                                Channels[channel].Users.Remove(nick);

                                Console.WriteLine(ex[2] + " " + Util.NickRegex.Match(ex[0]).Value + " left the channel.");
                            }
                        }
                    }
                    else if (ex[1] == "QUIT")
                    {
                        foreach (string channel in Channels.Keys)
                        {
                            Channels[channel].Users.Remove(Util.NickRegex.Match(ex[0]).Value);
                        }

                        Console.WriteLine(Util.NickRegex.Match(ex[0]).Value + " left the server.");
                    }
                }

            }

            if (ex.Count > 3 || console)
            {
                string command = "";

                if (console)
                {
                    command = ex[0].ToLower();
                    ex.InsertRange(0, new string[] { "", "", "" });
                }
                else
                {
                    command = _prefixRegex.Match(ex[3]).Value.ToLower();
                }

                //Print input from server
                if (!console)
                {
                    if (Util.NickRegex.IsMatch(ex[0]))
                    {
                        Console.WriteLine(ex[2] + " <" + Util.NickRegex.Match(ex[0]) + "!" + Util.NameRegex.Match(ex[0]) + "> " + Util.MessageRegex.Match(ex[3]) + " " + Util.JoinStringList(ex, " ", 4));
                    }
                    else if (Util.MessageRegex.IsMatch(ex[3]))
                    {
                        Console.WriteLine(Util.MessageRegex.Match(ex[3]) + " " + Util.JoinStringList(ex, " ", 4));
                    }
                }

                if (Util.HostMaskRegex.IsMatch(ex[0]) || console)
                {
                    string normalizedCommand = GetNormalizedCommand(command, console);

                    if (ex[2] == _config.Server.Login.Nick)
                    {
                        ex[2] = Util.NickRegex.Match(ex[0]).Value;
                    }

                    string nick = Util.NickRegex.Match(ex[0]).Value;

                    if (!Users.ContainsKey(nick) && !console)
                    {
                        _requestAuthentication(nick);
                    }

                    bool authorized = false;

                    if (!string.IsNullOrEmpty(normalizedCommand) && !console)
                    {
                        Command c = _config.Commands.Single(cmd => cmd.Name == normalizedCommand);

                        authorized = Users[nick].Rank >= c.Rank;

                        if (!authorized)
                        {
                            SendData("PRIVMSG", ex[2] + " :" + nick + ": You aren't allowed to do this. Minimum rank is [" + c.Rank + "] while yours is only [" + Users[nick].Rank + "]. If this was your first message, try again shortly.");
                        }
                    }

                    NewLine(ex, normalizedCommand, console ? true : Users[nick].Authenticated, string.IsNullOrEmpty(normalizedCommand) ? true : console ? true : authorized, console);
                }
            }
        }

        private void _requestAuthentication(string nick)
        {
            if (!Users.ContainsKey(nick))
            {
                Users.Add(nick, new User());
            }

            Users[nick].AuthenticationRequested = true;

            //Request Authentication
            SendData("PRIVMSG", Util.NickRegex.Match(_config.Permissions.Authenticator.HostMask).Value + " :ACC " + nick);
        }

        private void _handleLine(List<string> ex, string command, bool userAuthenticated, bool userAuthorized, bool console)
        {
            if (userAuthorized)
            {
                if (ex.Count > 3)
                {
                    //Commands with arguments
                    if (ex.Count > 4)
                    {
                        switch (command)
                        {
                            case "join":
                                SendData("JOIN", Util.JoinStringList(ex, ",", 4));
                                foreach (string channel in ex.GetRange(4, ex.Count - 4))
                                {
                                    if (!Channels.ContainsKey(channel.ToLower()))
                                    {
                                        Channels.Add(channel.ToLower(), new Channel());
                                    }
                                }
                                break;
                            case "about":
                                SendData("PRIVMSG", ex[2] + " :" + _config.About);
                                break;
                            case "help":
                                string help = "Available commands are: ";
                                help += Util.JoinStringList(_config.Commands.Where(cmd => cmd.ConsoleServer == (console ? ConsoleServer.Console : ConsoleServer.Server) || cmd.ConsoleServer == ConsoleServer.Both).Select(cmd => cmd.Name).ToList(), ", ");
                                help += " . With prefixes: " + Util.JoinStringList(_config.Prefixes, ", ") + " .";
                                SendData("PRIVMSG", ex[2] + " :" + help.Replace("\\", ""));
                                break;
                            case "letmegooglethatforyou":
                                SendData("PRIVMSG", ex[2] + " :http://lmgtfy.com/?q=" + Util.JoinStringList(ex, "+", 4));
                                break;
                            case "nick":
                                SendData("NICK", Util.JoinStringList(ex, "_", 4));
                                _config.Server.Login.Nick = Util.JoinStringList(ex, "_", 4);
                                break;
                            case "part":
                                SendData("PART", Util.JoinStringList(ex, ",", 4));

                                foreach (string channel in ex.GetRange(4, ex.Count - 4))
                                {
                                    if (Channels.ContainsKey(channel))
                                    {
                                        Channels.Remove(channel);
                                    }
                                }
                                break;
                            case "quit":
                                Quit(Util.JoinStringList(ex, " ", 4));
                                break;
                        }
                    }
                    else //Commands without arguments
                    {
                        switch (command)
                        {
                            case "about":
                                SendData("PRIVMSG", ex[2] + " :" + _config.About);
                                break;
                            case "help":
                                string help = "Available commands are: ";
                                help += Util.JoinStringList(_config.Commands.Where(cmd => cmd.ConsoleServer == (console ? ConsoleServer.Console : ConsoleServer.Server) || cmd.ConsoleServer == ConsoleServer.Both).Select(cmd => cmd.Name).ToList(), ", ") + ", ";
                                foreach (PluginInformation plugin in _plugins)
                                {
                                    help += Util.JoinStringList(plugin.Config.Commands.Where(cmd => cmd.ConsoleServer == (console ? ConsoleServer.Console : ConsoleServer.Server) || cmd.ConsoleServer == ConsoleServer.Both).Select(cmd => cmd.Name).ToList(), ", ");
                                }
                                help += "; With prefixes: " + Util.JoinStringList(_config.Prefixes, ", ");
                                SendData("PRIVMSG", ex[2] + " :" + help.Replace("\\", ""));
                                break;
                            case "plugins":
                                string plugins = "Loaded plugins: ";
                                plugins += Util.JoinStringList(_plugins.Select(plugin => plugin.Name).ToList(), ", ");
                                SendData("PRIVMSG", ex[2] + " :" + plugins + ".");
                                break;
                            case "part":
                                SendData("PART", ex[2]);

                                if (Channels.ContainsKey(ex[2]))
                                {
                                    Channels.Remove(ex[2]);
                                }
                                break;
                            case "quit":
                                Quit();
                                break;
                        }
                    }
                }
            }
        }

        private void Quit(string message = "")
        {
            Running = false;

            if (string.IsNullOrEmpty(message))
            {
                SendData("QUIT", ":" + _config.QuitMessages[new Random().Next(0, _config.QuitMessages.Count - 1)]);
            }
            else
            {
                SendData("QUIT", ":" + message);
            }

            Console.WriteLine("Disabling plugins ...");
            foreach (Kiwana.Plugins.PluginInformation plugin in _plugins)
            {
                Console.Write("  - " + plugin.Name + " ... ");
                plugin.Instance.Disable();
                Console.WriteLine("OK");
            }

            Console.WriteLine("Bot stopped.");
        }

        public delegate void NewLineHandler(List<string> ex, string command, bool userAuthenticated, bool userAuthorized, bool console);

        public event NewLineHandler NewLine;
    }
}
