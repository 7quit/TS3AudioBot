// TS3AudioBot - An advanced Musicbot for Teamspeak 3
// Copyright (C) 2017  TS3AudioBot contributors
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the Open Software License v. 3.0
//
// You should have received a copy of the Open Software License along with this
// program. If not, see <https://opensource.org/licenses/OSL-3.0>.

namespace TS3AudioBot.Helper
{
	using System;
	using System.IO;
	using System.Net;

	internal static class WebWrapper
	{
		private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(3);

		public static bool DownloadString(out string site, Uri link, params Tuple<string, string>[] optionalHeaders)
		{
			var request = WebRequest.Create(link);
			foreach (var header in optionalHeaders)
				request.Headers.Add(header.Item1, header.Item2);
			try
			{
				using (var response = request.GetResponse())
				{
					var stream = response.GetResponseStream();
					if (stream == null)
					{
						site = null;
						return false;
					}
					using (var reader = new StreamReader(stream))
					{
						site = reader.ReadToEnd();
						return true;
					}
				}
			}
			catch (WebException)
			{
				site = null;
				return false;
			}
		}

		//if (request.Method == "GET")
		//	request.Method = "HEAD";
		public static ValidateCode GetResponse(Uri link) => GetResponse(link, null);
		public static ValidateCode GetResponse(Uri link, TimeSpan timeout) => GetResponse(link, null, timeout);
		public static ValidateCode GetResponse(Uri link, Action<WebResponse> body) => GetResponse(link, body, DefaultTimeout);
		public static ValidateCode GetResponse(Uri link, Action<WebResponse> body, TimeSpan timeout)
		{
			var request = WebRequest.Create(link);
			try
			{
				request.Timeout = (int)timeout.TotalMilliseconds;
				using (var response = request.GetResponse())
				{
					body?.Invoke(response);
				}
				return ValidateCode.Ok;
			}
			catch (WebException webEx)
			{
				HttpWebResponse errorResponse;
				if (webEx.Status == WebExceptionStatus.Timeout)
				{
					Log.Write(Log.Level.Warning, "TH Request timed out");
					return ValidateCode.Timeout;
				}
				else if ((errorResponse = webEx.Response as HttpWebResponse) != null)
				{
					Log.Write(Log.Level.Warning, $"TH Web error: [{(int)errorResponse.StatusCode}] {errorResponse.StatusCode}");
					return ValidateCode.Restricted;
				}
				else
				{
					Log.Write(Log.Level.Warning, $"TH Unknown request error: {webEx}");
					return ValidateCode.UnknownError;
				}
			}
		}
	}

	internal enum ValidateCode
	{
		Ok,
		UnknownError,
		Restricted,
		Timeout,
	}
}
