// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

namespace TS3AudioBot
{
	using Dependency;
	using Helper;
	using System;
	using System.Collections.Generic;
	using System.Linq;
	using System.Threading;

	public class BotManager : IDisposable
	{
		private static readonly NLog.Logger Log = NLog.LogManager.GetCurrentClassLogger();

		private bool isRunning;
		private List<Bot> activeBots;
		private readonly object lockObj = new object();

		public CoreInjector CoreInjector { get; set; }

		public BotManager()
		{
			isRunning = true;
			Util.Init(out activeBots);
		}

		public void RunBots()
		{
			// TODO Rework with new config
			if (CreateBot() == null)
			{
				Log.Error("Default bot could not connect.");
			}
		}

		public BotInfo CreateBot(/*Ts3FullClientData bot*/)
		{
			bool removeBot = false;
			var bot = new Bot { Injector = CoreInjector.CloneRealm<BotInjector>() };
			if (!CoreInjector.TryInject(bot))
				Log.Warn("Partial bot dependency loaded only");

			lock (bot.SyncRoot)
			{
				if (bot.InitializeBot())
				{
					lock (lockObj)
					{
						activeBots.Add(bot);
						bot.Id = activeBots.Count - 1;
						removeBot = !isRunning;
					}
				}

				if (removeBot)
				{
					StopBot(bot);
					return null;
				}
			}
			return new BotInfo { Id = bot.Id };
		}

		public BotLock GetBotLock(int id)
		{
			Bot bot;
			lock (lockObj)
			{
				if (!isRunning)
					return null;
				bot = id >= 0 && id < activeBots.Count
					? activeBots[id]
					: null;
				if (bot == null)
					return new BotLock(false, null);
			}
			return bot.GetBotLock();
		}

		public void StopBot(Bot bot)
		{
			RemoveBot(bot);
			bot.Dispose();
		}

		internal void RemoveBot(Bot bot)
		{
			lock (lockObj)
			{
				activeBots.Remove(bot);
			}
		}

		public BotInfo[] GetBotInfolist()
		{
			lock (lockObj)
			{
				return activeBots.Select(x => x.GetInfo()).ToArray();
			}
		}

		public void Dispose()
		{
			List<Bot> disposeBots;
			lock (lockObj)
			{
				isRunning = false;
				disposeBots = activeBots;
				activeBots = new List<Bot>();
			}

			foreach (var bot in disposeBots)
			{
				StopBot(bot);
			}
		}
	}

	public class BotLock : IDisposable
	{
		private readonly Bot bot;
		public bool IsValid { get; private set; }
		public Bot Bot => IsValid ? bot : throw new InvalidOperationException("The bot lock is not valid.");

		internal BotLock(bool isValid, Bot bot)
		{
			IsValid = isValid;
			this.bot = bot;
		}

		public void Dispose()
		{
			if (IsValid)
			{
				IsValid = false;
				Monitor.Exit(bot.SyncRoot);
			}
		}
	}
}
