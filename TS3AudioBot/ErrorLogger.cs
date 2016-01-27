﻿namespace TS3AudioBot
{
	using System;
	using System.Collections.Generic;
	using System.Diagnostics;
	using System.Globalization;
	using System.Reflection;
	using System.Reflection.Emit;
	using System.Text;
	using System.Text.RegularExpressions;
	using System.Threading;

	public static class Log
	{
		public static bool Active { get; set; }
		public static int StackLevel { get; set; }

		private static readonly object writeLock;
		private static int callbackCount = 0;
		private static CallbackActionDelegate[] callbackAction;
		private delegate void CallbackProcessorDelegate(LogHelper lh);
		private static CallbackProcessorDelegate[] callbackProcessor;

		private static int longestelem = 0;
		private static string[] spaceup;

		static Log()
		{
			writeLock = new object();

			StackLevel = 10;
			Active = true;

			CalcSpaceLength();
		}

		private static void CalcSpaceLength()
		{
			string[] earr = Enum.GetNames(typeof(Level));

			for (int i = 0; i < earr.Length; i++)
				if (earr[i].Length > longestelem)
					longestelem = earr[i].Length;
			spaceup = new string[earr.Length];
			StringBuilder strb = new StringBuilder(longestelem + 1);
			for (int i = 0; i < earr.Length; i++)
			{
				strb.Append(' ', longestelem - earr[i].Length);
				strb.Append(earr[i]);
				spaceup[i] = strb.ToString();
				strb.Clear();
			}
		}

		public static void RegisterLogger(string format, string linebreakIndent, CallbackActionDelegate callback)
		{
			try
			{
				var validator = ParseAndValidate(format);
				RegisterLoggerUnsafe(validator, linebreakIndent, callback);
			}
			catch (ArgumentException) { throw; }
		}

		private static List<ParseToken> ParseAndValidate(string format)
		{
			var validator = new List<ParseToken>();

			int starttext = 0;
			for (int c = 0; c < format.Length; c++)
			{
				bool specSymbol = format[c] == '%';
				bool endoftext = c >= format.Length - 1;

				if ((specSymbol && c - starttext > 0) || endoftext) // static format text
				{
					if (endoftext) c++;
					validator.Add(new ParseToken(MethodBuildToken.Text, format.Substring(starttext, c - starttext)));
				}

				if (specSymbol)
				{
					if (c + 1 < format.Length)
					{
						switch (format[c + 1])
						{
						case 'M': // Message
							validator.Add(new ParseToken(MethodBuildToken.ErrorTextFormatted, null));
							break;
						case 'T': // Time
							validator.Add(new ParseToken(MethodBuildToken.DateFormatted, null));
							break;
						case 'L': // Level
							validator.Add(new ParseToken(MethodBuildToken.LogLevelSpaced, null));
							break;
						case 'S': // Stack
							validator.Add(new ParseToken(MethodBuildToken.StackFormatted, null));
							break;
						}
						c++;
						starttext = c + 1;
					}
					else
					{
						throw new ArgumentException("Missing variable name after '%' at char: " + c);
					}
				}
			}

			return validator;
		}

		private static void RegisterLoggerUnsafe(List<ParseToken> validator, string linebreakIndent, CallbackActionDelegate callback)
		{
			DynamicMethod dynLog = new DynamicMethod("LogWrite" + callbackCount, typeof(void), new[] { typeof(LogHelper) }, typeof(Log), true);
			var ilGen = dynLog.GetILGenerator();
			var localStrb = ilGen.DeclareLocal(typeof(StringBuilder));

			// common type arrays
			Type[] argsString = { typeof(string) };
			Type[] argsInt = { typeof(int) };

			// common InfosTypes
			MethodInfo miStringBuilder_Append_String = typeof(StringBuilder).GetMethod(nameof(StringBuilder.Append), argsString);
			MethodInfo miLog_GenLogLevelSpaced = typeof(LogHelper).GetMethod(nameof(LogHelper.GenLogLevelSpaced));
			MethodInfo miLog_GenDateFormatted = typeof(LogHelper).GetMethod(nameof(LogHelper.GenDateFormatted));
			MethodInfo miLog_GenErrorTextFormatted = typeof(LogHelper).GetMethod(nameof(LogHelper.GenErrorTextFormatted), argsInt);
			MethodInfo miLog_GenStackTraceFormatted = typeof(LogHelper).GetMethod(nameof(LogHelper.GenStackTraceFormatted), argsInt);

			// prepare callback invoke
			ilGen.Emit(OpCodes.Ldsfld, typeof(Log).GetField(nameof(callbackAction), BindingFlags.NonPublic | BindingFlags.Static));
			ilGen.Emit(OpCodes.Ldc_I4, callbackCount);
			ilGen.Emit(OpCodes.Ldelem_Ref);

			// Load stringbuilder
			ilGen.Emit(OpCodes.Newobj, typeof(StringBuilder).GetConstructor(Type.EmptyTypes));
			ilGen.Emit(OpCodes.Stloc, localStrb);
			ilGen.Emit(OpCodes.Ldloc, localStrb);

			foreach (var part in validator)
			{
				switch (part.TokenType)
				{
				case MethodBuildToken.Text:
					ilGen.Emit(OpCodes.Ldstr, (string)part.Value);
					break;
				case MethodBuildToken.LogLevelSpaced:
					ilGen.Emit(OpCodes.Ldarg_0);
					ilGen.EmitCall(OpCodes.Callvirt, miLog_GenLogLevelSpaced, null);
					break;
				case MethodBuildToken.ErrorTextFormatted:
					ilGen.Emit(OpCodes.Ldarg_0);
					ilGen.Emit(OpCodes.Ldc_I4_0);
					ilGen.EmitCall(OpCodes.Callvirt, miLog_GenErrorTextFormatted, null);
					break;
				case MethodBuildToken.DateFormatted:
					ilGen.Emit(OpCodes.Ldarg_0);
					ilGen.EmitCall(OpCodes.Callvirt, miLog_GenDateFormatted, null);
					break;
				case MethodBuildToken.StackFormatted:
					ilGen.Emit(OpCodes.Ldarg_0);
					ilGen.Emit(OpCodes.Ldc_I4_M1);
					ilGen.EmitCall(OpCodes.Callvirt, miLog_GenStackTraceFormatted, null);
					break;
				default:
					throw new InvalidProgramException("Undefined MethodBuildToken occoured");
				}
				ilGen.EmitCall(OpCodes.Callvirt, miStringBuilder_Append_String, null);
			}

			// call ToString and the callback method
			ilGen.EmitCall(OpCodes.Callvirt, typeof(StringBuilder).GetMethod(nameof(StringBuilder.ToString), Type.EmptyTypes), null);
			ilGen.EmitCall(OpCodes.Callvirt, typeof(CallbackActionDelegate).GetMethod(nameof(CallbackActionDelegate.Invoke), argsString), null);

			ilGen.Emit(OpCodes.Ret);

			lock (writeLock)
			{
				// Redim the calllist array
				if (callbackProcessor == null)
				{
					callbackProcessor = new CallbackProcessorDelegate[1];
					callbackAction = new CallbackActionDelegate[1];
				}
				else
				{
					CallbackProcessorDelegate[] tempProcessorArray = new CallbackProcessorDelegate[callbackCount + 1];
					CallbackActionDelegate[] tempActionArray = new CallbackActionDelegate[callbackCount + 1];
					Array.Copy(callbackProcessor, tempProcessorArray, callbackCount);
					Array.Copy(callbackAction, tempActionArray, callbackCount);
					callbackProcessor = tempProcessorArray;
					callbackAction = tempActionArray;
				}

				//Store event call in the calllist
				callbackProcessor[callbackCount] = (CallbackProcessorDelegate)dynLog.CreateDelegate(typeof(CallbackProcessorDelegate));
				callbackAction[callbackCount] = callback;

				callbackCount++;
			}
		}

		private static void DefaultTest(LogHelper lh)
		{
			StringBuilder strb = new StringBuilder();
			Log.callbackAction[0](strb.Append(lh.GenLogLevelSpaced())
			.Append(": ")
			.Append(lh.GenErrorTextFormatted(0)).ToString());
		}

		public static void Write(Level lvl, string errText, params object[] infos)
		{
			if (!Active)
				return;

			LogHelper lh = new LogHelper(lvl, new StackTrace(1), errText, infos);
			lh.GenStackTraceFormatted(-1);
			lock (writeLock)
			{
				foreach (var callbackProc in callbackProcessor)
					callbackProc(lh);
			}
		}

		private enum MethodBuildToken
		{
			Text,
			LogLevelSpaced,
			ErrorTextFormatted,
			DateFormatted,
			StackFormatted,
		}

		public enum Level : int
		{
			Info,
			Debug,
			Warning,
			Error,
		}

		private class LogHelper
		{
			private StackTrace stackTrace;
			private string errorTextRaw;
			private object[] infos;
			private Level level;

			public LogHelper(Level level, StackTrace stackTrace, string errorTextRaw, object[] infos)
			{
				this.stackTrace = stackTrace;
				this.errorTextRaw = errorTextRaw;
				this.infos = infos;
				this.level = level;
			}

			public string LogLevelRaw = null;
			public string LogLevelSpaced = null;
			public string ErrorTextFormatted = null;
			public string DateFormatted = null;
			public List<string> StackTraceFormatted = null;

			public string GenLogLevelRaw()
			{
				if (LogLevelRaw == null)
				{
					LogLevelRaw = level.ToString();
				}
				return LogLevelRaw;
			}

			public string GenLogLevelSpaced()
			{
				if (LogLevelSpaced == null)
				{
					LogLevelSpaced = spaceup[(int)level];
				}
				return LogLevelSpaced;
			}

			public string GenErrorTextFormatted(int linebreakIndent)
			{
				if (ErrorTextFormatted == null)
				{
					string inputbuffer = string.Format(CultureInfo.InvariantCulture, errorTextRaw, infos);
					if (linebreakIndent > 0)
					{
						string spaces = new string(' ', linebreakIndent);
						inputbuffer = Regex.Replace(inputbuffer, @"(\r\n?|\n)", (x) => x.Value + spaces); // Test vs compiled, vs StrB
					}
					ErrorTextFormatted = inputbuffer;
				}
				return ErrorTextFormatted;
			}

			public string GenDateFormatted()
			{
				if (DateFormatted == null)
				{
					DateFormatted = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
				}
				return DateFormatted;
			}

			public string GenStackTraceFormatted(int stackDepth)
			{
				var strb = new StringBuilder();
				if (stackDepth < 0) stackDepth = stackTrace.FrameCount;
				if (StackTraceFormatted == null)
				{
					StackTraceFormatted = new List<string>(stackDepth);

					foreach (var frames in stackTrace.GetFrames())
					{
						var method = frames.GetMethod();
						bool endOfIl = method.MethodImplementationFlags == MethodImplAttributes.InternalCall;
						if (endOfIl)
							strb.Append("$internal T:").Append(Thread.CurrentThread.Name);
						else
							strb.Append(method.ToString()).Append('@').Append(frames.GetFileLineNumber());
						StackTraceFormatted.Add(strb.ToString());
						strb.Clear();
						if (endOfIl) break;
					}
				}
				for (int i = 0; i < Math.Min(stackDepth, StackTraceFormatted.Count); i++)
					strb.Append(StackTraceFormatted[i]).Append('\n'); // or .Append('>')
				strb.Length--; // remove last char
				return strb.ToString();
			}

			public static string ExtractAnonymous(string name)
			{
				int startName = name.IndexOf('<');
				int endName = name.IndexOf('>');

				if (startName < 0 || endName < 0)
					return string.Empty;

				return name.Substring(startName, endName - startName);
			}
		}

		private class ParseToken
		{
			public readonly MethodBuildToken TokenType;
			public readonly object Value;

			public ParseToken(MethodBuildToken tokenType, object value)
			{
				TokenType = tokenType;
				Value = value;
			}
		}
	}

	public delegate void CallbackActionDelegate(string result);
}
