﻿using System;

using TeamSpeak3QueryApi.Net.Specialized.Responses;

using Response = System.Func<TS3AudioBot.BotSession, TeamSpeak3QueryApi.Net.Specialized.Notifications.TextMessage, bool, bool>;

namespace TS3AudioBot
{
	abstract class BotSession
	{
		protected QueryConnection QueryConnection;

		public AudioRessource UserRessource { get; set; }
		public Response ResponseProcessor { get; protected set; }
		public bool AdminResponse { get; protected set; }
		public object ResponseData { get; protected set; }

		public abstract bool IsPrivate { get; }

		public abstract void Write(string message);

		protected BotSession(QueryConnection queryConnection)
		{
			QueryConnection = queryConnection;
			UserRessource = null;
			ResponseProcessor = null;
			ResponseData = null;
		}

		public void SetResponse(Response responseProcessor, object responseData, bool requiresAdminCheck)
		{
			ResponseProcessor = responseProcessor;
			ResponseData = responseData;
			AdminResponse = requiresAdminCheck;
		}

		public void ClearResponse()
		{
			ResponseProcessor = null;
			ResponseData = null;
			AdminResponse = false;
		}
	}

	sealed class PublicSession : BotSession
	{
		public override bool IsPrivate { get { return false; } }

		public override async void Write(string message)
		{
			try
			{
				await QueryConnection.TSClient.SendGlobalMessage(message);
			}
			catch (Exception ex)
			{
				Log.Write(Log.Level.Error, "Could not write public message ({0})", ex);
			}
		}

		public PublicSession(QueryConnection queryConnection)
			: base(queryConnection)
		{ }
	}

	sealed class PrivateSession : BotSession
	{
		public GetClientsInfo Client { get; private set; }

		public override bool IsPrivate { get { return true; } }

		public override async void Write(string message)
		{
			await QueryConnection.TSClient.SendMessage(message, Client);
		}

		public PrivateSession(QueryConnection queryConnection, GetClientsInfo client)
			: base(queryConnection)
		{
			Client = client;
		}
	}
}
