﻿using System;
using System.IO;
using System.Text;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Collections.Generic;
using System.Text.RegularExpressions;

using TeamSpeak3QueryApi.Net.Specialized;
using TeamSpeak3QueryApi.Net.Specialized.Responses;
using TeamSpeak3QueryApi.Net.Specialized.Notifications;

namespace TS3AudioBot
{
	public sealed class MainBot : IDisposable
	{
		static void Main(string[] args)
		{
			using (MainBot bot = new MainBot())
			{
				if (!bot.ReadParameter(args))
				{
					bot.InitializeBot();
					bot.InitializeCommands();
					bot.Run();
				}
			}
		}

		bool run;
		bool noInput;
		bool silent;
		bool noLog;
		MainBotData mainBotData;
		Dictionary<string, BotCommand> commandDict;

		AudioFramework audioFramework;
		BobController bobController;
		QueryConnection queryConnection;
		SessionManager sessionManager;
		YoutubeFramework youtubeFramework;

		public MainBot()
		{
			run = true;
			noInput = false;
			silent = false;
			noLog = false;
			commandDict = new Dictionary<string, BotCommand>();
		}

		public bool ReadParameter(string[] args)
		{
			HashSet<string> launchParameter = new HashSet<string>();
			foreach (string parameter in args)
				launchParameter.Add(parameter);
			if (launchParameter.Contains("--help") || launchParameter.Contains("-h"))
			{
				Console.WriteLine(" --NoInput -I     Deactivates reading from stdin to enable background running.");
				Console.WriteLine(" --Silent -S      Deactivates all output to stdout.");
				Console.WriteLine(" --NoLog -L       Deactivates writing to the logfile.");
				Console.WriteLine(" --help -h        Prints this help....");
				return true;
			}
			noInput = launchParameter.Contains("--NoInput") || launchParameter.Contains("-I");
			silent = launchParameter.Contains("--Silent") || launchParameter.Contains("-S");
			noLog = launchParameter.Contains("--NoLog") || launchParameter.Contains("-L");

			if (!silent)
			{
				Log.OnLog += (o, e) => { Console.WriteLine(e.InfoMessage); };
			}

			if (!noLog)
			{
				Log.OnLog += (o, e) => { File.AppendAllText(mainBotData.logFile, e.DetailedMessage, Encoding.UTF8); };
			}
			return false;
		}

		public void InitializeBot()
		{
			// Read Config File
			string configFilePath = Util.GetFilePath(FilePath.ConfigFile);
			ConfigFile cfgFile = ConfigFile.Open(configFilePath) ?? ConfigFile.Create(configFilePath) ?? ConfigFile.GetDummy();
			AudioFrameworkData afd = cfgFile.GetDataStruct<AudioFrameworkData>(typeof(AudioFramework), true);
			BobControllerData bcd = cfgFile.GetDataStruct<BobControllerData>(typeof(BobController), true);
			QueryConnectionData qcd = cfgFile.GetDataStruct<QueryConnectionData>(typeof(QueryConnection), true);
			mainBotData = cfgFile.GetDataStruct<MainBotData>(typeof(MainBot), true);
			cfgFile.Close();

			// Initialize Modules
			audioFramework = new AudioFramework(afd);
			bobController = new BobController(bcd);
			queryConnection = new QueryConnection(qcd);
			sessionManager = new SessionManager();
			youtubeFramework = new YoutubeFramework();

			audioFramework.OnRessourceStarted += (audioRessource) =>
			{
				bobController.Start();
				bobController.Sending = true;
			};
			audioFramework.OnRessourceStopped += () =>
			{
				bobController.StartEndTimer();
				bobController.Sending = false;
			};

			queryConnection.OnMessageReceived += TextCallback;
			bobController.QueryConnection = queryConnection;
			sessionManager.defaultSession = new PublicSession(queryConnection);

			var connectTask = queryConnection.Connect();
		}

		public void InitializeCommands()
		{
			BotCommand tmp;

			commandDict.Add("add", new BotCommand(CommandRights.Private, CommandAdd));
			commandDict.Add("clear", new BotCommand(CommandRights.Admin, CommandClear));
			commandDict.Add("help", new BotCommand(CommandRights.AnyVisibility, CommandHelp));
			commandDict.Add("history", new BotCommand(CommandRights.Private, CommandHistory));
			commandDict.Add("kickme", new BotCommand(CommandRights.Private, CommandKickme));
			tmp = new BotCommand(CommandRights.Private, CommandLink);
			commandDict.Add("link", tmp);
			commandDict.Add("l", tmp);
			tmp = null;
			commandDict.Add("loop", new BotCommand(CommandRights.Private, CommandLoop));
			commandDict.Add("next", new BotCommand(CommandRights.Private, CommandNext));
			commandDict.Add("pm", new BotCommand(CommandRights.Public, CommandPM));
			tmp = new BotCommand(CommandRights.Private, CommandPlay);
			commandDict.Add("play", tmp);
			commandDict.Add("p", tmp);
			tmp = null;
			commandDict.Add("prev", new BotCommand(CommandRights.Private, CommandPrev));
			commandDict.Add("quit", new BotCommand(CommandRights.Admin, CommandQuit));
			commandDict.Add("repeat", new BotCommand(CommandRights.Private, CommandRepeat));
			commandDict.Add("seek", new BotCommand(CommandRights.Private, CommandSeek));
			commandDict.Add("stop", new BotCommand(CommandRights.Private, CommandStop));
			commandDict.Add("volume", new BotCommand(CommandRights.AnyVisibility, CommandVolume));
			tmp = new BotCommand(CommandRights.Private, CommandYoutube);
			commandDict.Add("youtube", tmp);
			commandDict.Add("yt", tmp);
			tmp = null;
		}

		public void Run()
		{
			while (run)
			{
				if (noInput)
				{
					Task.Delay(1000).Wait();
				}
				else
				{
					ReadConsole();
				}
			}
		}

		private void ReadConsole()
		{
			string input;
			try
			{
				input = Console.ReadLine();
			}
			catch
			{
				Task.Delay(1000).Wait();
				return;
			}
			if (input == null)
			{
				Task.Delay(1000).Wait();
				return;
			}
			if (input == "quit")
			{
				run = false;
				return;
			}
		}

		public void TextCallback(object sender, TextMessage textMessage)
		{
			Log.Write(Log.Level.Debug, "MB Got message from {0}: {1}", textMessage.InvokerName, textMessage.Message);

			if (!textMessage.Message.StartsWith("!"))
				return;
			bobController.HasUpdate();

			BotSession session = sessionManager.GetSession(textMessage);

			string commandSubstring = textMessage.Message.Substring(1);
			string[] commandSplit = commandSubstring.Split(new[] { ' ' }, 2);
			BotCommand command = null;
			if (!commandDict.TryGetValue(commandSplit[0], out command))
			{
				session.Write("Unknown command!");
				return;
			}

			// check if we need admin rights
			if (true)// admin -> false | else -> true
			{
				// check if the command must be written in a private/public session
				switch (command.CommandRights)
				{
				case CommandRights.Public:
					if (session.IsPrivate)
					{
						session.Write("Command must be used in public mode!");
						return;
					}
					break;
				case CommandRights.Private:
					if (!session.IsPrivate)
					{
						session.Write("Command must be used in a private session!");
						return;
					}
					break;
				}
			}

			// check if the user has an open request
			if (session.responseProcessor != null)
			{
				if (session.responseProcessor(session, textMessage))
				{
					session.responseProcessor = null;
					return;
				}
			}

			switch (command.CommandParameter)
			{
			case CommandParameter.Nothing:
				command.CommandN(session);
				break;

			case CommandParameter.Remainder:
				if (commandSplit.Length < 2)
					command.CommandS(session, string.Empty);
				else
					command.CommandS(session, commandSplit[1]);
				break;

			case CommandParameter.TextMessage:
				command.CommandTM(session, textMessage);
				break;

			case CommandParameter.Undefined:
			default:
				break;
			}
		}

		private void CommandAdd(BotSession session, string parameter)
		{
			PlayAuto(session, parameter, true);
		}

		private void CommandClear(BotSession session, string parameter)
		{
			audioFramework.Clear();
		}

		private void CommandHelp(BotSession session)
		{
			//TODO rework to use the new sysytem
			// add a description to each command (+ in command class)
			session.Write("\n" +
				"!pm: Get private audience with the AudioBot\n" +
				"!kickme: Does exactly what you think it does...\n" +
				"!play: Plays any file or media/youtube url [p]\n" +
				"!youtube: Plays a video from youtube [yt]\n" +
				"!link: Plays any media from the server [vlocal, vl]\n" +
				"!stop: Stops the current song\n" +
				"!startbot: Connects the MusicBot to TeamSpeak\n" +
				"!stopbot: Disconnects the MusicBot from TeamSpeak\n" +
				"!history: Shows you the last played songs\n");
		}

		private void CommandHistory(BotSession session, string parameter)
		{
			//TODO
		}

		private async void CommandKickme(BotSession session, TextMessage textMessage)
		{
			try
			{
				await queryConnection.TSClient.KickClient(textMessage.InvokerId, KickOrigin.Channel);
				// TODO determine furter stuff here
				//await queryConnection.TSClient.KickClient(textMessage.InvokerId, KickOrigin.Server);
			}
			catch (Exception ex)
			{
				Log.Write(Log.Level.Info, "Could not kick: {0}", ex);
			}
		}

		private void CommandLink(BotSession session, string parameter)
		{
			PlayLink(session, parameter, false);
		}

		private void CommandLoop(BotSession session, string parameter)
		{
			if (parameter == "on")
				audioFramework.Loop = true;
			else if (parameter == "off")
				audioFramework.Loop = false;
			else
				session.Write("Unkown parameter. Usage !loop (on|off)");
		}

		private void CommandNext(BotSession session)
		{
			audioFramework.Next();
		}

		private async void CommandPM(BotSession session, TextMessage textMessage)
		{
			BotSession ownSession = await sessionManager.CreateSession(queryConnection, textMessage);
			ownSession.Write("Hi " + textMessage.InvokerName);
		}

		private void CommandPlay(BotSession session, string parameter)
		{
			if (string.IsNullOrEmpty(parameter))
				audioFramework.Play();
			else
				PlayAuto(session, parameter, false);
		}

		private void CommandPrev(BotSession session, string parameter)
		{
			audioFramework.Previous();
		}

		private void CommandQuit(BotSession session)
		{
			//TODO
			Log.Write(Log.Level.Info, "Exiting...");
		}

		private void CommandRepeat(BotSession session, string parameter)
		{
			if (parameter == "on")
				audioFramework.Repeat = true;
			else if (parameter == "off")
				audioFramework.Repeat = false;
			else
				session.Write("Unkown parameter. Usage !repeat (on|off)");
		}

		private void CommandSeek(BotSession session, string parameter)
		{
			int seconds = -1;
			bool parsed = false;
			if (parameter.Contains(":"))
			{
				string[] splittime = parameter.Split(':');
				if (splittime.Length == 2)
				{
					int minutes = -1;
					parsed = int.TryParse(splittime[0], out minutes) && int.TryParse(splittime[1], out seconds);
					if (parsed)
					{
						seconds = (int)(TimeSpan.FromSeconds(seconds) + TimeSpan.FromMinutes(minutes)).TotalSeconds;
					}
				}
			}
			else
			{
				parsed = int.TryParse(parameter, out seconds);
			}

			if (!parsed)
			{
				session.Write("The parameter is not valid. Usage !seek [<min>:]<sek>");
				return;
			}

			if (!audioFramework.Seek(seconds))
				session.Write("The point of time is not within the songlenth.");
		}

		private void CommandStop(BotSession session, string parameter)
		{
			audioFramework.Stop();
		}

		private void CommandVolume(BotSession session, string parameter)
		{
			int volume;
			if (int.TryParse(parameter, out volume) && (volume >= 0 && volume <= AudioFramework.MAXVOLUME))
				audioFramework.Volume = volume;
			else
				session.Write("The parameter is not valid. Usage !volume <int>(0-200)");
		}

		private void CommandYoutube(BotSession session, string parameter)
		{
			PlayYoutube(session, parameter, false);
		}

		private string ExtractUrlFromBB(string ts3link)
		{
			if (ts3link.Contains("[URL]"))
				return Regex.Match(ts3link, @"\[URL\](.+?)\[\/URL\]").Groups[1].Value;
			else
				return ts3link;
		}

		private void PlayAuto(BotSession session, string message, bool enqueue)
		{
			string netlinkurl = ExtractUrlFromBB(message);
			if (Regex.IsMatch(netlinkurl, @"^(https?\:\/\/)?(www\.)?(youtube\.|youtu\.be)"))
			{
				//Is a youtube link
				PlayYoutube(session, message, enqueue);
			}
			else
			{
				//Is any media link
				PlayLink(session, message, enqueue);
			}
		}

		private void PlayLink(BotSession session, string message, bool enqueue)
		{
			string netlinkurl = ExtractUrlFromBB(message);
			var mediaRessource = new MediaRessource(netlinkurl, netlinkurl); // TODO better media-name
			mediaRessource.Enqueue = enqueue;
			if (!audioFramework.StartRessource(mediaRessource))
				session.Write("The ressource could not be played...");
		}

		private void PlayYoutube(BotSession session, string message, bool enqueue)
		{
			string netlinkurl = ExtractUrlFromBB(message);
			// TODO: lookup in history...
			YoutubeRessource youtubeRessource = null;
			if (youtubeFramework.ExtractURL(netlinkurl, out youtubeRessource) != ResultCode.Success)
			{
				session.Write("Invalid URL or no media found...");
				return;
			}
			youtubeRessource.Enqueue = enqueue;

			if (youtubeRessource.AvailableTypes.Count == 1)
			{
				if (!audioFramework.StartRessource(youtubeRessource))
					session.Write("The ressource could not be played...");
				return;
			}

			StringBuilder strb = new StringBuilder();
			strb.AppendLine("\nMultiple formats found please choose one with !f <number>");
			int count = 0;
			foreach (var videoType in youtubeRessource.AvailableTypes)
			{
				strb.Append("[");
				strb.Append(count++);
				strb.Append("] ");
				strb.Append(videoType.codec.ToString());
				strb.Append(" @ ");
				strb.AppendLine(videoType.qualitydesciption);
			}
			session.Write(strb.ToString());
			session.responseProcessor = ResponseYoutube;
		}

		private bool ResponseYoutube(BotSession session, TextMessage tm)
		{
			string[] command = tm.Message.Split(' ');
			if (command[0] != "!f")
				return false;
			if (command.Length != 2)
				return true;
			int entry;
			if (int.TryParse(command[1], out entry))
			{
				YoutubeRessource ytRessource = (YoutubeRessource)session.responseData;
				if (entry < 0 || entry >= ytRessource.AvailableTypes.Count)
					return true;
				ytRessource.Selected = entry;
				if (!audioFramework.StartRessource(ytRessource))
					session.Write("The youtube stream could not be played...");
			}
			return true;
		}

		public void Dispose()
		{
			run = false;
			if (audioFramework != null)
			{
				audioFramework.Dispose();
				audioFramework = null;
			}
			if (bobController != null)
			{
				bobController.Dispose();
				bobController = null;
			}
			if (queryConnection != null)
			{
				queryConnection.Dispose();
				queryConnection = null;
			}
			if (youtubeFramework != null)
			{
				youtubeFramework.Dispose();
				youtubeFramework = null;
			}
			if (sessionManager != null)
			{
				//sessionManager.Dispose();
				sessionManager = null;
			}
		}
	}

	class BotCommand
	{
		public Action<BotSession> CommandN { get; private set; }
		public Action<BotSession, string> CommandS { get; private set; }
		public Action<BotSession, TextMessage> CommandTM { get; private set; }
		public CommandParameter CommandParameter { get; private set; }

		public CommandRights CommandRights { get; private set; }

		private BotCommand(CommandRights commandRights)
		{
			CommandRights = commandRights;
			CommandParameter = CommandParameter.Undefined;
		}

		public BotCommand(CommandRights commandRights,
						  Action<BotSession> command)
			: this(commandRights)
		{
			CommandN = command;
			CommandParameter = CommandParameter.Nothing;
		}

		public BotCommand(CommandRights commandRights,
						  Action<BotSession, string> command)
			: this(commandRights)
		{
			CommandS = command;
			CommandParameter = CommandParameter.Remainder;
		}

		public BotCommand(CommandRights commandRights,
						  Action<BotSession, TextMessage> command)
			: this(commandRights)
		{
			CommandTM = command;
			CommandParameter = CommandParameter.TextMessage;
		}
	}

	enum CommandParameter
	{
		Undefined,
		Nothing,
		Remainder,
		TextMessage,
	}

	[Flags]
	enum CommandRights
	{
		Admin = 0,
		Public = 1 << 0,
		Private = 1 << 1,
		AnyVisibility = Public | Private,
	}

	struct MainBotData
	{
		[InfoAttribute("path to the logfile", "log_ts3audiobot")]
		public string logFile;
	}
}
