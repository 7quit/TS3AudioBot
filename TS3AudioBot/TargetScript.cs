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
	using CommandSystem;
	using System;

	class TargetScript
	{
		private const string defaultVoiceScript = "!whisper off";
		private const string defaultWhisperScript = "!xecute (!whisper subscription) (!unsubscribe temporary) (!subscribe channeltemp (!getuser channel))";

		private MainBot parent;
		private CommandManager CommandManager => parent.CommandManager;

		public TargetScript(MainBot bot)
		{
			parent = bot;
		}

		public void BeforeResourceStarted(object sender, PlayInfoEventArgs e)
		{
			var mode = AudioValues.audioFrameworkData.AudioMode;
			string script;
			if (mode.StartsWith("!", StringComparison.Ordinal))
				script = mode;
			else if (mode.Equals("voice", StringComparison.OrdinalIgnoreCase))
				script = defaultVoiceScript;
			else if (mode.Equals("whisper", StringComparison.OrdinalIgnoreCase))
				script = defaultWhisperScript;
			else
			{
				Log.Write(Log.Level.Error, "Invalid voice mode");
				return;
			}
			CallScript(script, e.Invoker);
		}

		private void CallScript(string script, InvokerData invoker)
		{
			try
			{
				var info = new ExecutionInformation(parent, invoker, null) { SkipRightsChecks = true };
				CommandManager.CommandSystem.Execute(info, script);
			}
			catch (CommandException) { }
		}
	}
}
