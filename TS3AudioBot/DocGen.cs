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
	using System;
	using System.Text;
	using System.IO;
	using System.Collections.Generic;
	using CommandSystem;

	/// <summary>
	/// Used to generate the command overview for GitHub.
	/// </summary>
	class DocGen
	{
		static void Main(string[] args)
		{
			const bool writeSubCommands = false;

			var bot = new MainBot();
			typeof(MainBot).GetProperty(nameof(MainBot.CommandManager)).SetValue(bot, new CommandManager());
			bot.CommandManager.RegisterMain(bot);
			
			var lines = new List<string>();

			foreach (var com in bot.CommandManager.AllCommands)
			{
				if (!writeSubCommands && com.InvokeName.Contains(" "))
					continue;

				string description;
				if (string.IsNullOrEmpty(com.Description))
					description = " - no description yet -";
				else
					description = com.Description;
				lines.Add($"* *{com.InvokeName}*: {description}");
			}

			// adding commands which only have subcommands
			lines.Add("* *history*: Shows recently played songs.");

			lines.Sort();

			string final = string.Join("\n", lines);
			Console.WriteLine(final);
			File.WriteAllText("docs.txt", final);

			Console.ReadLine();
		}
	}
}
