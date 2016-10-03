﻿namespace TS3Client
{
	using System;
	using System.Collections.Generic;
	using System.Text;
	using System.Text.RegularExpressions;

	public class TS3Command
	{
		private static readonly Regex CommandMatch = new Regex(@"[a-z0-9_]+", RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.ECMAScript);
		public static List<CommandParameter> NoParameter => new List<CommandParameter>();
		public static List<CommandOption> NoOptions => new List<CommandOption>();

		private string command;
		private List<CommandParameter> parameter;
		private List<CommandOption> options;

		public TS3Command(string command) : this(command, NoParameter) { }
		public TS3Command(string command, List<CommandParameter> parameter) : this(command, parameter, NoOptions) { }
		public TS3Command(string command, List<CommandParameter> parameter, List<CommandOption> options)
		{
			this.command = command;
			this.parameter = parameter;
			this.options = options;
		}

		public void AppendParameter(CommandParameter addParameter) => parameter.Add(addParameter);
		public void AppendOption(CommandOption addOption) => options.Add(addOption);

		public override string ToString() => BuildToString(command, parameter, options);

		public static string BuildToString(string command, IEnumerable<CommandParameter> parameter, IEnumerable<CommandOption> options)
		{
			if (string.IsNullOrWhiteSpace(command))
				throw new ArgumentNullException(nameof(command));
			if (!CommandMatch.IsMatch(command))
				throw new ArgumentException("Invalid command characters", nameof(command));

			StringBuilder strb = new StringBuilder(TS3String.Escape(command));

			foreach (var param in parameter)
				strb.Append(' ').Append(param.QueryString);

			foreach (var option in options)
				strb.Append(option.Value);

			return strb.ToString();
		}
	}
}
